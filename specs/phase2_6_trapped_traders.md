# Phase 2.6 — Trapped Traders Detector

Volume cluster at an extreme + rejection on bar B + no follow-through on bar B+1 → emit on bar B+2. A classic "trapped-aggressor" reversal pattern. Requires 2-bar deferred emit, so the detector carries its own small state. This spec lands the data only — scoring/wiring is Phase 2.7.

## Files
- **New**: `ModularStrategy/TrappedTraderDetector.cs`
- **Modified**: `ModularStrategy/FootprintCore.cs` (owns one detector instance; feeds it each bar)

## New Type

```csharp
public sealed class TrappedTraderDetector
{
    // Thresholds (constructor-injected for future Phase 2.7 tuning)
    private readonly double _clusterRatio;     // top/bottom level vol vs avg-level vol
    private readonly double _rejectionFrac;    // close position within bar range
    // "RejectionFrac": bull trap needs Close < rejectionFrac * range from High;
    //                  bear trap needs Close > (1 - rejectionFrac) * range from Low.

    public TrappedTraderDetector(double clusterRatio = 2.0, double rejectionFrac = 0.5);

    /// Single call per bar: emits flags based on prior 2-bar state, then advances
    /// the internal ring (snapshot of current bar) for the next call.
    /// Invalid current bars (`!current.IsValid`) do NOT advance state and do NOT emit.
    public void Evaluate(
        in FootprintResult current,
        out bool   trappedLongs,
        out bool   trappedShorts,
        out double trapLevel);

    public int BarsSeen { get; }  // warmup counter; emits only when >= 2.
}
```

All state lives in two `BarSnap` fields (`_barMinus1`, `_barMinus2`). Zero allocation in the hot path.

### Per-bar snapshot fields retained

Only what the detector needs — not the whole `FootprintResult`:

```
High, Low, Open, Close,
TopLevelTotalVol, BottomLevelTotalVol,
LevelCount, TotalBuyVol, TotalSellVol,
TickSize
```

`avgLevelVol = (TotalBuyVol + TotalSellVol) / LevelCount` is computed on demand inside `Evaluate`.

## New Fields on `FootprintResult`

Add immediately after `IcebergPrice` (the last Phase 2.5 field):

```csharp
// Phase 2.6 — Trapped Traders (2-bar deferred emit)
public bool   TrappedLongs  { get; }   // bull-trap at prior high
public bool   TrappedShorts { get; }   // bear-trap at prior low
public double TrapLevel     { get; }   // absolute price of the trap origin, else 0.0
```

Add 3 ctor params (after `icebergPrice`). Assign in ctor body. Mirror `false, false, 0.0` into `Zero`, `BuildZeroResult`, and every other invalid-path `new FootprintResult(...)` site.

## Evaluation Rules

Given `barB = _barMinus2`, `barB1 = _barMinus1`, current bar `C`:

### Bull Trap (longs trapped at a high — emits `TrappedLongs`)
```
avgB        = (barB.TotalBuyVol + barB.TotalSellVol) / barB.LevelCount
clusterHi   = barB.TopLevelTotalVol >= _clusterRatio * avgB
rangeB      = barB.High - barB.Low   // require > 0
rejectHi    = (barB.High - barB.Close) >= _rejectionFrac * rangeB
noFT_Hi     = barB1.High <= barB.High + 0.5 * barB.TickSize

trappedLongs = clusterHi && rejectHi && noFT_Hi
trapLevel    = barB.High   // if trappedLongs
```

### Bear Trap (shorts trapped at a low — emits `TrappedShorts`)
```
clusterLo   = barB.BottomLevelTotalVol >= _clusterRatio * avgB
rejectLo    = (barB.Close - barB.Low) >= _rejectionFrac * rangeB
noFT_Lo     = barB1.Low  >= barB.Low  - 0.5 * barB.TickSize

trappedShorts = clusterLo && rejectLo && noFT_Lo
trapLevel     = barB.Low   // if trappedShorts
```

### Both Fire
Extremely rare (bar B had clusters at both extremes AND both rejection halves met — only possible on doji-with-dual-clusters). Resolution: prefer `trappedLongs`'s `trapLevel = barB.High`. Document; do not add tiebreaker logic.

### Invalid Inputs
- `BarsSeen < 2` → emit `false, false, 0.0` and still advance.
- `rangeB <= 0` → no emit (skip rejection checks).
- `barB.LevelCount <= 0` → no emit.
- `current.IsValid == false` → do **not** advance state; skip `Evaluate` call entirely at the call site (see wiring below).

## Wiring in `FootprintCore`

1. Add private field:
   ```csharp
   private readonly TrappedTraderDetector _trappedTrader = new TrappedTraderDetector();
   ```

2. In `FinalizeResult` (already instance after Phase 2.5), immediately after the Phase 2.5 Iceberg block and **before** the `new FootprintResult(...)` ctor:

   ```csharp
   bool   trappedLongs  = false;
   bool   trappedShorts = false;
   double trapLevel     = 0.0;
   _trappedTrader.Evaluate(in baseResult, out trappedLongs, out trappedShorts, out trapLevel);
   ```

   Pass `trappedLongs, trappedShorts, trapLevel` into the ctor at the corresponding new positions.

3. `baseResult` at this point carries `IsValid = false` (it's the pre-finalize shape). `Evaluate` must branch on the raw fields it reads, not on `IsValid`. Since `FinalizeResult` is only entered on the valid path, this is fine — but the detector **must not** call itself on invalid-path `BuildZeroResult` sites (those already pass `false, false, 0.0` into the ctor and do not touch the detector).

4. No call from `TryComputeCurrentBar` — `Evaluate` self-advances inside `FinalizeResult`. State advance happens exactly once per valid bar.

## Invariants
- Zero allocation in steady state. Ring is two value-type fields, not arrays.
- Detector state is a private field of `FootprintCore`. Not exposed.
- `Evaluate` is the only entry point. No separate `Ingest`/`Advance` methods.
- Thresholds (`clusterRatio=2.0`, `rejectionFrac=0.5`) are ctor-injected defaults. Phase 2.7 may override.

## Do Not
- Touch `FootprintAssembler`, `LevelHistoryTracker`, `ConfluenceEngine`, `FootprintTradeAdvisor`, `HostStrategy`, or any other file.
- Query `LevelHistoryTracker` from `TrappedTraderDetector` — use the bar-level snapshot only. The tracker is for per-price detectors (Iceberg); this one is bar-geometry.
- Add `SnapKeys`, Layer C scoring, or veto rules — that's Phase 2.7.
- Look further back than 2 bars. The "deferred emit" is strictly 2-bar.

## Validation
- [ ] Project builds, no warnings.
- [ ] 6-week backtest. `Summary.csv` must match the `baselines/phase2_5_*` snapshot **exactly** — data-only phase.
- [ ] Temporary debug print on 20 bars where `TrappedLongs || TrappedShorts` fires, confirming:
  - `TrapLevel` equals `High` (bull trap) or `Low` (bear trap) of the bar two primary bars earlier.
  - The emit bar is always exactly 2 primary bars after the trap setup (no 1-bar or 3-bar drift).
  - Frequency: ~0.5–3% of bars on NQ 5-min (sparse by design).
  - Remove prints before committing.

## Rationale
Trapped Traders is a reversal-continuation pattern: a volume cluster shows aggressors hit an extreme hard, but the bar rejected (didn't close at the extreme) and the next bar failed to extend (confirming the aggressor hand was weak or absorbed). By bar B+2, those aggressors are underwater and their forced-exit flow extends the reversal. The 2-bar delay is load-bearing — emitting on B+1 conflates with simple rejection; emitting on B+3 or later loses the exit-flow edge.

Thresholds are conservative defaults:
- `clusterRatio=2.0` matches the Iceberg current-bar absorption gate for consistency.
- `rejectionFrac=0.5` treats the bar's midpoint as the rejection boundary (simple, symmetrical). Tune in Phase 2.7.
