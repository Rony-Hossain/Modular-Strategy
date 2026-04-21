import pandas as pd
import sys
import os
import re

# Fix console encoding
if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

def parse_log_comprehensive(log_path):
    print(f"Reading {log_path}...")
    results = []
    with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            if 'TOUCH_OUTCOME' not in line: continue
            parts = line.split(',')
            if len(parts) < 15: continue
            try:
                idx = parts.index('TOUCH_OUTCOME')
                timestamp = parts[0]
                direction = parts[idx+2]
                id_match = re.search(r',([^,:]+:[^,:]+:[^,]+),(TARGET|STOP),', line)
                if not id_match: continue
                sig_id = id_match.group(1)
                first_hit = id_match.group(2)
                sim_match = re.search(r'SIM_PNL=([-\d.]+)', line)
                sim_pnl = float(sim_match.group(1)) if sim_match else 0.0
                mfe_match = re.search(r'MFE=([-\d.]+)', line)
                mfe = float(mfe_match.group(1)) if mfe_match else 0.0
                is_rejected = ":REJ" in sig_id
                source = sig_id.split(':')[0]
                results.append({
                    'Timestamp': timestamp,
                    'Dir': direction,
                    'Source': source,
                    'FirstHit': first_hit,
                    'PnL': sim_pnl,
                    'MFE': mfe,
                    'IsRejected': is_rejected,
                    'SignalId': sig_id
                })
            except: pass
    return pd.DataFrame(results)

def analyze(log_path):
    df = parse_log_comprehensive(log_path)
    if df.empty: return
    print(f"\n=== COMPREHENSIVE LOG ANALYSIS ({len(df)} OUTCOMES) ===")
    taken = df[~df['IsRejected']]
    rejected = df[df['IsRejected']]
    
    for label, group in [("TRADED", taken), ("REJECTED", rejected)]:
        if group.empty: continue
        w = group[group['FirstHit'] == 'TARGET']
        l = group[group['FirstHit'] == 'STOP']
        print(f"{label}: {len(group)} total | Winners: {len(w)} ({len(w)/len(group)*100:.1f}%) | PnL: ${group['PnL'].sum():,.2f}")

    print("-" * 60)
    print("WINNERS BY SOURCE (TRADED vs REJECTED):")
    winners = df[df['FirstHit'] == 'TARGET']
    source_stats = winners.groupby(['Source', 'IsRejected']).size().unstack(fill_value=0)
    
    # Safely rename and print
    cols = {False: 'Traded', True: 'Rejected'}
    source_stats = source_stats.rename(columns=cols)
    print(source_stats.to_string())
    
    print("\nTOP 20 REJECTED WINNERS:")
    rej_winners = rejected[rejected['FirstHit'] == 'TARGET'].sort_values('PnL', ascending=False)
    print(rej_winners[['Timestamp', 'Dir', 'Source', 'PnL', 'MFE']].head(20).to_string(index=False))

if __name__ == "__main__":
    analyze('backtest/Log.csv')
