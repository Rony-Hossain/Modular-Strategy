# Phase 3.5 — Sweep Detector

Detect a "liquidity sweep": same-side aggressor ticks traversing **≥3 distinct price levels within ≤200ms**. This is the tape signature of a large order eating through book liquidity — a stop run, a desperate exit, or aggressive directional entry. Data-only; scoring is Phase 3.7.

## Files
- **New**: `ModularStrategy/SweepDetector.cs`
- **Modified**: `ModularStrategy/TapeRecorder.cs` (add detector hook, parallel to BigPrint/Velocity)
- **Modified**: `ModularStrategy/HostStrategy.cs` (instantiate, wire to tape, pass tickSize)

**Do not touch**: any Phase 2 file, ConfluenceEngine, CommonTypes.

## Design

### Why 200ms / 3 levels

- **200ms**: shorter than Phase 3.4's 1s velocity window by design. A sweep is a micro-event within a velocity burst. If it takes longer than 200ms, book liquidity was refreshing between trades — not a true sweep.
- **3 levels**: 2 levels could be a large print filling 2 queues; 3+ means the aggressor walked the book. Matches the conventional tape-reading threshold.
- **Same-side only**: a sweep is directional. Buy aggressors walking the ask = BuySweep. Sell aggressors walking the bid = SellSweep. Mixed-side ticks in the window don't combine.

### Level computation

```
priceRange = maxPrice - minPrice
levelsCrossed = (int)Math.Round(priceRange / tickSize) + 1
```

Round to nearest tick to avoid floating-point slop when prices are e.g., 17234.75 and 17235.00 (diff = 0.25, tickSize = 0.25 → 1 level delta → 2 levels crossed).

### Ring buffers (per side)

Two separate rings: `_buyRing` and `_sellRing`. Each stores `(timeMs, price)`. Capacity 512 each — at max tick rate (1000/s) and 200ms window, upper bound is 200 ticks per side.

On each `OnTick`:
1. Route by `tick.Side`. If `Unknown`, do nothing (no book direction to attribute).
2. Append to appropriate ring.
3. Evict entries older than `tick.TimeMs - WINDOW_MS` from that ring.
4. Scan the ring for min/max price (linear, n ≤ 200 typical).
5. If `levelsCrossed >= SWEEP_LEVELS`, flag the corresponding sweep.

Linear min/max scan per tick is acceptable: 200 double comparisons ≈ microseconds.

### Sweep event state

When a sweep fires:
- Record `LastSweepTimeMs`, `LastSweepSide`, `LastSweepLevels`, `LastSweepMinPrice`, `LastSweepMaxPrice`
- Compute `LastSweepAgeMs = tick.TimeMs - LastSweepTimeMs` on every tick (so consumers see freshness)

Flags `BuySweepActive` / `SellSweepActive` are level-gated booleans that stay true only while the window still contains the sweep (they self-clear when the triggering ticks age out). Phase 3.7 can treat "active" as "sweep happened in the last 200ms".

## New Type

```csharp
public sealed class SweepDetector
{
    private const long WINDOW_MS    = 200;
    private const int  SWEEP_LEVELS = 3;
    private const int  RING_CAPACITY = 512;

    private readonly double _tickSize;

    private readonly long[]   _buyTime;
    private readonly double[] _buyPrice;
    private int _buyHead, _buyOldest, _buyCount;

    private readonly long[]   _sellTime;
    private readonly double[] _sellPrice;
    private int _sellHead, _sellOldest, _sellCount;

    private bool _armed;

    public bool      BuySweepActive    { get; private set; }
    public bool      SellSweepActive   { get; private set; }
    public int       LastSweepLevels   { get; private set; }
    public Aggressor LastSweepSide     { get; private set; }
    public double    LastSweepMinPrice { get; private set; }
    public double    LastSweepMaxPrice { get; private set; }
    public long      LastSweepTimeMs   { get; private set; }
    public long      LastSweepAgeMs    { get; private set; }

    public SweepDetector(double tickSize);
    public void OnTick(in Tick tick);
    public void OnSessionOpen();
}
```

## Algorithm — OnTick(in Tick tick)

```
1. If !_armed, return.
2. If tick.Side is Buy:
    append (tick.TimeMs, tick.Price) to _buy ring
    evict entries where timeMs < tick.TimeMs - WINDOW_MS from _buy ring
    scan _buy ring for min/max price (skip if _buyCount < 2)
    levels = (int)Math.Round((max - min) / _tickSize) + 1
    if levels >= SWEEP_LEVELS:
        BuySweepActive = true
        update LastSweep* fields with Buy side
    else:
        BuySweepActive = false
3. Else if tick.Side is Sell: same logic with _sell ring and SellSweepActive
4. Else (Unknown): do nothing — don't even touch rings
5. Update LastSweepAgeMs = (LastSweepTimeMs > 0) ? tick.TimeMs - LastSweepTimeMs : long.MaxValue
```

Note: step 2/3 scan only the SAME-side ring. Opposite-side ring is unaffected on this tick.

### Eviction

```
cutoff = tick.TimeMs - WINDOW_MS
while _count > 0 && timeRing[_oldest] < cutoff:
    _oldest = (_oldest + 1) % RING_CAPACITY
    _count--
```

### Min/max scan (per side)

```
min = double.MaxValue; max = double.MinValue
idx = _oldest
for k in 0.._count:
    p = priceRing[(_oldest + k) % RING_CAPACITY]
    if p < min: min = p
    if p > max: max = p
```

## TapeRecorder Change

Add third detector hook (same pattern as BigPrint and Velocity):

```csharp
private SweepDetector _sweepDetector;

public void SetSweepDetector(SweepDetector detector)
{
    _sweepDetector = detector;
}

// In OnTick, after _velocityDetector?.OnTick(in lastTick):
_sweepDetector?.OnTick(in lastTick);
```

## Host Wiring

```csharp
private SweepDetector _sweep;

// In State.DataLoaded, after velocity wiring:
_sweep = new SweepDetector(Instrument.MasterInstrument.TickSize);
_tape.SetSweepDetector(_sweep);

// In session-open block, after _velocity?.OnSessionOpen():
_sweep?.OnSessionOpen();
```

## Invariants
- Zero allocation in OnTick. All rings pre-allocated.
- Ring capacity 512 handles 200ms at 2560 ticks/s peak — well above any realistic instrument. Overflow advances `_oldest` silently (old tick drops).
- `_tickSize` is captured at construction, never changes. Passed from NT8 `Instrument.MasterInstrument.TickSize` in HostStrategy.
- Unknown-side ticks are completely ignored: they can't attribute a sweep direction. Do not append them to either ring.
- Sweeps fire **every tick** while the window still contains enough same-side prints. `BuySweepActive=true` on tick N, then stays true on ticks N+1..N+M until the triggering ticks age out of the 200ms window. This is intentional — it lets consumers sample the flag without timing sensitivity.
- `LastSweepTimeMs` only advances on transitions from inactive→active or when a larger-range sweep re-fires; otherwise it holds the original sweep time.

## Validation
- [ ] Project builds, no warnings.
- [ ] Backtest identical to Phase 3.4 baseline — no scoring.
- [ ] Debug print on bar close: `Print($"sweep: buy={_sweep.BuySweepActive} sell={_sweep.SellSweepActive} lastLvls={_sweep.LastSweepLevels} age={_sweep.LastSweepAgeMs}ms");`
- [ ] During NQ 08:30 cash-open bar, expect at least one sweep per side per day. Zero sweeps across a full session means threshold is miscalibrated.

## Do Not
- Combine buy and sell ticks in a single ring. Sweeps are directional.
- Count Unknown-side ticks toward the level total.
- Use price diffs without rounding — floating-point NQ prices (17234.75 etc.) will occasionally miscount levels.
- Fire a "third detector hook" before the TapeRecorder's write+evict is complete. Order matters: `_bigPrintDetector?.OnTick → _velocityDetector?.OnTick → _sweepDetector?.OnTick`.

## Rationale
Sweeps are the third axis of microstructure (after BigPrint's size axis and Velocity's rate axis). Each detector is cheap, small, and independent. Phase 3.7 will combine them: a BuySweep + BuyVelocity spike + institutional BigPrint delta > 0 is a high-conviction long-side confirmation. Conversely, sweep AGAINST the signal direction is a veto candidate — but that's for 3.7 to decide.
