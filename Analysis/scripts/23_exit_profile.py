import pandas as pd
import numpy as np
from pathlib import Path

# Set seed for reproducibility
np.random.seed(42)

def main():
    repo_root = Path(".")
    artifacts_dir = repo_root / "Analysis/artifacts"
    
    # Load required artifacts
    files = {
        'tl': 'trade_lifecycle.parquet',
        'sig': 'signals.parquet',
        'be': 'dynamic_be.parquet',
        'sh': 'ta_shadows.parquet'
    }
    
    data = {}
    for k, v in files.items():
        p = artifacts_dir / v
        if not p.exists():
            print(f"ERROR: {p} not found.")
            return
        data[k] = pd.read_parquet(p)
    
    tl = data['tl']
    sig = data['sig']
    
    # Enrich trade_lifecycle with signal context
    # trade_id is unique in both for these trades
    df = tl.merge(sig[['trade_id', 'ctx_smf']], on='trade_id', how='left')
    
    # Constants
    TICK_SIZE = 0.25
    POINT_VALUE = 2.00
    COMM_RT = 3.00
    
    # BASELINE
    gross_pnl = df['realized_pnl_$'].sum()
    # Slippage already includes contracts in its calculation within parse_events usually? 
    # The baseline prompt says: sum(slip_ticks) * tick_size * point_value * mean_contracts
    total_slip_ticks = df['entry_slip_ticks'].fillna(0).sum() + df['exit_slip_ticks'].fillna(0).sum()
    mean_contracts = df['contracts'].mean()
    total_slippage_usd = total_slip_ticks * TICK_SIZE * POINT_VALUE * mean_contracts
    
    # =========================================================================
    # SECTION 1 — Slippage Deep Dive
    # =========================================================================
    # 1.1 Distribution
    entry_q = df['entry_slip_ticks'].quantile([0.25, 0.5, 0.75, 0.95])
    exit_q = df['exit_slip_ticks'].quantile([0.25, 0.5, 0.75, 0.95])
    
    # 1.3 Session Time
    # Convert to ET (UTC-5)
    df['hour_et'] = (df['entry_timestamp'].dt.tz_convert('America/New_York')).dt.hour
    hourly_slip = df.groupby('hour_et')['entry_slip_ticks'].median()
    worst_hour = hourly_slip.idxmax()
    
    # 1.6 Preventable Slippage
    # Worst 20%
    slip_threshold = df['entry_slip_ticks'].quantile(0.8)
    worst_20_mask = df['entry_slip_ticks'] >= slip_threshold
    preventable_slip_trades = df[worst_20_mask]
    
    # Calculation: If we skipped entries with >5t slippage
    skipped_gt5_df = df[df['entry_slip_ticks'] <= 5]
    recovery_gt5 = skipped_gt5_df['realized_pnl_$'].sum() - (len(skipped_gt5_df) * COMM_RT)
    baseline_net = gross_pnl - total_slippage_usd - (len(df) * COMM_RT)
    
    # =========================================================================
    # SECTION 3 — Signal Quality
    # =========================================================================
    # 3.1 Per-source PF
    source_stats = []
    for src, group in df.groupby('source'):
        if len(group) < 30: continue
        wins = group[group['realized_pnl_$'] > 0]['realized_pnl_$'].sum()
        losses = abs(group[group['realized_pnl_$'] < 0]['realized_pnl_$'].sum())
        pf = wins / losses if losses > 0 else np.inf
        source_stats.append({'source': src, 'n': len(group), 'pf': pf, 'sum_pnl': group['realized_pnl_$'].sum()})
    source_df = pd.DataFrame(source_stats).sort_values('pf', ascending=False)
    
    # 3.3 Losing sources
    losing_sources = source_df[source_df['pf'] < 1.0]
    losing_src_count = len(losing_sources)
    losing_src_drag = losing_sources['sum_pnl'].sum()

    # =========================================================================
    # SECTION 4 — BE-Arm Effectiveness
    # =========================================================================
    # 4.1 BE-arm impact
    # saved = (original_stop - entry) vs (be_stop - entry)
    # This requires original stop price which we have.
    be_trades = df[df['be_arm_reason'].notna()]
    # Simple saved estimate: (entry - stop_original) * side * contracts * PV for those that hit exit_subtype=='stop_exit'
    # but were likely saved by BE. 
    # For now, let's use the requested metrics.
    be_hit_t1 = be_trades['hit_t1'].mean()
    # saved: cases where exit_subtype is stop_exit but realized_pnl is near 0 instead of full stop loss
    # full_stop_loss = (stop_original - entry) * side...
    # net_impact is complex without full tick data, we'll estimate based on pnl vs original risk
    
    # =========================================================================
    # SECTION 5 — Grade Calibration
    # =========================================================================
    grade_stats = []
    grade_order = ['A+', 'A', 'B', 'C']
    for g in grade_order:
        group = df[df['grade'] == g]
        if group.empty: continue
        wins = group[group['realized_pnl_$'] > 0]['realized_pnl_$'].sum()
        losses = abs(group[group['realized_pnl_$'] < 0]['realized_pnl_$'].sum())
        pf = wins / losses if losses > 0 else np.inf
        grade_stats.append({'grade': g, 'n': len(group), 'win_rate': (group['realized_pnl_$'] > 0).mean(), 'pf': pf})
    grade_df = pd.DataFrame(grade_stats)
    
    # Calibration Check
    pfs = grade_df['pf'].tolist()
    is_monotone = all(x >= y for x, y in zip(pfs, pfs[1:]))
    calibration_verdict = "PASS (Monotone)" if is_monotone else "FAIL (Scoring Miscalibrated)"

    # =========================================================================
    # SECTION 7 — Best-case scenarios
    # =========================================================================
    # 1. As-is Net
    s0 = baseline_net
    # 2. Entry Slip 0
    # total_slippage_usd = (entry_slip + exit_slip) * ...
    entry_slip_usd = df['entry_slip_ticks'].fillna(0).sum() * TICK_SIZE * POINT_VALUE * mean_contracts
    s1 = s0 + entry_slip_usd
    # 3. Entry Slip capped at 2t
    capped_entry_ticks = df['entry_slip_ticks'].clip(upper=2).fillna(0).sum()
    capped_slip_usd = capped_entry_ticks * TICK_SIZE * POINT_VALUE * mean_contracts
    s2 = s0 + (entry_slip_usd - capped_slip_usd)
    # 4. Skip worst 20% entry slip
    s3 = df[~worst_20_mask]['realized_pnl_$'].sum() - (len(df[~worst_20_mask]) * (total_slippage_usd/len(df) + COMM_RT)) # Approx
    # 5. Filter sources PF > 1.1
    good_sources = source_df[source_df['pf'] > 1.1]['source'].tolist()
    s4 = df[df['source'].isin(good_sources)]['realized_pnl_$'].sum() - (len(df[df['source'].isin(good_sources)]) * COMM_RT)

    # FINAL PRINTOUTS
    print(f"Section 1.6: Preventable slippage (skip >5t): Recovery to Net P&L approx ${recovery_gt5:.2f}")
    print(f"Section 3.3: Losing-source count: {losing_src_count}, Total $ drag: ${losing_src_drag:.2f}")
    print(f"Section 4.1: BE-arm fired in {len(be_trades)} trades ({len(be_trades)/len(df):.1%})")
    print(f"Section 5: Grade calibration verdict: {calibration_verdict}")
    print(f"           PF Order: {' -> '.join([f'{p:.2f}' for p in pfs])}")
    
    print("\nSection 7: Best-case scenarios (Hypothetical Net P&L)")
    print(f"  - As-is baseline:               ${s0:,.2f}")
    print(f"  - Fix: Entry Slippage = 0:      ${s1:,.2f}")
    print(f"  - Fix: Entry Slip capped 2t:    ${s2:,.2f}")
    print(f"  - Filter: Skip worst 20% slip:  ${s3:,.2f}")
    print(f"  - Filter: Sources PF > 1.1:     ${s4:,.2f}")

if __name__ == "__main__":
    main()
