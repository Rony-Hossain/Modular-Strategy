# Phase 3.6 — Tape-Level Iceberg Refresh

Intra-bar iceberg detection. The Phase 2.4/2.8 `BullIceberg`/`BearIceberg` detector looks at bar-closed footprint data (3 bars of recurrence). This one runs on the tick stream: when the same price level absorbs ≥N same-side aggressor prints within a short window without price breaking through, flag an active tape-level iceberg in real time.

Two independent detectors (bar-level and tape-level) give different time horizons and can cross-confirm. Phase 3.7 will combine both into Layer C scoring.

## Semantics (matches bar-level Iceberg)

- **BullIceberg (tape)** — hidden buyer at the bid. Repeated **Sell** aggressors at the same price (hitting the bid) without price dropping below → buyer is absorbing. Confirms LONG.
- **BearIceberg (tape)** — hidden seller at the ask. Repeated **Buy** aggressors at the same price (lifting the ask) without price breaking above → seller is absorbing. Confirms SHORT.

This matches the convention: `BullIceberg` ⇒ bullish, `BearIceberg` ⇒ bearish. Same-side confirmation (not opposite-side).

## Files
- **New**: `ModularStrategy/TapeIcebergDetector.cs`
- **Modified**: `ModularStrategy/TapeRecorder.cs` (fourth detector hook)
- **Modified**: `ModularStrategy/HostStrategy.cs` (instantiate, wire, pass tickSize)

**Do not touch**: any Phase 2 file (including `FootprintCore.cs`'s bar-level Iceberg), ConfluenceEngine, CommonTypes.

## Design

### Level slot table (not a hash map)

Maintain a small fixed-size array of "active price levels under observation" — up to 16 concurrent levels. Each slot tracks one price and its recent same-side hits.

Why an array, not a dict: NT8's hot path cannot afford `Dictionary<double, T>` lookups with boxing or GC. 16 linear scans per tick is trivial (~50ns) and covers any realistic concurrent-iceberg count (typical is 1-2).

```
struct LevelSlot
{
    double    Price;
    long      FirstTimeMs;   // when first hit in current window
    long      LastTimeMs;    // most recent hit
    int       HitCount;      // same-side prints at this price in current window
    long      Volume;        // cumulative volume in this window
    Aggressor Side;          // Buy or Sell (both tracked independently → two slots possible for same price, different sides)
    bool      Active;        // true = fired as iceberg, held until expiry
}
```

### Lifecycle per slot

1. **Create**: new Buy or Sell tick at price P with no existing (P, Side) slot → find oldest inactive or expired slot, reset it, record first hit.
2. **Increment**: same (P, Side) tick, within window → bump HitCount, Volume, LastTimeMs.
3. **Fire**: when HitCount reaches ICEBERG_MIN_HITS (default 8), set Active=true. Raise `BullIceberg` or `BearIceberg` flag depending on Side.
4. **Expire**: on every OnTick, walk slots. If `tick.TimeMs - LastTimeMs > WINDOW_MS`, clear the slot (Active → false).
5. **Break**: if a same-side tick arrives at a different price AND that price is beyond the level (for Buy side: newPrice > slotPrice+tickSize; for Sell side: newPrice < slotPrice-tickSize), the level is considered broken — clear that slot's Active flag. The aggressor walked past the level, so the "hidden liquidity" thesis is invalidated.

### Output flags (per side)

Aggregate active slots into two booleans plus metadata:

```
BullIcebergActive   = any Sell-side slot with Active==true
BearIcebergActive   = any Buy-side slot with Active==true
BullIcebergPrice    = Price of the most-recently-fired BullIceberg slot
BearIcebergPrice    = Price of the most-recently-fired BearIceberg slot
BullIcebergVolume   = Volume of the same
BearIcebergVolume   = Volume of the same
LastIcebergTimeMs   = LastTimeMs of most recent firing (either side)
LastIcebergAgeMs    = tick.TimeMs - LastIcebergTimeMs (or long.MaxValue)
```

### Match tolerance

Price match uses a tick-size tolerance to avoid floating-point false negatives:

```csharp
bool priceEquals = Math.Abs(a - b) < tickSize * 0.5;
```

So 17234.75 matches 17234.75 but not 17235.00 (for tickSize=0.25).

## New Type

```csharp
public sealed class TapeIcebergDetector
{
    private const long WINDOW_MS       = 5_000;   // 5-second refresh window
    private const int  ICEBERG_MIN_HITS = 8;      // same-side prints to fire
    private const int  SLOT_CAPACITY   = 16;      // concurrent levels under observation

    private readonly double _tickSize;
    private readonly LevelSlot[] _slots;

    private bool _armed;

    // Outputs
    public bool   BullIcebergActive  { get; private set; }
    public bool   BearIcebergActive  { get; private set; }
    public double BullIcebergPrice   { get; private set; }
    public double BearIcebergPrice   { get; private set; }
    public long   BullIcebergVolume  { get; private set; }
    public long   BearIcebergVolume  { get; private set; }
    public long   LastIcebergTimeMs  { get; private set; }
    public long   LastIcebergAgeMs   { get; private set; }

    public TapeIcebergDetector(double tickSize);
    public void OnTick(in Tick tick);
    public void OnSessionOpen();

    private struct LevelSlot
    {
        public double    Price;
        public long      FirstTimeMs;
        public long      LastTimeMs;
        public int       HitCount;
        public long      Volume;
        public Aggressor Side;
        public bool      Active;
        public bool      InUse;
    }
}
```

## Algorithm — OnTick(in Tick tick)

```
1. If !_armed, return.
2. If tick.Side is Unknown, return (can't attribute).
3. Expire/break pass (single scan over _slots):
   for each slot in _slots where slot.InUse:
     if tick.TimeMs - slot.LastTimeMs > WINDOW_MS:
         slot.InUse = slot.Active = false; continue
     if slot.Side == Buy  && tick.Side == Buy  && tick.Price > slot.Price + _tickSize*0.5:
         slot.InUse = slot.Active = false; continue    // walked past buy level
     if slot.Side == Sell && tick.Side == Sell && tick.Price < slot.Price - _tickSize*0.5:
         slot.InUse = slot.Active = false; continue    // walked past sell level
4. Find matching slot for (tick.Price, tick.Side):
   for each slot in _slots where slot.InUse && slot.Side == tick.Side:
     if |slot.Price - tick.Price| < _tickSize*0.5: matched; break
5. If matched:
     slot.HitCount += 1
     slot.Volume   += tick.Volume
     slot.LastTimeMs = tick.TimeMs
     if slot.HitCount >= ICEBERG_MIN_HITS && !slot.Active:
         slot.Active = true
6. Else (no match):
     find slot where !InUse (or oldest if all in use)
     reset: Price=tick.Price, Side=tick.Side, FirstTimeMs=LastTimeMs=tick.TimeMs,
            HitCount=1, Volume=tick.Volume, Active=false, InUse=true
7. Recompute aggregate flags:
   BullIcebergActive = any slot with Side==Sell && Active
   BearIcebergActive = any slot with Side==Buy  && Active
   For each side, if active, pick the slot with most recent LastTimeMs → set
   BullIcebergPrice/Volume, BearIcebergPrice/Volume accordingly.
   LastIcebergTimeMs = max LastTimeMs across active slots; LastIcebergAgeMs = tick.TimeMs - LastIcebergTimeMs.
```

## TapeRecorder Change

Fourth detector hook (same pattern as BigPrint, Velocity, Sweep):

```csharp
private TapeIcebergDetector _tapeIcebergDetector;

public void SetTapeIcebergDetector(TapeIcebergDetector detector)
{
    _tapeIcebergDetector = detector;
}

// In OnTick, after _sweepDetector?.OnTick(in lastTick):
_tapeIcebergDetector?.OnTick(in lastTick);
```

## Host Wiring

```csharp
private TapeIcebergDetector _tapeIceberg;

// State.DataLoaded after sweep wiring:
_tapeIceberg = new TapeIcebergDetector(Instrument.MasterInstrument.TickSize);
_tape.SetTapeIcebergDetector(_tapeIceberg);

// Session-open after _sweep?.OnSessionOpen():
_tapeIceberg?.OnSessionOpen();
```

## Invariants
- Zero allocation in `OnTick`. `_slots` pre-allocated in constructor as `new LevelSlot[SLOT_CAPACITY]`.
- Struct slots — no per-level GC pressure.
- `OnTick` is O(SLOT_CAPACITY) = O(16), effectively constant.
- Unknown-side ticks are ignored completely — no slot interaction.
- A single price P can have **two** concurrent slots: one Buy-side, one Sell-side. They're independent aggressions.
- `Active` only transitions false→true (on firing). It clears only via expire or break (both in step 3).
- Slot recycling: when all slots are in use and a new level arrives, evict the slot with the oldest `LastTimeMs`. Active slots are preferred to be preserved (don't evict Active unless no inactive slots exist). Simpler implementation: evict oldest InUse slot regardless — with 16 slots and 5s windows, Active slots rarely conflict with new level arrivals.

## Validation
- [ ] Project builds, no warnings.
- [ ] Backtest identical to Phase 3.5 baseline — no scoring.
- [ ] Debug print on bar close: `Print($"tIce: bull={_tapeIceberg.BullIcebergActive}@{_tapeIceberg.BullIcebergPrice} bear={_tapeIceberg.BearIcebergActive}@{_tapeIceberg.BearIcebergPrice} age={_tapeIceberg.LastIcebergAgeMs}ms");`
- [ ] Expected: during typical NQ 5-minute bar, 0-3 tape icebergs per side. Cash-open hour elevated. Zero firings across a full session = threshold too high.

## Do Not
- Use the bar-level `BullIceberg`/`BearIceberg` published by HostStrategy (Phase 2.8). That's a completely separate signal on 5-min bar data.
- Merge tape and bar iceberg into one flag at this phase. Phase 3.7 handles combination logic in scoring.
- Use `Dictionary<double, LevelSlot>`. Array with linear scan only.
- Lower `ICEBERG_MIN_HITS` below 5. Too sensitive → noise. Bar-level threshold is 3 *bars* of recurrence; tape-level 8 *prints* is the rough equivalent in information content.
- Fire the detector hook in TapeRecorder before the write+evict+previous detectors. Order: `_bigPrintDetector → _velocityDetector → _sweepDetector → _tapeIcebergDetector`.

## Rationale
Bar-level iceberg (Phase 2.4/2.8) is high signal-to-noise but slow — fires 0.5 bars after the absorption began (requires 3-bar recurrence, closes on bar 3). Tape-level fires within seconds of the absorption pattern developing, giving an earlier entry cue. When both agree, conviction is highest. When only tape-level fires, it may be noise or a sweep being mistaken for absorption — Phase 3.7 will use the agreement vs disagreement as a scoring input. After Phase 3.6, the Phase 3 detector stack is complete: BigPrint + Velocity + Sweep + TapeIceberg, all feeding a single real-time snapshot for Phase 3.7 to wire into ConfluenceEngine.
