#!/usr/bin/env python3
"""
DEEP SIGNAL FORENSICS (V5.1) — High-Rigor Signal Quality & Filter Audit.

Statistical Foundations:
  - Section 0: Fingerprint headers (SHA256) for verification.
  - Volatile Isolation: BOTH_SAMEBAR excluded from Win Rate and reported separately.
  - Percentile distributions (p25, p50, p75) to reveal the shape of noise.
  - Bootstrap Confidence Intervals (95%) for all PnL means (n >= 30).
  - Filter Alpha CI: Bootstraps the difference of means (TAKEN - DROP).
  - Robust ID Matching: Invariant (SetId, BarIndex).
  - Tertile Bucketing: Absorption and other features bucketed by distribution quantiles.
"""

import csv
import os
import sys
import re
import random
import math
import hashlib
from collections import defaultdict, Counter
from datetime import datetime

# Fix Windows console encoding
if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

BACKTEST = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'backtest')

# ============================================================================
#  STATISTICAL UTILITIES
# ============================================================================

def get_bootstrap_ci(data, n_iterations=1000, alpha=0.05):
    """Calculates 95% CI for the mean using bootstrapping. Returns (0,0) if n < 30."""
    if not data or len(data) < 30: return (0.0, 0.0)
    means = []
    for _ in range(n_iterations):
        resample = [random.choice(data) for _ in range(len(data))]
        means.append(sum(resample) / len(resample))
    means.sort()
    return (means[int(n_iterations * (alpha / 2))], means[int(n_iterations * (1 - alpha / 2))])

def get_diff_ci(a_pnls, b_pnls, n_iterations=1000, alpha=0.05):
    if len(a_pnls) < 30 or len(b_pnls) < 30: return (0.0, 0.0)
    diffs = []
    for _ in range(n_iterations):
        resample_a = [random.choice(a_pnls) for _ in range(len(a_pnls))]
        resample_b = [random.choice(b_pnls) for _ in range(len(b_pnls))]
        diffs.append(sum(resample_a)/len(resample_a) - sum(resample_b)/len(resample_b))
    diffs.sort()
    return (diffs[int(n_iterations * (alpha / 2))], diffs[int(n_iterations * (1 - alpha / 2))])

def extract_match_key(signal_id):
    """Robust matching: Invariant (SetId, barIndex) handles both yyyyMMdd and yyyyMMdd_HHmmss."""
    if not signal_id or ':' not in signal_id: return None
    parts = signal_id.split(':')
    if len(parts) < 3: return None
    return (parts[0], parts[-1]) 

def pnl_stats(group, label=""):
    """Dense statistical summary with percentile distributions and volatile isolation."""
    pnls = [t['SimPnL'] for t in group]
    hits = [t['FirstHit'] for t in group]
    n = len(pnls)
    if n == 0: return f"    {label:<30} n=   0  (NO DATA)"
    
    sp = sorted(pnls)
    mean = sum(sp) / n
    std = math.sqrt(sum((x-mean)**2 for x in sp) / max(n-1, 1))
    
    # Isolation of volatility
    volatile_n = sum(1 for h in hits if h == 'BOTH_SAMEBAR')
    
    # Percentiles
    p25 = sp[int(n*0.25)]
    p50 = sp[n//2]
    p75 = sp[min(n-1, int(n*0.75))]
    
    # Corrected Win Rate (exclude volatile from denominator)
    denom = n - volatile_n
    pos = sum(1 for t in group if t['SimPnL'] > 0 and t['FirstHit'] != 'BOTH_SAMEBAR')
    win_pct = (100 * pos / denom) if denom > 0 else 0
    vol_pct = (100 * volatile_n / n)
    
    if n >= 30:
        lo, hi = get_bootstrap_ci(pnls)
        ci = f"[{lo:+6.0f}, {hi:+6.0f}]"
    else:
        ci = "  UNRELIABLE_N<30 "
    
    return (f"    {label:<30} n={n:>4}  mean={mean:+7.0f} CI95={ci}  std={std:>6.0f}  "
            f"p25={p25:>+6.0f} p50={p50:>+6.0f} p75={p75:>+6.0f}  win%={win_pct:>2.0f} vol%={vol_pct:>2.0f}")

# ============================================================================
#  DATA LOADERS
# ============================================================================

def parse_detail_string(detail):
    data = {}
    clean = detail.replace('|', ' ').replace(',', ' ')
    pairs = clean.split()
    for p in pairs:
        if '=' in p:
            parts = p.split('=')
            if len(parts) == 2:
                try:
                    k, v = parts[0].strip(), parts[1].strip()
                    data[k] = float(v) if '.' in v else int(v)
                except: pass
    return data

def load_comprehensive_touches(path, trade_ids):
    touches = {}
    trade_keys = {extract_match_key(tid) for tid in trade_ids if extract_match_key(tid)}
    if not os.path.exists(path): return []
    with open(path, newline='', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        for r in reader:
            tag, sig_id = r['Tag'], r['GateReason']
            if tag == 'TOUCH':
                touches[sig_id] = {
                    'SignalId': sig_id, 'Timestamp': r['Timestamp'],
                    'SetId': r['ConditionSetId'], 'Dir': r['Direction'],
                    'Decision': 'TAKEN' if extract_match_key(sig_id) in trade_keys else 'DROPPED',
                    'Context': parse_detail_string(r['Detail'])
                }
            elif tag == 'TOUCH_OUTCOME':
                if sig_id in touches:
                    outcome = parse_detail_string(r['Detail'])
                    touches[sig_id].update({
                        'SimPnL': outcome.get('SIM_PNL', 0), 'FirstHit': r['Label'], 'HasOutcome': True
                    })
    return [t for t in touches.values() if t.get('HasOutcome')]

def load_trades(path):
    rows = []
    if not os.path.exists(path): return rows
    with open(path, newline='', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        for r in reader:
            try:
                p_str = r.get('Profit', '0').replace('$','').replace(',','')
                if '(' in p_str: p_str = '-' + p_str.replace('(','').replace(')','')
                r['Profit'] = float(p_str)
                r['SignalId'] = r.get('Entry name', '')
                rows.append(r)
            except: pass
    return rows

# ============================================================================
#  FORMATTING HELPERS
# ============================================================================

def header(title): print(f"\n{'='*95}\n  {title}\n{'='*95}")
def pnl(val): return f"${val:>+9,.2f}"
def avg(lst): return sum(lst) / len(lst) if lst else 0
def pct(num, den): return f"{num/den*100:.1f}%" if den > 0 else "0.0%"

# ============================================================================
#  ANALYSIS SECTIONS
# ============================================================================

def section_fingerprint(touches, trades, log_path, trades_path):
    header("0. RUN FINGERPRINT")
    print(f"    Run time:         {datetime.now():%Y-%m-%d %H:%M}")
    for name, path in [('Log.csv', log_path), ('Trades.csv', trades_path), ('Script', __file__)]:
        if os.path.exists(path):
            size = os.path.getsize(path)
            with open(path, 'rb') as f:
                h = hashlib.sha256(f.read()).hexdigest()[:16]
            print(f"    {name:<12} {size/1e6:>7.2f} MB  sha256[:16]={h}")
    taken_n = sum(1 for t in touches if t['Decision'] == 'TAKEN')
    print(f"    Trades loaded:    {len(trades)}")
    print(f"    Touches parsed:   {len(touches)}")
    print(f"    TAKEN matched:    {taken_n}  (expected ≈ {len(trades)})")
    if touches:
        ts = sorted(t['Timestamp'] for t in touches)
        print(f"    Date range:       {ts[0][:10]} → {ts[-1][:10]}")
    print("\n    Touches per SetId (and take rates):")
    counts = Counter(t['SetId'] for t in touches)
    takens = Counter(t['SetId'] for t in touches if t['Decision'] == 'TAKEN')
    for s, n in sorted(counts.items(), key=lambda x:-x[1]):
        tk = takens.get(s, 0)
        print(f"      {s:<30} n={n:>4}  TAKEN={tk:>4}  rate={100*tk/n:>3.0f}%")

def section_partitioned_summary(touches):
    header("1. RIGOROUS SUMMARY & OOS VALIDATION")
    dates = sorted(list(set(t['Timestamp'][:10] for t in touches)))
    if not dates: return
    split_date = dates[int(len(dates) * 0.6)]
    is_set, oos_set = [t for t in touches if t['Timestamp'][:10] <= split_date], [t for t in touches if t['Timestamp'][:10] > split_date]
    
    print(f"    Date Split: {split_date} | Total: {len(touches)} | IS: {len(is_set)} | OOS: {len(oos_set)}")
    for label, group in [("IN-SAMPLE", is_set), ("OUT-OF-SAMPLE", oos_set)]:
        print(f"\n  -- {label} --")
        print(pnl_stats(group, "  ALL signals in group"))

def section_per_source(touches, trades):
    header("2. PER-SOURCE FORWARD-RETURN BREAKDOWN & REALIZED PNL")
    by_set = defaultdict(list)
    for t in touches: by_set[t['SetId']].append(t)
    
    print("\n  [A] SIMULATED EDGE & FILTER PERFORMANCE")
    for sid, group in sorted(by_set.items()):
        print(f"\n  -- {sid} (total n={len(group)}) --")
        print(pnl_stats(group, "  ALL signals"))
        print(pnl_stats([t for t in group if t['Decision'] == 'TAKEN'], "  TAKEN (Post-Filter)"))
        print(pnl_stats([t for t in group if t['Decision'] == 'DROPPED'], "  DROPPED (Filtered)"))

    print("\n  [B] REALIZED P&L vs SIMULATED (EXIT LOGIC AUDIT)")
    print(f"    {'SetId':<25} {'Trades':>6} {'Realized PnL':>15} {'Sim PnL (1R)':>15} {'Divergence':>15}")
    print(f"    {'-'*25} {'-'*6} {'-'*15} {'-'*15} {'-'*15}")
    
    trade_pnls = defaultdict(list)
    for t in trades:
        sid = t['SignalId'].split(':')[0] if ':' in t['SignalId'] else t['SignalId']
        trade_pnls[sid].append(t['Profit'])
        
    for sid in sorted(by_set.keys()):
        taken_sim = [t['SimPnL'] for t in by_set[sid] if t['Decision'] == 'TAKEN']
        real = trade_pnls.get(sid, [])
        if not real and not taken_sim: continue
        sum_real, sum_sim = sum(real), sum(taken_sim)
        div = sum_real - sum_sim
        flag = " [!] Exit Bleed" if div < -1000 else ""
        print(f"    {sid:<25} {len(real):>6} {pnl(sum_real):>15} {pnl(sum_sim):>15} {pnl(div):>15}{flag}")

def section_conditional_edge(touches):
    header("3. CONDITIONAL FORENSICS (FEATURE IMPACT)")
    print("\n  -- Feature: H4 EMA Bias --")
    buckets = defaultdict(list)
    for t in touches:
        b = t['Context'].get('H4B', 0)
        buckets["Bullish" if b > 0 else ("Bearish" if b < 0 else "Neutral")].append(t)
    for l in sorted(buckets.keys()): print(pnl_stats(buckets[l], l))

    print("\n  -- Feature: Absorption Score (Tertiles) --")
    abs_vals = sorted([t['Context'].get('ABS', 0) for t in touches])
    if len(abs_vals) >= 3:
        t1, t2 = abs_vals[len(abs_vals)//3], abs_vals[2*len(abs_vals)//3]
        buckets = defaultdict(list)
        for t in touches:
            v = t['Context'].get('ABS', 0)
            label = f"Low (<{t1:.1f})" if v <= t1 else (f"High (>{t2:.1f})" if v > t2 else f"Mid ({t1:.1f}-{t2:.1f})")
            buckets[label].append(t)
        for l in sorted(buckets.keys()): print(pnl_stats(buckets[l], l))

def section_filter_audit(touches):
    header("4. FILTER PERFORMANCE AUDIT")
    taken_p = [t['SimPnL'] for t in touches if t['Decision'] == 'TAKEN']
    dropped_p = [t['SimPnL'] for t in touches if t['Decision'] == 'DROPPED']
    if not taken_p or not dropped_p: return
    low, high = get_diff_ci(taken_p, dropped_p)
    diff = avg(taken_p) - avg(dropped_p)
    sig = " (NOT SIGNIFICANT)"
    if len(taken_p) >= 30 and len(dropped_p) >= 30:
        if low > 0: sig = " (SUCCESS: SIGNIFICANT ALPHA)"
        elif high < 0: sig = " (WARNING: SIGNIFICANT DRAG)"
    print(f"    Filter Alpha (TAKEN - DROP): {pnl(diff)} per trade")
    print(f"    95% CI on Alpha:           [{pnl(low)}, {pnl(high)}]{sig}")

def section_sanity(touches, trades):
    header("5. SANITY CHECKS")
    errors, warnings = [], []
    taken = [t for t in touches if t['Decision'] == 'TAKEN']
    if len(trades) > 0 and not taken: errors.append(f"ID-MATCH BROKEN: 0 TAKEN but {len(trades)} trades exist")
    elif abs(len(taken) - len(trades)) > 5: warnings.append(f"TAKEN count ({len(taken)}) vs Trades loaded ({len(trades)}) diverge by >5")
    sets = set(t['SetId'] for t in touches)
    if len(sets) < 3: warnings.append(f"Instrumentation scope narrow: only {len(sets)} signal source(s) found: {sets}")
    missing_pnl = sum(1 for t in touches if t.get('SimPnL') is None)
    if missing_pnl: errors.append(f"{missing_pnl} touches missing SimPnL outcomes")
    both = sum(1 for t in touches if t.get('FirstHit') == 'BOTH_SAMEBAR')
    if both / max(len(touches),1) > 0.10: warnings.append(f"High BOTH_SAMEBAR rate: {100*both/len(touches):.1f}%")
    neither = sum(1 for t in touches if t.get('FirstHit') == 'NEITHER')
    if neither / max(len(touches),1) > 0.20: warnings.append(f"High NEITHER rate: {100*neither/len(touches):.1f}%")
    print(f"  ERRORS: {len(errors)}  |  WARNINGS: {len(warnings)}")
    for e in errors: print(f"    !!! {e}")
    for w in warnings: print(f"    *   {w}")
    if not errors and not warnings: print("    PASS: All data integrity checks satisfied.")

if __name__ == '__main__':
    lp, tp = os.path.join(BACKTEST, 'Log.csv'), os.path.join(BACKTEST, 'Trades.csv')
    trades = load_trades(tp); all_touches = load_comprehensive_touches(lp, {t['SignalId'] for t in trades})
    if not all_touches: print("No data found."); sys.exit()
    section_fingerprint(all_touches, trades, lp, tp)
    section_per_source(all_touches, trades)
    section_partitioned_summary(all_touches)
    section_conditional_edge(all_touches)
    section_filter_audit(all_touches)
    section_sanity(all_touches, trades)
    print(f"\n{'#'*95}\n#  END OF REPORT\n{'#'*95}\n")
