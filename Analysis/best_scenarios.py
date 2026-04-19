import pandas as pd
import numpy as np
import os

def discover_best_scenarios():
    input_path = "ml_feature_matrix.csv"
    if not os.path.exists(input_path):
        print("Error: ml_feature_matrix.csv not found.")
        return

    df = pd.read_csv(input_path)
    df = df[df['hit'].isin(['TARGET', 'STOP'])].copy()
    df['win'] = (df['hit'] == 'TARGET').astype(int)

    features = [c for c in df.columns if c.startswith('f_') or c.startswith('s_')]
    
    print(f"\n--- DISCOVERING HIGH-PROBABILITY ALPHA CLUSTERS (n={len(df)}) ---")
    print(f"{'SCENARIO (CLUSTER)':<45} | {'COUNT':>5} | {'WIN%'}")
    print("-" * 65)

    scenarios = [
        # Skew + Imbalance
        ("High Skew + Bull Stack", df[(df['s_SKEW'] > 1.5) & (df['f_SBULL'] >= 1)]),
        ("Low Skew + Bear Stack", df[(df['s_SKEW'] < -1.5) & (df['f_SBEAR'] >= 1)]),
        
        # Delta Divergence + Absorption
        ("Bull Div + High Absorption", df[(df['f_BDIV'] > 0) & (df['f_ABS'] > 2.0)]),
        ("Bear Div + High Absorption", df[(df['f_BERDIV'] > 0) & (df['f_ABS'] > 2.0)]),
        
        # Tape Velocity / Big Prints
        ("Aggressive Tape + Momentum", df[(df['f_TRD'] > 1.5) & (df['f_CD'] > 1000)]),
        ("Seller Exhaustion (DSH)", df[(df['f_DSH'] > 5) & (df['f_CD'] < -500)]),
        
        # Structure + Delta
        ("VWAP Support + Bull Stack", df[(df['s_POC'] > 0) & (df['f_SBULL'] >= 2)]),
    ]

    for label, sub in scenarios:
        if len(sub) < 5: continue
        wr = sub['win'].mean() * 100
        print(f"{label:<45} | {len(sub):>5} | {wr:>5.1f}%")

if __name__ == "__main__":
    discover_best_scenarios()
