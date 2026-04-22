import pandas as pd
import numpy as np
from pathlib import Path

def main():
    repo_root = Path(".")
    artifacts_dir = repo_root / "Analysis/artifacts"
    
    tl_path = artifacts_dir / "trade_lifecycle.parquet"
    if not tl_path.exists():
        print(f"ERROR: {tl_path} not found.")
        return
        
    df = pd.read_parquet(tl_path)
    
    # Constants for MNQ (per schema)
    TICK_SIZE = 0.25
    POINT_VALUE = 2.00
    COMMISSION_RT = 3.00
    
    # 1. Total slippage cost
    # Formula: sum(entry_slip + exit_slip) * tick_size * point_value * mean_contracts
    total_slip_ticks = df['entry_slip_ticks'].fillna(0).sum() + df['exit_slip_ticks'].fillna(0).sum()
    mean_contracts = df['contracts'].mean()
    total_slippage_usd = total_slip_ticks * TICK_SIZE * POINT_VALUE * mean_contracts
    
    # 2. Gross vs net P&L
    gross_pnl = df['realized_pnl_$'].sum()
    net_pnl_after_slippage = gross_pnl - total_slippage_usd
    num_trades = len(df)
    commission_estimate = num_trades * COMMISSION_RT
    net_after_all_costs = net_pnl_after_slippage - commission_estimate
    
    # 3. Trade outcome bins
    wins = df[df['realized_pnl_$'] > 5]
    losses = df[df['realized_pnl_$'] < -5]
    scratches = df[df['realized_pnl_$'].abs() <= 5]
    
    # 4. Win/loss asymmetry
    median_win = wins['realized_pnl_$'].median() if not wins.empty else 0
    median_loss = losses['realized_pnl_$'].median() if not losses.empty else 0
    ratio = abs(median_win / median_loss) if median_loss != 0 else 0
    
    largest_win = wins['realized_pnl_$'].max() if not wins.empty else 0
    largest_loss = losses['realized_pnl_$'].min() if not losses.empty else 0
    
    profit_factor = wins['realized_pnl_$'].sum() / abs(losses['realized_pnl_$'].sum()) if not losses.empty else 0
    
    # PRINT RESULTS
    print("1. Total slippage cost:")
    print(f"   Slippage Ticks: {total_slip_ticks:.1f}")
    print(f"   Mean Contracts: {mean_contracts:.2f}")
    print(f"   Total $ lost to slippage: ${total_slippage_usd:.2f}")
    
    print("\n2. Gross vs net P&L:")
    print(f"   Gross PnL (from fills): ${gross_pnl:.2f}")
    print(f"   Net PnL (after extra slip adj): ${net_pnl_after_slippage:.2f}")
    print(f"   Commission Estimate ({num_trades} trades): ${commission_estimate:.2f}")
    print(f"   Net after all costs: ${net_after_all_costs:.2f}")
    
    print("\n3. Trade outcome bins:")
    print(f"   Win (> $5):     count={len(wins):>3}, total=${wins['realized_pnl_$'].sum():>8.2f}")
    print(f"   Loss (< -$5):   count={len(losses):>3}, total=${losses['realized_pnl_$'].sum():>8.2f}")
    print(f"   Scratch (<= $5): count={len(scratches):>3}, total=${scratches['realized_pnl_$'].sum():>8.2f}")
    
    print("\n4. Win/loss asymmetry:")
    print(f"   Median Winner: ${median_win:.2f}")
    print(f"   Median Loser:  ${median_loss:.2f}")
    print(f"   Ratio:         {ratio:.2f}x")
    print(f"   Largest Win:   ${largest_win:.2f}")
    print(f"   Largest Loss:  ${largest_loss:.2f}")
    print(f"   Profit Factor: {profit_factor:.2f}")

if __name__ == "__main__":
    main()
