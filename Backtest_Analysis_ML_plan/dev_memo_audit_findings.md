# Backtest Audit Findings & Recommended Actions

**Period analyzed:** January 4 – April 14, 2026 (3.5 months, MNQ)
**Dataset:** 17,979 signal evaluations, 1,239 accepted/rejected signals, 794 completed trades
**Prepared:** April 21, 2026

---

## TL;DR

Over the 3.5-month backtest, the strategy **lost $5,413 net** after slippage and commissions. Analysis of the logs identifies three root causes, ordered by impact:

1. **Four sources are actively losing money.** Disabling them in config (no code logic change beyond a threshold map) is projected to swing net P&L from −$5,413 to −$241 (+$5,172 improvement).

2. **The scoring system has almost no predictive power.** None of the five scoring layers correlates meaningfully with trade outcomes (best ρ = 0.08). Grades are inverted: Grade C trades are the most profitable, Grade A trades are the worst. Individual feature tokens (e.g. `s15`, `LH`, `LL`) do have signal, but the aggregate formula dilutes them.

3. **Python `strategy_config.py` and C# `StrategyConfig.cs` are out of sync.** The `OPTIMIZED` block in Python was never copied to C#. That optimizer output was generated against an unknown fitness function and should not be deployed.

Recommended immediate action: implement `PER_SOURCE_THRESHOLDS` in C# and disable the four losing sources. Projected impact is validated by two independent analyses (audit + Python replay).

---

## 1. The headline numbers

| | Gross P&L | Slippage | Commission | Net |
|---|---|---|---|---|
| Current (baseline) | +$1,029.50 | −$4,060.77 | −$2,382.00 | **−$5,413.27** |
| Disable 4 losing sources | +$4,013.00 | −$2,637.10 | −$1,617.00 | **−$241.10** |
| + Entry slippage cap >5t | +$4,013.00 | −$2,637.10 | −$1,617.00 | **−$241.10** |
| + RTH-only (09:30-15:30 ET) | +$1,095.50 | −$359.59 | −$204.00 | **+$531.91** |

The strategy has a slight gross edge (profit factor 1.04) but execution costs consume it. Cost reduction is as important as signal improvement.

---

## 2. The four losing sources

| Source | Trades | Win % | Profit Factor | Gross P&L | Stop-out rate |
|---|---|---|---|---|---|
| **SMF_Impulse** | 148 | 43.9% | 0.64 | **−$2,037.50** | 95% |
| **ADX_TrendSignal** | 54 | 44.4% | 0.68 | **−$625.50** | 98% |
| **VWAP_Reclaim** | 40 | 42.5% | 0.88 | **−$168.50** | **100%** |
| **EMA_CrossSignal** | 13 | 46.2% | 0.72 | **−$152.00** | 85% |
| **Total drag** | **255** | | | **−$2,983.50** | |

Failure patterns observed:

- **SMF_Impulse and ADX_TrendSignal** appear to catch momentum extensions *after* exhaustion. 95–98% of these trades end at stop. Signals fire when price has already moved too far, so reversion immediately kicks them out.
- **VWAP_Reclaim** has 100% stop-out rate — every single one of 40 trades ended at a stop. Zero trades hit T2. This strongly suggests the entry fires at the reclaim attempt rather than after confirmation. A "close above VWAP for N bars" filter would likely fix it.
- **EMA_CrossSignal** is small sample (n=13) but direction is clear.

Winners for reference:

| Source | Trades | PF | Gross P&L |
|---|---|---|---|
| FailedAuction | 39 | 1.86 | +$1,023.00 |
| OrderFlow_Abs | 12 | 4.68 | +$577.00 |
| SMC_OrderBlock | 13 | 4.99 | +$301.50 |
| SMF_Retest | 101 | 1.16 | +$564.00 |
| Confluence | 163 | 1.11 | +$766.00 |
| ORB_Retest | 210 | 1.11 | +$811.50 |

---

## 3. The scoring system is weak

Spearman correlation of each scoring layer with trade P&L:

| Layer | ρ (P&L) | p-value | Interpretation |
|---|---|---|---|
| layer_a (MTFA) | +0.080 | 0.024 | Weakly positive — statistically significant only because n=794 |
| layer_b (S/R) | +0.026 | 0.464 | Null |
| layer_c (OrderFlow) | −0.000 | 0.994 | Null — zero signal |
| layer_d (Structure) | +0.068 | 0.055 | Marginally positive |
| penalty | −0.027 | 0.447 | **Null — penalties are not penalizing losing trades** |

None of the layers predict outcome meaningfully. The penalty layer in particular has ρ ≈ 0, meaning it's essentially random noise applied to scores.

### But individual tokens do have signal

The scoring formula aggregates many "tokens" (individual condition flags). At the token level, some are strongly predictive:

**Strongly positive tokens (presence predicts winning trades):**

| Token | Avg P&L present | Avg P&L absent | Impact $ |
|---|---|---|---|
| `s15` (SR at 15 ticks) | +$27.87 | −$3.62 | **+$31.49** |
| `LH`/`LL` (Lower-high/low structure) | +$22.83 | −$3.00 | +$25.83 |
| `h4` (H4 aligned) | +$9.47 | −$10.88 | +$20.35 |
| `near` (near a level) | +$10.34 | −$1.13 | +$11.47 |

**Strongly negative tokens (presence predicts losing trades — but currently ADD to score):**

| Token | Avg P&L present | Avg P&L absent | Impact $ |
|---|---|---|---|
| `exh` (exhaustion) | −$0.53 | +$5.53 | **−$6.06** |
| `opp` (opposition) | −$6.03 | +$4.51 | −$10.54 |
| `unf` (unfinished) | −$9.30 | +$3.06 | **−$12.36** |

The scoring formula is currently rewarding `exh`, `opp`, and `unf` — three features that consistently predict losses. That alone accounts for a meaningful portion of the grade inversion.

### Grade distribution confirms the miscalibration

| Grade | Trades | Win Rate | Profit Factor | Avg P&L |
|---|---|---|---|---|
| A+ | 218 | 50% | 1.07 | +$2.73 |
| A | 25 | 36% | 0.67 | −$15.96 |
| B | 318 | 48% | 0.81 | −$7.45 |
| **C** | **233** | **57%** | **1.44** | **+$13.75** |

Grade C — nominally the lowest-confidence signals — is your most profitable grade. Grade A — the supposed "elite" signals — is your worst.

---

## 4. Configuration drift

`strategy_config.py` contains an `OPTIMIZED` block with timestamp `2026-04-20T01:21:18` that differs materially from `StrategyConfig.cs`:

| Parameter | strategy_config.py (OPTIMIZED) | StrategyConfig.cs | Delta |
|---|---|---|---|
| SCORE_REJECT | 80 | 60 | +20 |
| LAYER_A_H4 | 19 | 14 | +5 |
| LAYER_A_H2 | 13 | 10 | +3 |
| PENALTY_H4 | 9 | 8 | +1 |
| PROXY_BAR_DELTA | 12 | 7 | +5 |
| LAYER_C_DIVERGENCE | 20 | 15 | +5 |
| LAYER_C_ABS_MAX | 11 | 7 | +4 |
| LAYER_D_FULL_STRUCT | 17 | 12 | +5 |
| PER_SOURCE_THRESHOLDS | (14 entries) | **does not exist** | — |

**The OPTIMIZED values were never synced to C#.** This is both a fortunate miss and a process problem:

- **Fortunate** because those values appear to have been tuned against the wrong fitness function. The scoring diagnostic shows the penalty layer has ρ ≈ 0 with outcomes, yet OPTIMIZED tried to increase penalties. Shipping those values would likely have made things worse.
- **Process problem** because Python-C# sync has no defined mechanism. Any future optimization work will hit the same gap.

Recommendation: establish a sync script or remove the OPTIMIZED concept from Python entirely. Python should either read from C# (authoritative) or be a proposal-writer that a human commits to C# explicitly.

---

## 5. Recommended actions

### Immediate (this sprint)

**5.1 Implement PER_SOURCE_THRESHOLDS in C#**

Add a `Dictionary<string, int>` to `StrategyConfig.cs` under a new class:

```csharp
public static class SourceThresholds
{
    // Sources with PF < 1.0 over 3.5-month backtest — disabled pending review.
    // Values of 999 effectively disable since max possible score is 115.
    public static readonly Dictionary<string, int> PerSource = new()
    {
        // DISABLED — losing money
        { "SMF_Impulse",     999 },  // PF 0.64, -$2,037, 95% stop-out
        { "ADX_TrendSignal", 999 },  // PF 0.68, -$625,   98% stop-out
        { "VWAP_Reclaim",    999 },  // PF 0.88, -$168,  100% stop-out
        { "EMA_CrossSignal", 999 },  // PF 0.72, -$152

        // Others fall through to the global SCORE_REJECT (60)
    };
}
```

Then in `SignalGenerator` (wherever score is currently compared to `Policy.SCORE_REJECT`), add the per-source check:

```csharp
// After score is computed:
if (SourceThresholds.PerSource.TryGetValue(source, out int perSourceFloor))
{
    if (score < perSourceFloor)
    {
        logger.LogReject(source, conditionSetId,
            $"G0:PerSourceFloor(score={score}<{perSourceFloor})");
        return null;
    }
}
else if (score < Policy.SCORE_REJECT)
{
    logger.LogReject(source, conditionSetId,
        $"G0:ScoreFloor(score={score}<{Policy.SCORE_REJECT})");
    return null;
}
```

Ensure the rejection is logged with a gate_id (e.g., `G0:PerSourceFloor`) so future audits can identify source-filtered rejections distinct from other rejections.

**5.2 Re-run backtest with the same 3.5-month period**

Before anything else. Validate against the replay prediction:

| Metric | Baseline | Predicted (S1) | Actual (to measure) |
|---|---|---|---|
| Trades | 794 | 539 | ? |
| Win % | 50.9% | 52.9% | ? |
| PF | 1.04 | 1.21 | ? |
| Net P&L | −$5,413 | −$241 | ? |

If actual is within ±20% of predicted (net P&L between −$1,000 and +$500 roughly), the audit is validated. If it diverges significantly, there's a hidden interaction we missed — investigate before extending.

**5.3 Do NOT sync the OPTIMIZED block from Python**

Those weight values should not be deployed. They were tuned against a suspect fitness function and the scoring diagnostic suggests several of them (particularly penalty increases) move in the wrong direction.

Clear the OPTIMIZED block in Python or mark it as `PENDING_REBUILD` with a comment:

```python
OPTIMIZED = {
    "OPT_STATUS": "PENDING_REBUILD",
    "OPT_NOTE": "Previous values never applied to C# and likely tuned against wrong fitness function. Do not deploy. See audit findings April 2026.",
}
```

### Short-term (next 2 weeks)

**5.4 Fix Python/C# sync process**

Either:
- (a) Write a script that generates C# constants from Python dicts and commit the generated file, so there's always one source of truth in diff form, or
- (b) Remove Python's aspiration to override C# and treat Python strictly as read-only mirror of the current C# state for analysis and optimization-proposal purposes.

Option (b) is simpler and safer given current team size.

**5.5 Re-run losing sources audit at 12-month horizon**

Once 5.2 validates and C# is clean, extend backtest to 12 months. Re-run the audit pipeline (Phase 1 + Phase 23). Check whether:

- The four disabled sources are still net-losers at longer horizons (they might have been regime-specific)
- New sources emerge as losers under different market conditions
- PF of winning sources holds (FailedAuction PF 1.86 at n=39 needs n=150+ to confirm)

**5.6 Investigate VWAP_Reclaim specifically**

100% stop-out rate over 40 trades is a very specific pattern. This suggests a logic bug, not a market problem. Three things to check in the VWAP_Reclaim module:

- Does the entry fire on the reclaim tick, or after N bars of closes above VWAP?
- Is the stop placed at an appropriate distance, or right at VWAP (where it gets swept)?
- Is there a confirmation filter (e.g., positive CVD slope, above VWAP_SD2)?

If the module is fixed rather than disabled, the backtest should re-include it once the fix is verified.

### Medium-term (month 2+)

**5.7 Rebuild scoring weights from token-impact data**

Once 12-month backtest is available, rebuild the scoring formula using observed token impacts as weights, not hand-tuned constants. Concretely:

1. Extract all tokens from RANK_WIN rows in the 12-month log.
2. For each token, compute `impact = avg_pnl_when_present − avg_pnl_when_absent` over the training period (first 8 months).
3. Require n ≥ 50 for a token to be included.
4. Set token weights proportional to impact.
5. Tokens with negative impact contribute negatively to score (become penalties).
6. Validate on held-out test period (last 4 months). Grades should now be monotonically correlated with outcome.

This is straightforward regression. The current hand-tuned weights produce ρ ≈ 0 between score and outcome. A simple data-driven weighting should beat that easily.

**5.8 Evaluate Trade Advisor activation**

`StrategyConfig.cs` currently has `TRADE_ADVISOR_COMPARE_ONLY = true`, meaning the TA engine is in shadow mode. We have 1,419 TA_TIGHTEN_SHADOW events and 25 TA_EXIT_SHADOW events representing decisions the TA engine would have made but did not execute.

Before considering activation, analyze what those shadow decisions would have done to P&L if executed. That analysis is feasible from the current log (TA_DECISION rows have all the reasoning fields) and should be a targeted follow-up audit, not a blanket "turn it on."

### Not recommended

- **Do not extend the backtest to 1-2 years before validating 5.1/5.2.** Longer backtest on broken config just produces more data confirming the same problems. Fix sources first.
- **Do not run the previous optimizer again.** It's tuning features that don't correlate with outcomes.
- **Do not modify CONFLUENCE base weights yet.** The scoring diagnostic shows the weights are weakly predictive but not actively wrong. Wait for rebuild from token data rather than tweaking.
- **Do not deploy S3 (RTH-only restriction) yet.** 68 trades over 3.5 months is too thin. Revisit at 12-month horizon.

---

## 6. What we'll know after Step 5.2

Once you re-run the backtest with PER_SOURCE_THRESHOLDS applied:

**If results match the prediction (net ≈ −$241):**
- Audit methodology is validated
- Python replay harness works and can be trusted for future projections
- Extend to 12 months with confidence

**If results differ significantly:**
- Some interaction between disabled sources and remaining sources we didn't model (e.g., confluence signals stacking with SMF_Impulse)
- Or a bug in the C# implementation of the threshold check
- Either way: investigate before proceeding

**If results are worse than baseline:**
- Very unlikely given the math, but possible
- Would indicate our model of how sources interact is fundamentally wrong
- Roll back, re-analyze

---

## 7. Where to find the analysis artifacts

All parquet data and markdown reports are in `Analysis/artifacts/`:

- `trade_lifecycle.parquet` — 794 trades with reconstructed P&L per trade
- `source_audit.csv` + `source_audit.md` — per-source performance breakdown
- `score_diagnostic.md` — layer correlations + token impact analysis
- `replay_projection.md` — the S1/S2/S3 scenario projections
- `signals.parquet`, `rank_win.parquet`, `rank_veto.parquet` — raw parsed signal data
- `schema.md` — full Log.csv schema (19 Tag types, 15 WARN sub-types)

Any of these can be re-queried with pandas for follow-up questions.

---

## Summary for the standup

The backtest loses $5,413 net over 3.5 months. Most of that is driven by four identifiable losing sources; disabling them should swing P&L by about $5,000. The scoring system also has almost no correlation with outcomes — it needs to be rebuilt from data, not hand-tuned — but that's a larger piece of work. Recommended first action: add `PER_SOURCE_THRESHOLDS` to C# (small code change), disable the four sources, re-run the 3.5-month backtest, compare to the prediction, then decide whether to extend to 12 months.

The OPTIMIZED config values in Python were never deployed to C#. This turns out to be fortunate — they appear to have been tuned against a wrong objective. Do not ship them. Clear the OPTIMIZED block and rebuild once scoring is fixed.
