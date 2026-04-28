# Part 4 — Bug-Hunting Sprint (3.5-Month Dataset)

> Find every bug we can before extending the backtest. Five focused audits, ranked by finding probability, producing a concrete C# fix list.

## Sprint strategy

**The question we're answering:** what's broken in the current strategy, and where in the C# code does each issue live?

**Why this order:** Each audit uses data that the previous audits parsed, so the pipeline builds up. The first few audits are almost guaranteed to find issues; the later ones are fishing expeditions that sometimes strike gold.

| # | Audit | Estimated finding probability | What it surfaces |
|---|---|---|---|
| 23 | Exit profile & MFE leak | 95% | Trade-management dollar leaks |
| 24 | Score calibration | 80% | Whether your scoring system is honest |
| 25 | Zone & structure hygiene | 70% | Stale-zone / broken-SR bugs |
| 26 | TA_DECISION audit | 65% | Trade-management reasoning errors |
| 27 | Funnel & orphan detection | 60% | Missing logging, stuck rules |
| 28 | Consolidated bug list | — | The deliverable for your dev team |

## Shared principles (repeated from Part 1–3, in case you forgot)

**Minimum n=50 for any subgroup finding. Minimum n=100 for any claim that should change C# code. Report everything with confidence intervals; flag unstable findings. No subgroup means — always use medians for distributions with long tails (P&L).**

## Universal context block

Same as previous parts. Scripts in `Analysis/scripts/`, outputs in `Analysis/artifacts/`, schema in `Analysis/artifacts/schema.md` (v5.1). Every script prints `[INPUT]`, `[PARSE]`, `[CHECK]`, `[RESULT]`, `[SAVED]`. Execute after writing.

---

## PHASE 23 — Exit profile & MFE leak

**Why first:** highest ROI per hour of work. Uses the entire 795-trade sample without subgrouping into noise-prone cells. Findings are mechanical (arithmetic) not statistical — no p-values to worry about.

**Prompt:**

```
Write Analysis/scripts/23_exit_profile.py.

Uses Analysis/artifacts/schema.md v5.1 for ENTRY_FILL subtype parsing.

For every entry ENTRY_FILL (Label matches signal_id regex), reconstruct
the full trade:

  entry_price         = entry ENTRY_FILL.EntryPrice
  entry_timestamp     = entry ENTRY_FILL.Timestamp
  trade_id            = normalized signal_id from Label
  direction           = Direction column
  source              = look up from matching SIGNAL_ACCEPTED
  score, grade        = same
  stop_original       = SIGNAL_ACCEPTED.StopPrice
  t1_price, t2_price  = same
  stop_ticks          = SIGNAL_ACCEPTED.StopTicks
  contracts           = SIGNAL_ACCEPTED.Contracts

  all_stop_moves      = list of (timestamp, old_stop, new_stop, afterT1)
                        from STOP_MOVE within the trade's time range
  num_stop_moves      = len(all_stop_moves)
  final_stop_price    = last new_stop if any moves, else stop_original

  hit_t1              = T1_HIT exists in trade range (bool)
  t1_timestamp        = first T1_HIT in range or NaN
  t1_exit_price       = T1_HIT.ExitPrice if hit else NaN

  exit_subtype        = from closing ENTRY_FILL:
                          'stop_exit' | 't2_exit' | 'forced_exit' | 'open'
  exit_price          = closing ENTRY_FILL.EntryPrice
  exit_timestamp      = closing ENTRY_FILL.Timestamp
  trade_duration_min  = (exit - entry) in minutes

  # From TRADE_RESET:
  reported_mfe_ticks  = TRADE_RESET.mfe (authoritative MFE)

  # Reconstructed P&L using tick_size from strategy_config.INSTRUMENTS:
  side_sign           = +1 for Long, -1 for Short
  # For trades that hit T1 (partial at T1_PARTIAL_PCT contracts):
  t1_pnl_ticks        = (t1_exit_price - entry_price) * side_sign / tick_size
  runner_pnl_ticks    = (exit_price - entry_price) * side_sign / tick_size
  # Use contract breakdown from T1_HIT(remaining=X) + exit Contracts
  realized_pnl_ticks  = t1_pnl_ticks * partial_contracts
                        + runner_pnl_ticks * remaining_contracts
                        (or just exit_pnl * contracts if no t1)
  realized_pnl_$      = realized_pnl_ticks * tick_size * point_value

Save Analysis/artifacts/exit_profile.parquet with one row per trade.

Then compute these sections for exit_profile_report.md (no subgroup
printed with n < 50):

### SECTION 1 — Overall Expectancy
Total trades, exit mix (% Stop / % T2 / % NoOvernight / % open).
Win rate overall (win = realized_pnl_$ > 0).
Median and mean realized_pnl_$. Std. Profit factor = sum(wins)/|sum(losses)|.
Total cumulative realized P&L.
Annualized: total_pnl / (days_in_backtest / 252).

### SECTION 2 — MFE Leak (the big one)
trades_hit_t1_then_stop = trades where hit_t1=True AND exit_subtype='stop_exit'
For those trades:
  distribution of (reported_mfe_ticks - final_exit_pnl_ticks)
  # This is "how many ticks did we leave on the table?"
  total_dollar_leak = sum of that distribution * tick_value * contracts
Also: trades where mfe >= t2_distance_ticks but they didn't hit T2.
  (Price reached T2 level but exit happened at T1 or runner stop.)

### SECTION 3 — Stop Profile
Among exit_subtype='stop_exit' (n=701):
  % final_stop == stop_original  (stopped at original stop)
  % final_stop moved IN FAVOR (BE+ for Longs / BE- for Shorts)
  % final_stop moved AGAINST (this should be 0; if not = bug)
  Among "stopped at better-than-original":
    avg and median dollar saved vs original stop
  Among BE-arm trades (pre-T1 stop move, usually to near-BE):
    what % ended at the BE-arm stop vs continued to T1 or worse?

### SECTION 4 — Session-End Forced Closes
Among exit_subtype='forced_exit' (n=12):
  per-source count
  % that were profitable at forced-close (realized_pnl > 0)
  avg realized_pnl_$
Flag any source where NoOvernight happens frequently — that source
is missing a time-based exit rule.

### SECTION 5 — Time-to-Stop Distribution
Among exit_subtype='stop_exit':
  histogram of trade_duration_min buckets: <5min, 5-15, 15-60, 1-4h, >4h
  median duration
  If many stops are < 5 minutes, that's "stop hunted" territory — check
  MIN_STOP_TICKS config in strategy_config.

### SECTION 6 — Grade Calibration
Among all 795 trades:
  pivot by grade: n, win_rate, median_pnl, avg_pnl
  Rank A+ → A → B → C. If monotone in win_rate AND avg_pnl:
  "Grading is calibrated."
  Else: "GRADING INVERTED between grade X and grade Y" — flag loudly.

### SECTION 7 — Source Profitability
Per-source (n >= 20, else skip): n, win_rate, avg_pnl, profit_factor,
median duration, exit mix (% Stop/T2/Forced/Open).
Rank by total realized_pnl_$.
Flag any source with total realized_pnl_$ < 0 AND n >= 50 as "LOSING MONEY".
Flag any source with profit_factor < 1.0 AND n >= 50.

### SECTION 8 — Bug Candidates
Automatically detect and print:
- Trades with stop moved AGAINST position (direction-aware): count and
  first 10 with trade_id, old_stop, new_stop, direction.
- Trades spanning multiple sessions without NoOvernight exit: count
  and list.
- Trades where entry_price == stop_original (zero stop distance): bug.
- Trades where hit_t1=True but realized_pnl_ticks < 0 (T1 partial
  and runner gave back all of T1's gain): count and $ impact.
- TRADE_RESET.t1 flag conflicts with hit_t1 in log: count.

Print Section 1 in full, Section 2's total_dollar_leak prominently,
and all of Section 8.
```

**Report back:** Section 1 overall numbers, Section 2's MFE leak dollar figure, all of Section 8 bugs.

**🛑 Decision point:**
- **Section 1 total P&L tells you everything.** If the strategy is losing money over 3.5 months, optimization won't fix that — only C# logic changes will.
- **Section 2 MFE leak $ is your optimization headline.** If it's >$2000, trade management is the #1 fix. If it's <$500, trade management is fine and signal quality is the issue.
- **Section 6 grade calibration** — non-monotonic = bug in scoring or grading that your dev team should find in C#.
- **Section 8 bugs** are direct fix tickets.

---

## PHASE 24 — Score calibration audit

**Why:** If scoring is broken, *every* downstream optimization is broken. This audit is the unit test for your scoring engine.

**Prompt:**

```
Write Analysis/scripts/24_score_calibration.py.

Inputs:
  Analysis/artifacts/signals.parquet  (or generate via EVAL + SIGNAL_*
    pivot from Log.csv if not already parsed)
  Analysis/artifacts/rank_scores.parquet  (RANK_WEAK breakdowns — if
    this doesn't exist yet, parse from WARN rows per schema.md)
  Analysis/artifacts/exit_profile.parquet  (from Phase 23)

Join so each trade has: score, grade, layer_a, layer_b, layer_c, layer_d,
penalty, net_score, mult (if available), and outcome (win bool, pnl_$).

### SECTION 1 — Score → outcome correlation
Spearman ρ between score and:
  - hit_target (win bool)  → expected > +0.10 if scoring is honest
  - realized_pnl_$         → expected > +0.05
  - mfe_ticks              → expected > +0.10
  - mae_ticks              → expected near 0 or negative (higher score
                             shouldn't mean bigger drawdowns)

Report ρ with p-value and n for each. Flag any ρ with WRONG SIGN
explicitly: "score has NEGATIVE correlation with wins — scoring is
actively misleading."

### SECTION 2 — Score decile performance
Bucket trades into score deciles (deciles of score distribution, not
of 0-100 range).
For each decile: n, win_rate, avg_pnl, avg_mfe.
If win_rate is flat or inverted across deciles → scoring doesn't
discriminate. Flag.

### SECTION 3 — Per-layer predictive power
For each of layer_a, layer_b, layer_c, layer_d, penalty:
  Spearman ρ with hit_target (traded subset, n >= 100 required)
  Rank layers by |ρ|.

Compare to current config weight ratio in strategy_config.CONFLUENCE.
Example: if LAYER_A_H4 has current weight 19 but its ρ with hit_target
is 0.01, while LAYER_C has weight 20 total and ρ=0.18 — report:
  "Layer A is overweighted 4× relative to its predictive contribution.
   Layer C is underweighted. See Phase 13b for formal optimization."

### SECTION 4 — Grade boundary check
For each adjacent grade pair (A+ vs A, A vs B, B vs C):
  Mann-Whitney U test on realized_pnl distributions.
  p < 0.05 required for the grades to be "distinguishable."
If A is not distinguishable from B, the grade threshold is noise.

### SECTION 5 — Score-inflation check
Count trades where EVAL.score > 115 (above theoretical cap).
Count trades where SIGNAL_ACCEPTED.score != the score from the
preceding EVAL (if you can match them — if scores mutate between
EVAL and SIGNAL_ACCEPTED, there's a post-scoring boost somewhere).

### SECTION 6 — Penalty effectiveness
Among trades where penalty > 0:
  win_rate, avg_pnl
  vs trades where penalty == 0:
  win_rate, avg_pnl
Penalized trades SHOULD have worse outcomes if penalties are calibrated.
If penalized trades perform EQUAL OR BETTER than un-penalized → the
penalty system is backwards. Huge finding.

Save score_calibration.md + score_calibration.csv.

Print Sections 1, 3, and 6 in full.
```

**Report back:** Section 1 ρ table, Section 3 ranked layers, Section 6 penalty check.

**🛑 Decision point:**
- **Wrong-sign ρ in Section 1** = your scoring is worse than random. This is a drop-everything-and-fix emergency.
- **Section 3 layer imbalance** is the #1 target for Part 2 Phase 13b weight optimization — but only after Phase 23's C# bugs are fixed.
- **Section 6 backwards penalties** = direct C# bug. Check penalty-application order in scoring code.

---

## PHASE 25 — Zone & structure hygiene

**Why:** Stale-zone bugs are the most common category of signal-generation bugs in systems with zone-based logic (OB, FVG). Log already has `ZONE_MITIGATED` + `TOUCH.ZONE_TYPE` — these let us detect the bug precisely.

**Prompt:**

```
Write Analysis/scripts/25_zone_hygiene.py.

Inputs:
  signal_touch.parquet (from Phase 1/18, includes zone_type/lo/hi per signal)
  Analysis/artifacts/zone_lifecycle.parquet (from Phase 18, ZONE_MITIGATED events)
  diagnostics.parquet (from Phase 18, SR zone broken WARN rows)
  exit_profile.parquet (for outcome attachment)

### SECTION 1 — Stale zone signals
A signal "fires on a zone" if signal_touch.zone_type != 'NONE'.
For each such signal:
  Find ZONE_MITIGATED events with overlapping price range (zone_lo to
  zone_hi within 5 ticks of signal's zone range) AND
  mitigation_timestamp < signal_timestamp.
  If found: compute minutes_since_mitigation, direction_alignment.

  stale_zone_signal = True iff any mitigation found AND
                      minutes_since_mitigation < 60 AND
                      mitigation was in OPPOSITE direction to signal
                      (i.e., the zone was invalidated by price moving
                       against the signal's intended direction)

For each source:
  n_zone_signals, n_stale, stale_rate
  win_rate_stale vs win_rate_fresh
  Welch's t-test on pnl_$ between stale and fresh groups (n >= 20 each)
  If p < 0.05 AND stale loses more → bug confirmed

Specific to investigate in C#:
  - SMC/FVGDetector.cs zone mitigation tracking
  - OrderBlockDetector zone invalidation logic

### SECTION 2 — Signals at broken S/R
Parse diagnostics.parquet for warn_subtype == 'SR_ZONE_BROKEN' with
broken price level.

For each SIGNAL_ACCEPTED:
  Current NEAR_SUP / NEAR_RES from the matching STRUCT row same bar.
  Was this SUP or RES broken within the last 10 bars before signal?
  (Cross-reference SR_ZONE_BROKEN events within 10 bars × bar_duration
   with price proximity.)

signal_at_broken_sr = True if yes.

For each source: n_signals, n_at_broken_sr, broken_sr_rate
  win_rate_at_broken_sr vs win_rate_clean
  Welch's t-test (n >= 20 each)
  Flag significant losers.

### SECTION 3 — Cooldown violations
Per source, find consecutive same-direction EVAL rows within
COOLDOWN_<source> bars of each other (from strategy_config.MODULES).
Count violations.

For violations that became SIGNAL_ACCEPTED: compare win rate vs non-
violation accepted signals.

If cooldown violations both happen AND lose more: double bug (cooldown
not enforced + violating signals are noise).

### SECTION 4 — Zone with missing boundaries
Signals with zone_type != 'NONE' but zone_lo == 0.00 or zone_hi == 0.00.
Count per source. Any > 0 is a logger/detector bug — zone detected
but boundaries not populated.

### SECTION 5 — ZONE_MITIGATED but signal fired anyway
For each SIGNAL_ACCEPTED with zone_type != 'NONE', check if there's a
ZONE_MITIGATED event matching the signal's zone within 1 minute BEFORE
the signal. This is the classic "detector didn't check state" bug.

Count per source. Any > 0 is a direct C# bug in that module's candidate
list maintenance.

Save zone_hygiene.md + zone_hygiene.csv.

For each finding, include "Suggested C# investigation" with:
  - File/module name (best guess from source)
  - What to check (e.g., "zone candidate list pruning on mitigation event")
  - Dollar impact if computable

Print all flagged findings prominently.
```

**Report back:** per-source stale-zone rates with win-rate gaps, SR-broken stats, cooldown violations.

**🛑 Decision point:**
- **Any finding with n >= 20 in both groups AND p < 0.05 AND losing-more** = confirmed bug with dollar estimate. Give to dev team directly.
- **Section 5 ZONE_MITIGATED-but-fired = always a bug** regardless of outcome. Fix.

---

## PHASE 26 — TA_DECISION audit

**Why:** You have 3,888 TA_DECISION rows logging every trade-management decision with reasoning. Nobody else has this. Audit whether the decisions actually help.

**Prompt:**

```
Write Analysis/scripts/26_ta_decision_audit.py.

Parse TA_DECISION rows per schema.md v5.1. Associate each with its
trade_id (normalize TA_DECISION.ConditionSetId).

Join with exit_profile.parquet.

### SECTION 1 — Decision-outcome correlation
For each distinct decision (Hold, Tighten, Exit, others):
  Per trade, extract the SEQUENCE of decisions.
  For trades that ended in profit vs loss:
    Did profitable trades have different decision sequences?

Simple metric: per trade, compute fraction of bars where decision='Hold'
vs 'Tighten' vs 'Exit'.
Compare these fractions between winners and losers.
If winners had more 'Tighten' decisions while losers had more 'Hold'
→ the system tightens winners and holds losers (backwards).

### SECTION 2 — Severity threshold check
Each TA_DECISION has kvp `sev` and `thr` (threshold pair).
When sev exceeded thr (either inner or outer), what decision was
taken? What was the outcome?

If trades where sev was frequently above outer thr still ended at
full stop → thresholds are too high (decisions aren't firing early
enough).

If trades where a 'Tighten' decision fired later stopped at BE → the
tightening saved money. If they stopped pre-tighten distance → the
tightening was wasted.

### SECTION 3 — Action-result alignment
For each TA_DECISION with act='Tighten', find the next STOP_MOVE within
2 bars of the decision.
If none: the 'Tighten' decision was LOGGED but not EXECUTED. Bug.
Count cases per ta_family.

For each TA_DECISION with act='Exit', find the next exit ENTRY_FILL
within 2 bars. If none within 2 bars, but trade continued: the 'Exit'
decision was logged but not executed. Bug.

### SECTION 4 — Stage / persistence patterns
TA_DECISION payload has `stage` and `pers` (persistence) fields.
Bucket trades by max stage reached and max pers during trade.
Do high-stage trades have different outcomes than low-stage?

### SECTION 5 — CVD slope confirmation
TA_DECISION includes `slope` (CVD slope) and `slopeScore`.
Among trades where slope flipped sign during trade (CVD reversed):
  Did the system react?
  If slope went against position for >3 bars without a Tighten/Exit
  decision → the TA reasoning engine missed a reversal signal.

### SECTION 6 — Decision volume per trade
Distribution of num_ta_decisions per trade.
Trades with 0 decisions = short trades that stopped out before TA
evaluated (fine).
Trades with >20 decisions = held through lots of bars.
  - If those are mostly losers, the system held too long.
  - If mostly winners, good (let runners run).

Save ta_decision_audit.md + ta_decision_audit.csv.

Print Section 1 (backwards pattern?), Section 3 (action-result gap),
Section 5 (missed reversals).
```

**Report back:** decision-outcome pattern from Section 1, action-result gap from Section 3.

**🛑 Decision point:**
- **Section 3 action-result gap > 5% = bug.** TA engine is logging decisions that don't execute. Either the decision code path doesn't wire to the order manager, or there's a race condition. Direct C# fix.
- **Section 1 backwards pattern** = the TA family's rules are miscalibrated. Needs rule review, not code fix.

---

## PHASE 27 — Funnel & orphan detection

**Why:** Finds *missing* logging, silent filter stages, and state-tracking bugs. Lower-probability hits but each one uncovers observability gaps that prevent future analysis.

**Prompt:**

```
Write Analysis/scripts/27_funnel_orphan_audit.py.

This is a pure data-integrity audit. Find rows that shouldn't exist
and expected rows that don't.

### SECTION 1 — Funnel counts
For each source, compute the funnel:
  n_evals → n_rank_weak → n_rank_passed → n_rejected → n_accepted
  → n_ordered → n_filled → n_resolved (has TRADE_RESET)

Expected invariants:
  n_evals >= n_rank_passed (rank filter only subtracts)
  n_rank_passed == n_rejected + n_accepted
  n_accepted == n_ordered   (every accepted gets an order)
  n_filled <= n_ordered     (not every order fills)
  n_resolved <= n_filled    (open positions at end reduce this)

Flag sources where any invariant breaks.

### SECTION 2 — Orphans (by type)
- EVAL rows with no RANK_WEAK, no SIGNAL_ACCEPTED, no SIGNAL_REJECTED
  following them. Count per source.
  Interpretation: silent filter stage with no logging.
- SIGNAL_ACCEPTED rows with no ORDER_LMT.
  Interpretation: signal accepted but order never placed.
- ORDER_LMT with no ENTRY_FILL AND no cancellation log event.
  Interpretation: either fill lost, or cancellation not logged.
- T1_HIT or T2_HIT not belonging to any ENTRY_FILL→TRADE_RESET range.
  Interpretation: trade tracker bug.
- STOP_MOVE not belonging to any ENTRY_FILL→TRADE_RESET range.
  Interpretation: stray stop move or tracking bug.
- TRADE_RESET without a matching entry ENTRY_FILL.
  Interpretation: trade closed without opening?
- TA_DECISION with trade_id that doesn't match any ENTRY_FILL.
  Interpretation: TA reasoning for a trade that was never opened.

### SECTION 3 — Timestamp sanity
- Events with timestamps in the future (after backtest end).
- Events with timestamps before session OPEN for that session.
- TRADE_RESET.Timestamp < ENTRY_FILL.Timestamp (reset before entry).
- Events with timestamp=0001-01-01 that weren't WARN or ZONE_MITIGATED.

### SECTION 4 — Score consistency
For every trade where you can trace EVAL → SIGNAL_ACCEPTED:
  Did the score change between EVAL and SIGNAL_ACCEPTED?
If yes for a signal: that's score-boosting between stages, probably
legitimate if logged but worth knowing.

### SECTION 5 — RANK_WEAK Net = A+B+C+D−Pen
For every RANK_WEAK row:
  Compute A + B + C + D − Pen and compare to logged Net.
  Count mismatches.
If mismatches > 0 → the score breakdown is inconsistent and Phase 13b
weight optimization cannot be trusted.

### SECTION 6 — Same bar, duplicate accepts
Count cases where multiple SIGNAL_ACCEPTED rows fire on the same Bar
for same Source but different ConditionSetId. This is legitimate
confluence but should be tracked — are there duplicate trades in your
P&L?

Save funnel_orphan_audit.md.

Every finding includes "Logging gap to close" with a suggested C# file.

Print all non-zero orphan counts prominently.
```

**Report back:** orphan counts by type, funnel invariant breaks, score consistency check.

**🛑 Decision point:**
- **Section 5 Net mismatch > 0** blocks Phase 13b weight optimization. Must fix in `StrategyLogger.cs` before attempting full weight tuning.
- **Section 2 EVAL orphans > 1% of EVALs** = silent filter stage. Add logging.
- **Section 2 STOP_MOVE orphans > 0** = the stop management is tracking state incorrectly.

---

## PHASE 28 — Consolidated bug list

**Why:** Every previous phase produced findings. This phase aggregates them into one prioritized list to hand to your dev team.

**Prompt:**

```
Write Analysis/scripts/28_bug_list.py.

Read every report from Phases 23-27. Parse out flagged findings.

Produce Analysis/artifacts/bug_list.md as a single prioritized document.

For each bug, produce a GitHub-issue-style entry:

  ## [bug-001] [AREA] Short title
  
  **Severity:** CRITICAL | HIGH | MEDIUM | LOW
  **Found by:** Phase 2X section Y
  **Area:** signal generation | scoring | trade management | logging
  
  **Evidence:**
    Specific numbers from the audit. E.g., "127 signals fired at
    zones mitigated >30min prior, 42% win rate vs 65% baseline."
  
  **Dollar impact (if computable):** $X over backtest period
  
  **Hypothesis:**
    One-paragraph theory of what's broken.
  
  **Suggested investigation:**
    File or module to check. What to look at specifically.
  
  **Test after fix:**
    Re-run Phase 2X — this finding should disappear.

Severity rubric:
  CRITICAL: breaks core invariant (net score mismatch, TRADE_RESET
            before entry, missing core data) OR > $2000 impact
  HIGH:     significant finding, > $500 impact, OR clear C# logic bug
  MEDIUM:   findings with p<0.05 but smaller $ impact
  LOW:      observability gaps, small leaks, style issues

Ranking:
  Sort CRITICAL first, then by dollar impact descending within severity.

At the top of bug_list.md, include:

### TL;DR
The top 3 bugs, one-line each, with $ impact.

### Health scorecard (one-liner each)
- Strategy expectancy over 3.5mo: $X (profit/loss)
- Grade calibration: CALIBRATED | INVERTED | NOISY
- Score predictive power: STRONG | WEAK | BROKEN
- Trade management MFE leak: $X
- Stale zone signals: X% of zone-based signals
- Logging completeness: X% of events covered by documented tags

### Bugs requiring backtest re-run after fix
List of bugs where the fix changes which signals fire or how they're
sized. Fixing these requires re-running the NT backtest before Phase
23-27 can be rerun.

### Bugs NOT requiring backtest re-run
Logging improvements and analysis-only fixes.

### Out of scope
Things flagged but can't be diagnosed without more data (2+ years,
or missing log fields).

Print TL;DR, health scorecard, and CRITICAL+HIGH bugs.
```

**Report back:** the TL;DR, health scorecard, and CRITICAL/HIGH bug list.

**🛑 Decision point:** This is the deliverable. Each CRITICAL/HIGH bug is a C# ticket for your dev team.

---

## After Phase 28 — what to do with the bug list

**For each bug in the list, your workflow is:**

1. Dev team picks up the bug ticket from `bug_list.md`
2. Fixes it in C#
3. Re-runs the backtest (same 3.5-month period for speed)
4. You re-run Phases 23-27 targeting that specific finding
5. Confirm finding has disappeared
6. Move to next bug

**Fix them sequentially, not in parallel.** Batch fixes mean you can't tell which one fixed which metric. Sequential means you learn what each fix actually does.

**When the bug list is clean** (or residual bugs are "known limitations" you're OK with):

1. Extend backtest to 12-18 months
2. Re-run Phase 23-27 quickly to check nothing new broke at longer horizon
3. Proceed to Part 2 optimization with a trustworthy strategy

## Pitfalls for this sprint

| Trap | How it bites | Defense |
|---|---|---|
| Fixing multiple bugs at once | Can't tell which fix helped which metric | Sequential fixes, one at a time |
| Fixing a "bug" that's actually intentional | Wastes dev time, may break strategy | Every CRITICAL/HIGH bug gets a human review of the hypothesis before coding |
| False positives from n=25 subgroups | Chasing noise | All prompts enforce n>=50 for claims, n>=100 for code changes |
| Fixing symptoms not causes | Bug comes back in different form | Each bug entry includes "test after fix" — confirm finding disappears |
| Phase 23 catches nothing because the logger itself is broken | Garbage in garbage out | Phase 27 validates logger integrity first; if Section 5 fails, fix logging before trusting Phase 23 |

## Timeline estimate

Five audits, one bug-list compilation. If Gemini produces each script correctly on the first or second try:
- Phase 23: 1-2 hours to write + run
- Phase 24: 1 hour
- Phase 25: 1-2 hours
- Phase 26: 1-2 hours
- Phase 27: 1 hour
- Phase 28: 30 minutes

Full sprint: one productive day. Then dev cycles on each bug; probably 2-4 weeks total elapsed for fixes.

## Workflow reminders

- **Phase 23 first. Its MFE-leak dollar figure tells you whether the rest of the sprint is worth doing.** If Phase 23 shows the strategy is losing money systematically via mechanical issues, those fixes matter more than any statistical finding from 24-27.
- **Phase 27 is the "can I trust the data?" audit.** Run it before fully trusting any other finding. If score breakdowns are inconsistent (Section 5), those findings are suspect.
- **Don't extend the backtest until this sprint is done.** You committed to finding bugs on 3.5 months, and that commitment still holds.
