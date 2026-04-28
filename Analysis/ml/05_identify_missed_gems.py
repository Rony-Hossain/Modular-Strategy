import pandas as pd
from pathlib import Path

ML_DIR = Path(__file__).resolve().parent
DATA = ML_DIR / "feature_matrix.parquet"
ARTIFACTS = ML_DIR.parent / "artifacts"

def identify_missed_gems():
    print("--- IDENTIFYING MISSED GEMS (Profitable Dropped Signals) ---")
    
    # Load feature matrix
    df = pd.read_parquet(DATA)
    
    # Load signals to see who was actually traded
    signals = pd.read_parquet(ARTIFACTS / "signals.parquet")
    df = df.merge(signals[['signal_id', 'traded']], on='signal_id', how='left')
    
    # Define "Gems": Dropped signals that were actually profitable
    # We'll use sim_pnl (which is mapped to 'pnl' in the feature matrix)
    gems = df[(df['traded'] == False) & (df['pnl'] > 200)].copy()
    
    # Sort by profit magnitude
    gems = gems.sort_values('pnl', ascending=False)
    
    if len(gems) == 0:
        print("No missed gems found with PnL > $200.")
        return

    print(f"Found {len(gems)} missed gems. Showing top 20 for chart verification:")
    print("-" * 100)
    print(f"{'Timestamp':<20} | {'Source':<15} | {'Dir':<3} | {'Score':<5} | {'PnL':>8} | {'Reason for Miss'}")
    print("-" * 100)
    
    for _, row in gems.head(20).iterrows():
        # Heuristic for why it was missed:
        reason = []
        if row['score'] < 70:
            reason.append(f"Low Score ({row['score']})")
        if row['layer_c'] < 10:
            reason.append(f"Weak OrderFlow ({row['layer_c']})")
        if row['penalty'] < -10:
            reason.append(f"Heavy Penalty ({row['penalty']})")
            
        reason_str = ", ".join(reason) if reason else "Unknown (likely Gate filter)"
        
        ts_str = str(row['timestamp']).split('.')[0]
        print(f"{ts_str:<20} | {row['source']:<15} | {row['direction']:<3} | {row['score']:<5} | ${row['pnl']:>7.2f} | {reason_str}")

    print("-" * 100)
    print("ACTION: Verify these timestamps on your NinjaTrader chart.")
    print("If they look like high-quality setups, we will increase weights for the tokens present in these signals.")

if __name__ == "__main__":
    identify_missed_gems()
