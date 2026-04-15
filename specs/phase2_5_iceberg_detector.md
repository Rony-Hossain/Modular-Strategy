# Phase 2.5 — Iceberg Detector

Bar-level iceberg: repeated high-volume absorption at the same price across >=2 of the last 3 bars (current + 2 prior). Consumes Phase 2.4's `LevelHistoryTracker`. This spec lands the data only — scoring/wiring is Phase 2.7.

## File
`ModularStrategy/FootprintCore.cs` (only)

## New Fields on `FootprintResult`

Add immediately after `BearExhaustion` (the last Phase 2.3 bool):

```csharp
// Phase 2.5 — Iceberg (repeated absorption at same price across recent bars)
public bool   BullIceberg  { get; }   // cluster at/near bar Low
public bool   BearIceberg  { get; }   // cluster at/near bar High
public double IcebergPrice { get; }   // absorbed price, else 0.0
```

Add 3 ctor params (after `bearExhaustion`), assign in ctor body. Update all `Zero` / `BuildZeroResult` / invalid-path `new FootprintResult(...)` sites to pass `false, false, 0.0`.

## Instance-Method Conversion

Iceberg needs the tracker AND a scratch buffer. Currently `ComputeFromBar` and `FinalizeResult` are `private static`. Convert both to `private` instance methods on `FootprintCore`. No other call sites; local-only change. Remove the `in FootprintCoreConfig config` parameter from both — use the `_config` field directly.

## Scratch Buffer

Add as a private field on `FootprintCore`:

```csharp
private readonly LevelStat[] _iceQueryBuf = new LevelStat[2];   // ICE_LOOKBACK_BARS
```

Zero allocation in the hot path — buffer is reused every bar.

## Computation Site

In `FinalizeResult`, **immediately after** the existing Phase 2.2/2.3 block (after `bearExhaustion` is computed) and **before** the `new FootprintResult(...)` ctor call:

```csharp
// Phase 2.5 — Iceberg
// Current-bar absorption at the modal-volume price, repeated across prior bars at same price.
const double ICE_CURR_ABS_RATIO   = 2.0;   // current-bar: maxCombinedVol >= 2.0 * avgLevelVol
const double ICE_PRIOR_FLOOR_RATIO = 1.0;  // prior-bar floor at same price vs current avgLevelVol
const int    ICE_MIN_RECURRENCES  = 2;     // >=2 bars in the 3-bar window (current + 2 prior)
const int    ICE_LOOKBACK_BARS    = 2;     // prior bars queried

double iceAbsPrice = 0.0;
bool   bullIceberg = false;
bool   bearIceberg = false;

bool currAbsorbed = avgLevelVol > 0.0
                 && baseResult.MaxCombinedVol >= ICE_CURR_ABS_RATIO * avgLevelVol;

if (currAbsorbed)
{
    double candidatePrice = baseResult.MaxCombinedVolPrice;
    double priorFloor     = ICE_PRIOR_FLOOR_RATIO * avgLevelVol;

    int matches = _levelHistory.QueryPrice(candidatePrice, ICE_LOOKBACK_BARS, _iceQueryBuf);

    int hits = 1;  // current bar counts
    for (int i = 0; i < matches; i++)
    {
        if (_iceQueryBuf[i].TotalVol >= priorFloor)
            hits++;
    }

    if (hits >= ICE_MIN_RECURRENCES)
    {
        iceAbsPrice = candidatePrice;
        double halfTick = 0.5 * baseResult.TickSize;
        if (Math.Abs(candidatePrice - baseResult.Low)  <= halfTick) bullIceberg = true;
        if (Math.Abs(candidatePrice - baseResult.High) <= halfTick) bearIceberg = true;
        // Mid-bar iceberg (not touching either extreme): price recorded but no direction fires.
    }
}
```

Pass `bullIceberg, bearIceberg, iceAbsPrice` into the ctor at the corresponding new positions.

## Ingest Ordering (unchanged)

Current order in `TryComputeCurrentBar`:
1. `ComputeFromBar` → `FinalizeResult` builds current-bar result.
2. Caller receives result; if valid, tracker ingests the current bar.

This means at the time `FinalizeResult` queries `_levelHistory`, the tracker holds **only prior bars** (no self-reference). Correct by construction. Do **not** reorder ingestion.

## Do Not
- Touch `LevelHistoryTracker`, `FootprintAssembler`, `ConfluenceEngine`, `FootprintTradeAdvisor`, `HostStrategy`, or any other file.
- Query more than 2 prior bars — the 3-bar window is intentional.
- Add `SnapKeys`, Layer C scoring, or veto rules — that's Phase 2.7.
- Allocate inside the hot path. `_iceQueryBuf` is the only buffer permitted.

## Validation
- [ ] Project builds, no warnings.
- [ ] 6-week backtest. `Summary.csv` must match the `baselines/phase2_4_*` snapshot **exactly** — data-only phase.
- [ ] Temporary debug print on 20 random bars with `BullIceberg || BearIceberg` firing, confirming:
  - `IcebergPrice == MaxCombinedVolPrice` of the current bar.
  - `BullIceberg` only when `IcebergPrice == Low` (within 0.5 tick).
  - `BearIceberg` only when `IcebergPrice == High` (within 0.5 tick).
  - Frequency: ~1-5% of bars on NQ 5-min (sparse by design).
  - Remove prints before committing.

## Rationale
Classic iceberg: a large resting order sits at a price across multiple bars, absorbing aggressor flow each bar, but price cannot break through. The "same price with high volume >=2 of 3 bars" rule captures repetition; the `MaxCombinedVol >= 2 * avgLevelVol` gate filters noise bars where the modal price is just a random pick. Tying detection to bar extreme yields actionable direction (resistance-iceberg = bear, support-iceberg = bull). Mid-bar recurrences still record a price but fire no direction flag — they are informational and Phase 2.7 may use them as context.

Thresholds (2.0 / 1.0 / 2-of-3) are conservative defaults. Tune in Phase 2.7 once hit-rate-vs-forward-returns lands in `filter_autopsy.csv`.
