import pandas as pd
import numpy as np
from pathlib import Path
import sys

sys.path.append(str(Path("Analysis/scripts").resolve()))
import importlib.util

# Dynamically import 11_replay_harness if direct import fails
try:
    import replay_harness as rh
except ImportError:
    spec = importlib.util.spec_from_file_location("rh", "Analysis/scripts/11_replay_harness.py")
    rh = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(rh)

def main():
    repo_root = Path(".")
    artifacts_dir = repo_root / "Analysis/artifacts"
    
    outcomes_path = artifacts_dir / "outcomes.parquet"
    signals_path = artifacts_dir / "signals.parquet"
    tl_path = artifacts_dir / "trade_lifecycle.parquet"
    
    if not signals_path.exists() or not outcomes_path.exists() or not tl_path.exists():
        print(f"Missing parquets in {artifacts_dir}. Need signals, outcomes, and trade_lifecycle.")
        return
        
    print("Loading data...")
    signals = pd.read_parquet(signals_path)
    outcomes = pd.read_parquet(outcomes_path)
    tl = pd.read_parquet(tl_path)
    
    # Merge signals and outcomes for replay
    df = signals.merge(outcomes[['signal_id', 'sim_pnl']], on='signal_id', how='left')
    df['gate_reason'] = df.get('gate_expression', 'accepted')
    df['gate_reason'] = df['gate_reason'].fillna('accepted')
    
    config = rh.load_config()
    
    print("Running replay baseline...")
    replay_df = rh.replay(config, df)
    replay_m = rh.metrics(replay_df)
    
    # Actual metrics from trade_lifecycle.parquet
    print("Computing actual metrics...")
    actual_pnl = tl['realized_pnl_$'].sum()
    actual_trades = len(tl)
    actual_wins = len(tl[tl['realized_pnl_$'] > 0])
    actual_win_rate = actual_wins / actual_trades if actual_trades > 0 else 0
    
    # Approx max drawdown for actual
    tl_sorted = tl.sort_values('entry_timestamp').copy()
    tl_sorted['cum_pnl'] = tl_sorted['realized_pnl_$'].cumsum()
    actual_drawdown = tl_sorted['cum_pnl'].cummax() - tl_sorted['cum_pnl']
    actual_max_drawdown = actual_drawdown.max()
    
    # Consecutive losses actual
    tl_sorted['is_loss'] = (tl_sorted['realized_pnl_$'] < 0).astype(int)
    tl_sorted['loss_streak'] = tl_sorted['is_loss'].groupby((tl_sorted['is_loss'] == 0).cumsum()).cumsum()
    actual_max_consec_losses = tl_sorted['loss_streak'].max()
    
    metrics_comp = [
        {'metric': 'total_pnl', 'actual': actual_pnl, 'replay': replay_m.get('total_pnl', 0)},
        {'metric': 'n_trades', 'actual': actual_trades, 'replay': replay_m.get('n_trades', 0)},
        {'metric': 'win_rate', 'actual': actual_win_rate, 'replay': replay_m.get('win_rate', 0)},
        {'metric': 'max_drawdown', 'actual': actual_max_drawdown, 'replay': replay_m.get('max_drawdown', 0)},
        {'metric': 'max_consecutive_losses', 'actual': actual_max_consec_losses, 'replay': replay_m.get('max_consecutive_losses', 0)}
    ]
    
    for m in metrics_comp:
        m['abs_diff'] = abs(m['actual'] - m['replay'])
        m['pct_diff'] = (m['abs_diff'] / abs(m['actual'])) * 100 if m['actual'] != 0 else 0
        
    comp_df = pd.DataFrame(metrics_comp)
    
    # Per-source breakdown
    actual_by_source = tl.groupby('source').agg(
        actual_n=('trade_id', 'count'),
        actual_pnl=('realized_pnl_$', 'sum')
    ).reset_index()
    
    # For replay, we need to join back to source
    replay_accepted = replay_df[replay_df['accept_under_new_config']]
    replay_with_source = replay_accepted.merge(signals[['signal_id', 'source']], on='signal_id', how='left')
    
    replay_by_source = replay_with_source.groupby('source').agg(
        replay_n=('signal_id', 'count'),
        replay_pnl=('realized_pnl', 'sum')
    ).reset_index()
    
    source_comp = actual_by_source.merge(replay_by_source, on='source', how='outer').fillna(0)
    
    # Verdict
    max_pct_diff = comp_df['pct_diff'].max()
    if max_pct_diff > 15:
        verdict = "FAIL"
    elif max_pct_diff > 5:
        verdict = "WARN"
    else:
        verdict = "PASS"
        
    # Mismatches
    signals['traded_actual'] = signals['traded'] # assuming traded is bool
    replay_df['traded_replay'] = replay_df['accept_under_new_config']
    
    mismatch_df = signals[['signal_id', 'source', 'score', 'traded_actual']].merge(
        replay_df[['signal_id', 'traded_replay', 'reject_reason']], on='signal_id', how='left'
    )
    mismatch_df['traded_replay'] = mismatch_df['traded_replay'].fillna(False)
    mismatches = mismatch_df[mismatch_df['traded_actual'] != mismatch_df['traded_replay']]
    
    top_mismatches = mismatches.head(20)
    
    report_md = artifacts_dir / "replay_validation.md"
    with open(report_md, 'w', encoding='utf-8') as f:
        f.write("# Replay Baseline Validation\n\n")
        f.write(f"**Verdict:** {verdict} (Max pct diff: {max_pct_diff:.1f}%)\n\n")
        
        f.write("## Overall Metrics Comparison\n")
        f.write(comp_df.to_string(index=False) + "\n\n")
        
        f.write("## Per-Source Comparison\n")
        f.write(source_comp.to_string(index=False) + "\n\n")
        
        f.write(f"## Top 20 Mismatches (Total: {len(mismatches)})\n")
        f.write(top_mismatches.to_string(index=False) + "\n")
        
    print(f"Validation Verdict: {verdict}")
    print(comp_df.to_string(index=False))
    print(f"\nSaved report to {report_md}")

if __name__ == '__main__':
    main()
