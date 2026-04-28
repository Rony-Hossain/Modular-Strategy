# Phase 1 (consolidated, v5.1 schema) — The Foundation Parse

> One pivot, one time. Every later phase reads from the parquets this script produces; none should re-parse Log.csv.

**Paste this as-is to Gemini. Run to completion. Verify the outputs. Then proceed to Phase 23.**

---

## Universal context (standard)

```
CONTEXT
You are writing ONE Python script in this repo:
  D:/Ninjatrader-Modular-Startegy/
    ├── Analysis/scripts/
    ├── Analysis/artifacts/
    └── backtest/            ← read-only

Run commands from the repo root. Use pathlib.Path with forward slashes.
Use pandas, numpy, pyarrow only.

HARD RULES
- Read Analysis/artifacts/schema.md (v5.1) BEFORE writing any parsing
  code. Use ONLY columns and formats documented there.
- Log.csv is a LONG-FORMAT event log with 19 Tag types. Never treat it
  as one-row-per-signal.
- Every script prints, in order:
    [INPUT]  files loaded, row counts, tag breakdown
    [PARSE]  regex matches + any rows that failed to parse
    [CHECK]  dtype + non-null assertions
    [RESULT] shape + head(5) of every output table
    [SAVED]  full path of every file written
- pd.to_datetime(..., utc=True, errors='raise')  for timestamps.
- np.random.seed(42) wherever randomness appears.
- No silent fillna / ffill / dropna. If you must drop, print count + reason.
- Tabular outputs → parquet (pyarrow, snappy). Summaries → csv + md.
- Never write to backtest/.

EXECUTION
After writing the script, run it:
  python Analysis/scripts/01_parse_events.py
Show the full stdout. If it errors, show the traceback and propose
a fix. Do NOT auto-retry more than once.
```

---

## Prompt for Gemini

```
Write Analysis/scripts/01_parse_events.py.

This is the foundational parse. All downstream audits will read from
the parquets this script produces.

Read backtest/Log.csv per Analysis/artifacts/schema.md v5.1.

Produce ELEVEN tidy parquet tables.

─────────────────────────────────────────────────
TABLE A: signals.parquet
─────────────────────────────────────────────────
One row per SIGNAL_ACCEPTED or SIGNAL_REJECTED.

Columns:
  signal_id              = construct as "{ConditionSetId}:{yyyymmdd_hhmm}:{Bar}"
                           with ":REJ:" inserted after ConditionSetId
                           for rejected signals
  trade_id               = normalized form: regex replace _\d{4} → ""
                           so "HybridScalp_v1:20260104_2255:334" becomes
                           "HybridScalp_v1:20260104:334"
  timestamp, bar, direction, source, condition_set_id,
  score, grade, contracts, entry_price, stop_price, stop_ticks,
  t1_price, t2_price, rr_ratio,
  traded (bool: True if Tag==SIGNAL_ACCEPTED),
  label (original Label column),
  # parsed from context string in Detail:
  ctx_h4, ctx_h2, ctx_h1   (in {+, -, 0}),
  ctx_smf                  (in {bull, bear, neutral, flat}),
  ctx_str                  (float),
  ctx_sw                   (int),
  # parsed from GateReason (NaN for accepted):
  gate_id, gate_name, gate_expression

─────────────────────────────────────────────────
TABLE B: signal_touch.parquet
─────────────────────────────────────────────────
One row per TOUCH event.

Join strategy: TOUCH rows immediately follow their SIGNAL_*. Match on
same (Bar, Source, ConditionSetId). Exactly one TOUCH per SIGNAL_*
expected — assert and print orphan count if any.

Columns:
  signal_id, trade_id, touch_timestamp,
  zone_type, zone_lo, zone_hi,
  touch_trd, bos_l,
  fvg_bl, fvg_bl_lo, fvg_bl_hi,
  touch_bd, touch_cd, touch_abs, touch_sbull,
  touch_poc, touch_vah,
  h4b, touch_bdiv, touch_regime, atr, touch_hasvol

─────────────────────────────────────────────────
TABLE C: outcomes.parquet
─────────────────────────────────────────────────
One row per TOUCH_OUTCOME.

Parse signal_id from the Label column (handles :REJ: suffix).

Columns:
  signal_id, trade_id (derived by normalization),
  outcome_timestamp, outcome_bar, sim_pnl,
  mfe, mae, hit_stop, hit_target, first_hit, bars_to_hit,
  close_end, window_bars

─────────────────────────────────────────────────
TABLE D: context_bars.parquet
─────────────────────────────────────────────────
Long format: 5 rows per signal (bar offsets -5 to -1).

Columns:
  signal_id, trade_id, bar_offset (in {-5,-4,-3,-2,-1}),
  open, high, low, close, volume, delta

─────────────────────────────────────────────────
TABLE E: forward_bars.parquet
─────────────────────────────────────────────────
Long format: 5 rows per signal (bar offsets +1 to +5).

Columns:
  signal_id, trade_id, bar_offset (in {1,2,3,4,5}),
  bar_timestamp, open, high, low, close, volume, delta

─────────────────────────────────────────────────
TABLE F: flow_bars.parquet
─────────────────────────────────────────────────
One row per FLOW Tag (every bar).

Columns:
  timestamp, bar,
  regime, str_val, bd, cd, dsl, dsh,
  dex, bdiv, berdiv, abs_score, sbull, sbear,
  izb, izs, hasvol, sw_val,
  trd, ch_l, ch_s, bos_l, bos_s

─────────────────────────────────────────────────
TABLE G: struct_bars.parquet
─────────────────────────────────────────────────
One row per STRUCT Tag (every bar).

Columns:
  timestamp, bar,
  poc, vah, val,
  near_sup_price, near_sup_ticks,
  near_res_price, near_res_ticks,
  h4_high, h4_low, h1_high, h1_low,
  pp, skew

Parsing quirk: POC=0.00 VAH=0.00 VAL=0.00 means value area not
accumulated; store as NaN, not zero.

─────────────────────────────────────────────────
TABLE H: evals.parquet
─────────────────────────────────────────────────
One row per EVAL Tag.

Columns:
  eval_id (constructed same as signal_id — no :REJ:),
  timestamp, bar, direction, source, condition_set_id,
  score, entry_price, stop_price, t1_price, t2_price, label,
  ctx_h4, ctx_h2, ctx_h1, ctx_smf, ctx_str, ctx_sw

─────────────────────────────────────────────────
TABLE I: rank_scores.parquet
─────────────────────────────────────────────────
One row per WARN with sub-type RANK_WEAK.

Parse Detail: "RANK_WEAK [<module_id>] A=<int> B=<int> C=<int> D=<int>
               Pen=<int> Net=<int> Mult=<float>"

Join: RANK_WEAK follows its EVAL(s) in log order. Match on
  same Bar AND module_id (from brackets) equals EVAL.ConditionSetId.

Columns:
  eval_id (matched via join),
  timestamp, bar, condition_set_id,
  layer_a, layer_b, layer_c, layer_d,
  penalty, net_score, mult

Print join success rate. MUST be >= 98%.

─────────────────────────────────────────────────
TABLE J: zone_lifecycle.parquet
─────────────────────────────────────────────────
One row per ZONE_MITIGATED Tag.

Handle timestamp==0001-01-01 by carry-forward of last valid timestamp.

Columns:
  timestamp, direction,
  zone_side (BULL/BEAR from Label),
  close_price, zone_lo, zone_hi,
  zone_width (= zone_hi - zone_lo)

─────────────────────────────────────────────────
TABLE K: trade_lifecycle.parquet  [NEW IN v5.1]
─────────────────────────────────────────────────
One row per trade — collapses entry ENTRY_FILL, ORDER_LMT, T1_HIT,
STOP_MOVE events, exit ENTRY_FILL, and TRADE_RESET into one wide row.

Parse ENTRY_FILL rows by event_subtype per schema §1.3:
  event_subtype='entry'        if Label matches signal_id regex
  event_subtype='stop_exit'    if Label == 'Stop'
  event_subtype='t2_exit'      if Label == 'T2'
  event_subtype='forced_exit'  if Label == 'NoOvernight'
  event_subtype='unknown'      otherwise (assert none exist — stop if found)

For each entry ENTRY_FILL:
  trade_id            = normalized signal_id from Label
  entry_timestamp     = Timestamp
  entry_price         = EntryPrice (actual fill)
  direction           = from matching SIGNAL_ACCEPTED (lookup by trade_id)
  source              = same
  condition_set_id    = same
  score, grade        = same
  contracts           = same
  stop_original       = SIGNAL_ACCEPTED.StopPrice
  stop_ticks          = SIGNAL_ACCEPTED.StopTicks
  t1_price, t2_price  = same
  rr_ratio            = same
  intended_entry      = SIGNAL_ACCEPTED.EntryPrice
  slippage_ticks      = (entry_price - intended_entry)
                        * side_sign (+1 Long, -1 Short) / tick_size

  # Find all lifecycle events between entry_timestamp and next
  # TRADE_RESET sharing (Source, ConditionSetId, Direction):

  num_stop_moves      = count of STOP_MOVE rows in range
  first_stop_move_ts  = earliest STOP_MOVE timestamp in range (or NaN)
  final_stop_price    = last STOP_MOVE.StopPrice in range,
                        or stop_original if no moves
  be_arm_fired        = any STOP_MOVE with afterT1=False in range (bool)
  trail_fired         = any STOP_MOVE with afterT1=True in range (bool)

  hit_t1              = T1_HIT row exists in range (bool)
  t1_timestamp        = first T1_HIT in range or NaN
  t1_exit_price       = T1_HIT.ExitPrice or NaN
  t1_remaining        = T1_HIT.Detail 'remaining' value or NaN

  exit_subtype        = closing event_subtype: 'stop_exit' | 't2_exit' |
                        'forced_exit' | 'open' (if no exit fill found)
  exit_price          = closing ENTRY_FILL.EntryPrice
  exit_timestamp      = closing ENTRY_FILL.Timestamp
  trade_duration_min  = (exit - entry) in minutes

  trade_reset_ts      = matching TRADE_RESET timestamp
  reset_rem           = TRADE_RESET Detail 'rem' value
  reset_t1_flag       = TRADE_RESET Detail 't1' value
  reported_mfe_ticks  = TRADE_RESET Detail 'mfe' value

  # Reconstruct realized P&L per schema §1.3:
  instrument          = detect from config or default MNQ
  tick_size           = from INSTRUMENTS config
  point_value         = from INSTRUMENTS config

  if hit_t1:
    partial_contracts = contracts - t1_remaining
    runner_contracts  = t1_remaining
    t1_pnl_ticks      = (t1_exit_price - entry_price) * side_sign / tick_size
    runner_pnl_ticks  = (exit_price - entry_price) * side_sign / tick_size
    realized_pnl_ticks = (t1_pnl_ticks * partial_contracts
                          + runner_pnl_ticks * runner_contracts) / contracts
  else:
    realized_pnl_ticks = (exit_price - entry_price) * side_sign / tick_size
    partial_contracts  = 0
    runner_contracts   = contracts

  realized_pnl_$     = realized_pnl_ticks * tick_size * point_value
                       * contracts   # final dollar figure

Save trade_lifecycle.parquet with all these columns.

─────────────────────────────────────────────────
PRINTS REQUIRED
─────────────────────────────────────────────────
[INPUT]:
  - Row count of Log.csv
  - Tag breakdown (as you already did in Phase 0)

[PARSE]:
  - Parse success rate per Tag (target: ≥ 99%)
  - Any unparseable rows — print first 5 of each failing type

[CHECK]:
  - Assertion pass/fail for each of the 17 validation checks in
    schema.md §6. Print PASS/FAIL per check with counts.
  - RANK_WEAK → EVAL join success rate (target: ≥ 98%)
  - ENTRY_FILL subtype counts:
      entries / stop_exits / t2_exits / forced_exits / unknown
      (should match: 795 / 701 / 80 / 12 / 0)
  - Orphan counts:
      SIGNAL_ACCEPTED without TOUCH
      SIGNAL_ACCEPTED without TOUCH_OUTCOME
      SIGNAL_ACCEPTED without ORDER_LMT
      ORDER_LMT without ENTRY_FILL
      ENTRY_FILL without TRADE_RESET
      TA_DECISION with unresolvable trade_id

[RESULT]:
  - For each of the 11 tables: shape, dtype summary, head(5)
  - trade_lifecycle.parquet summary:
      exit_subtype value_counts
      hit_t1 value_counts
      realized_pnl_$ mean, median, sum
      trade_duration_min mean, median

[SAVED]:
  - Full path to every parquet file

Execute the script. Paste all stdout.
```

---

## What to verify before moving on

After the script runs, check these numbers against what we know:

| Check | Expected | Red flag if |
|---|---|---|
| signals.parquet rows | 795 + 444 = 1,239 | ≠ 1,239 |
| Rows in signals where traded=True | 795 | ≠ 795 |
| signal_touch.parquet rows | 795 (accepted) + 444 (rejected) = ~1,239, OR 797 if both SIGNAL_* variants have TOUCH | Much less |
| outcomes.parquet rows | 2,036 | ≠ 2,036 |
| context_bars.parquet rows | 1,239 × 5 = 6,195 (if every SIGNAL_* gets 5 bars) | Far less |
| flow_bars.parquet rows | 20,509 | ≠ 20,509 |
| struct_bars.parquet rows | 20,509 | ≠ 20,509 |
| evals.parquet rows | 17,979 | ≠ 17,979 |
| rank_scores join rate | ≥ 98% | < 98% |
| trade_lifecycle.parquet rows | 795 (one per entry ENTRY_FILL) | ≠ 795 |
| exit_subtype counts | 701 / 80 / 12 / (795 - rest) open | Any unknown |
| Validation check 9 | PASS (reconciles to 1,588) | FAIL |
| Validation check 16 | Current mismatch noted (80 vs 70) | New mismatches |

If anything's off, **fix before moving on**. Phase 23 onward all depend on `trade_lifecycle.parquet` being correct.

---

## Then what

Once Phase 1 produces clean outputs and the sanity checks above pass:

1. **Phase 23 (exit profile).** This is the highest-value first audit because it tells you whether the strategy is profitable at all over these 3.5 months, and quantifies MFE leak. It reads from `trade_lifecycle.parquet` — which you'll now have.

2. **Phase 27 (funnel + orphan) second.** Run this before Phases 24-26 to confirm the data pipeline is clean. Specifically its Section 5 (RANK_WEAK Net consistency) — if that fails, Phase 24's layer analysis is suspect.

3. **Then Phase 24, 25, 26 in any order** — they don't depend on each other.

4. **Phase 28 (bug list compilation) last.**
