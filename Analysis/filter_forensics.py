"""
FILTER FORENSICS — Why are winners being killed? Why are losers getting through?

Two questions answered with data:
  Q1: LEAKED WINNERS  — Signals the filter killed that would have hit target
  Q2: PASSED LOSERS   — Signals the filter allowed that hit stop

Data sources:
  - Log.csv:    EVAL, FLOW, STRUCT, WARN(RANK_*), TOUCH_OUTCOME rows
  - Trades.csv: Actual executed trades with real PnL
  - filter_autopsy.csv: TOOK/DROP decisions with sim PnL
  - tape_autopsy.csv:   Confluence flags on taken trades

Matching strategy:
  TOUCH_OUTCOME fires at RESOLUTION bar (different timestamp than EVAL/RANK).
  GateReason format = "CID:YYYYMMDD:origBar"
  We match via origBar extracted from GateReason, NOT by timestamp.
"""

import csv
import os
import sys
import re
from collections import defaultdict, Counter

if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'backtest')


# ============================================================================
#  DATA LOADING
# ============================================================================

def load_log(path):
    """Parse Log.csv into structured buckets, indexed correctly."""
    evals = {}        # key: (bar, cid) -> {ts, dir, score, context}
    flow_by_bar = {}  # key: bar_str -> parsed dict
    struct_by_bar = {}
    rank_decisions = {}  # key: (ts, cid) -> {status, net, mult, detail}
    touch_outcomes = {}  # key: "CID:YYYYMMDD:bar" -> {pnl, hit, mfe, mae, bars}

    with open(path, newline='', encoding='utf-8-sig') as f:
        reader = csv.reader(f)
        header = next(reader)
        for row in reader:
            if len(row) < 21:
                continue
            ts, tag, bar = row[0], row[1].strip(), row[2]
            cid = row[5] if len(row) > 5 else ''
            detail = row[20] if len(row) > 20 else ''

            if tag == 'EVAL':
                evals[(bar, cid)] = {
                    'ts': ts, 'bar': bar, 'cid': cid,
                    'dir': row[3], 'score': row[6],
                    'context': detail
                }

            elif tag == 'FLOW':
                parsed = {}
                for m in re.finditer(r'([A-Z_]+)=([-+]?[\d.]+)', detail):
                    parsed[m.group(1)] = float(m.group(2))
                flow_by_bar[bar] = parsed

            elif tag == 'STRUCT':
                parsed = {}
                for m in re.finditer(r'([A-Z_a-z]+)=([-+]?[\d.]+)', detail):
                    parsed[m.group(1)] = float(m.group(2))
                struct_by_bar[bar] = parsed

            elif tag == 'WARN' and 'RANK_' in detail:
                cid_m = re.search(r'\[([^\]]+)\]', detail)
                rcid = cid_m.group(1) if cid_m else ''

                if 'RANK_WIN' in detail:
                    status = 'WIN'
                elif 'RANK_VETO' in detail:
                    status = 'VETO'
                elif 'RANK_WEAK' in detail:
                    status = 'WEAK'
                elif 'RANK_CONFLICT' in detail:
                    status = 'CONFLICT'
                else:
                    status = 'OTHER'

                net_m = re.search(r'Net=(-?\d+)', detail)
                mult_m = re.search(r'Mult=([\d.]+)', detail)
                rank_decisions[(ts, rcid)] = {
                    'status': status,
                    'net': int(net_m.group(1)) if net_m else 0,
                    'mult': float(mult_m.group(1)) if mult_m else 0,
                    'detail': detail
                }

            elif tag == 'TOUCH_OUTCOME':
                gate = row[18] if len(row) > 18 else ''
                pnl_m = re.search(r'SIM_PNL=([-+]?[\d.]+)', detail)
                hit_m = re.search(r'FIRST_HIT=(\w+)', detail)
                mfe_m = re.search(r'MFE=([\d.]+)', detail)
                mae_m = re.search(r'MAE=([\d.]+)', detail)
                bars_m = re.search(r'BARS_TO_HIT=(\d+)', detail)

                touch_outcomes[gate] = {
                    'pnl': float(pnl_m.group(1)) if pnl_m else 0,
                    'hit': hit_m.group(1) if hit_m else '',
                    'mfe': float(mfe_m.group(1)) if mfe_m else 0,
                    'mae': float(mae_m.group(1)) if mae_m else 0,
                    'bars': int(bars_m.group(1)) if bars_m else 0,
                    'gate': gate, 'resolution_ts': ts
                }

    return evals, flow_by_bar, struct_by_bar, rank_decisions, touch_outcomes


def load_trades(path):
    """Load Trades.csv with real PnL. Falls back to Executions.csv if empty."""
    trades = {}

    # Try Trades.csv first
    if os.path.exists(path) and os.path.getsize(path) > 10:
        with open(path, newline='', encoding='utf-8-sig') as f:
            reader = csv.DictReader(f)
            for r in reader:
                sig_id = r.get('Entry name', '')
                p_str = r.get('Profit', '0').replace('$', '').replace(',', '')
                if '(' in p_str:
                    p_str = '-' + p_str.replace('(', '').replace(')', '')
                trades[sig_id] = {
                    'profit': float(p_str),
                    'mae': float(r.get('MAE', '0').replace('$', '').replace(',', '')),
                    'mfe': float(r.get('MFE', '0').replace('$', '').replace(',', '')),
                    'exit': r.get('Exit name', ''),
                    'bars': int(r.get('Bars', '0')),
                    'entry_time': r.get('Entry time', ''),
                    'exit_time': r.get('Exit time', ''),
                }
        if trades:
            return trades

    # Fallback: reconstruct from Executions.csv
    exec_path = os.path.join(os.path.dirname(path), 'Executions.csv')
    if not os.path.exists(exec_path):
        return trades

    entries, exits = [], []
    with open(exec_path, newline='', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        for row in reader:
            ex_type = row.get('E/X', '').strip()
            name = row.get('Name', '').strip()
            price = float(row.get('Price', '0'))
            action = row.get('Action', '').strip()
            time_str = row.get('Time', '')
            if ex_type == 'Entry':
                entries.append({'name': name, 'price': price, 'action': action, 'time': time_str})
            elif ex_type == 'Exit':
                exits.append({'name': name, 'price': price, 'action': action, 'time': time_str})

    for i in range(min(len(entries), len(exits))):
        e, x = entries[i], exits[i]
        is_long = e['action'] == 'Buy'
        pnl = (x['price'] - e['price']) * 20 * (1 if is_long else -1)
        trades[e['name']] = {
            'profit': pnl, 'mae': 0, 'mfe': 0,
            'exit': x['name'], 'bars': 0,
            'entry_time': e['time'], 'exit_time': x['time'],
        }

    return trades


# ============================================================================
#  CORE MATCHING: Build unified signal records
# ============================================================================

def _make_gate_keys(cid, ts, bar):
    """Generate all possible gate key formats for matching.

    TOUCH_OUTCOME uses: CID:YYYYMMDD_HHMMSS:bar
    Trades.csv uses:    CID:YYYYMMDD:bar
    We generate both so matching works regardless of format.
    """
    date_str = ts[:10].replace('-', '')  # "2026-03-02" -> "20260302"
    time_str = ts[11:19].replace(':', '')  # "10:05:00" -> "100500"
    return [
        f"{cid}:{date_str}_{time_str}:{bar}",  # TOUCH format
        f"{cid}:{date_str}:{bar}",              # Trades format
    ]


def build_signal_records(evals, flow_by_bar, struct_by_bar, rank_decisions, touch_outcomes, trades):
    """
    Build one record per signal with: eval context, flow data, rank decision,
    sim outcome, and actual trade result (if taken).

    Matching chain:
      EVAL(bar, cid) -> RANK(ts, cid) via shared timestamp
      EVAL(bar, cid) -> TOUCH_OUTCOME via GateReason (multiple formats tried)
      EVAL(bar, cid) -> Trades.csv via Entry name (multiple formats tried)
    """
    records = []

    for (bar, cid), ev in evals.items():
        ts = ev['ts']
        gate_keys = _make_gate_keys(cid, ts, bar)

        # Rank decision (matched by timestamp + cid)
        rank = rank_decisions.get((ts, cid), None)
        rank_status = rank['status'] if rank else 'PRE_RANK_KILL'
        rank_net = rank['net'] if rank else 0
        rank_mult = rank['mult'] if rank else 0
        rank_detail = rank['detail'] if rank else ''

        # Sim outcome (try all gate key formats)
        outcome = None
        for gk in gate_keys:
            outcome = touch_outcomes.get(gk)
            if outcome:
                break
        sim_pnl = outcome['pnl'] if outcome else None
        sim_hit = outcome['hit'] if outcome else ''
        sim_mfe = outcome['mfe'] if outcome else 0
        sim_mae = outcome['mae'] if outcome else 0

        # Real trade (try all gate key formats)
        trade = None
        for gk in gate_keys:
            trade = trades.get(gk)
            if trade:
                break
        real_pnl = trade['profit'] if trade else None
        real_exit = trade['exit'] if trade else ''

        # Flow context at this bar
        flow = flow_by_bar.get(bar, {})
        struct = struct_by_bar.get(bar, {})

        records.append({
            'bar': bar, 'cid': cid, 'ts': ts, 'dir': ev['dir'],
            'score': ev['score'], 'context': ev['context'],
            'rank_status': rank_status, 'rank_net': rank_net,
            'rank_mult': rank_mult, 'rank_detail': rank_detail,
            'sim_pnl': sim_pnl, 'sim_hit': sim_hit,
            'sim_mfe': sim_mfe, 'sim_mae': sim_mae,
            'real_pnl': real_pnl, 'real_exit': real_exit,
            'gate_key': gate_keys[0],
            'was_traded': trade is not None,
            # Flow features
            'BD': flow.get('BD', 0), 'CD': flow.get('CD', 0),
            'ABS': flow.get('ABS', 0),
            'SBULL': flow.get('SBULL', 0), 'SBEAR': flow.get('SBEAR', 0),
            'BDIV': flow.get('BDIV', 0), 'BERDIV': flow.get('BERDIV', 0),
            'IZB': flow.get('IZB', 0), 'IZS': flow.get('IZS', 0),
            'REGIME': flow.get('REGIME', 0), 'STR': flow.get('STR', 0),
            'DSL': flow.get('DSL', 0), 'DSH': flow.get('DSH', 0),
            'DEX': flow.get('DEX', 0), 'SW': flow.get('SW', 0),
            'TRD': flow.get('TRD', 0), 'HASVOL': flow.get('HASVOL', 0),
            # Struct features
            'POC': struct.get('POC', 0), 'VAH': struct.get('VAH', 0),
            'VAL': struct.get('VAL', 0), 'SKEW': struct.get('SKEW', 0),
            'PP': struct.get('PP', 0),
        })

    return records


# ============================================================================
#  ANALYSIS: Q1 — LEAKED WINNERS (filtered out but would have won)
# ============================================================================

def analyze_leaked_winners(records):
    """Signals killed by filter that sim shows would have hit target."""
    print("\n" + "=" * 95)
    print("  Q1: LEAKED WINNERS — Profitable signals the filter killed")
    print("=" * 95)

    killed = [r for r in records
              if not r['was_traded'] and r['sim_pnl'] is not None and r['sim_pnl'] > 0
              and r['rank_status'] in ('VETO', 'WEAK', 'PRE_RANK_KILL')]

    if not killed:
        print("  No leaked winners found.")
        return

    # Group by kill reason and signal type
    by_reason_cid = defaultdict(list)
    for r in killed:
        by_reason_cid[(r['rank_status'], r['cid'])].append(r)

    print(f"\n  {'KILL REASON':<15} {'SIGNAL':<28} {'COUNT':>5} {'SIM $':>10} {'AVG $':>8} {'AVG MFE':>8} {'AVG MAE':>8}")
    print(f"  {'-'*15} {'-'*28} {'-'*5} {'-'*10} {'-'*8} {'-'*8} {'-'*8}")

    total_leaked = 0
    rows_for_detail = []

    for (reason, cid), group in sorted(by_reason_cid.items(), key=lambda x: -sum(r['sim_pnl'] for r in x[1])):
        total = sum(r['sim_pnl'] for r in group)
        avg = total / len(group)
        avg_mfe = sum(r['sim_mfe'] for r in group) / len(group)
        avg_mae = sum(r['sim_mae'] for r in group) / len(group)
        total_leaked += total
        print(f"  {reason:<15} {cid:<28} {len(group):>5} {total:>+10,.0f} {avg:>+8,.0f} {avg_mfe:>8,.0f} {avg_mae:>8,.0f}")
        rows_for_detail.extend(group)

    print(f"\n  TOTAL LEAKED PROFIT: ${total_leaked:>+,.0f} across {len(killed)} signals")

    # Top 15 individual leaked winners
    print(f"\n  TOP 15 LEAKED WINNERS:")
    print(f"  {'TIME':<20} {'DIR':>3} {'SIGNAL':<25} {'KILL':>8} {'SIM $':>8} {'MFE':>6} {'MAE':>6} {'Net':>4} {'FLOW CONTEXT'}")
    print(f"  {'-'*20} {'-'*3} {'-'*25} {'-'*8} {'-'*8} {'-'*6} {'-'*6} {'-'*4} {'-'*40}")

    for r in sorted(killed, key=lambda x: -x['sim_pnl'])[:15]:
        flow_ctx = f"BD={r['BD']:.0f} CD={r['CD']:.0f} ABS={r['ABS']:.1f} SB={r['SBULL']:.0f}/{r['SBEAR']:.0f}"
        print(f"  {r['ts']:<20} {r['dir']:>3} {r['cid']:<25} {r['rank_status']:>8} {r['sim_pnl']:>+8,.0f} {r['sim_mfe']:>6,.0f} {r['sim_mae']:>6,.0f} {r['rank_net']:>4} {flow_ctx}")

    # Pattern analysis: what flow conditions characterize leaked winners?
    print(f"\n  FLOW PATTERN: Leaked winners vs correctly killed")
    correctly_killed = [r for r in records
                        if not r['was_traded'] and r['sim_pnl'] is not None and r['sim_pnl'] <= 0
                        and r['rank_status'] in ('VETO', 'WEAK', 'PRE_RANK_KILL')]

    if killed and correctly_killed:
        features = ['BD', 'CD', 'ABS', 'SBULL', 'SBEAR', 'REGIME', 'STR', 'SKEW', 'DSL', 'DSH']
        print(f"  {'FEATURE':<12} {'LEAKED WIN avg':>14} {'CORRECT KILL avg':>16} {'DELTA':>10} {'DIRECTION'}")
        print(f"  {'-'*12} {'-'*14} {'-'*16} {'-'*10} {'-'*12}")
        for feat in features:
            avg_leak = sum(r[feat] for r in killed) / len(killed)
            avg_correct = sum(r[feat] for r in correctly_killed) / len(correctly_killed)
            delta = avg_leak - avg_correct
            direction = "HIGHER in wins" if delta > 0.1 else ("LOWER in wins" if delta < -0.1 else "similar")
            print(f"  {feat:<12} {avg_leak:>14.2f} {avg_correct:>16.2f} {delta:>+10.2f} {direction}")


# ============================================================================
#  ANALYSIS: Q2 — PASSED LOSERS (filter let through but lost money)
# ============================================================================

def analyze_passed_losers(records):
    """Signals the filter allowed that ended up losing money."""
    print("\n" + "=" * 95)
    print("  Q2: PASSED LOSERS — Why are traded signals still losing?")
    print("=" * 95)

    traded = [r for r in records if r['was_traded']]
    winners = [r for r in traded if r['real_pnl'] is not None and r['real_pnl'] > 0]
    losers = [r for r in traded if r['real_pnl'] is not None and r['real_pnl'] <= 0]

    if not traded:
        print("  No traded signals found.")
        return

    total_win = sum(r['real_pnl'] for r in winners)
    total_loss = sum(r['real_pnl'] for r in losers)

    print(f"\n  Traded: {len(traded)}  Winners: {len(winners)} (${total_win:+,.0f})  Losers: {len(losers)} (${total_loss:+,.0f})")
    print(f"  Win rate: {len(winners)/len(traded)*100:.1f}%  Avg win: ${total_win/max(len(winners),1):,.0f}  Avg loss: ${total_loss/max(len(losers),1):,.0f}")

    # Losers by signal type
    print(f"\n  LOSERS BY SIGNAL TYPE:")
    print(f"  {'SIGNAL':<28} {'#LOSS':>5} {'TOTAL $':>10} {'AVG $':>8} {'AVG MAE':>8} {'AVG MFE':>8} {'#STOP':>5} {'#TIME':>5}")
    print(f"  {'-'*28} {'-'*5} {'-'*10} {'-'*8} {'-'*8} {'-'*8} {'-'*5} {'-'*5}")

    loser_by_cid = defaultdict(list)
    for r in losers:
        loser_by_cid[r['cid']].append(r)

    for cid, group in sorted(loser_by_cid.items(), key=lambda x: sum(r['real_pnl'] for r in x[1])):
        total = sum(r['real_pnl'] for r in group)
        avg = total / len(group)
        avg_mae = sum(float(r['sim_mae']) for r in group) / len(group)
        avg_mfe = sum(float(r['sim_mfe']) for r in group) / len(group)
        n_stop = sum(1 for r in group if 'Stop' in str(r['real_exit']))
        n_time = sum(1 for r in group if 'Time' in str(r['real_exit']) or 'TIME' in str(r['real_exit']))
        print(f"  {cid:<28} {len(group):>5} {total:>+10,.0f} {avg:>+8,.0f} {avg_mae:>8,.0f} {avg_mfe:>8,.0f} {n_stop:>5} {n_time:>5}")

    # Every losing trade with flow context
    print(f"\n  ALL {len(losers)} LOSING TRADES:")
    print(f"  {'TIME':<20} {'DIR':>3} {'SIGNAL':<25} {'REAL $':>8} {'EXIT':<8} {'MFE':>6} {'MAE':>6} {'Net':>4} {'FLOW CONTEXT'}")
    print(f"  {'-'*20} {'-'*3} {'-'*25} {'-'*8} {'-'*8} {'-'*6} {'-'*6} {'-'*4} {'-'*40}")

    for r in sorted(losers, key=lambda x: x['real_pnl']):
        flow_ctx = f"BD={r['BD']:.0f} CD={r['CD']:.0f} ABS={r['ABS']:.1f} SB={r['SBULL']:.0f}/{r['SBEAR']:.0f}"
        exit_short = r['real_exit'][:8] if r['real_exit'] else ''
        print(f"  {r['ts']:<20} {r['dir']:>3} {r['cid']:<25} {r['real_pnl']:>+8,.0f} {exit_short:<8} {r['sim_mfe']:>6,.0f} {r['sim_mae']:>6,.0f} {r['rank_net']:>4} {flow_ctx}")

    # Pattern analysis: what distinguishes winners from losers among TRADED signals?
    print(f"\n  FLOW PATTERN: Traded winners vs traded losers")
    if winners and losers:
        features = ['BD', 'CD', 'ABS', 'SBULL', 'SBEAR', 'REGIME', 'STR', 'SKEW', 'DSL', 'DSH']
        print(f"  {'FEATURE':<12} {'WINNER avg':>14} {'LOSER avg':>14} {'DELTA':>10} {'FILTER HINT'}")
        print(f"  {'-'*12} {'-'*14} {'-'*14} {'-'*10} {'-'*30}")
        for feat in features:
            avg_win = sum(r[feat] for r in winners) / len(winners)
            avg_loss = sum(r[feat] for r in losers) / len(losers)
            delta = avg_win - avg_loss
            if abs(delta) > 0.3:
                hint = f"Block when {feat} {'<' if delta > 0 else '>'} {(avg_win+avg_loss)/2:.1f}"
            else:
                hint = "no edge"
            print(f"  {feat:<12} {avg_win:>14.2f} {avg_loss:>14.2f} {delta:>+10.2f} {hint}")

    # Sim vs Real divergence (exit quality)
    print(f"\n  SIM vs REAL PnL DIVERGENCE (exit bleed):")
    print(f"  {'SIGNAL':<28} {'SIM $':>10} {'REAL $':>10} {'BLEED $':>10} {'BLEED %':>8}")
    print(f"  {'-'*28} {'-'*10} {'-'*10} {'-'*10} {'-'*8}")

    bleed_by_cid = defaultdict(lambda: {'sim': 0, 'real': 0, 'n': 0})
    for r in traded:
        if r['sim_pnl'] is not None and r['real_pnl'] is not None:
            bleed_by_cid[r['cid']]['sim'] += r['sim_pnl']
            bleed_by_cid[r['cid']]['real'] += r['real_pnl']
            bleed_by_cid[r['cid']]['n'] += 1

    for cid, d in sorted(bleed_by_cid.items()):
        bleed = d['real'] - d['sim']
        bleed_pct = (bleed / abs(d['sim']) * 100) if d['sim'] != 0 else 0
        print(f"  {cid:<28} {d['sim']:>+10,.0f} {d['real']:>+10,.0f} {bleed:>+10,.0f} {bleed_pct:>+7.1f}%")


# ============================================================================
#  ANALYSIS: Q3 — VETO CONDITION FORENSICS
# ============================================================================

def analyze_veto_conditions(records):
    """Which veto conditions are killing the most profit?"""
    print("\n" + "=" * 95)
    print("  Q3: VETO CONDITION FORENSICS — Which conditions kill profitable signals?")
    print("=" * 95)

    vetoed = [r for r in records if r['rank_status'] == 'VETO' and r['sim_pnl'] is not None]

    if not vetoed:
        print("  No vetoed signals with sim data.")
        return

    # Since ConfluenceEngine doesn't log which specific veto fired,
    # we infer from FLOW flags at the signal bar.
    condition_impact = defaultdict(lambda: {'count': 0, 'pnl': 0, 'wins': 0, 'losses': 0})

    for r in vetoed:
        is_long = r['dir'] == 'Long'
        fired = []

        # Rule 1: Divergence
        if is_long and r['BERDIV'] > 0:
            fired.append('BearDivergence')
        if not is_long and r['BDIV'] > 0:
            fired.append('BullDivergence')

        # Rule 2: ImbalZone
        if is_long and r['IZS'] > 0:
            fired.append('ImbalZone_Bear')
        if not is_long and r['IZB'] > 0:
            fired.append('ImbalZone_Bull')

        # Rule 3: Exhausted CD
        if is_long and r['CD'] > 2500 and r['SBULL'] < 3:
            fired.append('ExhaustedCD')
        if not is_long and r['CD'] < -2500 and r['SBEAR'] < 3:
            fired.append('ExhaustedCD')

        # Trapped (TRD field: +1 = trapped longs, -1 = trapped shorts)
        if is_long and r['TRD'] > 0:
            fired.append('TrappedLongs')
        if not is_long and r['TRD'] < 0:
            fired.append('TrappedShorts')

        if not fired:
            fired.append('HIDDEN(Iceberg/Sweep/NonConf/BrickWall)')

        for cond in fired:
            condition_impact[cond]['count'] += 1
            condition_impact[cond]['pnl'] += r['sim_pnl']
            if r['sim_pnl'] > 0:
                condition_impact[cond]['wins'] += 1
            else:
                condition_impact[cond]['losses'] += 1

    print(f"\n  {'VETO CONDITION':<40} {'COUNT':>5} {'SIM $':>10} {'WIN%':>6} {'VERDICT':>12}")
    print(f"  {'-'*40} {'-'*5} {'-'*10} {'-'*6} {'-'*12}")

    for cond, d in sorted(condition_impact.items(), key=lambda x: -x[1]['pnl']):
        wr = d['wins'] / (d['wins'] + d['losses']) * 100 if (d['wins'] + d['losses']) > 0 else 0
        verdict = 'LEAK' if d['pnl'] > 500 else ('CORRECT' if d['pnl'] < -500 else 'MARGINAL')
        print(f"  {cond:<40} {d['count']:>5} {d['pnl']:>+10,.0f} {wr:>5.1f}% {verdict:>12}")


# ============================================================================
#  ANALYSIS: Q4 — CONFLUENCE SCORE EDGE MAP
# ============================================================================

def analyze_score_edge(records):
    """Map confluence net score to actual win rate and PnL."""
    print("\n" + "=" * 95)
    print("  Q4: CONFLUENCE SCORE EDGE MAP — Does higher score = better outcome?")
    print("=" * 95)

    with_outcome = [r for r in records if r['sim_pnl'] is not None and r['rank_status'] in ('WIN', 'VETO', 'WEAK')]

    if not with_outcome:
        print("  No scored signals with outcomes.")
        return

    # Bucket by net score ranges
    buckets = defaultdict(lambda: {'count': 0, 'pnl': 0, 'wins': 0, 'traded': 0})
    for r in with_outcome:
        net = r['rank_net']
        if net < 0:
            bucket = 'negative'
        elif net < 20:
            bucket = '0-19'
        elif net < 40:
            bucket = '20-39'
        elif net < 60:
            bucket = '40-59'
        elif net < 80:
            bucket = '60-79'
        else:
            bucket = '80+'

        buckets[bucket]['count'] += 1
        buckets[bucket]['pnl'] += r['sim_pnl']
        if r['sim_pnl'] > 0:
            buckets[bucket]['wins'] += 1
        if r['was_traded']:
            buckets[bucket]['traded'] += 1

    print(f"\n  {'NET SCORE':<12} {'COUNT':>6} {'TRADED':>6} {'SIM $':>10} {'AVG $':>8} {'WIN%':>6} {'EDGE'}")
    print(f"  {'-'*12} {'-'*6} {'-'*6} {'-'*10} {'-'*8} {'-'*6} {'-'*20}")

    for bucket in ['negative', '0-19', '20-39', '40-59', '60-79', '80+']:
        d = buckets[bucket]
        if d['count'] == 0:
            continue
        avg = d['pnl'] / d['count']
        wr = d['wins'] / d['count'] * 100
        edge = 'POSITIVE' if avg > 50 else ('NEGATIVE' if avg < -50 else 'FLAT')
        print(f"  {bucket:<12} {d['count']:>6} {d['traded']:>6} {d['pnl']:>+10,.0f} {avg:>+8,.0f} {wr:>5.1f}% {edge}")


# ============================================================================
#  ANALYSIS: Q5 — PER-SIGNAL REPORT CARD
# ============================================================================

def signal_report_card(records):
    """Per-signal-type breakdown: traded PnL, sim PnL, filter accuracy."""
    print("\n" + "=" * 95)
    print("  Q5: SIGNAL REPORT CARD")
    print("=" * 95)

    by_cid = defaultdict(list)
    for r in records:
        by_cid[r['cid']].append(r)

    # Skip pre-filter-only signals (HybridScalp, SMF_Full_Switch)
    skip = {'HybridScalp_v1', 'SMF_Full_Switch_v1'}

    print(f"\n  {'SIGNAL':<28} {'EVAL':>5} {'TRADED':>6} {'REAL $':>10} {'FILT':>5} {'FILT SIM $':>10} {'FILT WR%':>8} {'FILTER OK?':<12}")
    print(f"  {'-'*28} {'-'*5} {'-'*6} {'-'*10} {'-'*5} {'-'*10} {'-'*8} {'-'*12}")

    for cid, group in sorted(by_cid.items(), key=lambda x: -sum(r['real_pnl'] for r in x[1] if r['real_pnl'] is not None)):
        if cid in skip:
            continue

        traded = [r for r in group if r['was_traded'] and r['real_pnl'] is not None]
        filtered = [r for r in group if not r['was_traded'] and r['sim_pnl'] is not None]

        real_total = sum(r['real_pnl'] for r in traded)
        filt_sim = sum(r['sim_pnl'] for r in filtered)
        filt_wr = sum(1 for r in filtered if r['sim_pnl'] > 0) / max(len(filtered), 1) * 100

        # Filter is "OK" if filtered sim PnL is negative (filter saved money)
        # Filter is "LEAKING" if filtered sim PnL is positive (filter cost money)
        if not filtered:
            filter_ok = 'NO DATA'
        elif filt_sim < -500:
            filter_ok = 'SAVING $'
        elif filt_sim > 1000:
            filter_ok = 'LEAKING $'
        else:
            filter_ok = 'MARGINAL'

        print(f"  {cid:<28} {len(group):>5} {len(traded):>6} {real_total:>+10,.0f} {len(filtered):>5} {filt_sim:>+10,.0f} {filt_wr:>7.1f}% {filter_ok:<12}")


# ============================================================================
#  MAIN
# ============================================================================

if __name__ == '__main__':
    log_path = os.path.join(BASE, 'Log.csv')
    trades_path = os.path.join(BASE, 'Trades.csv')

    if not os.path.exists(log_path):
        print(f"Error: Log.csv not found at {log_path}")
        sys.exit(1)
    if not os.path.exists(trades_path):
        print(f"Error: Trades.csv not found at {trades_path}")
        sys.exit(1)

    print("=" * 95)
    print("  FILTER FORENSICS — Signal Quality & Filter Accuracy Audit")
    print(f"  Data: {log_path}")
    print("=" * 95)

    # Load data
    evals, flow, struct, rank_dec, touch_out = load_log(log_path)
    trades = load_trades(trades_path)

    print(f"\n  Loaded: {len(evals)} EVALs, {len(rank_dec)} RANK decisions, "
          f"{len(touch_out)} sim outcomes, {len(trades)} real trades")

    # Build unified records
    records = build_signal_records(evals, flow, struct, rank_dec, touch_out, trades)
    print(f"  Built {len(records)} unified signal records")

    matched = sum(1 for r in records if r['sim_pnl'] is not None)
    traded = sum(1 for r in records if r['was_traded'])
    print(f"  Matched sim outcomes: {matched}  |  Matched real trades: {traded}")

    # Run analyses
    signal_report_card(records)
    analyze_leaked_winners(records)
    analyze_passed_losers(records)
    analyze_veto_conditions(records)
    analyze_score_edge(records)

    print(f"\n{'=' * 95}")
    print(f"  ANALYSIS COMPLETE")
    print(f"{'=' * 95}")
