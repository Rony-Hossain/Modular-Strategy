# Phase 3.1 — Tape Recorder (tick stream capture)

First Phase 3 task. Wires NT8's `OnMarketData(Last)` into a new allocation-free ring buffer that holds ~30 seconds of executed trades (ticks). No detectors, no scoring, no advisor consumers — just a clean, zero-GC tick stream that downstream Phase 3.2–3.7 detectors (aggressor, BigPrint, velocity, sweep, tape-level iceberg refresh) will read.

This is the foundation for moving from bar-local footprint data (Phase 1–2) to within-bar tape behavior (Phase 3).

## Files
- **New**: `ModularStrategy/TapeRecorder.cs`
- **Modified**: `ModularStrategy/HostStrategy.cs`
  - Add `OnMarketData` override
  - Add `_tape` field and instantiate in `State.DataLoaded` init block
  - Set `Calculate = Calculate.OnBarClose` already in place — no change; `OnMarketData` fires independently of `Calculate`

**Do not touch**: `FootprintCore.cs`, any detector, any snapshot/scoring code. Phase 3.1 is pure data capture.

## New Type

```csharp
public sealed class TapeRecorder
{
    // Ring capacity sized for NQ-class peak rate: ~1000 ticks/sec × 30s, headroom ×1.5
    private const int DEFAULT_CAPACITY = 45000;
    private const long DEFAULT_WINDOW_MS = 30_000;

    private readonly Tick[] _ring;
    private readonly int    _capacity;
    private readonly long   _windowMs;
    private int _head;       // next write slot
    private int _count;      // number of live ticks (<= _capacity)
    private long _lastSeqNo; // monotonically increasing tick id

    public TapeRecorder(int capacity = DEFAULT_CAPACITY, long windowMs = DEFAULT_WINDOW_MS);

    /// Single call per NT8 OnMarketData(Last). Zero allocation. Thread-affine:
    /// NT8 drives this on the instrument thread — no locking needed.
    public void OnTick(DateTime timeUtc, double price, long volume, double bid, double ask);

    /// Reset on session boundaries. Keeps capacity; drops all stored ticks.
    public void OnSessionOpen();

    // Read API (Phase 3.2+ consumers). All O(1) or O(n) with n=count; no allocations.
    public int  Count      { get; }
    public long WindowMs   { get; }
    public long LatestSeq  { get; }   // last assigned sequence number

    /// Enumerate live ticks oldest→newest into caller's buffer.
    /// Returns number copied. If dst is smaller than Count, copies the most recent dst.Length.
    public int CopyTo(Tick[] dst);

    /// Indexed access, 0 = oldest, Count-1 = newest. For detectors that scan linearly.
    public Tick At(int i);
}

public readonly struct Tick
{
    public readonly long     SeqNo;   // monotonic, for dedup/ordering
    public readonly long     TimeMs;  // ms since session open (or epoch ms — see Invariants)
    public readonly double   Price;
    public readonly long     Volume;
    public readonly double   Bid;     // best bid at time of print
    public readonly double   Ask;     // best ask at time of print
    public readonly Aggressor Side;   // classification: Buy if Price >= Ask, Sell if <= Bid, Unknown otherwise

    public Tick(long seqNo, long timeMs, double price, long volume, double bid, double ask, Aggressor side);
}

public enum Aggressor : byte { Unknown = 0, Buy = 1, Sell = 2 }
```

### Aggressor classification (Phase 3.1 — basic only)

Phase 3.2 will own the richer aggressor classifier (pre-tick BBO capture, tick-at-mid handling, uptick/downtick fallback). Phase 3.1 does the **cheap inline classification only**, so the stored ticks already carry a usable side field for downstream work:

```
if (price >= ask) Buy
else if (price <= bid) Sell
else Unknown
```

This is intentionally naive — it will misclassify mid-trades — but it unblocks Phase 3.2 which will *replace* this field using prior-BBO state before emitting events. The inline tag is a best-effort default, not the source of truth.

## Ring Semantics

### Write path (`OnTick`)
1. Assign `seqNo = ++_lastSeqNo`.
2. Compute `timeMs` (see "Time base" below).
3. Classify aggressor (naive rule above).
4. Write to `_ring[_head]`, advance `_head = (_head + 1) % _capacity`.
5. If `_count < _capacity`, `_count++`; else the oldest slot is overwritten (capacity-based eviction).
6. Apply **time-based eviction**: walk forward from the oldest slot, advancing an internal "oldest pointer" as long as `latest.TimeMs - oldest.TimeMs > _windowMs`. Decrement `_count` for each evicted slot.

Both evictions coexist: capacity caps worst-case storage; window caps the visible age. On a slow tape, capacity is never hit and the 30s window evicts idle ticks. On a burst, capacity protects against unbounded backlog.

Implementation detail: track the oldest index as `_oldest = (_head - _count + _capacity) % _capacity`. Time eviction just increments `_oldest` and decrements `_count` until the time gap is within window.

### Read path
- `At(i)`: `return _ring[(_oldest + i) % _capacity];`
- `CopyTo(dst)`: straight loop over the live range, no LINQ, no yield.

## Time base

Two reasonable choices; **pick ms-since-session-open** for Phase 3.1:

- NT8 `MarketDataEventArgs.Time` is `DateTime` local — converting to `DateTimeOffset.ToUnixTimeMilliseconds()` per tick is one allocation-free struct op, but sensitive to clock adjustments.
- Ms-since-session-open = `(int)(e.Time - _sessionOpenTime).TotalMilliseconds` cast to long. Wraps safely within a session (never >24h). Detectors only need relative deltas, so this is sufficient and avoids DST/UTC confusion.

Session-open time is captured in `OnSessionOpen()` from `Time[0]` of the primary series. If `OnTick` fires before the first bar (pre-session arming), `_sessionOpenTime` is unset → tick is dropped (`return` early). A `_armed` bool gates this.

## Host Wiring

In `HostStrategy.cs`:

### Field
```csharp
private TapeRecorder _tape;
```

### `State.DataLoaded` init (same block that creates `_feed`, `_fpCore`, etc.)
```csharp
_tape = new TapeRecorder();
```

### Session-open hook
In `OnBarUpdate` session-open block (around line 270, next to `_feed.OnSessionOpen()`):
```csharp
_tape.OnSessionOpen(Time[0]);
```

### `OnMarketData` override (new)
```csharp
protected override void OnMarketData(MarketDataEventArgs e)
{
    if (e.MarketDataType != MarketDataType.Last) return;
    if (_tape == null) return;
    _tape.OnTick(e.Time, e.Price, e.Volume, GetCurrentBid(), GetCurrentAsk());
}
```

Rationale for `GetCurrentBid/Ask()` rather than `e.Bid/e.Ask`: NT8's `MarketDataEventArgs` for a `Last` event does not carry BBO — `e.Price` is the trade price. `GetCurrentBid()` and `GetCurrentAsk()` return the L1 quote as last seen by the strategy, which is the correct pre-trade context for aggressor classification. These are zero-allocation property reads.

## Invariants
- **Zero allocation** in `OnTick`. No `DateTime.Now`, no boxing, no array resizes, no LINQ.
- **Single-threaded**: NT8 delivers `OnMarketData` on the instrument dispatcher. No locks.
- **Monotonic sequence**: `SeqNo` is strictly increasing within a session. Reset to 0 on `OnSessionOpen`.
- **Time base**: ms since session open, `long`. Wraps never occur within a trading session.
- **Eviction order**: window eviction always runs *after* the write, so the newest tick is always visible even if it alone is older than the window (pathological quiet tape).
- **Capacity**: sized for 1000 ticks/s × 30s × 1.5 = 45000. Change via constructor only; no runtime resize.
- **Graceful under-arm**: if `OnMarketData` fires before `OnSessionOpen` has set the session anchor, ticks are silently dropped (not buffered, not errored).
- Ring never leaks `Tick` references externally except by value-copy through `At`/`CopyTo` — the struct is `readonly`.

## Do Not
- Add detector logic (BigPrint, velocity, sweep) in this phase. Phase 3.1 is capture only.
- Expose the internal `_ring` array. Detectors go through `At`/`CopyTo`.
- Use `List<Tick>` or any growable collection.
- Subscribe to `MarketDataType.Bid` or `Ask` streams — Phase 3.1 only records `Last`. BBO is fetched lazily via `GetCurrentBid/Ask` at trade time.
- Log per-tick to `_log`. 30,000+ log lines per 30 seconds would crash the strategy.
- Set `Calculate = Calculate.OnEachTick`. `OnMarketData` is independent of `Calculate` and fires regardless.

## Validation
- [ ] Project builds, no warnings, no allocations flagged by the build.
- [ ] Load on a single NQ session (replay). Confirm:
  - `_tape.Count` grows during active tape and plateaus at the window-dictated steady state.
  - `_tape.LatestSeq` increases monotonically.
  - After a 1-minute idle period mid-session, `Count` falls to near zero (window eviction works).
- [ ] Add a one-shot debug print in `OnBarUpdate` (remove before commit):
  - `Print($"tape: count={_tape.Count} lastSeq={_tape.LatestSeq}");`
  - Expected: on a live NQ 1-min bar, count in the 2k–30k range depending on activity.
- [ ] Stress: an NQ cash-open minute (08:30 CT) should not cause capacity overrun. If it does, bump capacity — do not silently drop.
- [ ] No `OutOfMemory` or GC pressure visible in NT8's performance tab across a full session.
- [ ] `Tick.Side` distribution on a normal bar: ~40/40/20 Buy/Sell/Unknown. Unknown fraction >50% means BBO capture is broken — investigate `GetCurrentBid/Ask` timing.

## Rationale
NT8's volumetric bars already aggregate ticks into per-level buckets — Phase 1/2 detectors ride on top. But every tick-level pattern we care about in Phase 3 (sweeps across levels, velocity spikes, big prints, tape-level iceberg confirmation) needs the *raw* tick sequence with precise timestamps. Volumetric bar data loses sub-bar ordering.

The 30-second window is long enough to hold several bursts and short enough to stay small in memory (45k × ~40 bytes = 1.8 MB, fixed). No GC churn, no allocations per tick, no threading.

Keeping Phase 3.1 capture-only — no detectors — lets us validate the recorder in isolation before Phase 3.2 builds the aggressor classifier that will overwrite `Tick.Side` with a correct uptick/downtick-aware value. Phases 3.3–3.7 all consume the same buffer; getting the ring right once pays off across five downstream features.
