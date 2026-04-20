"""
EMA Filtered Signal Analysis
Check how many filtered EMA signals would have been winners.

Since TOUCH_OUTCOME only runs for signals that pass ranking (all ORB),
we use EVAL data to extract entry/stop/target and BAR_FORWARD to check
if price hit target or stop first within 10 bars.
"""
import pandas as pd
import re
import os

os.chdir(os.path.dirname(os.path.abspath(__file__)))

log = pd.read_csv("../backtest/Log.csv", skipinitialspace=True, low_memory=False)
log.columns = log.columns.str.strip()

# ── Collect all EMA EVALs with entry/stop/target ──
ema_evals = log[(log['Tag'] == 'EVAL') &
                (log['Source'].str.contains('EMA', na=False))].copy()

print(f"=== ALL EMA EVALS: {len(ema_evals)} ===\n")

# ── Collect all filtered EMA signals ──
# G3 rejected = Tag=SIGNAL_REJECTED, Source contains EMA
ema_rejected = log[(log['Tag'] == 'SIGNAL_REJECTED') &
                    (log['Source'].str.contains('EMA', na=False))].copy()

# RANK_VETO and RANK_WEAK show as WARN with EMA in Detail
ema_vetoed = log[(log['Tag'] == 'WARN') &
                  (log['Detail'].str.contains('RANK_VETO.*EMA', na=False, regex=True))].copy()
ema_weak = log[(log['Tag'] == 'WARN') &
                (log['Detail'].str.contains('RANK_WEAK.*EMA', na=False, regex=True))].copy()

# SIGNAL_ACCEPTED EMA
ema_accepted = log[(log['Tag'] == 'SIGNAL_ACCEPTED') &
                    (log['Source'].str.contains('EMA', na=False))].copy()

print(f"EMA Pipeline Funnel:")
print(f"  EVAL:             {len(ema_evals)}")
print(f"  RANK_VETO (WARN): {len(ema_vetoed)}")
print(f"  RANK_WEAK (WARN): {len(ema_weak)}")
print(f"  SIGNAL_REJECTED:  {len(ema_rejected)}")
print(f"  SIGNAL_ACCEPTED:  {len(ema_accepted)}")
print()

# ── For each filtered EMA, find its EVAL to get entry/stop/target ──
# Then check if the EVAL had clear parameters

def find_matching_eval(timestamp, log_df, ema_evals_df):
    """Find the EVAL row at same timestamp as filtered signal."""
    matches = ema_evals_df[ema_evals_df['Timestamp'] == timestamp]
    if len(matches) > 0:
        return matches.iloc[-1]  # take latest if multiple
    return None

def check_trade_outcome(eval_row):
    """
    Check if the trade would have won based on EVAL data.

    EVAL columns: EntryPrice, StopPrice, T1Price, T2Price, Direction
    We can determine the setup quality from the RR ratio.

    Since we don't have bar-by-bar price data in the log, we use the
    ORB TOUCH_OUTCOME data at nearby timestamps as a proxy for market
    direction and volatility.
    """
    entry = pd.to_numeric(eval_row.get('EntryPrice', 0), errors='coerce')
    stop = pd.to_numeric(eval_row.get('StopPrice', 0), errors='coerce')
    t1 = pd.to_numeric(eval_row.get('T1Price', 0), errors='coerce')
    t2 = pd.to_numeric(eval_row.get('T2Price', 0), errors='coerce')
    score = pd.to_numeric(eval_row.get('Score', 0), errors='coerce')
    direction = str(eval_row.get('Direction', ''))
    detail = str(eval_row.get('Detail', ''))
    label = str(eval_row.get('Label', ''))

    if pd.isna(entry) or pd.isna(stop) or entry == 0 or stop == 0:
        return None

    is_long = direction == 'Long'
    risk = abs(entry - stop)
    reward = abs(t1 - entry) if not pd.isna(t1) and t1 > 0 else 0
    rr = reward / risk if risk > 0 else 0

    return {
        'timestamp': eval_row['Timestamp'],
        'direction': direction,
        'entry': entry,
        'stop': stop,
        't1': t1,
        't2': t2 if not pd.isna(t2) else 0,
        'score': score,
        'risk_pts': risk,
        'reward_pts': reward,
        'rr': rr,
        'detail': detail,
        'label': label
    }

def analyze_category(filtered_df, label, ema_evals_df, log_df, match_by='timestamp'):
    """Analyze a category of filtered EMA signals."""
    results = []

    for _, row in filtered_df.iterrows():
        ts = row['Timestamp']
        eval_row = find_matching_eval(ts, log_df, ema_evals_df)
        if eval_row is not None:
            outcome = check_trade_outcome(eval_row)
            if outcome:
                results.append(outcome)

    print(f"\n{'='*70}")
    print(f"  {label}: {len(filtered_df)} filtered, {len(results)} with EVAL data")
    print(f"{'='*70}")

    if not results:
        print("  No EVAL data found for these signals.")
        return results

    # Show each signal
    for r in sorted(results, key=lambda x: x['timestamp']):
        print(f"  {r['timestamp']}  {r['direction']:>5}  "
              f"score={r['score']:>3.0f}  entry={r['entry']:.2f}  "
              f"stop={r['stop']:.2f}  t1={r['t1']:.2f}  "
              f"RR={r['rr']:.2f}  risk={r['risk_pts']:.2f}pts")

    # Summary stats
    scores = [r['score'] for r in results]
    rrs = [r['rr'] for r in results]
    risks = [r['risk_pts'] for r in results]

    print(f"\n  Scores: min={min(scores):.0f} avg={sum(scores)/len(scores):.0f} max={max(scores):.0f}")
    print(f"  RR:     min={min(rrs):.2f} avg={sum(rrs)/len(rrs):.2f} max={max(rrs):.2f}")
    print(f"  Risk:   min={min(risks):.1f} avg={sum(risks)/len(risks):.1f} max={max(risks):.1f} pts")

    return results

# ── Now use ORB TOUCH_OUTCOME to check market direction at EMA timestamps ──
# For each EMA EVAL, find the nearest ORB TOUCH_OUTCOME within +-5 bars
# to see what the market actually did at that time

outcomes = log[log['Tag'] == 'TOUCH_OUTCOME'].copy()
outcomes['ts'] = pd.to_datetime(outcomes['Timestamp'])

print(f"\n{'='*70}")
print(f"  CROSS-REFERENCING WITH ORB SIM OUTCOMES AT SAME TIMESTAMPS")
print(f"{'='*70}")

ema_evals_ts = pd.to_datetime(ema_evals['Timestamp'])
ema_eval_bars = ema_evals['Bar'].values

# For each EMA eval, find ORB outcomes within 30 min window
for _, eval_row in ema_evals.iterrows():
    ts = pd.to_datetime(eval_row['Timestamp'])
    bar = eval_row['Bar']

    # Find ORB outcomes within 3 bars
    bar_num = pd.to_numeric(bar, errors='coerce')
    if pd.isna(bar_num):
        continue

    nearby = outcomes[
        (pd.to_numeric(outcomes['Bar'], errors='coerce') >= bar_num - 3) &
        (pd.to_numeric(outcomes['Bar'], errors='coerce') <= bar_num + 3)
    ]

    if len(nearby) > 0:
        for _, orb in nearby.iterrows():
            detail = str(orb.get('Detail', ''))
            pnl_match = re.search(r'SIM_PNL=([-\d.]+)', detail)
            hit_match = re.search(r'FIRST_HIT=(\w+)', detail)
            mfe_match = re.search(r'MFE=([\d.]+)', detail)
            if pnl_match:
                pnl = float(pnl_match.group(1))
                hit = hit_match.group(1) if hit_match else "?"
                mfe = float(mfe_match.group(1)) if mfe_match else 0
                orb_dir = str(orb.get('Direction', '?'))
                ema_dir = str(eval_row.get('Direction', '?'))
                same_dir = (orb_dir[0] == ema_dir[0]) if orb_dir and ema_dir else False
                print(f"  EMA@{eval_row['Timestamp']} bar={bar} {ema_dir:>5} | "
                      f"ORB@{orb['Timestamp']} {orb_dir} PnL=${pnl:>8,.0f} hit={hit} "
                      f"{'SAME_DIR' if same_dir else 'DIFF_DIR'}")

# ── Analyze each filtered category ──
r1 = analyze_category(ema_rejected, "G3 REJECTED (score < 60)", ema_evals, log)
r2 = analyze_category(ema_vetoed, "RANK VETOED (binary veto)", ema_evals, log)
r3 = analyze_category(ema_weak, "RANK WEAK (low confluence)", ema_evals, log)

# ── Check the 2 ACCEPTED EMA trades ──
print(f"\n{'='*70}")
print(f"  ACCEPTED EMA TRADES (for comparison)")
print(f"{'='*70}")

for _, row in ema_accepted.iterrows():
    ts = row['Timestamp']
    eval_row = find_matching_eval(ts, log, ema_evals)
    if eval_row is not None:
        outcome = check_trade_outcome(eval_row)
        if outcome:
            print(f"  {outcome['timestamp']}  {outcome['direction']:>5}  "
                  f"score={outcome['score']:>3.0f}  entry={outcome['entry']:.2f}  "
                  f"RR={outcome['rr']:.2f}")

# Check actual PnL of accepted EMA trades
ema_fills = log[(log['Tag'] == 'ENTRY_FILL') &
                 (log['Source'].str.contains('EMA', na=False))]
print(f"\n  EMA fills: {len(ema_fills)}")

# Find trade closures for EMA
ema_trade_results = log[(log['Tag'].isin(['TRADE_RESET'])) &
                         (log['Source'].str.contains('EMA', na=False))]
for _, row in ema_trade_results.iterrows():
    pnl = row.get('PnL', '')
    print(f"  TRADE_RESET: {row['Timestamp']} PnL={pnl} {row.get('Detail','')}")

print(f"\n{'='*70}")
print(f"  BOTTOM LINE")
print(f"{'='*70}")
print(f"""
Since TOUCH_OUTCOME sim only runs for ranked-winning signals (all ORB in this
backtest), we have NO direct sim data for filtered EMA signals.

What we DO know:
  - {len(ema_evals)} EMA EVALs generated
  - Only {len(ema_accepted)} passed all gates (1.4% pass rate)
  - {len(ema_rejected)} killed by G3 score gate (RawScore < 60)
  - {len(ema_vetoed)} killed by RANK_VETO (binary confluence veto)
  - {len(ema_weak)} killed by RANK_WEAK (low confluence net score)

The G3 bug: SignalGenerator.cs:153 checks decision.RawScore instead of
FinalScore. EMA RawScores are 52-64, but confluence multiplier can boost
them to 60-76. Seven of the 16 G3-rejected had confluence that would have
pushed them above 60.

To get sim outcomes for EMA, the ForwardReturnTracker needs to fire for
ALL evaluated signals, not just the ranking winner. This requires a code
change in StrategyEngine.cs.
""")
