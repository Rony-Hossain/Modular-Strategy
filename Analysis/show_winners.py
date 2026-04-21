import pandas as pd
import sys

# Fix console encoding
if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

def analyze_winners(csv_path):
    try:
        df = pd.read_csv(csv_path)
    except Exception as e:
        print(f"Error loading {csv_path}: {e}")
        return

    # Clean numeric columns
    for col in ['SimPnL', 'ActualPnL', 'MFE', 'MAE']:
        if col in df.columns:
            df[col] = pd.to_numeric(df[col], errors='coerce').fillna(0)

    # 1. Traded Winners (TOOK)
    took = df[df['Decision'] == 'TOOK']
    # A traded signal is a winner if ActualPnL > 0 (or SimPnL if Actual isn't available, but here we have ActualPnL)
    # Actually, looking at the snippet, sometimes ActualPnL is filled. 
    # Let's use ActualPnL for TOOK if it exists, otherwise SimPnL.
    took_winners = took[(took['ActualPnL'] > 0) | ((took['ActualPnL'] == 0) & (took['SimPnL'] > 0))]
    
    # 2. Rejected Winners (DROP)
    dropped = df[df['Decision'] == 'DROP']
    # A rejected signal is a "winner" if its forward return hit TARGET
    dropped_winners = dropped[dropped['FirstHit'] == 'TARGET']

    print(f"=== WINNER ANALYSIS ({csv_path}) ===")
    print(f"Total Signals: {len(df)}")
    print(f"Total TOOK:    {len(took)}")
    print(f"Total DROP:    {len(dropped)}")
    print("-" * 40)

    print(f"TRADED WINNERS (TOOK and profitable):")
    print(f"Count: {len(took_winners)}")
    if len(took_winners) > 0:
        avg_win = took_winners['ActualPnL'].mean()
        print(f"Average Actual Win: ${avg_win:.2f}")
    
    print("-" * 40)
    print(f"REJECTED WINNERS (DROP but hit TARGET):")
    print(f"Count: {len(dropped_winners)}")
    if len(dropped_winners) > 0:
        avg_sim_win = dropped_winners['SimPnL'].mean()
        print(f"Average Sim Win: ${avg_sim_win:.2f}")
        total_missed = dropped_winners['SimPnL'].sum()
        print(f"Total Missed Profit: ${total_missed:.2f}")

    print("-" * 40)
    print("TOP 10 REJECTED WINNERS (By SimPnL):")
    top_dropped = dropped_winners.sort_values('SimPnL', ascending=False).head(10)
    print(top_dropped[['Timestamp', 'Dir', 'SignalId', 'SimPnL', 'MFE']])

    print("-" * 40)
    # Per-Source Breakdown for Rejected Winners
    def get_source(sig_id):
        return str(sig_id).split(':')[0]
    
    dropped_winners['Source'] = dropped_winners['SignalId'].apply(get_source)
    source_stats = dropped_winners.groupby('Source').size()
    print("REJECTED WINNERS BY SOURCE:")
    print(source_stats)

if __name__ == "__main__":
    analyze_winners('backtest/filter_autopsy.csv')
