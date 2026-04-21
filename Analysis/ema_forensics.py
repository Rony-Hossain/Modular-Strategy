import pandas as pd
import sys
import os
import re

if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

def analyze_ema_with_forensics(log_path):
    print(f"Deep forensic analysis of EMA signals from {log_path}...")
    
    results = []
    rejection_map = {}

    # Single pass to collect both outcomes and rejections
    with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            if 'EMA_Cross_v1' not in line:
                continue
                
            parts = line.split(',')
            if len(parts) < 3: continue
            
            ts = parts[0]
            tag = parts[1]

            # Collect rejection details
            if tag == 'WARN' and ('RANK_WEAK' in line or 'RANK_VETO' in line):
                rejection_map[ts] = parts[20].strip()
                continue

            # Collect outcomes
            if tag == 'TOUCH_OUTCOME':
                # regex to find the ID and the outcome
                # Pattern: EMA_Cross_v1:TIMESTAMP:BAR or EMA_Cross_v1:BAR:REJ
                id_match = re.search(r',([^,]*EMA_Cross_v1[^,]*),(TARGET|STOP),', line)
                if not id_match: continue
                
                sig_id = id_match.group(1)
                first_hit = id_match.group(2)
                
                sim_match = re.search(r'SIM_PNL=([-\d.]+)', line)
                sim_pnl = float(sim_match.group(1)) if sim_match else 0.0
                
                results.append({
                    'Timestamp': ts,
                    'FirstHit': first_hit,
                    'PnL': sim_pnl,
                    'IsRejected': ":REJ" in sig_id or ":REJ" in line,
                    'SignalId': sig_id
                })

    if not results:
        print("No EMA outcomes found.")
        return

    df = pd.DataFrame(results)
    
    def get_reason(row):
        if not row['IsRejected']: return "TRADED"
        return rejection_map.get(row['Timestamp'], "Unknown Rejection")

    df['Reason'] = df.apply(get_reason, axis=1)

    print(f"\nEMA REJECTION FORENSICS:")
    rej_df = df[df['IsRejected']]
    
    if rej_df.empty:
        print("No rejected signals found in this set.")
    else:
        winners = rej_df[rej_df['FirstHit'] == 'TARGET']
        print(f"Total Rejected EMA Signals: {len(rej_df)}")
        print(f"Total Rejected EMA Winners: {len(winners)}")
        
        print("\nBreakdown of Reasons for ALL Rejected EMA Signals:")
        print(rej_df['Reason'].value_counts().to_string())
        
        if not winners.empty:
            print("\nBreakdown of Reasons for Missing Winners:")
            print(winners['Reason'].value_counts().to_string())

if __name__ == "__main__":
    analyze_ema_with_forensics('backtest/Log.csv')
