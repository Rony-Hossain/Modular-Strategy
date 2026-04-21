import pandas as pd
import sys
import os
import re

if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

def analyze_ema_window_based(log_path):
    print(f"Window-based forensic analysis of EMA signals...")
    
    # We will store the last known evaluation/rejection for EMA and then 
    # look for the outcome that hits later.
    
    pending_signals = {} # Bar -> { 'Score': x, 'Reason': y, 'Label': z }
    outcomes = []

    with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            parts = line.split(',')
            if len(parts) < 3: continue
            
            tag = parts[1]
            bar = parts[2]
            
            if 'EMA_Cross_v1' not in line: continue

            if tag == 'EVAL':
                # EVAL,301,Long,EMA_CrossSignal,EMA_Cross_v1,64...
                score = parts[6]
                label = parts[20].strip() if len(parts) > 20 else ""
                pending_signals[bar] = {
                    'Score': score,
                    'Label': label,
                    'Reason': 'PASSED (Target found later)', # Default if no WARN follows
                    'IsRejected': False
                }
            
            elif tag == 'WARN' and ('RANK_WEAK' in line or 'RANK_VETO' in line or 'RANK_WIN' in line):
                # These often follow an EVAL on the same bar or logically belong to it
                # For RANK_WEAK/VETO, the Bar column is often 0, so we use the most recent pending EVAL
                detail = parts[20].strip()
                # Find the most recent bar that doesn't have a reason yet
                # Or just assume it's the most recent EVAL since logs are sequential
                if pending_signals:
                    last_bar = list(pending_signals.keys())[-1]
                    pending_signals[last_bar]['Reason'] = detail
                    if 'VETO' in line or 'WEAK' in line:
                        pending_signals[last_bar]['IsRejected'] = True

            elif tag == 'TOUCH_OUTCOME':
                id_match = re.search(r',([^,:]+:EMA_Cross_v1:[^,]+),(TARGET|STOP),', line)
                if not id_match:
                    # Alternative ID style: EMA_Cross_v1:TIMESTAMP:BAR:REJ
                    id_match = re.search(r',(EMA_Cross_v1:[^,]+),(TARGET|STOP),', line)
                
                if id_match:
                    sig_id = id_match.group(1)
                    first_hit = id_match.group(2)
                    
                    # Extract BAR from sig_id if possible
                    # EMA_Cross_v1:20260105:355:REJ -> bar is 355
                    bar_match = re.search(r':(\d+)(?::|REJ|$)', sig_id)
                    bar_key = bar_match.group(1) if bar_match else bar
                    
                    sim_match = re.search(r'SIM_PNL=([-\d.]+)', line)
                    sim_pnl = float(sim_match.group(1)) if sim_match else 0.0
                    
                    info = pending_signals.get(bar_key, {
                        'Score': '?', 'Reason': 'Unknown (EVAL missing)', 'Label': '', 'IsRejected': ':REJ' in sig_id
                    })
                    
                    outcomes.append({
                        'Bar': bar_key,
                        'SignalId': sig_id,
                        'FirstHit': first_hit,
                        'PnL': sim_pnl,
                        'Score': info['Score'],
                        'Reason': info['Reason'],
                        'IsRejected': info['IsRejected'] or (':REJ' in sig_id),
                        'Timestamp': parts[0]
                    })

    df = pd.DataFrame(outcomes)
    if df.empty:
        print("No correlated EMA outcomes found.")
        return

    print(f"\nEMA COMPREHENSIVE FORENSICS ({len(df)} correlated outcomes):")
    
    rej_df = df[df['IsRejected']]
    winners = rej_df[rej_df['FirstHit'] == 'TARGET']
    
    print(f"Total Rejected EMA Winners: {len(winners)}")
    
    if not winners.empty:
        print("\nTOP 20 REJECTED WINNERS WITH REASONS:")
        # Clean up reason string for display - extract Net and A/B/C/D
        def clean_reason(r):
            match = re.search(r'(RANK_\w+ .*?Net=\d+)', r)
            return match.group(1) if match else r[:60]
            
        winners['BriefReason'] = winners['Reason'].apply(clean_reason)
        print(winners[['Timestamp', 'PnL', 'Score', 'BriefReason']].sort_values('PnL', ascending=False).head(20).to_string(index=False))

if __name__ == "__main__":
    analyze_ema_window_based('backtest/Log.csv')
