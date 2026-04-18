# Phase 3.7 — Tape-Signal Scoring (BigPrint / Velocity / Sweep / TapeIceberg)

Wire the four Phase 3 detectors (BigPrint 3.3, Velocity 3.4, Sweep 3.5, TapeIceberg 3.6) into `MarketSnapshot` and `ConfluenceEngine`. This is the final Phase 3 task — after this, tape data shapes trade signals in real time.

**Phase 2.9 lesson honored**: only ONE binary veto (Sweep opposition), chosen because sweeps are rare (≥3 levels in 200ms — a few per session at most). Velocity and tape-iceberg contribute scoring only. If autopsy shows they're reliable in a later phase, Phase 3.8+ can elevate them to vetoes.

## Files
- **Modified**: `ModularStrategy/CommonTypes.cs` — add 9 new `SnapKeys`
- **Modified**: `ModularStrategy/HostStrategy.cs` — publish detector outputs each bar
- **Modified**: `ModularStrategy/ConfluenceEngine.cs` — four scoring blocks, one veto block, reason tags

**Do not touch**: `FootprintCore.cs`, any Phase 2 detector, `BigPrintDetector.cs`, `VelocityDetector.cs`, `SweepDetector.cs`, `TapeIcebergDetector.cs`, `TapeRecorder.cs` — the detectors themselves are frozen after 3.3–3.6.

## Direction Semantics

| Signal | Confirms | Mechanism |
|--------|----------|-----------|
| `BigPrintDelta > 0` | LONG  | Institutional buyers net-aggressive in last 30s |
| `BigPrintDelta < 0` | SHORT | Institutional sellers net-aggressive |
| `VelocityBuySpike` | LONG  | 1s buy volume > 3× EMA |
| `VelocitySellSpike` | SHORT | 1s sell volume > 3× EMA |
| `BuySweep` | LONG (confirms) | Buy aggressors walked ≥3 levels up in 200ms |
| `SellSweep` | SHORT (confirms) | Sell aggressors walked ≥3 levels down |
| `BuySweep` | SHORT (vetoes) | Active buying into a short is a warning |
| `SellSweep` | LONG (vetoes) | Active selling into a long is a warning |
| `TapeBullIceberg` | LONG  | Hidden bid absorption (repeated sells at bid) |
| `TapeBearIceberg` | SHORT | Hidden ask absorption (repeated buys at ask) |

## New SnapKeys

In `CommonTypes.cs`, add immediately after the Phase 2.9 `UnfinishedBottom` block:

```csharp
// ── Phase 3.7 Tape Signals (BigPrint / Velocity / Sweep / TapeIceberg) ──

/// <summary>
/// Net institutional directional pressure in last 30s.
/// buyVol - sellVol across ticks that exceeded p95 size threshold.
/// Positive = buyers net-aggressive; negative = sellers.
/// Zero during tape warmup or quiet tape.
/// </summary>
public const string BigPrintDelta = "BigPrintDelta";

/// <summary>Aggregate Buy-side BigPrint volume in last 30s (double).</summary>
public const string BigPrintBuyVol = "BigPrintBuyVol";

/// <summary>Aggregate Sell-side BigPrint volume in last 30s (double).</summary>
public const string BigPrintSellVol = "BigPrintSellVol";

/// <summary>
/// 1.0 when last 1s buy volume > 3× its 20-sample EMA baseline.
/// Confirms LONG. Resets within milliseconds after the burst.
/// </summary>
public const string VelocityBuySpike = "VelocityBuySpike";

/// <summary>1.0 when last 1s sell volume > 3× EMA. Confirms SHORT.</summary>
public const string VelocitySellSpike = "VelocitySellSpike";

/// <summary>
/// 1.0 when buy aggressors traversed ≥3 distinct price levels in ≤200ms.
/// Confirms LONG. Vetoes SHORT (active buying into a short setup).
/// </summary>
public const string BuySweep = "BuySweep";

/// <summary>
/// 1.0 when sell aggressors traversed ≥3 levels in ≤200ms.
/// Confirms SHORT. Vetoes LONG.
/// </summary>
public const string SellSweep = "SellSweep";

/// <summary>
/// 1.0 when a tape-level bull iceberg is active: repeated SELL aggressors
/// at the same price (hidden buyer absorbing at bid). Confirms LONG.
/// Independent of bar-level BullIceberg (Phase 2.4/2.8).
/// </summary>
public const string TapeBullIceberg = "TapeBullIceberg";

/// <summary>
/// 1.0 when a tape-level bear iceberg is active: repeated BUY aggressors
/// at the same price (hidden seller absorbing at ask). Confirms SHORT.
/// </summary>
public const string TapeBearIceberg = "TapeBearIceberg";
```

## Snapshot Publish

In `HostStrategy.cs`, `OnPopulateIndicatorBag`, immediately after the Phase 2.9 block:

```csharp
// Phase 3.7 — Tape signals (BigPrint / Velocity / Sweep / TapeIceberg)
if (_bigPrint != null)
{
    snapshot.Set(SnapKeys.BigPrintDelta,   _bigPrint.BigPrintDelta);
    snapshot.Set(SnapKeys.BigPrintBuyVol,  _bigPrint.BigPrintBuyVolume);
    snapshot.Set(SnapKeys.BigPrintSellVol, _bigPrint.BigPrintSellVolume);
}
else
{
    snapshot.Set(SnapKeys.BigPrintDelta,   0.0);
    snapshot.Set(SnapKeys.BigPrintBuyVol,  0.0);
    snapshot.Set(SnapKeys.BigPrintSellVol, 0.0);
}

if (_velocity != null)
{
    snapshot.Set(SnapKeys.VelocityBuySpike,  _velocity.BuySpike  ? 1.0 : 0.0);
    snapshot.Set(SnapKeys.VelocitySellSpike, _velocity.SellSpike ? 1.0 : 0.0);
}
else
{
    snapshot.Set(SnapKeys.VelocityBuySpike,  0.0);
    snapshot.Set(SnapKeys.VelocitySellSpike, 0.0);
}

if (_sweep != null)
{
    snapshot.Set(SnapKeys.BuySweep,  _sweep.BuySweepActive  ? 1.0 : 0.0);
    snapshot.Set(SnapKeys.SellSweep, _sweep.SellSweepActive ? 1.0 : 0.0);
}
else
{
    snapshot.Set(SnapKeys.BuySweep,  0.0);
    snapshot.Set(SnapKeys.SellSweep, 0.0);
}

if (_tapeIceberg != null)
{
    snapshot.Set(SnapKeys.TapeBullIceberg, _tapeIceberg.BullIcebergActive ? 1.0 : 0.0);
    snapshot.Set(SnapKeys.TapeBearIceberg, _tapeIceberg.BearIcebergActive ? 1.0 : 0.0);
}
else
{
    snapshot.Set(SnapKeys.TapeBullIceberg, 0.0);
    snapshot.Set(SnapKeys.TapeBearIceberg, 0.0);
}
```

## Layer C Scoring

In `ConfluenceEngine.cs`, add four weight constants next to `LAYER_C_UNFINISHED_AGREE`:

```csharp
private const int LAYER_C_BIGPRINT_AGREE  = 4;  // BigPrint delta sign matches direction
private const int LAYER_C_VELOCITY_AGREE  = 4;  // 1s velocity spike on signal side
private const int LAYER_C_SWEEP_AGREE     = 6;  // same-side sweep — rare, high signal
private const int LAYER_C_TAPEICE_AGREE   = 4;  // tape-level iceberg on signal side
```

In `Evaluate`, immediately after the Phase 2.9 Unfinished block:

```csharp
// ── BigPrint directional agreement (Phase 3.7) ─────────────────
// BigPrintDelta > 0 → institutional buyers net-aggressive → confirms LONG.
// BigPrintDelta < 0 → institutional sellers net-aggressive → confirms SHORT.
double bpDelta = snap.Get(SnapKeys.BigPrintDelta);
if ( isLong && bpDelta > 0) layerC += LAYER_C_BIGPRINT_AGREE;
if (!isLong && bpDelta < 0) layerC += LAYER_C_BIGPRINT_AGREE;

// ── Velocity spike agreement (Phase 3.7) ───────────────────────
bool velBuyFlag  = snap.GetFlag(SnapKeys.VelocityBuySpike);
bool velSellFlag = snap.GetFlag(SnapKeys.VelocitySellSpike);
if ( isLong && velBuyFlag ) layerC += LAYER_C_VELOCITY_AGREE;
if (!isLong && velSellFlag) layerC += LAYER_C_VELOCITY_AGREE;

// ── Sweep agreement (Phase 3.7) ────────────────────────────────
// Same-side sweep = aggressive directional flow confirming signal.
bool buySweepFlag  = snap.GetFlag(SnapKeys.BuySweep);
bool sellSweepFlag = snap.GetFlag(SnapKeys.SellSweep);
if ( isLong && buySweepFlag ) layerC += LAYER_C_SWEEP_AGREE;
if (!isLong && sellSweepFlag) layerC += LAYER_C_SWEEP_AGREE;

// ── Tape Iceberg agreement (Phase 3.7) ─────────────────────────
// Independent of Phase 2.4/2.8 bar-level iceberg. Cross-confirmation
// when both fire; tape-level alone is lighter-weight evidence.
bool tapeBullIceFlag = snap.GetFlag(SnapKeys.TapeBullIceberg);
bool tapeBearIceFlag = snap.GetFlag(SnapKeys.TapeBearIceberg);
if ( isLong && tapeBullIceFlag) layerC += LAYER_C_TAPEICE_AGREE;
if (!isLong && tapeBearIceFlag) layerC += LAYER_C_TAPEICE_AGREE;
```

Max all-confirming stack from Phase 3.7: +18. Combined with max Phase 2 stack (+28 from Trap/Iceberg/Exhaustion/Unfinished confirming plus others), total pre-cap would be +46; `Math.Min(layerC, 30)` cap keeps it bounded. No change to the cap.

## Veto Rule (Sweep opposition only)

Immediately after the Phase 2.8 Iceberg opposition veto (the Phase 2.9 Exhaustion veto was removed; we insert after the Iceberg veto and before the removed-Exhaustion comment block):

```csharp
// Phase 3.7 — Sweep opposition veto
// Buy sweep during a short signal = live buying pressure walking up the book.
// Sell sweep during a long signal = live selling pressure walking down.
// Sweep is rare (≥3 levels in 200ms) — binary veto is appropriate.
if ( isLong && sellSweepFlag) { isVetoed = true; }
if (!isLong && buySweepFlag ) { isVetoed = true; }
```

**No veto for BigPrint, Velocity, or TapeIceberg.** They're scoring-only until autopsy proves conviction. Honors Phase 2.9 lesson: high-frequency binary vetoes are destructive.

## Reason Strings

**LayerC reasons** — after the Phase 2.9 `unf+` tag:

```csharp
if ((isLong && bpDelta > 0)      || (!isLong && bpDelta < 0))       sb.Append("bp+");
if ((isLong && velBuyFlag)       || (!isLong && velSellFlag))       sb.Append("vel+");
if ((isLong && buySweepFlag)     || (!isLong && sellSweepFlag))     sb.Append("swp+");
if ((isLong && tapeBullIceFlag)  || (!isLong && tapeBearIceFlag))   sb.Append("tice+");
```

**Veto reasons** — in the isLong branch, after `vICE`:
```csharp
if (sellSweepFlag) sb.Append("vSWP");
```

In the `!isLong` branch:
```csharp
if (buySweepFlag) sb.Append("vSWP");
```

## Invariants
- Zero allocation on hot path.
- `BigPrintDelta` is a `double` (stored as such in the snapshot). All others are binary flags.
- Snapshot publish is null-safe (detector may not be instantiated in edge cases like configuration failure).
- Weights total intentionally lighter than Phase 2 confirmations: tape signals are supporting evidence, not primary. Bar-close detectors (Trap/Iceberg/Exhaustion) still drive the +8 backbone.
- `BuySweep` and `SellSweep` are mutually exclusive in practice (a sweep is one-sided), but the code doesn't assume this — independent flags.

## Validation
- [ ] Project builds, no warnings.
- [ ] Unit sanity on a mock snapshot:
  - `BigPrintDelta = +500`, long candidate → `LayerC` +4, reason contains `bp+`
  - `VelocitySellSpike = 1.0`, short candidate → `LayerC` +4, reason contains `vel+`
  - `BuySweep = 1.0`, long candidate → `LayerC` +6, reason contains `swp+`, no veto
  - `BuySweep = 1.0`, short candidate → `IsVetoed = true`, reason contains `vSWP`
  - `TapeBullIceberg = 1.0`, long candidate → `LayerC` +4, reason contains `tice+`
- [ ] 6-week backtest vs Phase 3.6 baseline ($23,570 / 46 trades):
  - Trade count expected to DROP slightly due to Sweep veto (rare signal, should bite 1-3 trades over the window).
  - Expectancy on surviving trades should RISE from scoring bonuses pushing marginal setups into higher grades.
  - **Net profit non-decreasing** is the target. If it drops significantly, Sweep veto is the first suspect — measure sweep fire rate via `filter_autopsy.csv`.
- [ ] Spot-check 5 `swp+` bonus trades and 5 `vSWP` vetoed bars.
- [ ] Spot-check `bp+` alignment across a sample: `BigPrintDelta` sign should match trade direction on bonus-tagged trades.

## Do Not
- Add BigPrint, Velocity, or TapeIceberg vetoes in this phase. Scoring only. Phase 2.9 taught us to be conservative.
- Couple tape signals to bar-level signals (e.g., "tape iceberg + bar iceberg → doubled score"). Each detector stands alone — autopsy needs separable attribution.
- Gate tape signals by distance to entry or time-in-bar. The raw flags are what the snapshot carries; any refinement belongs in a future phase.
- Touch detector thresholds (BigPrintDetector's p95, Velocity's 3× multiplier, Sweep's 3 levels, Iceberg's 8 hits). Those are calibrated empirically; changing them is a separate phase.

## Rationale
Phase 3 was scaffolded for this moment: all four detectors have been running in the shadows for phases 3.3–3.6 with zero behavior change, proving the plumbing is correct. Now we bolt them into scoring. The conservative-weight strategy (total tape contribution capped at +18) means the backbone Phase 2 detectors still dominate — tape signals are tie-breakers that upgrade a borderline setup. Only Sweep gets a veto because:
1. It's the rarest tape signal (<1% of bars).
2. It represents the strongest real-time evidence (aggressor walked through levels RIGHT NOW).
3. Phase 2.9 showed high-frequency vetoes are destructive; low-frequency vetoes are safe.

After Phase 3.7: Phase 3 is complete. The strategy now has five bar-close detectors (LevelHistory, Trap, Iceberg, Exhaustion, Unfinished) and four tape-level detectors (BigPrint, Velocity, Sweep, TapeIceberg) feeding Layer C. All nine are separable in `filter_autopsy.csv` via their reason tags: `trap+/vTRAP`, `ice+/vICE`, `exh+`, `unf+`, `bp+`, `vel+`, `swp+/vSWP`, `tice+`.
