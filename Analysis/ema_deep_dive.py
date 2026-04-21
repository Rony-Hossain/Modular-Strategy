import pandas as pd
import sys
import os
import re

if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

def analyze_ema(log_path):
    print(f"Deep dive into EMA signals from {log_path}...")
    results = []
    rejection_details = {}

    with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            # Capture rejection reasons first
            if 'EMA_Cross_v1' in line and ('RANK_VETO' in line or 'RANK_WEAK' in line):
                parts = line.split(',')
                if len(parts) >= 21:
                    ts = parts[0]
                    rejection_details[ts] = parts[20].strip()

            if 'TOUCH_OUTCOME' not in line or 'EMA_Cross_v1' not in line:
                continue
                
            parts = line.split(',')
            try:
                idx = parts.index('TOUCH_OUTCOME')
                timestamp = parts[0]
                id_match = re.search(r',([^,:]+:EMA_Cross_v1:[^,]+),(TARGET|STOP),', line)
                if not id_match:
                    # Try alternate search if ID format differs
                    id_match = re.search(r',(EMA_Cross_v1:[^,]+),(TARGET|STOP),', line)
                
                if not id_match: continue
                    
                sig_id = id_match.group(1)
                first_hit = id_match.group(2)
                
                sim_match = re.search(r'SIM_PNL=([-\d.]+)', line)
                sim_pnl = float(sim_match.group(1)) if sim_match else 0.0
                
                is_rejected = ":REJ" in sig_id
                
                results.append({
                    'Timestamp': timestamp,
                    'FirstHit': first_hit,
                    'PnL': sim_pnl,
                    'IsRejected': is_rejected,
                    'Reason': rejection_details.get(timestamp, "Unknown")
                })
            except: pass

    df = pd.DataFrame(results)
    if df.empty:
        print("No EMA signals found.")
        return

    print(f"\n{'='*60}")
    print(f" EMA_CROSS_V1 PERFORMANCE PROFILE ")
    print(f"{'='*60}")

    for label, group in [("TRADED", df[~df['IsRejected']]), ("REJECTED", df[df['IsRejected']])]:
        total = len(group)
        if total == 0:
            print(f"{label}: No signals found.\n")
            continue
            
        w = group[group['FirstHit'] == 'TARGET']
        l = group[group['FirstHit'] == 'STOP']
        wr = (len(w) / total) * 100
        pnl = group['PnL'].sum()
        
        print(f"{label}:")
        print(f"  Total Signals: {total}")
        print(f"  Win Rate:      {wr:.1f}% ({len(w)}W / {len(l)}L)")
        print(f"  Net PnL:       ${pnl:,.2f}")
        print(f"  Avg Win:       ${w['PnL'].mean():.2f}" if not w.empty else "  Avg Win: N/A")
        print(f"  Avg Loss:      ${l['PnL'].mean():.2f}" if not l.empty else "  Avg Loss: N/A")
        
        if label == "REJECTED":
            print("\n  Top Rejection Reasons for EMA Winners:")
            top_rej = w['Reason'].value_counts().head(5)
            for reason, count in top_rej.items():
                print(f"    - {reason} ({count} times)")
        print()

if __name__ == "__main__":
    analyze_ema('backtest/Log.csv')
