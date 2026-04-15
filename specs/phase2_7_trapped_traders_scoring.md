# Phase 2.7 — Trapped Traders Scoring & Veto

Wire the Phase 2.6 `TrappedLongs` / `TrappedShorts` / `TrapLevel` data into `MarketSnapshot`, score Layer C on agreement, and veto on direct opposition. Scope is **Trapped Traders only** — `Iceberg` (2.5), `Exhaustion` (2.3), and `UnfinishedAuction` (2.2) remain unwired and get their own follow-up phases.

## Files
- **Modified**: `ModularStrategy/CommonTypes.cs` (add 3 `SnapKeys` constants)
- **Modified**: `ModularStrategy/HostStrategy.cs` (publish from `_lastFpResult` in `OnPopulateIndicatorBag`)
- **Modified**: `ModularStrategy/ConfluenceEngine.cs` (Layer C bonus + veto + reason strings)

**Do not touch**: `FootprintCore.cs`, `TrappedTraderDetector.cs`, `FootprintTradeAdvisor.cs`, `FootprintEntryAdvisor.cs`, or any detector file. The data source is frozen after 2.6.

## New SnapKeys

In `CommonTypes.cs`, add immediately after `AbsorptionScore` (line ~511):

```csharp
// ── Trapped Traders (Phase 2.6 detector, Phase 2.7 wiring) ───────
// Published by HostStrategy.OnPopulateIndicatorBag() each bar.
// Requires Volumetric. Values are stable across the 2-bar detector
// delay — emit fires exactly on bar B+2.

/// <summary>
/// 1.0 when longs were trapped at a prior-bar high (bull-trap pattern):
/// volume cluster at the high, rejection close, no follow-through on the
/// next bar. Confirms SHORT signals — trapped-long exit flow extends the
/// reversal. Vetoes LONG signals near TrapLevel.
/// Zero without Volumetric or when detector is warming up (&lt;2 bars).
/// </summary>
public const string TrappedLongs = "TrappedLongs";

/// <summary>
/// 1.0 when shorts were trapped at a prior-bar low (bear-trap pattern):
/// volume cluster at the low, rejection close, no follow-through on the
/// next bar. Confirms LONG signals. Vetoes SHORT signals near TrapLevel.
/// Zero without Volumetric or during warm-up.
/// </summary>
public const string TrappedShorts = "TrappedShorts";

/// <summary>
/// Absolute price of the trap origin — equals the prior-bar high (bull trap)
/// or low (bear trap). Zero when neither flag fires.
/// Consumers may gate scoring/veto by proximity of current close to this level.
/// </summary>
public const string TrapLevel = "TrapLevel";
```

## Snapshot Publish

In `HostStrategy.cs`, `OnPopulateIndicatorBag`, immediately after the Phase 2.1 location-aware block (after the `if (_lastFpResult.IsValid) { ... } else { ... }` that handles `MaxBidVolPrice` etc., around line 574):

```csharp
// Phase 2.7 — Trapped Traders
if (_lastFpResult.IsValid)
{
    snapshot.Set(SnapKeys.TrappedLongs,  _lastFpResult.TrappedLongs  ? 1.0 : 0.0);
    snapshot.Set(SnapKeys.TrappedShorts, _lastFpResult.TrappedShorts ? 1.0 : 0.0);
    snapshot.Set(SnapKeys.TrapLevel,     _lastFpResult.TrapLevel);
}
else
{
    snapshot.Set(SnapKeys.TrappedLongs,  0.0);
    snapshot.Set(SnapKeys.TrappedShorts, 0.0);
    snapshot.Set(SnapKeys.TrapLevel,     0.0);
}
```

Follow the exact pattern of the existing `MaxBidVolPrice` block: valid-path reads `_lastFpResult`, invalid-path zeroes all three. No gating on `HasVolumetric` — the detector already self-warms and emits 0/0/0.0 without volumetric input.

## Layer C Scoring

In `ConfluenceEngine.cs`, `Evaluate`, immediately after the existing Higher1 direction block (around line 168, before the `// SMF NonConfirmation veto` comment). Add a new weight constant at the top of the class (after `LAYER_C_IMBAL_ZONE`, line ~51):

```csharp
private const int LAYER_C_TRAPPED_AGREE = 8;   // trapped flag on opposite side confirms direction
```

Then in `Evaluate`:

```csharp
// ── Trapped Traders agreement (Phase 2.7) ───────────────────────
// Long is confirmed when SHORTS were trapped at a low (their exit flow
// extends upward). Short is confirmed when LONGS were trapped at a high.
// The flag is already 2-bar-deferred by the detector, so no re-timing here.
bool trapLongsFlag  = snap.GetFlag(SnapKeys.TrappedLongs);
bool trapShortsFlag = snap.GetFlag(SnapKeys.TrappedShorts);

if ( isLong && trapShortsFlag) layerC += LAYER_C_TRAPPED_AGREE;
if (!isLong && trapLongsFlag ) layerC += LAYER_C_TRAPPED_AGREE;
```

Place **before** the Layer C cap logic — if Layer C already caps at 22 elsewhere, this bonus is still subject to it. Do not raise the cap. If the cap is implicit via the individual-weight budget (original pattern), the new 8 slots in alongside the other proxy weights.

## Veto Rule

In the same file, in the **veto block** (around line 193, where the Phase 1.1 veto rules live — after the `NonConfirmation veto` block). Add:

```csharp
// Phase 2.7 — Trapped Traders opposition veto
// Long signal when LONGS are trapped at a high → trapped-long exit flow
// will push price DOWN. The signal is trading into forced-seller flow.
// Symmetric for shorts.
if (isLong  && trapLongsFlag ) { isVetoed = true; }
if (!isLong && trapShortsFlag) { isVetoed = true; }
```

`trapLongsFlag` / `trapShortsFlag` are already fetched in the scoring block above — reuse, do not re-read.

**Do not gate by distance to `TrapLevel`.** The detector's cluster+rejection+no-FT rules are already restrictive (~0.5–3% of bars). Adding a proximity filter here would further dilute and we don't yet have forward-return data to set the threshold. Phase 2.8 may add distance-gating if autopsy shows it helps.

## Reason Strings

In `ConfluenceEngine.cs` reason-builder block (around line 320+):

1. **LayerC reasons** — in the section that appends `ncVETO` / `bd` / etc. (around line 327–336), add at the end:
   ```csharp
   if ((isLong && trapShortsFlag) || (!isLong && trapLongsFlag)) sb.Append("trap+");
   ```

2. **Phase 1.1/2.7 veto reasons** — in the isLong-branch block (around line 341–345), add:
   ```csharp
   if (trapLongsFlag) sb.Append("vTRAP");
   ```
   In the short-branch block (around line 347–351), add:
   ```csharp
   if (trapShortsFlag) sb.Append("vTRAP");
   ```

Keep tags short (`trap+`, `vTRAP`) to match the existing 2–5 char convention. The `+` suffix distinguishes scoring from veto.

## Invariants
- Zero allocation in the hot path. `GetFlag` returns `bool` from the existing snapshot dictionary — same pattern as every other snap flag.
- Veto is binary and stacks with existing vetoes — `isVetoed = true` does not early-return. All layers still compute (for logging).
- `trapShortsFlag` and `trapLongsFlag` are mutually exclusive in the common case but may both fire on doji-with-dual-clusters (see 2.6 "Both Fire" note). If both fire: scoring bonus applies based on `isLong`; the veto also applies based on `isLong`. Result: the signal that agrees with one trap side gets both bonus AND veto from the other — net = VETOED. This is the right outcome for ambiguous doji setups.
- `LAYER_C_TRAPPED_AGREE = 8` matches the scale of `LAYER_C_DELTA_SL = 8` and `LAYER_C_DELTA_EXHST = 8` — equivalent-weight orderflow confirmations.

## Validation

- [ ] Project builds, no warnings.
- [ ] Unit sanity: on a bar where `TrappedLongs == 1.0`:
  - Short candidate: `LayerC` +8 vs prior baseline, no veto.
  - Long candidate: `IsVetoed == true`, reason string contains `vTRAP`.
- [ ] 6-week backtest. `Summary.csv` will **differ** from `baselines/phase2_6_*` — this is the first scoring phase, so expect changes:
  - Short win-rate should improve vs phase 2.6 baseline.
  - Long win-rate should improve (fewer bad longs via veto).
  - Total trade count should drop 1–4% (veto removes some longs/shorts).
  - Net expectancy should be non-decreasing. If it drops, revert and file the regression.
- [ ] Spot-check 10 VETOED bars in `filter_autopsy.csv`: confirm reason string contains `vTRAP` and the 2.6 detector did fire on that bar.
- [ ] Snapshot of new baseline → `baselines/phase2_7_*`.

## Do Not
- Wire Iceberg / Exhaustion / Unfinished Auction in this phase — separate specs coming.
- Add `TrapLevel` to `LevelRegistry` or Layer B. This is a Layer C orderflow signal, not a structural level.
- Tune `LAYER_C_TRAPPED_AGREE` or `_clusterRatio` / `_rejectionFrac` in the detector. Tuning waits on `filter_autopsy.csv` from this phase's backtest.
- Expose `TrapLevel` to the UI renderer. Data only for now.
- Change the 2-bar detector delay. That's a 2.6 invariant.

## Rationale
Trapped Traders is the first non-divergence orderflow confirmation we've wired. The agreement case (+8 on opposite-side trap) is conservative — it's half the weight of `LAYER_C_DIVERGENCE=15`, reflecting that the detector is new and forward returns are unvalidated. The veto case is binary and symmetric: trading *into* trapped-trader exit flow is trading into known adverse liquidity, which is the same category of rule as `BearDivergence` vetoing longs (Phase 1.1). Keeping scoring and veto on the same signal lets the autopsy cleanly attribute each trade to either "confirmed by trap" or "avoided a trap" — two validation paths for one detector.

The "trap confirms opposite direction" mapping is the crux: a bull trap (`TrappedLongs`) is bearish context, because the trapped longs must sell to exit. New readers often expect `TrappedLongs` to confirm longs (it doesn't) — the SnapKey doc comments call this out explicitly to prevent future regression.
