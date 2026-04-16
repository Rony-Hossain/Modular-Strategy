# Phase 3.3 — BigPrint Detector

Detect abnormally large trades (≥p95 of rolling volume distribution) from the TapeRecorder tick stream. BigPrints are institutional footprints — a single print that exceeds the 95th percentile of recent trade sizes signals aggressive positioning. This phase is detection only; scoring/wiring is Phase 3.7.

## Files
- **New**: `ModularStrategy/BigPrintDetector.cs`
- **Modified**: `ModularStrategy/TapeRecorder.cs` (add hook for per-tick notification to detector)
- **Modified**: `ModularStrategy/HostStrategy.cs` (instantiate detector, wire to tape)

**Do not touch**: `FootprintCore.cs`, `ConfluenceEngine.cs`, any Phase 2 detector.

## Design

### Rolling p95 threshold

Maintain a ring buffer of the last 2000 trade volumes. Every 200 ticks, recompute the p95 threshold by partial-sorting a scratch buffer (pre-allocated, zero new allocations). Between recomputes, use the cached threshold.

With NQ at ~500 ticks/sec during active periods, recompute fires ~2.5×/sec — negligible cost. The 2000-tick window covers ~4 seconds of active tape, long enough for a stable distribution but short enough to adapt to regime changes (e.g., pre-market → cash-open).

### Detection

On each tick from TapeRecorder, if `tick.Volume >= _p95Threshold` and `_p95Threshold > 0` (warmup complete):
- Flag as BigPrint
- Record side (Buy/Sell/Unknown from tick's Lee-Ready classification)
- Record price and volume

### Accumulated state (for Phase 3.7 consumers)

Track a rolling window of recent BigPrints (last 30 seconds, matching TapeRecorder window):

```
BigPrintBuyVolume   — sum of Buy-side BigPrint volumes in window
BigPrintSellVolume  — sum of Sell-side BigPrint volumes in window  
BigPrintDelta       — BuyVol - SellVol (net directional pressure)
BigPrintCount       — number of BigPrints in window
LastBigPrintSide    — side of most recent BigPrint
LastBigPrintPrice   — price of most recent BigPrint
LastBigPrintAgeMs   — ms since most recent BigPrint (relative to last tick time)
```

These are what Phase 3.7 will publish to SnapKeys for ConfluenceEngine scoring.

## New Type

```csharp
public sealed class BigPrintDetector
{
    private const int    VOL_RING_SIZE       = 2000;  // ticks for p95 calculation
    private const int    RECOMPUTE_INTERVAL  = 200;   // ticks between p95 recalc
    private const double PERCENTILE          = 0.95;
    private const int    BP_RING_SIZE        = 500;   // max BigPrints in rolling window

    // Volume distribution tracking
    private readonly long[] _volRing;       // ring of recent trade volumes
    private readonly long[] _sortBuf;       // scratch buffer for partial sort
    private int  _volHead;
    private int  _volCount;
    private int  _ticksSinceRecompute;
    private long _p95Threshold;

    // BigPrint ring (for rolling aggregation)
    private readonly BPEntry[] _bpRing;
    private int _bpHead;
    private int _bpCount;

    // Accumulated state
    public long      BigPrintBuyVolume  { get; private set; }
    public long      BigPrintSellVolume { get; private set; }
    public long      BigPrintDelta      { get; private set; }  // Buy - Sell
    public int       BigPrintCount      { get; private set; }
    public Aggressor LastBigPrintSide   { get; private set; }
    public double    LastBigPrintPrice  { get; private set; }
    public long      LastBigPrintAgeMs  { get; private set; }

    public long      P95Threshold       { get; private set; }  // expose for debug
    public bool      IsWarmedUp         { get; private set; }  // true after VOL_RING_SIZE ticks

    public BigPrintDetector();

    /// Called per tick by TapeRecorder. Updates volume ring, detects BigPrints,
    /// maintains rolling aggregation. Zero allocation.
    public void OnTick(in Tick tick);

    /// Reset on session boundaries.
    public void OnSessionOpen();
}

internal struct BPEntry
{
    public long      TimeMs;
    public long      Volume;
    public Aggressor Side;
}
```

### p95 computation

On recompute (every 200 ticks):
1. Copy `_volRing[0.._volCount]` into `_sortBuf`.
2. `Array.Sort(_sortBuf, 0, _volCount)`.
3. `_p95Threshold = _sortBuf[(int)(_volCount * PERCENTILE)]`.

`Array.Sort` on 2000 longs is ~50μs — fine at 2.5×/sec. No allocation because `_sortBuf` is pre-allocated.

### Rolling window eviction

The BigPrint ring tracks entries with `TimeMs`. On each `OnTick`, evict entries where `tick.TimeMs - entry.TimeMs > 30_000` (matching TapeRecorder's 30s window). Recompute `BigPrintBuyVolume`, `BigPrintSellVolume`, etc. after eviction.

Optimization: don't recompute from scratch every tick. Track running sums; on eviction, subtract the evicted entry's volume from the appropriate sum. On new BigPrint, add to the appropriate sum.

## TapeRecorder Change

Add a callback hook so BigPrintDetector receives every tick without polling:

```csharp
// In TapeRecorder:
private BigPrintDetector _bigPrintDetector;

public void SetBigPrintDetector(BigPrintDetector detector)
{
    _bigPrintDetector = detector;
}

// In OnTick, after writing to ring and eviction:
_bigPrintDetector?.OnTick(in tick);  // where tick is the Tick just written
```

This avoids a separate scan loop. The detector piggybacks on TapeRecorder's OnTick hot path.

## Host Wiring

In `HostStrategy.cs`:

### Field
```csharp
private BigPrintDetector _bigPrint;
```

### `State.DataLoaded` init (after `_tape = new TapeRecorder()`)
```csharp
_bigPrint = new BigPrintDetector();
_tape.SetBigPrintDetector(_bigPrint);
```

### Session-open (after `_tape?.OnSessionOpen(Time[0])`)
```csharp
_bigPrint?.OnSessionOpen();
```

## Invariants
- Zero allocation in `OnTick`. `_sortBuf` and all rings are pre-allocated.
- `_p95Threshold` is 0 until `_volCount >= VOL_RING_SIZE` (warmup). During warmup, no BigPrints are detected — `IsWarmedUp` stays false.
- BigPrint rolling sums use incremental add/subtract, not full recompute per tick.
- `BPEntry` is a struct, not a class. `_bpRing` is a flat array of structs.
- `LastBigPrintAgeMs` is recomputed on each `OnTick` call: `tick.TimeMs - _bpRing[lastEntry].TimeMs`. If no BigPrints in window, returns `long.MaxValue`.
- The 500-slot BP ring handles worst case: if p95 is very low (quiet tape transitioning to burst), up to 5% of 2000 ticks could qualify = 100 per recompute window. 500 gives 5× headroom.

## Validation
- [ ] Project builds, no warnings.
- [ ] Backtest results identical to Phase 3.2 baseline ($23,570) — detector is data-only, no scoring.
- [ ] Debug print on bar close: `Print($"bp: p95={_bigPrint.P95Threshold} count={_bigPrint.BigPrintCount} delta={_bigPrint.BigPrintDelta}");`
  - Expected on active NQ: p95 threshold in 5-50 range (contracts), BigPrintCount > 0 during active session.
- [ ] During cash-open (08:30 CT), BigPrintCount should spike. If zero, detector warmup or threshold is miscalibrated.

## Do Not
- Add scoring or SnapKeys in this phase. Phase 3.7 wires all tape detectors into ConfluenceEngine.
- Use `List<T>` or any growable collection.
- Sort the full volume ring on every tick. Use the cached threshold with periodic recompute.
- Filter BigPrints by side during detection. Accumulate both sides; directional logic belongs in scoring (Phase 3.7).

## Rationale
BigPrints are the simplest institutional signal on the tape: a trade 20× the median size is not retail. The p95 threshold adapts automatically to the instrument and session — no hardcoded volume thresholds needed. NQ during Asian session (thin tape, 1-2 lots) gets a low threshold; cash-open (thick tape, 10-50 lots) gets a high one. The rolling 30-second window matches TapeRecorder for consistent time semantics. The delta (buy vol - sell vol of BigPrints) is the key scoring input for Phase 3.7: institutional net positioning within the current microstructure window.
