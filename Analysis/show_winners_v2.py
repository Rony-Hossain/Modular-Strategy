import pandas as pd
import sys
import os

# Fix console encoding
if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

def load_log_rejections(log_path):
    """Parse Log.csv to find rejection reasons for signals."""
    rejections = {}
    if not os.path.exists(log_path):
        return rejections
        
    with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            if 'RANK_VETO' in line or 'RANK_WEAK' in line:
                # 2026-01-05 02:30:00,WARN,0,,,,,,,,,,,,,,,,,,RANK_VETO [ORB_Value_v2] L conf=...
                parts = line.split(',')
                if len(parts) >= 21:
                    ts = parts[0]
                    detail = parts[20].strip()
                    # Use (Timestamp, Detail starts with) as a loose key
                    # Or extract SetId if possible
                    try:
                        set_id = detail.split('[')[1].split(']')[0]
                        rejections[(ts, set_id)] = detail
                    except:
                        pass
    return rejections

def analyze_winners(autopsy_path, log_path):
    try:
        df = pd.read_csv(autopsy_path)
    except Exception as e:
        print(f"Error loading {autopsy_path}: {e}")
        return

    # Clean numeric columns
    for col in ['SimPnL', 'ActualPnL', 'MFE', 'MAE']:
        if col in df.columns:
            df[col] = pd.to_numeric(df[col], errors='coerce').fillna(0)

    rejections = load_log_rejections(log_path)

    # 1. Traded Winners (TOOK)
    took = df[df['Decision'] == 'TOOK']
    took_winners = took[(took['ActualPnL'] > 0) | ((took['ActualPnL'] == 0) & (took['SimPnL'] > 0))]
    
    # 2. Rejected Winners (DROP)
    dropped = df[df['Decision'] == 'DROP']
    dropped_winners = dropped[dropped['FirstHit'] == 'TARGET'].copy()

    print(f"=== COMPREHENSIVE WINNER ANALYSIS ===")
    print(f"File: {autopsy_path}")
    print(f"Total Signals: {len(df)}")
    print(f"Total TOOK:    {len(took)}  (Winners: {len(took_winners)})")
    print(f"Total DROP:    {len(dropped)} (Winners: {len(dropped_winners)})")
    print("-" * 60)

    print(f"MISSING PROFIT: ${dropped_winners['SimPnL'].sum():,.2f}")
    print("-" * 60)

    print(f"{'Timestamp':<20} {'Dir':<3} {'Source':<20} {'SimPnL':>10} {'MFE':>8} {'Reason':<40}")
    
    # Try to match reasons
    top_missed = dropped_winners.sort_values('SimPnL', ascending=False).head(20)
    for _, row in top_missed.iterrows():
        ts = row['Timestamp']
        sig_id = row['SignalId']
        source = str(sig_id).split(':')[0]
        
        # Try to find reason in log
        # Match by timestamp and source
        reason = "Unknown (Check Log)"
        for (r_ts, r_set), r_detail in rejections.items():
            if r_ts == ts and r_set == source:
                reason = r_detail
                break
        
        print(f"{ts:<20} {row['Dir']:<3} {source:<20} ${row['SimPnL']:>9.2f} {row['MFE']:>8.0f} {reason[:60]}")

if __name__ == "__main__":
    analyze_winners('backtest/filter_autopsy.csv', 'backtest/Log.csv')
