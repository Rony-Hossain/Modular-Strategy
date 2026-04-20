import pandas as pd
import numpy as np
import itertools
import os

FEATURE_FILE = 'feature_matrix.csv'

def run_optimization():
    if not os.path.exists(FEATURE_FILE):
        print(f"Error: {FEATURE_FILE} not found.")
        return

    df = pd.read_csv(FEATURE_FILE)
    
    # 1. Prepare Target
    # We use actual dollar PnL for expectancy optimization
    if df['pnl'].dtype == object:
        df['pnl'] = pd.to_numeric(df['pnl'].str.replace('$', '').str.replace(',', ''), errors='coerce')
    df = df.dropna(subset=['pnl'])
    df['target'] = (df['pnl'] > 0).astype(int)
    
    # Only look at signals that reached a target or stop
    df = df[df['hit'].isin(['TARGET', 'STOP', 'BOTH_SAMEBAR'])].copy()
    
    print(f"Analyzing {len(df)} candidate signals...")

    # 2. Define Granular Search Space
    # We vary the core weights of the modular strategy
    search_grid = {
        'la': [5, 10, 15],       # Layer A (Bias)
        'lb': [5, 10, 15, 20],   # Layer B (Structure)
        'lc': [10, 15, 20, 25],  # Layer C (Order Flow)
        'pen': [5, 10, 15, 20],  # Penalties
        'floor': [20, 30, 40, 50, 60] # Acceptance Floor
    }

    best_expectancy = -999999
    best_params = {}

    keys = search_grid.keys()
    values = search_grid.values()
    combinations = list(itertools.product(*values))

    # Pre-calculate feature flags to speed up loops
    # Map CSV columns to simulator logic
    df['is_long'] = (df['eval_dir'] == 'Long').astype(int)
    
    # BIAS Match
    df['bias_score'] = (
        ((df['f_h4b'] > 0) & (df['is_long'] == 1)) | ((df['f_h4b'] < 0) & (df['is_long'] == 0))
    ).astype(int) + (
        ((df['f_h2b'] > 0) & (df['is_long'] == 1)) | ((df['f_h2b'] < 0) & (df['is_long'] == 0))
    ).astype(int) + (
        ((df['f_h1b'] > 0) & (df['is_long'] == 1)) | ((df['f_h1b'] < 0) & (df['is_long'] == 0))
    ).astype(int)

    # FLOW Match (Divergence + Stacks)
    df['flow_score'] = (
        ((df['f_bdiv'] > 0) & (df['is_long'] == 1)) | ((df['f_berdiv'] > 0) & (df['is_long'] == 0))
    ).astype(int) + (
        ((df['f_sbull'] >= 1) & (df['is_long'] == 1)) | ((df['f_sbear'] >= 1) & (df['is_long'] == 0))
    ).astype(int)

    # PENALTY Flags
    df['penalty_count'] = df['v_trap'] + df['v_ice'] + df['v_sweep'] + df['v_brick']

    print(f"Running {len(combinations)} weight permutations...")

    for combo in combinations:
        la, lb, lc, pen, floor = combo
        
        # Calculate simulated score
        # Note: LB is simplified as a single structure agreement point for this sim
        # We assume 50% of signals have structure agreement if s_near_sup/res exists
        df['sim_score'] = (df['bias_score'] * la) + (df['flow_score'] * lc) - (df['penalty_count'] * pen)
        
        # We apply a fixed LB bonus to signals that weren't FP_VETOed (representing structural confluence)
        df.loc[df['filter_reason'] != 'FP_VETO', 'sim_score'] += lb

        # Filter by Floor
        taken = df[df['sim_score'] >= floor]
        
        if len(taken) < 25: continue # Stability constraint

        expectancy = taken['pnl'].mean()
        win_rate = taken['target'].mean()

        if expectancy > best_expectancy:
            best_expectancy = expectancy
            best_params = {
                'LayerA': la, 'LayerB': lb, 'LayerC': lc, 
                'Penalty': pen, 'Floor': floor, 
                'N': len(taken), 'WinRate': f"{win_rate:.1%}",
                'Expectancy': f"${expectancy:.2f}"
            }

    print("\n--- GLOBAL OPTIMUM REACHED ---")
    if best_params:
        for k, v in best_params.items():
            print(f"  {k:<10}: {v}")
    else:
        print("  Search failed to find positive edge.")

if __name__ == "__main__":
    run_optimization()