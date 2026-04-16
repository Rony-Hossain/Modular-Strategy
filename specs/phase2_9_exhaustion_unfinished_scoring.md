# Phase 2.9 — Exhaustion & Unfinished Auction Scoring

> **Revision 2026-04-15** — Exhaustion opposition veto was **removed** post-backtest.
> First full-stack run showed −91% net profit vs phase2_3 baseline ($1.7K vs $19K).
> Test A (veto off, scoring on) recovered to $23.5K — beating baseline by +$4.5K.
> Root cause: Exhaustion fires on 5–15% of bars (3–10× more often than Trap or
> Iceberg). As a binary veto it killed valid short-continuation setups where
> price briefly thinned at the low mid-move. The opposite-side mapping is still
> correct for scoring — but too noisy for a binary gate.
> **Authoritative behavior** is now: +8 Layer C bonus on agreement, no veto.
> Reason tag `vEXH` removed from the reason-builder. The code reflects this;
> the veto block below is retained as historical rationale only.


Wire Phase 2.3 `BullExhaustion` / `BearExhaustion` and Phase 2.2 `UnfinishedTop` / `UnfinishedBottom` into `MarketSnapshot`. Exhaustion gets full scoring + veto (opposite-side mapping, mirroring Trapped Traders). Unfinished gets modest **target-side** scoring only — no veto, because the signal is weaker and more context-dependent.

This is the last of the Phase 2.x detector-wiring work. After 2.9, all Phase 2 detectors (Unfinished / Exhaustion / LevelHistory / Iceberg / TrappedTraders) are data + scored.

## Files
- **Modified**: `ModularStrategy/CommonTypes.cs` (add 4 `SnapKeys` constants)
- **Modified**: `ModularStrategy/HostStrategy.cs` (publish from `_lastFpResult`)
- **Modified**: `ModularStrategy/ConfluenceEngine.cs` (two scoring blocks, one veto block, reason strings)

**Do not touch**: `FootprintCore.cs`, `LevelHistoryTracker.cs`, `TrappedTraderDetector.cs`, or any detector file. Data sources frozen after 2.3 / 2.2.

## Direction Semantics

### Exhaustion (opposite-side mapping — mirrors Trapped Traders)

| Flag | Meaning | Confirms | Vetoes |
|------|---------|----------|--------|
| `BullExhaustion` | Top-level volume sparse — **bullish thrust exhausted at high** | SHORT | LONG |
| `BearExhaustion` | Bottom-level volume sparse — **bearish thrust exhausted at low** | LONG | SHORT |

Naming note: `Bull*` means "bullish move exhausted" (not "bull signal"). This matches the FootprintCore.cs computation (`bullExhaustion = TopLevelTotalVol < 0.5 * avgLevelVol`) and the Phase 2.3 rationale: "a thrust ran out of participants".

### Unfinished Auction (target-side magnet — scoring only, no veto)

| Flag | Meaning | Confirms (modest) |
|------|---------|-------------------|
| `UnfinishedTop` | Both aggressors at bar high — high is a revisit magnet | **LONG** (target lies above) |
| `UnfinishedBottom` | Both aggressors at bar low — low is a revisit magnet | **SHORT** (target lies below) |

Unfinished is **target-side** because the implication is "price will be drawn back to the unfinished extreme." A long benefits from an overhead magnet; a short benefits from an underfoot magnet. No veto: the signal is too context-dependent (how far price has traveled, where the magnet is relative to current close) to justify killing a trade on it. Weight is half of Exhaustion.

## New SnapKeys

In `CommonTypes.cs`, add immediately after the Phase 2.8 `IcebergPrice` block:

```csharp
// ── Exhaustion (Phase 2.3 detector, Phase 2.9 wiring) ────────────
// Published by HostStrategy.OnPopulateIndicatorBag() each bar.
// Requires Volumetric and ≥4 levels in the bar for meaningful avg.

/// <summary>
/// 1.0 when top-level volume was &lt;50% of avg-level volume: the bullish
/// thrust ran out of participants at the high. Confirms SHORT signals.
/// Vetoes LONG signals (buying into a confirmed failed-thrust high).
/// Zero without Volumetric or on thin bars (&lt;4 levels).
/// </summary>
public const string BullExhaustion = "BullExhaustion";

/// <summary>
/// 1.0 when bottom-level volume was &lt;50% of avg-level volume: the bearish
/// thrust ran out of participants at the low. Confirms LONG signals.
/// Vetoes SHORT signals.
/// Zero without Volumetric or on thin bars.
/// </summary>
public const string BearExhaustion = "BearExhaustion";

// ── Unfinished Auction (Phase 2.2 detector, Phase 2.9 wiring) ────
// Published by HostStrategy.OnPopulateIndicatorBag() each bar.
// Requires Volumetric. Extreme prints both bid AND ask aggressor
// volume → auction didn't fully reject → magnet for future revisit.

/// <summary>
/// 1.0 when both bid and ask aggressors printed at the bar High:
/// the high is a revisit magnet. Modest LONG confirmation (target-side).
/// No veto — signal is context-dependent.
/// </summary>
public const string UnfinishedTop = "UnfinishedTop";

/// <summary>
/// 1.0 when both bid and ask aggressors printed at the bar Low:
/// the low is a revisit magnet. Modest SHORT confirmation (target-side).
/// No veto.
/// </summary>
public const string UnfinishedBottom = "UnfinishedBottom";
```

## Snapshot Publish

In `HostStrategy.cs`, `OnPopulateIndicatorBag`, immediately after the Phase 2.8 Iceberg block. Single combined valid/invalid block for both detector sets:

```csharp
// Phase 2.9 — Exhaustion + Unfinished Auction
if (_lastFpResult.IsValid)
{
    snapshot.Set(SnapKeys.BullExhaustion,   _lastFpResult.BullExhaustion   ? 1.0 : 0.0);
    snapshot.Set(SnapKeys.BearExhaustion,   _lastFpResult.BearExhaustion   ? 1.0 : 0.0);
    snapshot.Set(SnapKeys.UnfinishedTop,    _lastFpResult.UnfinishedTop    ? 1.0 : 0.0);
    snapshot.Set(SnapKeys.UnfinishedBottom, _lastFpResult.UnfinishedBottom ? 1.0 : 0.0);
}
else
{
    snapshot.Set(SnapKeys.BullExhaustion,   0.0);
    snapshot.Set(SnapKeys.BearExhaustion,   0.0);
    snapshot.Set(SnapKeys.UnfinishedTop,    0.0);
    snapshot.Set(SnapKeys.UnfinishedBottom, 0.0);
}
```

## Layer C Scoring

In `ConfluenceEngine.cs`, add two weight constants next to `LAYER_C_ICEBERG_AGREE`:

```csharp
private const int LAYER_C_EXHAUSTION_AGREE = 8;   // opposite-side exhaustion confirms direction
private const int LAYER_C_UNFINISHED_AGREE = 4;   // target-side magnet — lighter, no veto
```

In `Evaluate`, immediately after the Phase 2.8 Iceberg scoring block:

```csharp
// ── Exhaustion agreement (Phase 2.9) ────────────────────────────
// BullExhaustion (top exhausted) confirms SHORT. BearExhaustion (bottom
// exhausted) confirms LONG. Opposite-side mapping — mirrors Trapped.
bool bullExhFlag = snap.GetFlag(SnapKeys.BullExhaustion);
bool bearExhFlag = snap.GetFlag(SnapKeys.BearExhaustion);

if ( isLong && bearExhFlag) layerC += LAYER_C_EXHAUSTION_AGREE;
if (!isLong && bullExhFlag) layerC += LAYER_C_EXHAUSTION_AGREE;

// ── Unfinished Auction target-side (Phase 2.9) ──────────────────
// UnfinishedTop (high = magnet) gives longs a target overhead.
// UnfinishedBottom (low = magnet) gives shorts a target below.
bool unfinTopFlag    = snap.GetFlag(SnapKeys.UnfinishedTop);
bool unfinBottomFlag = snap.GetFlag(SnapKeys.UnfinishedBottom);

if ( isLong && unfinTopFlag   ) layerC += LAYER_C_UNFINISHED_AGREE;
if (!isLong && unfinBottomFlag) layerC += LAYER_C_UNFINISHED_AGREE;
```

The `Math.Min(layerC, 30)` cap applies without change.

## Veto Rule (Exhaustion only — no Unfinished veto)

Immediately after the Phase 2.8 Iceberg opposition veto:

```csharp
// Phase 2.9 — Exhaustion opposition veto
// Long signal while BULL exhaustion fires (top failed) → buying into
// a confirmed failed thrust. Symmetric for shorts.
if ( isLong && bullExhFlag) { isVetoed = true; }
if (!isLong && bearExhFlag) { isVetoed = true; }
```

**No Unfinished veto.** Unfinished extremes get revisited, but that's a magnet signal — it doesn't say "don't trade this direction," only "expect a pullback toward the magnet." A veto here would kill too many valid trades. If post-backtest autopsy shows unfinished-into-direction trades underperform badly, Phase 2.10 can add a distance-gated veto.

## Reason Strings

1. **LayerC reasons** — after the Phase 2.8 `ice+` tag:
   ```csharp
   if ((isLong && bearExhFlag)    || (!isLong && bullExhFlag))    sb.Append("exh+");
   if ((isLong && unfinTopFlag)   || (!isLong && unfinBottomFlag)) sb.Append("unf+");
   ```

2. **Veto reasons** — in the isLong branch, after Phase 2.8 `vICE`:
   ```csharp
   if (bullExhFlag) sb.Append("vEXH");
   ```
   In the `!isLong` branch:
   ```csharp
   if (bearExhFlag) sb.Append("vEXH");
   ```
   No `vUNF` — Unfinished has no veto.

## Invariants
- Zero allocation; same pattern as 2.7/2.8.
- Binary vetoes stack with existing.
- `BullExhaustion` and `BearExhaustion` are mutually exclusive in the common case (a bar can't have both extremes sparse if the thrust direction is clear), but both can fire on quiet/coiling bars where both extremes are underweighted. If both fire on a trade: scoring applies on the confirming side, veto applies on the opposing side → net VETOED.
- `UnfinishedTop` and `UnfinishedBottom` can **both be true** on a bar where both extremes saw two-sided aggression. In that case both longs and shorts get +4 — informational parity, correct behavior.
- Weights: Exhaustion=8 (matches Trapped/Iceberg — full-conviction signal), Unfinished=4 (half-weight reflects target-only semantics and no veto).

## Validation

- [ ] Project builds, no warnings.
- [ ] Unit sanity: on a bar where `BullExhaustion == 1.0`:
  - Short candidate: `LayerC` +8, reason contains `exh+`, no veto.
  - Long candidate: `IsVetoed == true`, reason contains `vEXH`.
- [ ] Unit sanity: on a bar where `UnfinishedTop == 1.0`:
  - Long candidate: `LayerC` +4, reason contains `unf+`, no veto.
  - Short candidate: no effect (target mismatch).
- [ ] 6-week backtest. `Summary.csv` will differ from `baselines/phase2_8_*`:
  - Exhaustion fires on ~5–15% of bars (per 2.3 rationale), so veto bite is larger than Iceberg — expect trade-count drop of 2–5%.
  - Unfinished adds bonus points but no cuts — expect small expectancy lift on confirmed trades.
  - Net expectancy non-decreasing. If it drops, first suspect: Unfinished weight too high — try 2 or 3.
- [ ] Spot-check 10 `vEXH` vetoed bars in `filter_autopsy.csv` and 10 `unf+` bonus bars.
- [ ] Snapshot new baseline → `baselines/phase2_9_*`.

## Do Not
- Add Unfinished veto rules in this phase.
- Gate Unfinished scoring by distance to the unfinished extreme. Distance-gating is a 2.10+ refinement.
- Combine Exhaustion with Iceberg into a single "absorption score" bonus. Each detector stands alone; autopsy needs separable attribution.
- Touch thresholds in `FootprintCore.cs` (LOW_VOL_RATIO, MIN_LEVELS_FOR_EXH). Detector is frozen.

## Rationale
Exhaustion and Unfinished are the two bar-local patterns from Phase 2.2/2.3. Exhaustion is directional (a failed thrust is a reversal cue — classic Volume Profile / Order Flow pedagogy), so it gets the same full-conviction treatment as Trapped and Iceberg. Unfinished is a "price memory" signal: the level will be revisited, but the *timing* is unspecified. That makes it unsuitable for a veto but valid as a modest target-side confirmation — the trade has a known liquidity magnet in its favor.

Three detectors now feed Layer C with +8 bonus on agreement: Trap (opposite-side), Iceberg (same-side), Exhaustion (opposite-side). Max simultaneous stack: +24, hitting the Layer C cap at 30 together with existing proxies. This is by design — on a rare "triple confirmation" bar, the signal is saturated, and the cap prevents one detector from dominating. Autopsy will tell us which detectors pull weight; the uniform +8 is the MVP scaffold.

After 2.9, the Phase 2 detector suite is complete: five detectors, three with vetoes, five with Layer C bonuses, all separable in `filter_autopsy.csv`.
