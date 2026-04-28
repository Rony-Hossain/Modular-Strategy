# Part 2 — Optimization, Stress Testing & Trade Management

> Prerequisite: Part 1 (v4) Phases 0–10 complete. You have `feature_matrix.parquet`, full tick data in `backtest/TicData.csv`, and `Analysis/strategy_config.py` as the authoritative config.

## What this workflow produces

- `config_proposal.json` — optimized parameter set, ready to copy into `OPTIMIZED` block of `strategy_config.py`
- `stress_report.md` — Monte Carlo + choppy-day simulation showing max drawdown and P(ruin) distributions for both current and optimized configs
- `trade_mgmt_diagnostic.md` — specific issues found in your current order/exit logic, with tick-level evidence
- `trade_mgmt_proposal.json` — optimized trade management parameters

## Core insight: we replay, we don't re-backtest

NinjaTrader re-runs are slow and gate iteration speed. But we can replay everything in Python cheaply because:

- **Acceptance decisions** (should this signal trade?) reduce to: `score >= threshold` AND `not vetoed` AND `passes floors`. All inputs are logged. Replay = pure pandas ops.
- **Trade outcomes** under new management params can be re-simulated from tick data. Given entry + new stop/target/BE/MFE-lock rules, walk the ticks, decide exits. ~ms per trade.
- **Score *computation*** with new weights requires either per-layer contribution logging OR reconstructing the formula. We start *without* this and optimize thresholds + management only. Weight tuning is Phase 13b (optional).

This means 10,000 optimizer trials are minutes, not days.

## Workflow

```
Phase 11 → Replay harness                  (build the engine)
Phase 12 → Baseline validation              (prove replay matches reality)
Phase 13 → Threshold & policy optimization  (walk-forward Bayesian)
Phase 14 → Monte Carlo stress tests         (survive choppy days)
Phase 15 → Trade management diagnostic      (what's broken today)
Phase 16 → Trade management optimization    (tick-level simulation)
```

Phases 13b and 17 are *optional advanced* — only attempt after per-layer logging is added to `StrategyLogger.cs`.

## Universal context block (paste at top of every Part 2 prompt)

```
CONTEXT (Part 2: Optimization & Validation)
Repo root: Ninjatrader-Modular-Startegy/
  ├── Analysis/scripts/               ← your script here
  ├── Analysis/artifacts/             ← all outputs
  ├── Analysis/strategy_config.py     ← authoritative config, IMPORT don't copy values
  └── backtest/                       ← read-only inputs

HARD RULES
- ALL config values come from importing strategy_config. NEVER hardcode
  thresholds. When you need a base value, do:
      from strategy_config import POLICY, VETOES, FLOORS, MODULES, PENALTIES,
                                  CONFLUENCE, OPTIMIZED
  and read from those dicts.
- OPTIMIZED dict OVERRIDES base dicts. Merge with overlay: base | OPTIMIZED.
  Implement this as a helper load_config() returning one flat dict.
- Never write to Analysis/strategy_config.py from these scripts. Proposed
  changes go to Analysis/artifacts/config_proposal.json only.
- feature_matrix.parquet is read-only — it's the ground truth for replay.
- All probabilistic methods: np.random.seed(42). If a script uses multiple
  seeds (per fold, per trial), document the seed derivation.
- Every optimization output must include: baseline metrics, optimized
  metrics, delta, and a "would you accept this change?" flag based on
  guardrails you apply.

EXECUTION
After writing, run with: python Analysis/scripts/<filename>.py
Show full stdout. Do not auto-retry more than once on error.
```

---

## PHASE 11 — Build the replay harness

This is the engine every later phase depends on. Get it right.

**Prompt:**

```
Write Analysis/scripts/11_replay_harness.py.

Goal: given a config dict, decide which historical signals would have
been accepted, and compute the resulting PnL curve. Fast, deterministic,
reproducible.

Inputs:
  Analysis/artifacts/feature_matrix.parquet
  Analysis/artifacts/signals.parquet
  Analysis/artifacts/outcomes.parquet      (for sim_pnl per signal)
  Analysis/strategy_config.py              (import; do not copy values)

Structure the script as a reusable module:

def load_config(overrides: dict = None) -> dict:
    """Return merged config = base dicts overlaid by OPTIMIZED then overrides."""

def decide_accept(signal_row, config) -> tuple[bool, str]:
    """Return (accepted, reason). Reason is 'accepted' or a rejection code."""
    Rules (apply in order, first fail wins):
      1. Score floor: if score < config['SCORE_REJECT'] → reject 'below_score_floor'
      2. Per-source threshold: if source in PER_SOURCE_THRESHOLDS and
         score < PER_SOURCE_THRESHOLDS[source] → reject 'below_source_threshold'
      3. H4 alignment: if config.get('REQUIRE_H4_ALIGNED') and
         ctx_h4 not aligned with direction → reject 'h4_misaligned'
      4. Active veto replay: for each signal previously rejected with a
         veto GateReason, re-evaluate the expression under new config.
         Parse gate_expression (logged in signals.parquet) and see if
         the new threshold still trips.
         Example: G3.5:ThinMarket(vt=150<400=0.40×1000)
           → original: 150 < (0.40 × 1000) = 400 → blocked
           → if new threshold is 0.10: 150 < (0.10 × 1000) = 100 → not blocked
         Implement a parser for the {var=value}{op}{expr} format found in
         gate_expression, using simple regex + eval with restricted locals.
         For gates whose expression CAN'T be re-evaluated (missing data),
         treat them as "sticky": if originally vetoed, stays vetoed.
         Print the count of sticky vetoes — this is a limitation to
         surface clearly.
      5. Conviction floors, MIN_RR_RATIO, MAX_CONSECUTIVE_LOSS, etc from
         POLICY — apply as stateful checks (see below).

def replay(config, features_df) -> pd.DataFrame:
    """Walk features_df in timestamp order. Apply decide_accept. For
    accepted signals, draw sim_pnl from outcomes. Apply session/daily
    state (MAX_DAILY_LOSS, MAX_CONSECUTIVE_LOSS) as circuit breakers.
    Return a per-signal df with columns:
      signal_id, timestamp, accept_under_new_config, reject_reason,
      realized_pnl, cum_pnl, daily_pnl, consecutive_losses, halted_flag
    """

def metrics(replay_df) -> dict:
    """Return dict of:
      total_pnl, n_trades, win_rate, avg_pnl, std_pnl,
      sharpe_daily (assume 252 trading days),
      max_drawdown, max_drawdown_pct, avg_drawdown_duration_days,
      max_consecutive_losses, halted_days_count,
      by_source: {source: {n, win_rate, pnl}, ...}"""

Testing:
Include an if __name__ == '__main__' block that:
  1. Loads current config (with OPTIMIZED applied)
  2. Calls replay()
  3. Prints metrics()
  4. Saves Analysis/artifacts/replay_baseline.parquet
  5. Prints sticky-veto count

This script is imported by later phases — keep the interface stable.
```

**Report back:** the baseline metrics dict, sticky-veto count, and head(10) of replay_baseline.parquet.

**🛑 Decision point:**
- **Sticky-veto count should be < 20% of total vetoes.** Higher = too much of your gate logic can't be replayed, and the optimizer has too little lever. Fix by adding more structured expressions to GateReason in the C# logger.
- The `halted_flag` logic is worth double-checking: if MAX_DAILY_LOSS=500 is hit, the next signals in that day must be marked halted AND excluded from pnl. Print a sample halted day to verify.

---

## PHASE 12 — Baseline validation

Before we optimize, prove replay reproduces reality. If it doesn't, optimization is meaningless.

**Prompt:**

```
Write Analysis/scripts/12_baseline_validation.py.

Imports the replay harness from 11_replay_harness.

Compares:
  A. Replay metrics with current config (OPTIMIZED applied)
  B. Actual backtest metrics from Trades.csv (compute directly)

For each of: total_pnl, n_trades, win_rate, max_drawdown, max_consecutive_losses,
produce a side-by-side comparison table with:
  metric, actual, replay, abs_diff, pct_diff

Also per-source breakdown: actual vs replay for each signal source.

Produces Analysis/artifacts/replay_validation.md with:
  - The comparison tables
  - A list of signals where replay decision ≠ actual decision
    (traded in backtest but rejected in replay, or vice versa)
    Top 20 such cases with signal_id, source, score, reason for mismatch
  - A verdict: PASS if all absolute deltas within 5%, WARN 5-15%, FAIL >15%

Print the verdict prominently.

If FAIL, do NOT continue to later phases. The replay is broken and
later results are untrustworthy.
```

**Report back:** the full comparison table and verdict.

**🛑 Decision point:**
- **PASS is required before continuing.** If WARN or FAIL, typical causes:
  - Timestamp-ordering bug in replay (signals processed out of order → state-dependent rules misfire)
  - Missing stateful rule (e.g., cooldown between signals from same source not implemented)
  - Wrong config key names in `load_config` merge logic
  - `sim_pnl` in outcomes.parquet uses different sign convention than Trades.csv pnl
- Per-source breakdown is where bugs surface — if overall PnL matches but ORB is off by 30%, the ORB floor rule is wrong.

---

## PHASE 13 — Threshold & policy optimization

Now the real work. Walk-forward Bayesian optimization over thresholds + policy parameters.

**Prompt:**

```
Write Analysis/scripts/13_optimize_thresholds.py.

Install optuna if missing: pip install optuna --break-system-packages

Imports replay harness.

PARAMETER SPACE (flat list of Optuna suggestions):
  # Score floors
  SCORE_REJECT:         int  [55, 90]     step 1
  SCORE_GRADE_B:        int  [60, 80]     step 1
  SCORE_GRADE_A:        int  [70, 90]     step 1
  SCORE_GRADE_A_PLUS:   int  [80, 95]     step 1

  # Per-source thresholds (one per source found in signals.parquet)
  PER_SOURCE_THRESHOLDS[source]:  int [40, 95]  step 1

  # Vetoes
  CUMDELTA_EXHAUSTED:   float [1000, 5000]  step 100
  WEAK_STACK_COUNT:     float [1, 6]        step 1
  BRICK_WALL_ATR:       float [0.10, 0.40]  step 0.02

  # Floors
  MIN_NET_VOLUMETRIC:   int  [20, 60]       step 1
  MIN_NET_STRUCTURE:    int  [10, 40]       step 1
  BOS_FLOOR_VOLUMETRIC: int  [10, 40]       step 1
  LONG_H4_BEARISH_FLOOR: int [40, 80]       step 1

  # Policy / risk
  MIN_RR_RATIO:            float [1.0, 2.5] step 0.1
  MIN_STOP_TICKS:          float [2, 12]    step 1
  MAX_CONSECUTIVE_LOSS:    int [3, 8]       step 1

  # Binary toggles
  REQUIRE_H4_ALIGNED:      bool

Do NOT include layer weights here — those need per-layer logging (Phase 13b).

WALK-FORWARD PROTOCOL:
  1. Split feature_matrix by DAY (not by signal — signals within a day
     are not independent). Sort days chronologically.
  2. Three windows:
       train:  first 60%
       val:    next 20%  (used by Optuna for trial scoring)
       test:   last 20%  (held out, evaluated ONCE at the end)
  3. Optuna samples a trial config → replay on train+val → score the val segment.
  4. After N_TRIALS (default 300), pick the best-on-val config.
  5. Evaluate the best config on test segment. Report train/val/test metrics.

OBJECTIVE FUNCTION:
  Let pnl = sum of daily pnl on the val segment.
  Let dd  = absolute max drawdown on val.
  Let n   = number of trades on val.

  objective = pnl - 2.0 * dd - 500 * (1 if n < 30 else 0)

  The dd penalty keeps optimizer from taking concentrated risk.
  The n<30 term prevents "one lucky trade" trials.

GUARDRAILS (hard constraints, applied post-trial):
  - No parameter moves > 3× its baseline value.
  - max_drawdown(test) must not exceed 1.5× max_drawdown(baseline_test).
  - n_trades(test) must be >= 0.5 × n_trades(baseline_test). (Don't
    over-filter into a dead strategy.)
  If the best trial fails any guardrail, step down to the next best
  that passes, and log which guardrail bound.

OUTPUT:
  Analysis/artifacts/config_proposal.json  (the OPTIMIZED-style dict)
  Analysis/artifacts/optimization_report.md with:
    - Train/val/test metrics for baseline vs optimized (side-by-side)
    - Per-source accept-rate change (which sources get loosened/tightened?)
    - Parameter importance from Optuna (top 10 by contribution to objective)
    - The Optuna trial history summary (best-so-far curve)
    - Which guardrail bound (if any)

Print the verdict: ACCEPT / REJECT / NEEDS_REVIEW and rationale.
```

**Report back:** train/val/test comparison table, top 10 parameter importances, accept/reject verdict.

**🛑 Decision point:**
- **Guardrail "test dd exceeds 1.5× baseline" is critical.** If fine-tuning the val segment hurts the test segment, the optimizer overfit. Shrink the parameter space and re-run.
- **Parameter importance tells you where to add more range.** If `SCORE_REJECT` pinned at one bound, the range was too narrow — re-run with wider bounds.
- **If test metrics < val metrics by a lot** (typical overfit signature), increase `N_TRIALS` or reduce parameter space.

---

## PHASE 13b (OPTIONAL) — Full weight optimization

Only attempt after instrumenting `StrategyLogger.cs` to log per-layer contributions. Until then, skip.

**Prompt (for later use):**

```
Prerequisite: StrategyLogger.cs emits a new row type SCORE_BREAKDOWN
with Detail like:
  la_h4=14 la_h2=10 la_h1=0 lb_raw=22 lb_capped=22 lc_raw=35 lc_capped=30
  ld_struct=12 pen_h4=0 pen_both=0 fair=-15 total=85

Extend replay harness with recompute_score(breakdown_row, config_weights)
that rebuilds total_score from logged components. Then Phase 13 expands
to include CONFLUENCE and PENALTY dicts in the search space.

Do NOT attempt this without the SCORE_BREAKDOWN logging in place. Forcing
it by reconstructing the scoring formula from StrategyConfig.cs will
produce subtle mismatches that invalidate the optimizer.
```

---

## PHASE 14 — Monte Carlo stress testing

Both baseline and optimized configs get stress-tested. Two bootstrap regimes: general and choppy-day.

**Prompt:**

```
Write Analysis/scripts/14_stress_test.py.

Imports replay harness.

Two configs to test:
  A. Baseline    (strategy_config with OPTIMIZED applied)
  B. Proposal    (from Analysis/artifacts/config_proposal.json)

Two bootstrap regimes:

  GENERAL:
    - Sample N_DAYS historical trading days with replacement.
    - Concatenate them into a synthetic timeline.
    - Run replay. Record daily_pnl curve, max_dd, consecutive_losses.
    - Repeat 1000 times.

  CHOPPY:
    - Identify "choppy" days from feature_matrix: days where
        regime_vol == 'high_vol' on >50% of signals AND
        regime_structure == 'weak_swing' on >50% of signals
      If that subset is < 20 days, relax to "any day meeting either".
    - Bootstrap from ONLY that subset.
    - Same procedure, 1000 iterations.
    - This simulates "what if we hit a month of choppy conditions?"

For each (config, regime) pair, produce:
  - P5, P25, P50, P75, P95 of: total_pnl, max_drawdown, max_consecutive_losses
  - P(ruin) = pct of iterations where cumulative_pnl hits -max_allowable_loss
    (default max_allowable = 3 × MAX_DAILY_LOSS × 5 = $7500; configurable)
  - Sharpe distribution
  - Worst iteration's daily pnl curve (saved as csv for plotting)

COMPARISON OUTPUT:
Analysis/artifacts/stress_report.md contains:
  1. Side-by-side baseline vs proposal for GENERAL bootstrap
  2. Side-by-side baseline vs proposal for CHOPPY bootstrap
  3. A "stress verdict": does the proposal reduce P(ruin) vs baseline?
     Reduce P95 drawdown? If EITHER gets worse in CHOPPY, flag the
     proposal as fragile.
  4. "Choppy-day sensitivity": which sources drive losses in choppy
     bootstrap? Break down per-source pnl in worst 10% of iterations.

Print the stress verdict and the P(ruin) numbers.
```

**Report back:** P(ruin) baseline vs proposal for both regimes, worst drawdown percentiles, any fragility flag.

**🛑 Decision point:**
- **If proposal is worse than baseline in CHOPPY bootstrap, reject the proposal.** The optimizer found something that works on average but breaks in adverse conditions.
- **If P(ruin) > 5% in CHOPPY for either config, your MAX_DAILY_LOSS or MAX_CONSECUTIVE_LOSS is too loose** — tighten those circuit breakers before shipping.
- **"Choppy-day sensitivity" per-source** is where you find that one of your signal sources is specifically toxic in chop — a candidate for a regime-conditional disable.

---

## PHASE 15 — Trade management diagnostic

Descriptive first: what's broken before we try to fix it.

**Prompt:**

```
Write Analysis/scripts/15_trade_mgmt_diagnostic.py.

Inputs:
  feature_matrix.parquet (traded rows)
  outcomes.parquet
  forward_bars.parquet
  backtest/TicData.csv   (stream in chunks indexed by timestamp)
  backtest/Trades.csv    (actual fill/exit times if available)
  strategy_config.py

For each traded signal, using TicData between entry_timestamp and
exit_timestamp (from Trades.csv), compute:

  time_to_mae_seconds      when MAE was hit
  time_to_mfe_seconds      when MFE was hit
  time_to_t1_seconds       first tick reaching T1 (or NaN)
  time_to_t2_seconds       first tick reaching T2 (or NaN)
  time_to_stop_seconds     first tick reaching stop (or NaN)
  exit_reason              parsed from Trades.csv: target_hit / stop_hit /
                           be_stop / time_out / manual_close / session_end
  mfe_reached_pct          MFE / T1_distance   (>=1.0 means we passed T1)
  mfe_ratio_locked         how much of MFE we actually exited at
  reached_be_arm_level     bool (price moved BE_ARM fraction toward T1)
  reached_mfe_lock_start   bool (price moved MFE_LOCK_START fraction toward T1)

Save trade_journey.parquet.

Produce trade_mgmt_diagnostic.md with these diagnostic sections:

### 1. Exit reason distribution
  Counts and percentages by exit_reason. Any warning patterns:
    - be_stop rate > 25%: you're being stopped at break-even a lot
    - time_out rate > 10%: you're holding trades too long
    - session_end rate > 5%: you're forced-closing winners at the bell

### 2. MFE leak — classic "profit given back"
  Signals where mfe_reached_pct >= 1.0 BUT exit_reason == 'stop_hit'
  or 'be_stop'. For each source, show:
    n_leak, avg_mfe_given_back_$, total_$_leaked
  This is the big one — it quantifies how much money you're making
  and then losing back.

### 3. T1 partial effectiveness
  For signals that HIT T1:
    - Of those that partialled, how many went on to hit T2?
    - Of those that partialled, how many runners stopped back at BE?
    - Compute "effective R" = actual_pnl / intended_full_t2_pnl.
    Low effective R means partialling is cutting winners short.

### 4. BE arm timing
  For signals that reached BE arm level:
    - % that then hit stop (BE or original)
    - % that continued to T1
    - % that peaked and reversed exactly near BE arm level
  If "peaked near BE arm" rate > 20%, your BE_ARM threshold is set to
  the market's median retrace — moving stops in too early.

### 5. Slippage audit
  From Phase 3's slippage_ticks: distribution by session and by source.
  Flag sources where median slippage > MIN_STOP_TICKS / 4 — the
  backtest is overstating those trades.

### 6. Stop proximity
  For signals stopped out: distribution of final MAE in ticks vs
  stop_ticks. Spikes just past stop_ticks = "stop-hunted" trades.

### 7. Summary: top 3 diagnosed issues ranked by $ impact

Print Section 7 prominently.
```

**Report back:** exit-reason distribution, MFE leak total dollars, top 3 issues.

**🛑 Decision point:**
- **MFE leak > $1000 aggregate** = clear trade management fix warranted. That's direct optimization target for Phase 16.
- **High `peaked_near_be_arm` rate** = BE_ARM level needs to move, not necessarily parameters around it.
- **High `stop_hunted` rate (MAE clustering just past stop)** = add a buffer or use ATR-dynamic stops.

---

## PHASE 16 — Trade management optimization

Tick-level simulation of alternate management rules. This is the high-value phase because it operates on the cleanest data (ticks) and fixes the issues Phase 15 surfaced.

**Prompt:**

```
Write Analysis/scripts/16_optimize_trade_mgmt.py.

pip install optuna --break-system-packages   (if not yet)

Imports replay harness from Phase 11 for acceptance decisions.

For each accepted signal, load tick stream from entry_timestamp to
max(entry_timestamp + 4 hours, session_end). Cache these as parquet
at Analysis/artifacts/tick_cache/{signal_id}.parquet to avoid
re-reading TicData.csv.

def simulate_trade_mgmt(signal, tick_stream, tm_params) -> dict:
    """Walk ticks. Track:
       - Did stop get hit?
       - Did T1 get hit? If so, partial at T1_PARTIAL_PCT of position.
       - Did BE arm trigger? (price moved BE_ARM fraction toward T1)
       - Did MFE lock trigger? (price moved MFE_LOCK_START fraction,
         then retraced MFE_LOCK_PCT from peak)
       - Did T2 get hit?
       - Session end?
     Apply exits in priority: stop > mfe_lock_exit > t2 > t1_partial > time_out.
     Return per-trade dict with realized_pnl, exit_reason, exit_time, etc."""

Important: partialling means position size halves after T1. Recompute
$ exposure correctly.

PARAMETER SPACE (trade-mgmt Optuna search):
  BE_ARM_RETEST:          float [0.10, 0.40]  step 0.01
  BE_ARM_BOS:             float [0.15, 0.45]
  BE_ARM_IMPULSE:         float [0.20, 0.50]
  MFE_LOCK_START_RETEST:  float [0.30, 0.70]
  MFE_LOCK_START_BOS:     float [0.35, 0.75]
  MFE_LOCK_START_IMPULSE: float [0.50, 0.90]
  MFE_LOCK_PCT_RETEST:    float [0.30, 0.60]
  MFE_LOCK_PCT_BOS:       float [0.25, 0.55]
  MFE_LOCK_PCT_IMPULSE:   float [0.15, 0.45]
  T1_PARTIAL_PCT:         float [0.25, 0.75]   step 0.05
  MIN_PROFIT_EXIT_TICKS:  float [4, 16]        step 1

WALK-FORWARD same as Phase 13: 60% train, 20% val, 20% test, by day.

OBJECTIVE:
  For each signal, compute improvement_$ = new_pnl - baseline_pnl.
  Sum improvement across val segment.
  Penalize increase in max_drawdown as in Phase 13.

GUARDRAIL: The optimized trade management must not REDUCE any source's
win_rate below 0.90 × baseline_win_rate. Prevents optimizer from
converging on "take every tiny profit, hold every loser" traps.

OUTPUT:
  Analysis/artifacts/trade_mgmt_proposal.json
  Analysis/artifacts/trade_mgmt_report.md
    - Baseline vs proposed per-parameter
    - Aggregate $ improvement on test segment
    - Per-source breakdown: where improvement comes from
    - Exit-reason shift: does proposal change the mix? (e.g., fewer
      be_stops → more t2_hits?)

Print per-source $ improvement and the aggregate.
```

**Report back:** aggregate test-segment $ improvement, per-source breakdown, exit-reason shift.

**🛑 Decision point:**
- **If aggregate test improvement < 10% of baseline PnL, don't ship.** Overhead of new logic isn't worth marginal gain.
- **If improvement is driven 80%+ by one source**, you're over-fitting to that source's quirks. Either narrow the change to apply only to that source, or reject.
- **Exit-reason shift should be sensible**: fewer be_stops / stop_hits, more t1_partials / t2_hits. If it's shifting toward time_out, the optimizer found a way to "just hold longer" which is often a drawdown waiting to happen.

---

## Final phase: package the proposal

Merge `config_proposal.json` (Phase 13) and `trade_mgmt_proposal.json` (Phase 16) into a single patch file ready for the dev team to apply.

**Prompt:**

```
Write Analysis/scripts/17_finalize_proposal.py.

Reads:
  Analysis/artifacts/config_proposal.json
  Analysis/artifacts/trade_mgmt_proposal.json
  Analysis/artifacts/stress_report.md
  Analysis/artifacts/optimization_report.md
  Analysis/artifacts/trade_mgmt_report.md

Produces Analysis/artifacts/final_proposal.md with:

### 1. Recommended OPTIMIZED block
  One JSON block ready to paste into strategy_config.py's OPTIMIZED dict.
  Include OPT_TIMESTAMP set to now, ISO format.

### 2. Expected impact
  Table: metric, current, expected (from test segment), delta.

### 3. Risk posture
  Pulled from Phase 14:
    - P(ruin) general, P(ruin) choppy
    - P95 drawdown general, P95 drawdown choppy
    - Worst-case scenario description

### 4. Rollout checklist
  - [ ] Run proposal on last 30 days of paper-trading data
  - [ ] Verify replay match > 95%
  - [ ] Hand off to dev for StrategyConfig.cs copy
  - [ ] Retain current config as fallback for 2 weeks
  - [ ] Set alert thresholds: if live drawdown exceeds expected P75, revert

### 5. What we did NOT optimize
  - Layer weights (requires per-layer logging first)
  - Signal generation logic (lives in C#)
  - The specific rules inside each signal module

### 6. Outstanding questions for the dev team
  Any parameters where the optimizer pushed to a bound (indicates
  search space was too narrow) or where guardrails bound (indicates
  aggressive proposal pulled back).

Print final_proposal.md in full.
```

**Report back:** the full `final_proposal.md`.

**🛑 Decision point:** This is the deliverable. Before applying, run it past a human skeptic who asks: (a) does the rationale for each change make physical sense? (b) are any of the changes large enough to destabilize live behavior even if backtest metrics look good?

---

## Pitfalls specific to this workflow

| Trap | How it bites | Defense |
|---|---|---|
| Lookahead in validation | Feature depends on future data | Phase 12 verdict will fail — that's its job |
| Survivorship in bootstrap | Only sample winning days | Phase 14 uses ALL days weighted uniformly |
| Overfitting to val | Impressive val, bad test | Walk-forward with held-out test + guardrails |
| Sticky vetoes | Real config has binding vetoes optimizer can't move | Phase 11 counts & reports them; address in C# |
| Correlated trades | Bootstrap by signal violates independence | Phases 13/14/16 bootstrap by **day**, not signal |
| Phantom optimizer wins | Low n_trades test = lucky | `n < 30` penalty + guardrails catch these |
| Tick data gaps | Missing ticks around session boundary silently skew tick simulation | Phase 16 should assert tick count per minute ≥ 10; log gaps |
| Circuit breakers never fire in replay | MAX_DAILY_LOSS / MAX_CONSECUTIVE_LOSS state tracking forgotten | Phase 11 `halted_flag` test — print a sample halted day |

## Workflow reminders

- **Phases 11 and 12 are the foundation.** A broken replay makes everything downstream meaningless. Fight hard for the PASS verdict on Phase 12.
- **Weight optimization (13b / full Phase 17) is not worth the implementation risk** until per-layer contribution logging is in `StrategyLogger.cs`. A 10-line C# change beats 500 lines of formula reconstruction.
- **Trade management (15–16) is likely where the biggest $ improvement lives.** Score optimization tightens which signals fire; trade management fixes how much you make on the ones that do. The latter is usually higher leverage.
- **Stress testing isn't optional.** An optimizer without a stress test is a tool for generating overfit garbage with statistical gloss.
- **The final deliverable is a proposal, not a commit.** Human review, then paper trading, then live.
