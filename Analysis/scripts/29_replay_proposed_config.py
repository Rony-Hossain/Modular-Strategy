import pandas as pd
import numpy as np
from pathlib import Path
import sys

# Set seed for reproducibility
np.random.seed(42)

def to_md_table(df):
    if df.empty: return ""
    cols = df.columns.tolist()
    header = "| " + " | ".join(cols) + " |"
    divider = "| " + " | ".join(["---"] * len(cols)) + " |"
    rows = []
    for _, row in df.iterrows():
        rows.append("| " + " | ".join([str(val) for val in row.values]) + " |")
    return "\n".join([header, divider] + rows)

def compute_metrics(df_subset, mean_contracts):
    # Constants
    TICK_SIZE = 0.25
    PV = 2.00
    COMM_RT = 3.00
    
    n = len(df_subset)
    if n == 0:
        return {
            'n_trades': 0, 'win_rate': 0, 'pf': 0,
            'gross_pnl': 0, 'slippage': 0, 'comm': 0, 'net_pnl': 0
        }
    
    gross = df_subset['realized_pnl_$'].sum()
    slip_ticks = df_subset['entry_slip_ticks'].fillna(0).sum() + df_subset['exit_slip_ticks'].fillna(0).sum()
    slippage_usd = slip_ticks * TICK_SIZE * PV * mean_contracts
    comm = n * COMM_RT
    net = gross - slippage_usd - comm
    
    wins = df_subset[df_subset['realized_pnl_$'] > 0]['realized_pnl_$'].sum()
    losses = abs(df_subset[df_subset['realized_pnl_$'] < 0]['realized_pnl_$'].sum())
    pf = wins / losses if losses > 0 else np.inf
    wr = (df_subset['realized_pnl_$'] > 0).mean()
    
    return {
        'n_trades': n,
        'win_rate': wr,
        'pf': pf,
        'gross_pnl': gross,
        'slippage': slippage_usd,
        'comm': comm,
        'net_pnl': net
    }

def main():
    repo_root = Path(".")
    artifacts_dir = repo_root / "Analysis/artifacts"
    
    # [INPUT]
    print("[INPUT] Loading artifacts...")
    tl_path = artifacts_dir / "trade_lifecycle.parquet"
    if not tl_path.exists():
        print(f"ERROR: {tl_path} not found.")
        sys.exit(1)
    
    df = pd.read_parquet(tl_path)
    print(f"  Loaded trade_lifecycle.parquet: {len(df)} rows")
    
    # [PARSE]
    print("[PARSE] Preparing simulation data...")
    # Baseline mean contracts
    mean_contracts = df['contracts'].mean()
    
    # Hour ET for Scenario 3
    df['hour_et'] = df['entry_timestamp'].dt.tz_convert('America/New_York').dt.hour
    df['min_et'] = df['entry_timestamp'].dt.tz_convert('America/New_York').dt.minute
    df['time_float'] = df['hour_et'] + df['min_et']/60.0
    
    DISABLED_SOURCES = ["SMF_Impulse", "ADX_TrendSignal", "VWAP_Reclaim", "EMA_CrossSignal"]

    # Simulation Logic
    # Baseline
    m0 = compute_metrics(df, mean_contracts)
    
    # Scenario 1: Source Filtering
    s1_df = df[~df['source'].isin(DISABLED_SOURCES)]
    m1 = compute_metrics(s1_df, mean_contracts)
    
    # Scenario 2: Source + Slippage Cap
    s2_df = s1_df[s1_df['entry_slip_ticks'].fillna(0) <= 5]
    m2 = compute_metrics(s2_df, mean_contracts)
    
    # Scenario 3: Source + Slip + Hours (09:30-15:30 ET)
    s3_df = s2_df[(s2_df['time_float'] >= 9.5) & (s2_df['time_float'] <= 15.5)]
    m3 = compute_metrics(s3_df, mean_contracts)
    
    # [RESULT]
    print("[RESULT] Simulation Comparison Table")
    
    results = []
    metrics = [
        ('n_trades', 'Trades', '{:.0f}'),
        ('win_rate', 'Win %', '{:.1%}'),
        ('pf', 'PF', '{:.2f}'),
        ('gross_pnl', 'Gross PnL $', '{:,.2f}'),
        ('slippage', 'Slippage $', '{:,.2f}'),
        ('comm', 'Comm $', '{:,.2f}'),
        ('net_pnl', 'Net PnL $', '{:,.2f}')
    ]
    
    for key, label, fmt in metrics:
        v0 = m0[key]
        v1 = m1[key]
        v2 = m2[key]
        v3 = m3[key]
        results.append({
            'Metric': label,
            'Baseline': fmt.format(v0),
            'S1_SrcFilter': fmt.format(v1),
            'S2_SlipCap': fmt.format(v2),
            'S3_RTH_Only': fmt.format(v3),
            'S3_Delta': fmt.format(v3 - v0)
        })
    
    res_df = pd.DataFrame(results)
    print(res_df.to_string(index=False))

    # [SAVED]
    print("\n[SAVED] Saving projections...")
    f_csv = artifacts_dir / "replay_projection.csv"
    res_df.to_csv(f_csv, index=False)
    print(f"  CSV: {f_csv}")
    
    f_md = artifacts_dir / "replay_projection.md"
    with open(f_md, 'w') as f:
        f.write("# Proposed Config Replay Projection\n\n")
        f.write("## Scenario Definitions\n")
        f.write("- **Baseline**: Current backtest results\n")
        f.write(f"- **S1_SrcFilter**: Disable high-drag sources ({', '.join(DISABLED_SOURCES)})\n")
        f.write("- **S2_SlipCap**: S1 + Skip trades with >5t entry slippage\n")
        f.write("- **S3_RTH_Only**: S2 + Limit entries to 09:30-15:30 ET\n\n")
        f.write("## Comparison Table\n\n")
        f.write(to_md_table(res_df))
    print(f"  Markdown: {f_md}")

if __name__ == "__main__":
    main()
