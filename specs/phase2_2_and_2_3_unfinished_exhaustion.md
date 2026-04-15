# Phase 2.2 + 2.3 — Unfinished Auction + Exhaustion Detectors

Two detectors, same file, same pattern. Both consume the `TopLevel*`/`BottomLevel*` fields added in Phase 2.1. This spec lands the data only — scoring/wiring is Phase 2.7.

## File
`ModularStrategy/FootprintCore.cs`

## New Fields on `FootprintResult`

Add after `BottomLevelTotalVol` (line ~275):

```csharp
// Phase 2.2 — Unfinished Auction (both aggressor sides printed at extreme)
public bool UnfinishedTop { get; }
public bool UnfinishedBottom { get; }

// Phase 2.3 — Exhaustion (low volume at bar extreme vs avg-level volume)
public bool BullExhaustion { get; }
public bool BearExhaustion { get; }
```

Add 4 ctor params (after `bottomLevelTotalVol`), assign in ctor body.

## Computation Site

In the primary assembly path — the `new FootprintResult(...)` at line ~890 (the one that receives `baseResult.TopLevelAskVol`, etc.). **Immediately before** that constructor call, compute:

```csharp
// Phase 2.2 — Unfinished Auction
// Both sides printed at extreme → neither aggressor "won" the auction at the high/low.
// Historically these levels get revisited ("unfinished business").
bool unfinishedTop    = baseResult.TopLevelAskVol    > 0.0 && baseResult.TopLevelBidVol    > 0.0;
bool unfinishedBottom = baseResult.BottomLevelAskVol > 0.0 && baseResult.BottomLevelBidVol > 0.0;

// Phase 2.3 — Exhaustion
// Volume at the extreme is far below the bar's per-level average.
// Signals failed thrust / loss of participation at the turn.
const double LOW_VOL_RATIO = 0.5;          // ≤50% of avg-level vol
const int    MIN_LEVELS_FOR_EXH = 4;        // need 4+ levels for meaningful "avg"

double totalBarVol  = baseResult.TotalBuyVol + baseResult.TotalSellVol;
double avgLevelVol  = baseResult.LevelCount > 0 ? totalBarVol / baseResult.LevelCount : 0.0;

bool exhaustionCondMet = baseResult.LevelCount >= MIN_LEVELS_FOR_EXH && avgLevelVol > 0.0;

bool bullExhaustion = exhaustionCondMet
    && baseResult.TopLevelTotalVol < LOW_VOL_RATIO * avgLevelVol;

bool bearExhaustion = exhaustionCondMet
    && baseResult.BottomLevelTotalVol < LOW_VOL_RATIO * avgLevelVol;
```

Then pass the 4 booleans into the `new FootprintResult(...)` constructor at the corresponding new param positions.

## Mirror into `Zero`, `BuildZeroResult`, and the other `new FootprintResult(...)` call site (line ~665)

Pass `false, false, false, false` for the 4 new bool params at all zero/invalid construction sites.

## Do Not
- Touch `FootprintAssembler`, `ConfluenceEngine`, `FootprintTradeAdvisor`, or any other file.
- Add `SnapKeys`, Layer C scoring, or veto rules — that's Phase 2.7.
- Read prior bars or maintain any new state in `FootprintCore`. Both detectors are per-bar pure functions of the `FootprintResult` data already assembled this bar.

## Validation
- [ ] Project builds, no warnings.
- [ ] Run 6-week backtest. `Summary.csv` must match `baselines/phase1_1_2026-04-15/Summary.csv` **exactly** — this phase only adds data, no logic change.
- [ ] Add temporary debug print on 20 random bars confirming:
  - `UnfinishedTop` true on bars where TopAsk>0 AND TopBid>0.
  - `BullExhaustion` true on bars where top-level-vol is visibly sparse.
  - Frequency roughly: Unfinished ~10–30% of bars, Exhaustion ~5–15% of bars (NQ 5-min is noisy).
  - Remove prints before committing.

## Rationale (for future-you reading the diff)
- **Unfinished**: Dalton / Steidlmayer classic — both sides transacted at extreme ⇒ auction didn't fully reject ⇒ magnet for revisit.
- **Exhaustion**: Volume contraction at the bar's extreme ⇒ the move ran out of participants. When combined with a swing-high (Phase 2.7 wiring), this is the canonical "Exhaustion" reversal signal.

Thresholds (`LOW_VOL_RATIO = 0.5`, `MIN_LEVELS_FOR_EXH = 4`) are conservative defaults. Tune in Phase 2.7 once we measure hit rate vs forward returns in `filter_autopsy.csv`.
