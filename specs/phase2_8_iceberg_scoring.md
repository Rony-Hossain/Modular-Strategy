# Phase 2.8 — Iceberg Scoring & Veto

Wire the Phase 2.5 `BullIceberg` / `BearIceberg` / `IcebergPrice` data into `MarketSnapshot`, score Layer C on agreement, and veto on direct opposition. Mirrors the Phase 2.7 pattern exactly, with **inverted direction semantics** (iceberg is same-side confirmation, trap is opposite-side confirmation).

## Files
- **Modified**: `ModularStrategy/CommonTypes.cs` (add 3 `SnapKeys` constants)
- **Modified**: `ModularStrategy/HostStrategy.cs` (publish from `_lastFpResult` in `OnPopulateIndicatorBag`)
- **Modified**: `ModularStrategy/ConfluenceEngine.cs` (Layer C bonus + veto + reason strings)

**Do not touch**: `FootprintCore.cs`, `LevelHistoryTracker.cs`, or any detector file. Data source frozen after 2.5.

## Direction Semantics (Critical — Opposite of Trapped)

| Flag | Meaning | Confirms | Vetoes |
|------|---------|----------|--------|
| `BullIceberg` | Cluster absorbed at bar **Low** — support held | **LONG** | SHORT |
| `BearIceberg` | Cluster absorbed at bar **High** — resistance held | **SHORT** | LONG |

This is **same-side** confirmation: `Bull*` helps longs. Opposite of `Trapped*` (where `TrappedLongs` helps shorts). Call this out in SnapKey doc comments — easy to misread.

## New SnapKeys

In `CommonTypes.cs`, add immediately after the Phase 2.7 `TrapLevel` block (around line ~540):

```csharp
// ── Iceberg Absorption (Phase 2.5 detector, Phase 2.8 wiring) ────
// Published by HostStrategy.OnPopulateIndicatorBag() each bar.
// Requires Volumetric. Detector requires absorption at bar extreme
// repeated across 2-of-3 recent bars (current + 2 prior).

/// <summary>
/// 1.0 when a repeated volume cluster was absorbed at the bar Low:
/// support wall held over the 3-bar window. Confirms LONG signals —
/// buyers defending the level. Vetoes SHORT signals (shorting into
/// known support is trading into absorbed flow).
/// Zero without Volumetric or when detector hasn't found recurrence.
/// </summary>
public const string BullIceberg = "BullIceberg";

/// <summary>
/// 1.0 when a repeated volume cluster was absorbed at the bar High:
/// resistance wall held. Confirms SHORT signals. Vetoes LONG signals.
/// Zero without Volumetric or no recurrence.
/// </summary>
public const string BearIceberg = "BearIceberg";

/// <summary>
/// Absolute price of the absorbed level — equals the bar Low (bull iceberg),
/// bar High (bear iceberg), or an intra-bar price (mid-bar recurrence,
/// informational only). Zero when no recurrence detected this bar.
/// </summary>
public const string IcebergPrice = "IcebergPrice";
```

## Snapshot Publish

In `HostStrategy.cs`, `OnPopulateIndicatorBag`, immediately after the Phase 2.7 Trapped Traders block (the `if (_lastFpResult.IsValid) { TrappedLongs... }` publish). Same valid/invalid mirror pattern:

```csharp
// Phase 2.8 — Iceberg
if (_lastFpResult.IsValid)
{
    snapshot.Set(SnapKeys.BullIceberg,  _lastFpResult.BullIceberg  ? 1.0 : 0.0);
    snapshot.Set(SnapKeys.BearIceberg,  _lastFpResult.BearIceberg  ? 1.0 : 0.0);
    snapshot.Set(SnapKeys.IcebergPrice, _lastFpResult.IcebergPrice);
}
else
{
    snapshot.Set(SnapKeys.BullIceberg,  0.0);
    snapshot.Set(SnapKeys.BearIceberg,  0.0);
    snapshot.Set(SnapKeys.IcebergPrice, 0.0);
}
```

## Layer C Scoring

In `ConfluenceEngine.cs`, add a new weight constant next to `LAYER_C_TRAPPED_AGREE` (around line ~52):

```csharp
private const int LAYER_C_ICEBERG_AGREE = 8;   // iceberg flag on SAME side confirms direction
```

In `Evaluate`, immediately after the Phase 2.7 Trapped Traders scoring block (where `trapLongsFlag` / `trapShortsFlag` are declared). Place **before** the NonConfirmation veto:

```csharp
// ── Iceberg agreement (Phase 2.8) ───────────────────────────────
// BullIceberg (cluster at Low) confirms LONG. BearIceberg (cluster at High)
// confirms SHORT. Same-side mapping — opposite of Trapped Traders.
bool bullIcebergFlag = snap.GetFlag(SnapKeys.BullIceberg);
bool bearIcebergFlag = snap.GetFlag(SnapKeys.BearIceberg);

if ( isLong && bullIcebergFlag) layerC += LAYER_C_ICEBERG_AGREE;
if (!isLong && bearIcebergFlag) layerC += LAYER_C_ICEBERG_AGREE;
```

The `Math.Min(layerC, 30)` cap already in place applies without change.

## Veto Rule

Immediately after the Phase 2.7 Trapped Traders opposition veto:

```csharp
// Phase 2.8 — Iceberg opposition veto
// Long signal while a BEAR iceberg (overhead wall) is active → trading
// into absorbed resistance. Short signal while BULL iceberg is active →
// trading into absorbed support.
if ( isLong && bearIcebergFlag) { isVetoed = true; }
if (!isLong && bullIcebergFlag) { isVetoed = true; }
```

Reuse `bullIcebergFlag` / `bearIcebergFlag` from the scoring block — no re-reads.

**Do not gate by distance to `IcebergPrice`.** The detector's 2-of-3 recurrence gate already keeps firing rate to ~1–5%. Proximity filtering waits on 2.8 autopsy data.

## Reason Strings

1. **LayerC reasons** — immediately after the Phase 2.7 `trap+` tag:
   ```csharp
   if ((isLong && bullIcebergFlag) || (!isLong && bearIcebergFlag)) sb.Append("ice+");
   ```

2. **Veto reasons** — in the isLong branch, after the Phase 2.7 `vTRAP`:
   ```csharp
   if (bearIcebergFlag) sb.Append("vICE");
   ```
   In the `!isLong` branch:
   ```csharp
   if (bullIcebergFlag) sb.Append("vICE");
   ```

## Invariants
- Zero allocation, same pattern as Trapped Traders.
- Binary veto stacks with existing vetoes.
- `BullIceberg` and `BearIceberg` are mutually exclusive by detector construction (iceberg price can be at Low OR High within half-tick, not both on a normal bar). On a degenerate doji bar where Low ≈ High, both could in principle fire; if so, scoring applies on one side and veto applies from the other → net VETOED (correct for ambiguous bars).
- `LAYER_C_ICEBERG_AGREE = 8` matches Trapped weight for MVP. Differentiation waits on autopsy.

## Validation

- [ ] Project builds, no warnings.
- [ ] Unit sanity: on a bar where `BullIceberg == 1.0`:
  - Long candidate: `LayerC` +8, reason contains `ice+`, no veto.
  - Short candidate: `IsVetoed == true`, reason contains `vICE`.
- [ ] 6-week backtest. `Summary.csv` will differ from `baselines/phase2_7_*`:
  - Long win-rate should improve (confirmed by support-side iceberg).
  - Trade count should drop 1–3% (veto bite — note: smaller than 2.7 because iceberg fires less often).
  - Net expectancy non-decreasing. Revert if it drops.
- [ ] Spot-check 10 VETOED bars in `filter_autopsy.csv`: confirm `vICE` reason and that 2.5 detector fired on that bar.
- [ ] Snapshot new baseline → `baselines/phase2_8_*`.

## Do Not
- Wire Exhaustion or Unfinished Auction — that's Phase 2.9.
- Use `IcebergPrice` for Layer B / LevelRegistry. It's a transient per-bar level, not a structural one.
- Score mid-bar icebergs (where `IcebergPrice > 0` but both flags are false). The detector already refuses to fire a direction in that case; scoring is symmetric.
- Raise the Layer C cap. The +8 from iceberg can stack with +8 from trap + other proxies, but the `Math.Min(layerC, 30)` cap is the safety net.

## Rationale
Iceberg is the cleanest structural orderflow signal in the detector set: repeated large-volume absorption at a specific extreme across multiple bars is precisely the signature of a resting-order wall. The same-side mapping (bull absorption → long) is the reverse of Trapped Traders (bull trap → short) because the underlying mechanics are opposite: iceberg = defender holds ground; trap = aggressor overextends and will reverse. Keeping both scoring and veto symmetric (trading WITH the wall = bonus, trading INTO the wall = veto) maximizes autopsy signal — each trade attributes to one of two buckets.

`+8` weight matches the Trapped `LAYER_C_TRAPPED_AGREE` for MVP; the prior-bar recurrence gate makes iceberg *less* frequent but *more* reliable, so the equal weight is conservative. If autopsy shows iceberg outperforming trap, the weight can rise in a tuning phase.
