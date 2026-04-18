"""
Forward Return & Lost Signal Analysis
Compares current backtest vs phase3_6 baseline to understand:
  1. Which signals were TOOK in 3.6 but DROP now (the "lost trades")
  2. What forward returns say about every dropped signal
  3. Whether vetoes are correct or leaving money on the table
  4. Per-signal-source breakdown of order flow signals from Log.csv
"""

import csv
import os
import sys
from collections import defaultdict

# Fix Windows console encoding
if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'backtest')

# ─── Load filter_autopsy ──────────────────────────────────────────────
def load_autopsy(path):
    rows = []
    with open(path, newline='', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        for r in reader:
            r['SimPnL'] = float(r.get('SimPnL', 0) or 0)
            r['MFE'] = float(r.get('MFE', 0) or 0)
            r['MAE'] = float(r.get('MAE', 0) or 0)
            r['ActualPnL'] = float(r['ActualPnL']) if r.get('ActualPnL') else None
            rows.append(r)
    return rows

def load_log_evals(path):
    """Extract EVAL and RANK_VETO/RANK_WEAK entries from Log.csv"""
    evals = []
    vetoes = []
    with open(path, newline='', encoding='utf-8-sig') as f:
        reader = csv.reader(f)
        for row in reader:
            if len(row) < 21:
                continue
            tag = row[1].strip() if len(row) > 1 else ''
            detail = row[20].strip() if len(row) > 20 else ''

            if tag == 'EVAL':
                evals.append({
                    'Timestamp': row[0],
                    'Bar': row[2],
                    'Direction': row[3],
                    'Source': row[4],
                    'SetId': row[5],
                    'Score': row[6],
                    'Label': detail,
                    'Context': row[20] if len(row) > 20 else ''
                })
            elif tag == 'WARN' and ('RANK_VETO' in detail or 'RANK_WEAK' in detail):
                vetoes.append({
                    'Timestamp': row[0],
                    'Detail': detail
                })
    return evals, vetoes

def load_tape_autopsy(path):
    """Load tape_autopsy.csv for confluence breakdown of taken signals"""
    rows = []
    if not os.path.exists(path):
        return rows
    with open(path, newline='', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        for r in reader:
            rows.append(r)
    return rows

# ─── Analysis functions ───────────────────────────────────────────────

def analyze_autopsy(rows, label):
    """Full breakdown of a filter_autopsy dataset"""
    print(f"\n{'='*80}")
    print(f"  {label}")
    print(f"{'='*80}")

    took = [r for r in rows if r['Decision'] == 'TOOK']
    drop = [r for r in rows if r['Decision'] == 'DROP']

    print(f"\n  Total signals: {len(rows)}  |  TOOK: {len(took)}  |  DROP: {len(drop)}")
    print(f"  Take rate: {len(took)/len(rows)*100:.1f}%")

    # ── TOOK signals breakdown ──
    print(f"\n  ── TOOK signals ({len(took)}) ──")
    took_by_dir = defaultdict(list)
    for r in took:
        took_by_dir[r['Dir']].append(r)

    for d in ['L', 'S']:
        group = took_by_dir[d]
        if not group:
            continue
        actual = [r['ActualPnL'] for r in group if r['ActualPnL'] is not None]
        sim = [r['SimPnL'] for r in group]
        wins = [p for p in actual if p > 0]
        losses = [p for p in actual if p <= 0]

        first_target = sum(1 for r in group if r['FirstHit'] == 'TARGET')
        first_stop = sum(1 for r in group if r['FirstHit'] == 'STOP')
        first_both = sum(1 for r in group if r['FirstHit'] == 'BOTH_SAMEBAR')

        print(f"\n    {'LONG' if d=='L' else 'SHORT'} TOOK: {len(group)}")
        print(f"      Actual PnL:  sum=${sum(actual):,.0f}  avg=${sum(actual)/len(actual):,.0f}")
        print(f"      Sim PnL:     sum=${sum(sim):,.0f}  avg=${sum(sim)/len(sim):,.0f}")
        if wins:
            print(f"      Winners:     {len(wins)}  avg=${sum(wins)/len(wins):,.0f}")
        if losses:
            print(f"      Losers:      {len(losses)}  avg=${sum(losses)/len(losses):,.0f}")
        print(f"      FirstHit:    TARGET={first_target}  STOP={first_stop}  BOTH={first_both}")
        print(f"      Avg MFE:     ${sum(r['MFE'] for r in group)/len(group):,.0f}")
        print(f"      Avg MAE:     ${sum(r['MAE'] for r in group)/len(group):,.0f}")

    # ── DROP signals — THE MONEY LEFT ON THE TABLE ──
    print(f"\n  ── DROP signals ({len(drop)}) — Forward Return Analysis ──")
    drop_by_dir = defaultdict(list)
    for r in drop:
        drop_by_dir[r['Dir']].append(r)

    for d in ['L', 'S']:
        group = drop_by_dir[d]
        if not group:
            continue
        sim = [r['SimPnL'] for r in group]
        sim_wins = [r for r in group if r['SimPnL'] > 0]
        sim_losses = [r for r in group if r['SimPnL'] <= 0]

        first_target = [r for r in group if r['FirstHit'] == 'TARGET']
        first_stop = [r for r in group if r['FirstHit'] == 'STOP']
        first_both = [r for r in group if r['FirstHit'] == 'BOTH_SAMEBAR']

        print(f"\n    {'LONG' if d=='L' else 'SHORT'} DROP: {len(group)}")
        print(f"      SimPnL sum:  ${sum(sim):,.0f}  avg=${sum(sim)/len(sim):,.0f}")
        print(f"      Sim winners: {len(sim_wins)} ({len(sim_wins)/len(group)*100:.0f}%)  avg=${sum(r['SimPnL'] for r in sim_wins)/len(sim_wins):,.0f}" if sim_wins else "      Sim winners: 0")
        print(f"      Sim losers:  {len(sim_losses)} ({len(sim_losses)/len(group)*100:.0f}%)  avg=${sum(r['SimPnL'] for r in sim_losses)/len(sim_losses):,.0f}" if sim_losses else "      Sim losers: 0")
        print(f"      FirstHit:    TARGET={len(first_target)}  STOP={len(first_stop)}  BOTH={len(first_both)}")

        if first_target:
            avg_target_pnl = sum(r['SimPnL'] for r in first_target) / len(first_target)
            avg_target_mfe = sum(r['MFE'] for r in first_target) / len(first_target)
            print(f"      TARGET-first: avg SimPnL=${avg_target_pnl:,.0f}  avg MFE=${avg_target_mfe:,.0f}")
        if first_stop:
            avg_stop_pnl = sum(r['SimPnL'] for r in first_stop) / len(first_stop)
            avg_stop_mae = sum(r['MAE'] for r in first_stop) / len(first_stop)
            print(f"      STOP-first:   avg SimPnL=${avg_stop_pnl:,.0f}  avg MAE=${avg_stop_mae:,.0f}")

        # Hypothetical: if we took ALL drops, what would happen?
        if sim_wins and sim_losses:
            win_rate = len(sim_wins) / len(group) * 100
            expectancy = sum(sim) / len(group)
            print(f"      Hypothetical take-all: {win_rate:.0f}% WR, ${expectancy:,.0f}/trade, ${sum(sim):,.0f} total")

def compare_autopsies(current_rows, baseline_rows):
    """Find signals that were TOOK in baseline but DROP in current"""
    print(f"\n{'='*80}")
    print(f"  LOST TRADES: TOOK in phase3.6 → DROP in current")
    print(f"{'='*80}")

    # Index baseline by SignalId
    baseline_took = {r['SignalId']: r for r in baseline_rows if r['Decision'] == 'TOOK'}
    current_by_id = {r['SignalId']: r for r in current_rows}

    # Signals that existed in both but changed from TOOK to DROP
    lost = []
    for sig_id, base_r in baseline_took.items():
        cur_r = current_by_id.get(sig_id)
        if cur_r and cur_r['Decision'] == 'DROP':
            lost.append({
                'SignalId': sig_id,
                'Dir': base_r['Dir'],
                'Timestamp': base_r['Timestamp'],
                'BaseActual': base_r['ActualPnL'],
                'BaseExit': base_r.get('ExitName', ''),
                'CurSimPnL': cur_r['SimPnL'],
                'CurFirstHit': cur_r['FirstHit'],
                'CurMFE': cur_r['MFE'],
                'CurMAE': cur_r['MAE'],
            })

    # Signals that were TOOK in baseline but don't even appear in current
    missing = []
    for sig_id, base_r in baseline_took.items():
        if sig_id not in current_by_id:
            missing.append({
                'SignalId': sig_id,
                'Dir': base_r['Dir'],
                'Timestamp': base_r['Timestamp'],
                'BaseActual': base_r['ActualPnL'],
                'BaseExit': base_r.get('ExitName', ''),
            })

    if lost:
        print(f"\n  Found {len(lost)} signals that were TOOK in 3.6 but DROP now:\n")

        total_base_pnl = sum(r['BaseActual'] or 0 for r in lost)
        total_sim_pnl = sum(r['CurSimPnL'] for r in lost)

        for r in sorted(lost, key=lambda x: x['Timestamp']):
            base_pnl_str = f"${r['BaseActual']:>+9,.0f}" if r['BaseActual'] is not None else "     N/A"
            print(f"    {r['Timestamp']}  {r['Dir']:>1}  {r['SignalId']:<40}")
            print(f"      Phase3.6: {base_pnl_str} ({r['BaseExit']})  →  Current: DROP  SimPnL=${r['CurSimPnL']:>+9,.0f}  FirstHit={r['CurFirstHit']}  MFE={r['CurMFE']:.0f}  MAE={r['CurMAE']:.0f}")

        print(f"\n  TOTAL lost from these trades:")
        print(f"    Phase3.6 actual:  ${total_base_pnl:,.0f}")
        print(f"    Forward sim:      ${total_sim_pnl:,.0f}")

        # Breakdown by direction
        lost_longs = [r for r in lost if r['Dir'] == 'L']
        lost_shorts = [r for r in lost if r['Dir'] == 'S']

        if lost_longs:
            long_base = sum(r['BaseActual'] or 0 for r in lost_longs)
            long_sim = sum(r['CurSimPnL'] for r in lost_longs)
            long_target = sum(1 for r in lost_longs if r['CurFirstHit'] == 'TARGET')
            long_stop = sum(1 for r in lost_longs if r['CurFirstHit'] == 'STOP')
            print(f"\n    Lost LONGS:  {len(lost_longs)} trades, base=${long_base:,.0f}, sim=${long_sim:,.0f}")
            print(f"                 FirstHit: TARGET={long_target} STOP={long_stop}")

        if lost_shorts:
            short_base = sum(r['BaseActual'] or 0 for r in lost_shorts)
            short_sim = sum(r['CurSimPnL'] for r in lost_shorts)
            short_target = sum(1 for r in lost_shorts if r['CurFirstHit'] == 'TARGET')
            short_stop = sum(1 for r in lost_shorts if r['CurFirstHit'] == 'STOP')
            print(f"\n    Lost SHORTS: {len(lost_shorts)} trades, base=${short_base:,.0f}, sim=${short_sim:,.0f}")
            print(f"                 FirstHit: TARGET={short_target} STOP={short_stop}")

    if missing:
        print(f"\n  Additionally, {len(missing)} signals were TOOK in 3.6 but COMPLETELY ABSENT from current autopsy:")
        total_missing_pnl = sum(r['BaseActual'] or 0 for r in missing)
        for r in sorted(missing, key=lambda x: x['Timestamp']):
            base_pnl_str = f"${r['BaseActual']:>+9,.0f}" if r['BaseActual'] is not None else "     N/A"
            print(f"    {r['Timestamp']}  {r['Dir']:>1}  {base_pnl_str}  {r['SignalId']}")
        print(f"    Total missing PnL: ${total_missing_pnl:,.0f}")

def analyze_order_flow_evals(evals):
    """Analyze EVAL entries for non-ORB signals from Log.csv"""
    print(f"\n{'='*80}")
    print(f"  ORDER FLOW SIGNAL EVALUATIONS (from Log.csv)")
    print(f"{'='*80}")

    of_evals = [e for e in evals if not e['SetId'].startswith('ORB_')]

    if not of_evals:
        print("\n  NO order flow signal evaluations found in Log.csv!")
        print("  This means DeltaDivergence, ImbalanceReAggression, IcebergAbsorption,")
        print("  and HybridScalp never even produced a valid RawDecision, OR the log")
        print("  doesn't tag them as EVAL.")
        return

    by_set = defaultdict(list)
    for e in of_evals:
        by_set[e['SetId']].append(e)

    for set_id, group in sorted(by_set.items()):
        print(f"\n  {set_id}: {len(group)} evaluations")
        for e in group:
            print(f"    {e['Timestamp']}  {e['Direction']:>5}  Score={e['Score']:>3}  {e['Label'][:80]}")

def analyze_veto_breakdown(vetoes):
    """Breakdown of RANK_VETO and RANK_WEAK reasons"""
    print(f"\n{'='*80}")
    print(f"  VETO & WEAK SIGNAL BREAKDOWN")
    print(f"{'='*80}")

    veto_count = 0
    weak_count = 0
    veto_by_set = defaultdict(lambda: {'L': 0, 'S': 0, 'total_net': 0})
    weak_by_set = defaultdict(lambda: {'count': 0, 'total_net': 0, 'nets': []})

    for v in vetoes:
        detail = v['Detail']
        if 'RANK_VETO' in detail:
            veto_count += 1
            # Parse: RANK_VETO [SetId] L/S conf=A=x B=y C=z D=w Pen=p Net=n Mult=0.00 VETOED
            try:
                set_id = detail.split('[')[1].split(']')[0]
                direction = detail.split(']')[1].strip()[0]
                # Extract Net value
                net_part = detail.split('Net=')[1].split(' ')[0]
                net_val = int(net_part)
                veto_by_set[set_id][direction] += 1
                veto_by_set[set_id]['total_net'] += net_val
            except:
                pass
        elif 'RANK_WEAK' in detail:
            weak_count += 1
            try:
                set_id = detail.split('[')[1].split(']')[0]
                net_part = detail.split('Net=')[1].split(' ')[0]
                net_val = int(net_part)
                weak_by_set[set_id]['count'] += 1
                weak_by_set[set_id]['total_net'] += net_val
                weak_by_set[set_id]['nets'].append(net_val)
            except:
                pass

    print(f"\n  Total RANK_VETO: {veto_count}")
    for set_id, data in sorted(veto_by_set.items()):
        total = data['L'] + data['S']
        avg_net = data['total_net'] / total if total > 0 else 0
        print(f"    {set_id}: {total} vetoes (L={data['L']} S={data['S']}) avg Net={avg_net:.0f}")

    print(f"\n  Total RANK_WEAK: {weak_count}")
    for set_id, data in sorted(weak_by_set.items()):
        avg_net = data['total_net'] / data['count'] if data['count'] > 0 else 0
        nets = sorted(data['nets'])
        print(f"    {set_id}: {data['count']} weak signals, avg Net={avg_net:.1f}, range=[{min(nets)}-{max(nets)}]")

def analyze_dropped_by_date_direction(rows):
    """Show daily SimPnL of dropped signals by direction"""
    print(f"\n{'='*80}")
    print(f"  DAILY DROP FORWARD RETURNS — What the filter discarded")
    print(f"{'='*80}")

    drops = [r for r in rows if r['Decision'] == 'DROP']

    # Group by date and direction
    by_date = defaultdict(lambda: {'L': [], 'S': []})
    for r in drops:
        date = r['Timestamp'][:10]
        by_date[date][r['Dir']].append(r)

    print(f"\n  {'Date':<12} {'L_drop':>6} {'L_tgtPct':>8} {'L_simSum':>10} {'S_drop':>6} {'S_tgtPct':>8} {'S_simSum':>10}")
    print(f"  {'-'*12} {'-'*6} {'-'*8} {'-'*10} {'-'*6} {'-'*8} {'-'*10}")

    total_l_target = 0
    total_l_count = 0
    total_s_target = 0
    total_s_count = 0
    total_l_sim = 0
    total_s_sim = 0

    for date in sorted(by_date.keys()):
        longs = by_date[date]['L']
        shorts = by_date[date]['S']

        l_count = len(longs)
        l_target = sum(1 for r in longs if r['FirstHit'] == 'TARGET')
        l_sim = sum(r['SimPnL'] for r in longs)

        s_count = len(shorts)
        s_target = sum(1 for r in shorts if r['FirstHit'] == 'TARGET')
        s_sim = sum(r['SimPnL'] for r in shorts)

        l_pct = f"{l_target/l_count*100:.0f}%" if l_count else "-"
        s_pct = f"{s_target/s_count*100:.0f}%" if s_count else "-"

        total_l_count += l_count
        total_l_target += l_target
        total_s_count += s_count
        total_s_target += s_target
        total_l_sim += l_sim
        total_s_sim += s_sim

        if l_count or s_count:
            print(f"  {date:<12} {l_count:>6} {l_pct:>8} {l_sim:>+10,.0f} {s_count:>6} {s_pct:>8} {s_sim:>+10,.0f}")

    print(f"  {'-'*12} {'-'*6} {'-'*8} {'-'*10} {'-'*6} {'-'*8} {'-'*10}")
    l_pct = f"{total_l_target/total_l_count*100:.0f}%" if total_l_count else "-"
    s_pct = f"{total_s_target/total_s_count*100:.0f}%" if total_s_count else "-"
    print(f"  {'TOTAL':<12} {total_l_count:>6} {l_pct:>8} {total_l_sim:>+10,.0f} {total_s_count:>6} {s_pct:>8} {total_s_sim:>+10,.0f}")

def analyze_tape_autopsy_patterns(tape_rows):
    """Analyze which confluence patterns win vs lose in tape_autopsy"""
    print(f"\n{'='*80}")
    print(f"  TAPE AUTOPSY — Confluence Flag Patterns on TAKEN trades")
    print(f"{'='*80}")

    if not tape_rows:
        print("\n  No tape_autopsy.csv data available")
        return

    # Analyze each flag's impact on PnL
    flags_of_interest = ['bp+', 'bp-', 'vel+', 'vel-', 'swp+', 'swp-',
                         'tice+', 'tice-', 'trap+', 'ice+', 'exh+', 'unf+', 'bd+', 'vwap+']

    for direction in ['Long', 'Short']:
        dir_rows = [r for r in tape_rows if r['Dir'] == direction]
        if not dir_rows:
            continue

        print(f"\n  ── {direction} trades ({len(dir_rows)}) ──")

        wins = [r for r in dir_rows if float(r.get('PnL', 0)) > 0]
        losses = [r for r in dir_rows if float(r.get('PnL', 0)) <= 0]

        print(f"    Winners: {len(wins)}  Losers: {len(losses)}")
        print(f"\n    {'Flag':<8} {'Win%':>5} {'W_avg':>8} {'L_avg':>8} {'Count':>6} {'Edge':>8}")
        print(f"    {'-'*8} {'-'*5} {'-'*8} {'-'*8} {'-'*6} {'-'*8}")

        for flag in flags_of_interest:
            col = flag.replace('+', '+').replace('-', '-')
            flag_on = [r for r in dir_rows if r.get(col, '0') == '1']
            flag_off = [r for r in dir_rows if r.get(col, '0') != '1']

            if not flag_on:
                continue

            on_wins = [r for r in flag_on if float(r.get('PnL', 0)) > 0]
            on_losses = [r for r in flag_on if float(r.get('PnL', 0)) <= 0]
            on_pnls = [float(r.get('PnL', 0)) for r in flag_on]

            win_pct = len(on_wins) / len(flag_on) * 100 if flag_on else 0
            avg_win = sum(float(r['PnL']) for r in on_wins) / len(on_wins) if on_wins else 0
            avg_loss = sum(float(r['PnL']) for r in on_losses) / len(on_losses) if on_losses else 0
            edge = sum(on_pnls) / len(on_pnls)

            print(f"    {flag:<8} {win_pct:>4.0f}% {avg_win:>+8,.0f} {avg_loss:>+8,.0f} {len(flag_on):>6} {edge:>+8,.0f}")

def summary_comparison(current_rows, baseline_rows):
    """Side-by-side summary of key metrics"""
    print(f"\n{'='*80}")
    print(f"  SUMMARY: Current vs Phase 3.6 Baseline")
    print(f"{'='*80}")

    for label, rows in [("Current", current_rows), ("Phase 3.6", baseline_rows)]:
        took = [r for r in rows if r['Decision'] == 'TOOK']
        drop = [r for r in rows if r['Decision'] == 'DROP']

        took_actual = [r['ActualPnL'] for r in took if r['ActualPnL'] is not None]
        drop_sim = [r['SimPnL'] for r in drop]
        drop_target = [r for r in drop if r['FirstHit'] == 'TARGET']
        drop_stop = [r for r in drop if r['FirstHit'] == 'STOP']

        took_l = [r for r in took if r['Dir'] == 'L']
        took_s = [r for r in took if r['Dir'] == 'S']

        print(f"\n  {label}:")
        print(f"    Signals generated:   {len(rows)}")
        print(f"    TOOK:                {len(took)} ({len(took_l)}L / {len(took_s)}S)")
        print(f"    DROP:                {len(drop)}")
        print(f"    Take rate:           {len(took)/len(rows)*100:.1f}%")
        print(f"    Actual PnL (took):   ${sum(took_actual):,.0f}")
        if drop_sim:
            print(f"    Sim PnL (dropped):   ${sum(drop_sim):,.0f}")
            print(f"    Drop TARGET-first:   {len(drop_target)} ({len(drop_target)/len(drop)*100:.0f}%)")
            print(f"    Drop STOP-first:     {len(drop_stop)} ({len(drop_stop)/len(drop)*100:.0f}%)")

            # Money left on table from TARGET-first drops
            target_sim = sum(r['SimPnL'] for r in drop_target)
            print(f"    TARGET-first sim $:  ${target_sim:,.0f}")

# ─── MAIN ─────────────────────────────────────────────────────────────

if __name__ == '__main__':
    current_autopsy_path = os.path.join(BASE, 'filter_autopsy.csv')
    baseline_autopsy_path = os.path.join(BASE, 'baselines', 'phase3_6_2026-04-16', 'filter_autopsy.csv')
    log_path = os.path.join(BASE, 'Log.csv')
    tape_path = os.path.join(BASE, 'tape_autopsy.csv')

    current = load_autopsy(current_autopsy_path)
    baseline = load_autopsy(baseline_autopsy_path)
    tape = load_tape_autopsy(tape_path)

    # 1. Summary comparison
    summary_comparison(current, baseline)

    # 2. Current autopsy deep dive
    analyze_autopsy(current, "CURRENT BACKTEST — Forward Return Analysis")

    # 3. Baseline autopsy for reference
    analyze_autopsy(baseline, "PHASE 3.6 BASELINE — Forward Return Analysis")

    # 4. Lost trades: TOOK in 3.6 but DROP now
    compare_autopsies(current, baseline)

    # 5. Daily drop analysis
    analyze_dropped_by_date_direction(current)

    # 6. Tape autopsy patterns
    analyze_tape_autopsy_patterns(tape)

    # 7. Order flow signal evaluations from log
    if os.path.exists(log_path):
        evals, vetoes = load_log_evals(log_path)
        analyze_order_flow_evals(evals)
        analyze_veto_breakdown(vetoes)

    print(f"\n{'='*80}")
    print(f"  ANALYSIS COMPLETE")
    print(f"{'='*80}")
