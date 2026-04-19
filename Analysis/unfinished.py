import csv
import os
import re
from datetime import datetime
from collections import defaultdict

# --- CONFIGURATION ---
LOG_PATH = "../backtest/Log.csv"

def analyze_filters():
    if not os.path.exists(LOG_PATH):
        print(f"Error: Could not find Log.csv at {LOG_PATH}")
        return

    print("--- STARTING DEEP SIGNAL DETECTIVE ---")
    
    decisions = {} 
    outcomes = []
    rejection_details = {} 

    try:
        with open(LOG_PATH, 'r', encoding='utf-8-sig') as f:
            reader = csv.DictReader(f)
            for row in reader:
                tag = row.get('Tag','').strip()
                cid = row.get('ConditionSetId','')
                ts = row.get('Timestamp','')
                detail = row.get('Detail','')

                if tag == 'TOUCH_OUTCOME':
                    sim_pnl = 0; hit = ''; mae = 0
                    for part in detail.split():
                        if part.startswith('SIM_PNL='): sim_pnl = float(part.split('=')[1].replace('$','').replace(',',''))
                        elif part.startswith('FIRST_HIT='): hit = part.split('=')[1]
                        elif part.startswith('MAE='): mae = float(part.split('=')[1].replace('$','').replace(',',''))
                    
                    outcomes.append({
                        'pnl': sim_pnl, 'hit': hit, 'mae': mae, 'ts': ts, 'cid': cid
                    })

                elif tag == 'WARN':
                    if 'RANK_' in detail:
                        status = 'UNKNOWN'
                        if 'RANK_WIN' in detail: status = 'TRADED'
                        elif 'RANK_SOFT_VETO' in detail: status = 'SOFT_VETO'
                        elif 'RANK_VETO' in detail: status = 'HARD_VETO'
                        elif 'RANK_WEAK' in detail: status = 'WEAK_SCORE'
                        
                        m = re.search(r'\[([^\]]+)\]', detail)
                        cid_match = m.group(1) if m else cid
                        
                        reason = "Won Ranking"
                        if "conf=" in detail:
                            veto_match = re.findall(r'v[A-Z]+', detail)
                            if veto_match: reason = ", ".join(veto_match)
                            elif "WEAK" in detail: reason = "Low Confluence"
                        
                        decisions[(ts, cid_match)] = {'status': status, 'reason': reason}
                    
                    elif 'VETO:FP' in detail:
                        m_cid = re.search(r'\[([^\]]+)\]', detail)
                        cid_match = m_cid.group(1) if m_cid else cid
                        m_reason = re.search(r'reason=([^ ]+)', detail)
                        reason = m_reason.group(1) if m_reason else "Footprint"
                        decisions[(ts, cid_match)] = {'status': 'VETO:FP', 'reason': reason}

    except Exception as e:
        print(f"Error parsing log: {e}")
        return

    # Categorize
    stats = defaultdict(lambda: defaultdict(list))
    for o in outcomes:
        d = decisions.get((o['ts'], o['cid']), {'status': 'FILTERED', 'reason': 'Internal Kill Switch'})
        o['status'] = d['status']
        o['reason'] = d['reason']
        stats[d['status']][o['cid']].append(o)

    # --- REPORTING ---
    print("\n" + "="*115)
    print(f"{'STATUS':<12} | {'SIGNAL ORIGIN':<22} | {'COUNT':>5} | {'SIM PNL':>10} | {'WIN%'} | {'PRIMARY REASON'}")
    print("-" * 115)

    for status in sorted(stats.keys()):
        for cid in sorted(stats[status].keys()):
            res = stats[status][cid]
            net_pnl = sum(r['pnl'] for r in res)
            wr = (sum(1 for r in res if r['pnl'] > 0) / len(res)) * 100
            reasons = [r['reason'] for r in res]
            top_reason = max(set(reasons), key=reasons.count) if reasons else "N/A"
            print(f"{status:<12} | {cid:<22} | {len(res):>5} | ${net_pnl:>9,.0f} | {wr:>3.0f}% | {top_reason}")

    print("="*115)
    
    # List actual misses to verify on charts
    print("\n=== TOP 10 MISSES: WHO VETOED THEM? ===")
    all_missed = [o for o in outcomes if o['status'] != 'TRADED' and o['pnl'] > 800]
    for r in sorted(all_missed, key=lambda x: x['pnl'], reverse=True)[:10]:
        print(f"{r['ts']:<20} | {r['cid']:<22} | ${r['pnl']:>7.0f} | {r['status']:<10} | KILLED BY: {r['reason']}")

if __name__ == "__main__":
    analyze_filters()
