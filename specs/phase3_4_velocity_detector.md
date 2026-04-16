# Phase 3.4 — Velocity Detector (EMA + Spike)

Detect sudden bursts of tape activity — "velocity spikes" where 1-second buy or sell volume exceeds 3× its rolling EMA baseline. A velocity spike signals aggressive one-sided participation compressed into a short window, often preceding a move. Data-only; scoring is Phase 3.7.

## Files
- **New**: `ModularStrategy/VelocityDetector.cs`
- **Modified**: `ModularStrategy/TapeRecorder.cs` (add detector hook, same pattern as BigPrintDetector)
- **Modified**: `ModularStrategy/HostStrategy.cs` (instantiate, wire to tape)

**Do not touch**: any Phase 2 file, ConfluenceEngine, CommonTypes.

## Design

### Velocity metric

**Velocity = volume in the trailing 1-second window**, tracked separately for Buy and Sell sides. Side comes from `tick.Side` (Lee-Ready, Phase 3.2).

Why volume, not tick count: a 50-lot print counts the same as fifty 1-lot prints in tick-count terms, but a single large print is more informative. Volume captures "how much got done" directly.

Why 1-second: NQ cash-open bursts last 200-800ms; 1s covers a full burst without diluting with surrounding quiet. Shorter windows are noisy, longer windows smear the spike across normal tape.

### EMA baseline

Maintain `_emaBuyVel` and `_emaSellVel`, exponentially weighted averages of the 1-second rolling volume, sampled every 100ms. EMA alpha = 0.05 (~20-sample effective window ≈ 2s of velocity history).

Sampling every 100ms (not per tick) keeps the EMA stable and avoids over-weighting burst ticks. On each `OnTick`, check if `tick.TimeMs - _lastSampleTimeMs >= 100`; if so, sample current velocity into EMA and advance `_lastSampleTimeMs`.

### Spike detection

```
BuySpike  = (CurrentBuyVel  > SPIKE_MULTIPLIER * EmaBuyVel)  && (EmaBuyVel  > EMA_MIN)
SellSpike = (CurrentSellVel > SPIKE_MULTIPLIER * EmaSellVel) && (EmaSellVel > EMA_MIN)
```

`EMA_MIN = 1.0` guards against spike firing during warmup or idle tape where EMA is near zero. Without this, any tick during a dead period would trigger a spike.

`SPIKE_MULTIPLIER = 3.0` — 3× baseline is the empirical threshold where microstructure practitioners call a volume event "aggressive". Configurable via constructor for Phase 3.7 tuning.

## New Type

```csharp
public sealed class VelocityDetector
{
    private const long   WINDOW_MS         = 1000;   // rolling velocity window
    private const long   SAMPLE_INTERVAL_MS = 100;   // EMA sample rate
    private const double EMA_ALPHA         = 0.05;   // smoothing
    private const double SPIKE_MULTIPLIER  = 3.0;
    private const double EMA_MIN           = 1.0;
    private const int    RING_CAPACITY     = 2000;   // max ticks in 1s window (headroom)

    // Rolling window of (timeMs, volume, side) for the last WINDOW_MS
    private readonly long[]      _timeRing;
    private readonly long[]      _volRing;
    private readonly Aggressor[] _sideRing;
    private int  _head;
    private int  _oldest;
    private int  _count;

    // Running sums (incremental maintenance, no rescan)
    private long _currentBuyVol;
    private long _currentSellVol;

    // EMA state
    private double _emaBuyVel;
    private double _emaSellVel;
    private long   _lastSampleTimeMs;
    private bool   _armed;

    // Outputs (read by Phase 3.7 scoring)
    public long   CurrentBuyVel   { get { return _currentBuyVol;  } }
    public long   CurrentSellVel  { get { return _currentSellVol; } }
    public double EmaBuyVel       { get { return _emaBuyVel;      } }
    public double EmaSellVel      { get { return _emaSellVel;     } }
    public bool   BuySpike        { get; private set; }
    public bool   SellSpike       { get; private set; }
    public long   LastSpikeTimeMs { get; private set; }    // most recent of either side
    public long   LastSpikeAgeMs  { get; private set; }    // relative to last OnTick

    public VelocityDetector();
    public void OnTick(in Tick tick);
    public void OnSessionOpen();
}
```

## TapeRecorder Change

Same pattern as Phase 3.3 BigPrintDetector hook. Add a second field + setter + call site:

```csharp
private VelocityDetector _velocityDetector;

public void SetVelocityDetector(VelocityDetector detector)
{
    _velocityDetector = detector;
}

// In OnTick, after _bigPrintDetector?.OnTick(in lastTick):
_velocityDetector?.OnTick(in lastTick);
```

## Host Wiring

In `HostStrategy.cs`:

### Field
```csharp
private VelocityDetector _velocity;
```

### `State.DataLoaded` init (after BigPrint wiring)
```csharp
_velocity = new VelocityDetector();
_tape.SetVelocityDetector(_velocity);
```

### Session-open (after `_bigPrint?.OnSessionOpen()`)
```csharp
_velocity?.OnSessionOpen();
```

## Algorithm Detail — OnTick(in Tick tick)

```
1. If !_armed, return (no session anchor yet — shouldn't happen since TapeRecorder gates)
2. Append to ring:
   _timeRing[_head] = tick.TimeMs
   _volRing[_head]  = tick.Volume
   _sideRing[_head] = tick.Side
   if tick.Side == Buy  → _currentBuyVol  += tick.Volume
   if tick.Side == Sell → _currentSellVol += tick.Volume
   advance _head = (_head + 1) % RING_CAPACITY
   if _count < RING_CAPACITY → _count++, else → _oldest advances (overflow; should not happen with 2000 slots)
3. Evict expired entries:
   cutoff = tick.TimeMs - WINDOW_MS
   while _count > 0 && _timeRing[_oldest] < cutoff:
       if _sideRing[_oldest] == Buy  → _currentBuyVol  -= _volRing[_oldest]
       if _sideRing[_oldest] == Sell → _currentSellVol -= _volRing[_oldest]
       _oldest = (_oldest + 1) % RING_CAPACITY
       _count--
4. EMA sample (if interval elapsed):
   if tick.TimeMs - _lastSampleTimeMs >= SAMPLE_INTERVAL_MS:
       _emaBuyVel  = EMA_ALPHA * _currentBuyVol  + (1 - EMA_ALPHA) * _emaBuyVel
       _emaSellVel = EMA_ALPHA * _currentSellVol + (1 - EMA_ALPHA) * _emaSellVel
       _lastSampleTimeMs = tick.TimeMs
5. Spike detection (every tick):
   BuySpike  = _currentBuyVol  > SPIKE_MULTIPLIER * _emaBuyVel  && _emaBuyVel  > EMA_MIN
   SellSpike = _currentSellVol > SPIKE_MULTIPLIER * _emaSellVel && _emaSellVel > EMA_MIN
   if BuySpike || SellSpike:
       LastSpikeTimeMs = tick.TimeMs
   LastSpikeAgeMs = (LastSpikeTimeMs > 0) ? tick.TimeMs - LastSpikeTimeMs : long.MaxValue
```

## Invariants
- Zero allocation in `OnTick`. All rings pre-allocated in constructor.
- Running sums use incremental add on entry, subtract on eviction. Never rescan the ring.
- EMA initializes to 0.0 on session open. First SAMPLE_INTERVAL_MS of session has EMA near 0, so `EMA_MIN=1.0` guard prevents false spikes.
- `_lastSampleTimeMs` resets to 0 on session open. First tick of session forces an EMA sample (`tick.TimeMs >= 0 - anyval >= 100` is true after ~100ms).
- Ring capacity 2000 handles 2000 ticks/s peak. If it ever overflows (ring full), the oldest entry is silently dropped and its sum-contribution becomes stale — this is acceptable because at such tick rates we're in burst territory anyway and EMA will lag.
- Sides other than Buy/Sell (Unknown) do not contribute to velocity. Mid-spread ticks without uptick/downtick are excluded from velocity, which is correct: Unknown means we couldn't determine aggressor direction.

## Validation
- [ ] Project builds, no warnings.
- [ ] Backtest results identical to Phase 3.3 baseline — no scoring.
- [ ] Debug print on bar close: `Print($"vel: buy={_velocity.CurrentBuyVel} sell={_velocity.CurrentSellVel} emaB={_velocity.EmaBuyVel:F1} emaS={_velocity.EmaSellVel:F1} spike={_velocity.BuySpike}/{_velocity.SellSpike}");`
- [ ] During cash-open (08:30 CT): EMA values should be 10-100× higher than pre-market; spikes should fire at the open bar.

## Do Not
- Use `DateTime.Now` or any wall-clock call. Use `tick.TimeMs` from the tape.
- Sort or scan the ring on every tick. Incremental maintenance only.
- Couple BuySpike and SellSpike — they're independent flags. A bar could have both (unusual but valid on chop).
- Use velocity in Unknown-side ticks.

## Rationale
Velocity spikes are the third leg of the Phase 3 microstructure stack (after BigPrint and the upcoming Sweep). Each detector looks at a different axis:
- **BigPrint**: single-tick size extremes
- **Velocity**: rate of participation over a short window
- **Sweep**: spatial extent (levels crossed) in a short window

A directional velocity spike (e.g., BuySpike with CurrentBuyVel >> CurrentSellVel) is a standalone signal of urgent buying pressure. Paired with an Exhaustion or Iceberg detector, it's a high-conviction reversal cue. Phase 3.7 will encode that in scoring.
