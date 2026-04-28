# Part 3 — Signal Generation Diagnostic

> We can't optimize your C# signal-generation code from Python, but we can produce a **ranked punchlist** of what's most likely broken, backed by data, so your dev team knows exactly where to look.

## Prerequisites & context

- Part 1 (v4) Phases 0–10 complete.
- The log sample reveals new row types not in v4 schema — this document patches them in and builds on top.
- The output is one file: `signal_generation_notes.md` — a prioritized list of candidate C# fixes with evidence.

## Schema additions (patch these into `schema.md` before running anything)

**Four more Tag types in Log.csv:**

| Tag | When | Notes |
|---|---|---|
| `EVAL` | Every candidate signal evaluation | Score computed but not yet gated; Detail = context string |
| `WARN` | Diagnostic events | Heterogeneous; sub-type in Detail (RANK_WEAK / SR zone broken / ORB_VP_DIAG / …) |
| `SESSION` | Session open/close markers | Detail: `OPEN VWAP=<f> ATR=<f>` or `CLOSE …` |
| `ZONE_MITIGATED` | Zone invalidated | Label=BULL/BEAR, Detail: `close=<f> zone=<lo>-<hi>` |

**Parsing specs:**

```
EVAL Detail (same as SIGNAL_* context string):
  h4=[+|-|0] h2=[+|-|0] h1=[+|-|0] smf=[bull|bear|flat|neutral]
  str=<float> sw=<int>

WARN Detail variants:
  RANK_WEAK [<module_id>] A=<int> B=<int> C=<int> D=<int>
                          Pen=<int> Net=<int> Mult=<float>
  SR zone broken at <price> [<source>] close=<price>
  ORB_VP_DIAG: bar accumulated levels=<int> vol=<int> time=<timestamp>
  (other WARN subtypes exist; parse best-effort, fall into 'unknown' bucket)

SESSION Detail:
  OPEN VWAP=<float> ATR=<float>
  CLOSE VWAP=<float> ATR=<float>

ZONE_MITIGATED:
  Direction column: Long or Short
  Label column: BULL or BEAR
  Detail: close=<float> zone=<lo>-<hi>

Updated FLOW Detail (now includes trade-signal suffix):
  REGIME=<int> STR=<f> | BD=<i> CD=<i> DSL=<i> DSH=<i>
  | DEX=<int> BDIV=<0|1> BERDIV=<0|1>
  | ABS=<f> SBULL=<i> SBEAR=<i>
  | IZB=<0|1> IZS=<0|1>
  | HASVOL=<0|1> SW=<i>
  | TRD=<int> CH_L=<0|1> CH_S=<0|1> BOS_L=<0|1> BOS_S=<0|1>

Updated STRUCT Detail (now includes H1 swings):
  POC=<f> VAH=<f> VAL=<f>
  | NEAR_SUP=<f>(<f>t) NEAR_RES=<f>(<f>t)
  | H4=<high>/<low> H1=<high>/<low>
  | PP=<f> SKEW=<f>

Quirks to handle:
  - Some WARN and ZONE_MITIGATED rows carry timestamp "0001-01-01 00:00:00"
    (a .NET default). Treat as "occurred near surrounding valid rows" —
    during parsing, carry-forward the last valid timestamp.
  - NEAR_SUP=<price>(0.0t) means "price is AT support" (distance = 0).
    Do not filter these out as invalid.
  - POC=0.00 VAH=0.00 VAL=0.00 means value-area computation hadn't run yet
    for that session. Treat as NaN, not zero.
```

## The signal funnel we're diagnosing

```
    EVAL (N candidates)
      │
      ├─► RANK_WEAK → silently dropped      (biggest filter, usually)
      │
      ├─► SIGNAL_REJECTED → gate vetoed     (visible filter)
      │
      └─► SIGNAL_ACCEPTED → traded          (what Part 1 analyzed)
                 │
                 └─► TOUCH_OUTCOME → hit/stop
```

Part 1 looked at the bottom two stages. Part 3 looks at the full funnel — where candidates die, and whether they die for the right reasons.

---

## Universal context block

Same as Parts 1 & 2. All scripts read schema.md, import strategy_config, follow the `[INPUT]/[PARSE]/[CHECK]/[RESULT]/[SAVED]` print convention, and execute after writing.

---

## PHASE 18 — Parse signal-generation events

Extends Phase 1's parsed tables with the new Tag types.

**Prompt:**

```
Write Analysis/scripts/18_parse_signal_gen_events.py.

Prerequisite: schema.md has been updated with the additions documented
in Part 3 header. If it hasn't, stop and tell me to do it first.

Reads backtest/Log.csv.

Produces FIVE new parquet tables:

A. evals.parquet  (one row per EVAL Tag)
   Columns:
     eval_id (constructed: {ConditionSetId}:{yyyymmdd_hhmm}:{Bar}),
     timestamp, bar, direction, source, condition_set_id,
     score, entry_price, stop_price, t1_price, t2_price, label,
     # parsed context:
     ctx_h4, ctx_h2, ctx_h1, ctx_smf, ctx_str, ctx_sw

   NOTE: eval_id uses the SAME format as signal_id from Phase 1, so
   an EVAL followed by a SIGNAL_ACCEPTED or SIGNAL_REJECTED at the
   same (bar, source, condition_set_id) will share the ID — this is
   your join key.

B. rank_scores.parquet  (one row per RANK_WEAK WARN)
   Columns:
     eval_id (constructed as above, matched to the preceding EVAL),
     timestamp, bar, condition_set_id,
     layer_a, layer_b, layer_c, layer_d, penalty, net_score, mult

   Join strategy: RANK_WEAK WARN rows appear IMMEDIATELY after their
   EVAL row(s). Parse module_id from "[...]" in Detail, match back to
   EVALs on (bar, condition_set_id).
   Print join success rate — MUST be >98%.

C. sessions.parquet  (one row per SESSION event)
   Columns:
     timestamp, session_type ∈ {OPEN, CLOSE},
     vwap_at_boundary, atr_at_boundary

   Then ADD session_date and session_id columns so every timestamp
   in the log can be mapped to a session. Save session_id lookup as
   part of the parquet.

D. zone_lifecycle.parquet  (one row per ZONE_MITIGATED)
   Columns:
     timestamp (carry-forward if 0001-01-01),
     direction, zone_side (BULL/BEAR), close_price,
     zone_lo, zone_hi, zone_width

E. diagnostics.parquet  (all OTHER WARN rows)
   Columns:
     timestamp (carry-forward if 0001-01-01),
     bar, warn_subtype, detail_raw, parsed_fields (dict-as-json)

   Recognize subtypes: SR_ZONE_BROKEN, ORB_VP_DIAG, others.
   Unrecognized → warn_subtype='unknown', store raw Detail.

Emit a parsing-stats summary:
  - total WARN rows, split by subtype
  - RANK_WEAK join success rate
  - EVAL rows without a matching SIGNAL_* or RANK_WEAK (unexplained
    drops — should be near zero)
  - Session coverage (% of log timestamps inside a known session)

Print all five table shapes + head(5) each.
```

**Report back:** five table shapes, join success, WARN subtype distribution, unexplained-drop count.

**🛑 Decision point:**
- **RANK_WEAK join < 98%** = parsing bug. The `[module_id]` regex probably misses some variants. Print 20 unmatched Details and fix.
- **Unexplained EVAL drops > 1%** = there's a filter stage in your C# code that doesn't emit either SIGNAL_* or RANK_WEAK. That itself is a finding — add logging for the missing stage.
- **Session coverage < 98%** = some of your log lies between a CLOSE and the next OPEN. Either the logger missed a session boundary or you're running during unusual hours.

---

## PHASE 19 — Funnel analysis & layer health

The two most valuable diagnostic views, combined because they share the same data.

**Prompt:**

```
Write Analysis/scripts/19_funnel_layer_health.py.

Inputs:
  evals.parquet, rank_scores.parquet, signals.parquet (from Phase 1),
  feature_matrix.parquet, zone_lifecycle.parquet

PART A — Funnel by source
For each (source, condition_set_id), compute:
  n_evaluated         total EVALs
  n_rank_weak         evaluated but RANK_WEAK filtered
  n_rank_passed       evaluated and made it past RANK filter (= n_rejected + n_accepted + n_other)
  n_rejected          gate rejected (SIGNAL_REJECTED)
  n_accepted          SIGNAL_ACCEPTED
  n_outcome_known     has a TOUCH_OUTCOME (traded or :REJ)
  n_hit_target        target hit in outcome
  n_hit_stop          stop hit in outcome

Derived rates:
  rank_pass_rate      = n_rank_passed / n_evaluated
  gate_pass_rate      = n_accepted / n_rank_passed
  win_rate            = n_hit_target / n_outcome_known

Save funnel_by_source.csv.

Flag in funnel_by_source.md:
  - rank_pass_rate < 15%  (most candidates die at rank floor — is the
    floor calibrated correctly?)
  - gate_pass_rate < 30%  (rank-passed signals are still mostly gated —
    redundant filtering?)
  - n_evaluated < 20      (too few data points to trust anything)
  - rank_pass_rate × gate_pass_rate × win_rate < 0.02  (overall yield
    so low the source probably shouldn't exist)

PART B — Layer health per source
For each (source, condition_set_id), compute across ALL evals
(rank_weak rows joined by eval_id):
  layer_a_mean, layer_a_std, layer_a_zero_rate
  layer_b_mean, layer_b_std, layer_b_zero_rate
  layer_c_mean, layer_c_std, layer_c_zero_rate
  layer_d_mean, layer_d_std, layer_d_zero_rate
  penalty_mean, penalty_max

Flag:
  - layer_X_zero_rate > 70%  "Layer X is dead — contributes nothing to
    Y% of this source's candidates"
  - layer_X_std / max(1, layer_X_mean) < 0.2 "Layer X is constant —
    not informative"

PART C — Predictive power of each layer
For the subset of EVALs that actually got outcomes (either traded or
:REJ counterfactual):
  For each source, compute Spearman ρ between each layer contribution
  (A, B, C, D, Pen, Net) and the outcome (hit_target bool).
  Requires joining rank_scores to outcomes via eval_id/signal_id.

Save layer_health.csv.

Produce funnel_and_layers.md with:
  - Part A funnel table + flags
  - Part B layer-zero heatmap (source × layer)
  - Part C predictive-power table (source × layer)

Print Part A in full; print top 5 dead-layer flags from Part B;
print top 5 most-predictive-layer findings from Part C.
```

**Report back:** funnel table, dead-layer flags, most-predictive-layer findings.

**🛑 Decision point:**
- **Dead layer for a source = that layer's input data isn't reaching that source's scoring, OR the source shouldn't depend on it.** Check the C# module — is it referencing the right indicator? Is the lookback long enough?
- **High predictive ρ for a layer that currently has LOW weight** = underweighted signal, worth boosting.
- **Low/negative ρ for a layer that currently has HIGH weight** = overweighted noise. Most common finding in this category is Layer A (MTFA) — traders weight it heavily by intuition but it often has weak predictive power relative to its weight.

---

## PHASE 20 — Zone & structural hygiene

Is your strategy firing signals at zones that have already been broken, or at broken structure?

**Prompt:**

```
Write Analysis/scripts/20_zone_hygiene.py.

Inputs:
  evals.parquet, zone_lifecycle.parquet, diagnostics.parquet,
  signals.parquet, feature_matrix.parquet, signal_touch.parquet

PART A — Stale zone signals
A signal fires "on a zone" if the TOUCH row reports ZONE_TYPE != 'NONE'
(from Phase 1 signal_touch.parquet).

For each such signal, check whether that zone was mitigated BEFORE
the signal. Cross-reference:
  - signal's zone_lo / zone_hi (from signal_touch)
  - zone_lifecycle rows with overlapping price range and timestamp <
    signal's timestamp

If matched, compute:
  seconds_since_mitigation   = signal_timestamp - mitigation_timestamp
  signal_direction_aligned   = did the signal go WITH the mitigation
                               direction or against it?

Tag signals as:
  stale_zone_signal = True if seconds_since_mitigation < 3600 AND
                      signal_direction_aligned_with_mitigation is False

Save stale_zone_signals.csv.

Flag:
  - stale_zone_rate by source
  - if stale signals win SIGNIFICANTLY less than fresh signals
    (Welch's t-test p < 0.05), the zone-freshness check is missing
    in that source's C# logic

PART B — Signals at broken S/R
Parse diagnostics.parquet for warn_subtype == 'SR_ZONE_BROKEN'. Each
such row invalidates the pivot S/R level.

For every SIGNAL_ACCEPTED, check: was the current NEAR_SUP or
NEAR_RES (from signal_touch.parquet or struct_bars.parquet) broken
within the last N bars (default 10)?

If yes:
  signal_at_broken_sr = True

Compare win_rate of signal_at_broken_sr=True vs False, per source.

PART C — Consecutive/repeated signal firing
For each source, find cases where the same source fires on CONSECUTIVE
bars with the same direction. This is a hygiene flag — the cooldown
logic (COOLDOWN_SMC, COOLDOWN_EMA, etc.) may not be doing its job.

Compute:
  For each source: count of same-direction back-to-back firings
  in the EVAL stream.

If > 5% of that source's EVALs are "consecutive repeats", flag.

Save hygiene_summary.md with:
  - Part A flagged sources + win-rate gap
  - Part B flagged sources + broken-SR rate
  - Part C sources with broken cooldown logic
  - For each flag: a suggested C# area to check (e.g., "check
    SMC_FVG zone mitigation tracking — zones mitigated >5min ago are
    still being traded")

Print all flags.
```

**Report back:** per-source stale-zone rates, broken-SR incidence, cooldown violations.

**🛑 Decision point:**
- **Stale-zone signals winning LESS than fresh ones, with p<0.05** = direct bug; zone tracking has a hole. This is usually one of the highest-$-impact findings.
- **Broken-SR signals winning LESS** = the S/R integration isn't invalidating levels after break. Tells you exactly where to look in C#.
- **Cooldown violations** = cooldown bar count isn't being applied to that source. Check `SIGNAL_COOLDOWN_*` is actually referenced where you think it is.

---

## PHASE 21 — Data quality audit

Surface bugs visible in the data itself before they silently corrupt later analyses.

**Prompt:**

```
Write Analysis/scripts/21_data_quality_audit.py.

Inputs: every parsed parquet from Phases 1 + 18.

Checks to run (each a named function; run all, collect results):

1. timestamp_zero_rows
   Count rows with timestamp == 0001-01-01. Split by Tag.
   Expected: WARN and ZONE_MITIGATED only; anything else = bug.

2. session_boundary_gaps
   Sessions.parquet should have alternating OPEN/CLOSE. Find:
     - consecutive OPENs (close missing)
     - consecutive CLOSEs (open missing)
     - sessions with no accepted signals (dead session — is logger running?)

3. zero_valued_structure
   STRUCT rows with POC=0 AND VAH=0 AND VAL=0 occur early in sessions
   before value-area accumulation. Should disappear within N bars of
   SESSION_OPEN. Flag sessions where it persists > 30 bars.

4. orphan_eval_count
   EVALs without RANK_WEAK or SIGNAL_* follow-up. Group by source.

5. dual_direction_same_bar
   Multiple EVALs at SAME (bar, source) with DIFFERENT directions.
   This is only sensible for confluence-detection modules; flag
   single-direction sources that produce both longs and shorts on
   same bar.

6. score_above_cap
   EVAL Score > theoretical max (sum of all layer caps: 30+40+30+15 = 115
   under current config). Anything over that is a logger bug.

7. net_score_mismatch
   For RANK_WEAK rows: does Net == (A + B + C + D - Pen)?
   Count mismatches. Any mismatch = logger bug in the score breakdown.

8. near_sr_sanity
   STRUCT NEAR_SUP_ticks == NEAR_RES_ticks (both zero) means price
   is simultaneously at support and resistance — impossible unless
   both levels are identical. Check if this happens a lot; if so,
   your S/R extraction is emitting duplicates.

9. flow_struct_bar_alignment
   For each FLOW row, is there a STRUCT row at the same bar? If large
   gap (>5% of bars missing a pair), one of the loggers drops rows
   under some condition.

10. outcome_without_signal
    TOUCH_OUTCOME rows whose signal_id doesn't match any signal in
    signals.parquet (neither accepted nor rejected).

Save Analysis/artifacts/data_quality_audit.md with each check's
result: PASS / WARN / FAIL with counts and first 5 violating rows.

Print the summary table. Any FAIL = must-fix in logger code.
```

**Report back:** the summary table + count of FAILs.

**🛑 Decision point:**
- **net_score_mismatch > 0 is a hard FAIL.** Your score breakdown is inconsistent — don't trust it for Part 2 weight optimization until fixed.
- **dual_direction_same_bar for single-direction sources** means your module is evaluating both directions and the logger picks one — likely indicates a C# evaluation-order issue.
- **orphan_eval_count > 0** means a filter stage in your C# code doesn't emit any log line — add logging to that stage or the funnel is lying.

---

## PHASE 22 — Synthesize the improvement notes

The deliverable. Aggregates findings from 19–21 and ranks by estimated impact.

**Prompt:**

```
Write Analysis/scripts/22_signal_gen_notes.py.

Reads every artifact from 19, 20, 21.

For each finding, compute an impact estimate where possible:
  - Dead layer: $ impact estimated as 0 (informational only)
  - Underweighted predictive layer: $ potential = (abs_rho × n_evals ×
    avg_stop_dollars) — signals this layer could correctly promote
  - Stale-zone signals: $ impact = sum of sim_pnl on stale-zone signals
    (negative = leak)
  - Broken-SR signals: same $ calculation on sim_pnl
  - Cooldown violations: $ impact = sum of sim_pnl on repeat firings
  - Data quality FAILs: tagged CRITICAL regardless of $ impact

Produce Analysis/artifacts/signal_generation_notes.md with:

### TL;DR
One paragraph: the single most likely bug, the $ leak it causes,
and the line of C# to investigate.

### Critical (must-fix before trusting further analysis)
All data quality FAILs with specific remediation notes.

### High-impact candidates (ranked by $)
For each finding, as a mini-GitHub-issue:

  ## [issue-001] Stale OB zones still tradable in SMC module
  **Evidence:** 127 SMC_OrderBlock signals fired at zones mitigated
  > 30 minutes prior. Win rate on these = 31%. Fresh-zone win rate
  for same source = 62%. Spearman of staleness vs hit_target:
  −0.24 (p < 0.001).
  **Estimated $ leak:** -$3,420 over backtest period.
  **Hypothesis:** Zone mitigation tracker in ORB/SMC module doesn't
  remove zones from the candidate list.
  **Suggested investigation:** `ModularStrategy/SMC/FVGDetector.cs`
  around zone-mitigation update. Does the mitigation event fire but
  the zone object persist in the candidate list?
  **Risk of fix:** low — adding an exclusion shouldn't reduce win
  rate of fresh signals.

Number them issue-001, issue-002, etc. Rank by |$ impact| descending.

### Tuning candidates (no bug, but weights could move)
Layers with high predictive ρ but low current weight, or vice versa.
Suggest specific weight changes (based on the ρ ratio) and note that
these go into Part 2 Phase 13b for formal optimization.

### Logging improvements (make future analysis easier)
Suggest C# logger changes identified during Phase 18–21 parsing:
orphan events, missing boundaries, etc.

### Out of scope for this analysis
Things we noticed but can't diagnose from logs alone. For each,
state what data would be needed.

Print the full notes.
```

**Report back:** the full `signal_generation_notes.md`.

**🛑 Decision point:** This goes straight to your dev team. Read the TL;DR first. If the single top finding doesn't sound plausible when you read it, the analysis has a bug somewhere — work back through Phases 19–21 to find where before handing off.

---

## What changes in Parts 1 and 2 after this

### Part 1 Phase 0 (patch)
Add EVAL, WARN, SESSION, ZONE_MITIGATED to the known Tag list. Expand FLOW Detail regex to accept the trailing `TRD/CH_L/CH_S/BOS_L/BOS_S` group. Expand STRUCT Detail regex to accept H1 swings. Re-run Phase 0 — validation should still PASS.

### Part 1 Phase 1 (optional extension)
v4's Phase 1 ignored EVAL rows because they weren't in the schema. You don't have to re-run it — Phase 18 parses EVALs into a separate table. But if you *do* re-run Phase 1, nothing breaks — EVALs just don't match any of its four target tables.

### Part 2 Phase 13b (now active)
**`rank_scores.parquet` gives you exactly the score breakdown I earlier said would require a C# change.** Phase 13b becomes:

```
Write Analysis/scripts/13b_optimize_weights.py.

Parameter space now includes:
  LAYER_A_H4, LAYER_A_H2, LAYER_A_H1,
  PENALTY_H4, PENALTY_H2, PENALTY_BOTH_EXTRA,
  LAYER_B_MAX_CAP, LAYER_C_MAX_CAP,
  PROXY_BAR_DELTA, PROXY_REGIME, PROXY_VWAP_SIDE, PROXY_H1_BAR_DIR,
  LAYER_C_DIVERGENCE, LAYER_C_ABS_MAX,
  LAYER_D_FULL_STRUCT, LAYER_D_TREND_ONLY

Replay score by:
  new_layer_a = weight_for(ctx_h4, direction) * LAYER_A_H4_new
              + weight_for(ctx_h2, direction) * LAYER_A_H2_new
              + weight_for(ctx_h1, direction) * LAYER_A_H1_new
  new_penalty = as per penalty rules applied to ctx_*
  # Layer B, C, D can only be RESCALED because we only have their
  # already-computed sum from rank_scores.parquet. So treat them as:
  new_layer_b = min(current_layer_b * (new_cap / old_cap), new_cap)
  # (only MAX_CAP is tunable; component weights inside B require
  # even finer logging)

Same walk-forward protocol as Phase 13. Same guardrails.
Same objective. Save layer_weights_proposal.json.
```

Layer A weight optimization is clean; Layer B/C/D becomes *cap* optimization only — still useful, not as precise as full component-weight tuning. To get full weight optimization of B/C/D, add per-component logging inside RANK_WEAK Detail (another small C# change).

---

## Pitfalls specific to Part 3

| Trap | How it bites | Defense |
|---|---|---|
| 0001-01-01 timestamps skew ordering | Sorted data puts these first, scrambling sequences | Carry-forward the last valid timestamp during parse; never sort raw Log.csv by timestamp alone |
| RANK_WEAK joined to wrong EVAL | When multiple modules fire at same bar, the `[module_id]` match is the only disambiguation | Always match on (bar, condition_set_id), never bar alone; assert 1:1 |
| Zone mitigation inferred as a "broken signal" when it's actually the SYSTEM correctly invalidating | Every mitigated zone looks like a bug | Only flag as bug when SIGNAL fires AFTER mitigation; pre-mitigation signals are fine |
| Confluence-source duplicates | Some sources legitimately evaluate both directions and pick one; looks like "dual_direction_same_bar" bug | Whitelist these sources before flagging |
| Weight optimization overfits layer A | Layer A has few discrete states (+1/0/-1 × 3 timeframes = 27 combos); easy to overfit | Use narrower bounds in Optuna (±30% of current weight); keep walk-forward strict |

## Workflow reminders

- **The improvement notes are not a fix list; they're a hypothesis list.** Every issue should read as "we think X is broken because Y; please check Z." The dev should verify the diagnosis before coding a fix.
- **Don't fix everything at once.** Address the top-1 finding, re-run Part 1 Phases 0–10, see how it changed. If the metrics improved as expected, the diagnosis was right. If not, the diagnosis was wrong or incomplete. Sequential verification beats batch fixes.
- **Re-running Phase 18 after C# fixes is the acceptance test** — same log format, different data, see whether the flagged issues disappeared.
