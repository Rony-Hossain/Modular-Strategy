import pandas as pd
import sys
import os
import re
import argparse

# Fix console encoding
if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

def forensic_ema_analysis(log_path, show_flipped=False, timeframe=5):
    print(f"Deep forensics on {timeframe}-min EMA signals in Log: {log_path}")
    
    signals_by_bar = {} 
    
    with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            if 'EMA_' not in line: continue
            parts = line.split(',')
            if len(parts) < 3: continue
            
            tag = parts[1]
            ts = parts[0]
            bar = parts[2]

            if tag == 'EVAL':
                signals_by_bar[bar] = {
                    'Timestamp': ts, 'Bar': bar, 'Direction': parts[3], 'RawScore': float(parts[6]) if parts[6] else 0.0,
                    'NetScore': 0.0, 'Decision': 'PENDING', 'Reason': '', 'FoundOutcome': False, 
                    'PnL': 0.0, 'MFE': 0.0, 'MAE': 0.0, 'FinalOutcome': 'NONE'
                }

            elif tag == 'WARN' and ('RANK_' in line):
                if bar in signals_by_bar:
                    detail = parts[20].strip()
                    signals_by_bar[bar]['Reason'] = detail
                    net_match = re.search(r'Net=(\d+)', detail)
                    if net_match: signals_by_bar[bar]['NetScore'] = float(net_match.group(1))
                    
                    if 'VETO' in line: signals_by_bar[bar]['Decision'] = 'VETOED'
                    elif 'WEAK' in line: signals_by_bar[bar]['Decision'] = 'FILTERED_WEAK'
                    elif 'WIN' in line: signals_by_bar[bar]['Decision'] = 'ACCEPTED'

            elif tag == 'TOUCH_OUTCOME':
                id_match = re.search(r',([^,]*EMA_[^,]*),(TARGET|STOP),', line)
                if id_match:
                    full_id = id_match.group(1)
                    hit = id_match.group(2)
                    pnl_match = re.search(r'SIM_PNL=([-\d.]+)', line)
                    mfe_match = re.search(r'MFE=([-\d.]+)', line)
                    mae_match = re.search(r'MAE=([-\d.]+)', line)
                    bar_match = re.search(r':(\d+)(?::|REJ|$)', full_id)
                    target_bar = bar_match.group(1) if bar_match else bar
                    
                    pnl = float(pnl_match.group(1)) if pnl_match else 0.0
                    mfe = float(mfe_match.group(1)) if mfe_match else 0.0
                    mae = float(mae_match.group(1)) if mae_match else 0.0
                    
                    if target_bar in signals_by_bar:
                        data = signals_by_bar[target_bar]
                        data['FoundOutcome'] = True
                        data['FinalOutcome'] = hit
                        data['PnL'] = pnl
                        data['MFE'] = mfe
                        data['MAE'] = mae
                        data['ActualDecision'] = 'REJECTED' if ':REJ' in full_id else 'TRADED'
                    else:
                        signals_by_bar[target_bar] = {
                            'Timestamp': ts, 'Bar': target_bar, 'Direction': '?', 'FoundOutcome': True, 
                            'FinalOutcome': hit, 'PnL': pnl, 'MFE': mfe, 'MAE': mae, 'NetScore': 0.0,
                            'ActualDecision': 'REJECTED' if ':REJ' in full_id else 'TRADED', 'Reason': 'N/A'
                        }

    final_list = [v for v in signals_by_bar.values() if v['FoundOutcome']]
    df = pd.DataFrame(final_list)
    if df.empty:
        print("No outcomes found.")
        return

    # Basic performance
    print(f"\nEMA PERFORMANCE SUMMARY ({len(df)} outcomes):")
    summary = df.groupby('ActualDecision').agg({
        'PnL': ['count', 'sum', 'mean'],
        'MFE': 'mean'
    })
    win_rates = df.groupby('ActualDecision')['FinalOutcome'].apply(lambda x: (x == 'TARGET').sum() / len(x) * 100)
    summary[('Outcome', 'WinRate%')] = win_rates
    print(summary.to_string())

    if show_flipped:
        df['IsFlipped'] = (df['FinalOutcome'] == 'STOP') & (df['MFE'] >= 300.0)
        flipped = df[df['IsFlipped']]
        print(f"\n{'='*60}\n EMA FLIPPED WINNER ANALYSIS\n{'='*60}")
        for dec in ['TRADED', 'REJECTED']:
            sub = df[df['ActualDecision'] == dec]
            if sub.empty: continue
            f_sub = sub[sub['IsFlipped']]
            print(f"{dec}: {len(f_sub)} flips out of {len(sub)} signals")
        if not flipped.empty:
            print("\nDETAILS:")
            print(flipped[['Timestamp', 'ActualDecision', 'PnL', 'MFE']].sort_values('MFE', ascending=False).head(10).to_string(index=False))
    else:
        print("\n(Flipped winner analysis hidden. Use --show-flipped to view.)")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument('--show-flipped', action='store_true', help='Show the flipped winner analysis section')
    parser.add_argument('--timeframe', type=int, default=5, help='Minute bar interval (default: 5)')
    args = parser.parse_args()
    
    forensic_ema_analysis('backtest/Log.csv', show_flipped=args.show_flipped, timeframe=args.timeframe)
