import pandas as pd
import numpy as np
from pathlib import Path
import sys

# Set seed for reproducibility
np.random.seed(42)

def main():
    repo_root = Path(".")
    artifacts_dir = repo_root / "Analysis/artifacts"
    
    # [INPUT]
    print("[INPUT] Loading artifacts...")
    files = {
        'tl': 'trade_lifecycle.parquet',
        'sig': 'signals.parquet',
        'rw': 'rank_win.parquet'
    }
    
    data = {}
    for k, v in files.items():
        p = artifacts_dir / v
        if not p.exists():
            print(f"ERROR: {p} not found.")
            sys.exit(1)
        data[k] = pd.read_parquet(p)
        print(f"  Loaded {v}: {len(data[k])} rows")

    tl = data['tl']
    sig = data['sig']
    rw = data['rw']

    # [PARSE]
    print("[PARSE] Merging datasets for deep dive...")
    # trade_lifecycle already has score, grade, source
    # We only need signal_id from signals to link to rank_win
    df_full = tl.merge(sig[['trade_id', 'signal_id']], on='trade_id', how='left')
    
    # Join to rank_win to get raw_score and final_score
    df_full = df_full.merge(rw[['eval_id', 'raw_score', 'final_score']], left_on='signal_id', right_on='eval_id', how='left')
    
    print(f"  Merge complete. Rows: {len(df_full)}")

    # [RESULT] 1. Source Level Stats
    print("[RESULT] 1. Source Level Performance")
    
    def get_pf(x):
        wins = x[x > 0].sum()
        losses = abs(x[x < 0].sum())
        return wins / losses if losses > 0 else (np.inf if wins > 0 else 0.0)

    # Use categorical for sorting if needed, but here we group
    source_stats_list = []
    for src, group in df_full.groupby('source'):
        wins = group['realized_pnl_$'][group['realized_pnl_$'] > 0].sum()
        losses = abs(group['realized_pnl_$'][group['realized_pnl_$'] < 0].sum())
        pf = wins / losses if losses > 0 else (np.inf if wins > 0 else 0.0)
        
        source_stats_list.append({
            'source': src,
            'n_trades': len(group),
            'n_wins': (group['realized_pnl_$'] > 5).sum(),
            'n_losses': (group['realized_pnl_$'] < -5).sum(),
            'n_scratch': (group['realized_pnl_$'].abs() <= 5).sum(),
            'gross_pnl_': group['realized_pnl_$'].sum(),
            'pf': pf,
            'avg_pnl_': group['realized_pnl_$'].mean(),
            'total_slip_ticks': group['entry_slip_ticks'].fillna(0).sum() + group['exit_slip_ticks'].fillna(0).sum(),
            'med_dur': group['trade_duration_min'].median(),
            'pct_stop': (group['exit_subtype'] == 'stop_exit').mean() * 100,
            'pct_t2': (group['exit_subtype'] == 't2_exit').mean() * 100,
            'pct_forced': (group['exit_subtype'] == 'forced_exit').mean() * 100
        })
    
    source_stats = pd.DataFrame(source_stats_list).set_index('source').sort_values('gross_pnl_')
    print(source_stats[['n_trades', 'gross_pnl_', 'pf', 'avg_pnl_', 'total_slip_ticks']])

    # 3. Highlight Losing Sources
    print("\n[RESULT] 2. Highlighted Losing Sources (PF < 1.0)")
    losers = source_stats[source_stats['pf'] < 1.0].index.tolist()
    if not losers:
        print("  No sources with PF < 1.0 found.")
    else:
        # score and grade are in df_full (from tl)
        loser_dive = df_full[df_full['source'].isin(losers)].groupby('source').agg(
            avg_score=('score', 'mean'),
            avg_raw=('raw_score', 'mean'),
            avg_final=('final_score', 'mean'),
            dom_grade=('grade', lambda x: x.value_counts().index[0] if not x.empty else 'N/A')
        )
        print(loser_dive)

    # 4. Grade Level Stats
    print("\n[RESULT] 3. Grade Level Performance")
    grade_order = ['C', 'B', 'A', 'A+']
    grade_stats = df_full.groupby('grade').agg(
        n_trades=('trade_id', 'count'),
        win_rate=('realized_pnl_$', lambda x: (x > 0).mean()),
        pf=('realized_pnl_$', get_pf),
        avg_pnl_=('realized_pnl_$', 'mean')
    ).reindex(grade_order)
    
    # Monotonicity check
    pfs = grade_stats['pf'].dropna().tolist()
    is_monotonic = all(x <= y for x, y in zip(pfs, pfs[1:]))
    verdict = "MONOTONIC" if is_monotonic else "SCRAMBLED"
    print(grade_stats)
    print(f"\nVERDICT: {verdict}")

    # 5. Source x Grade Combo (n >= 20)
    print("\n[RESULT] 4. Source x Grade Performance (n >= 20)")
    sg_grp = df_full.groupby(['source', 'grade']).agg(
        n=('trade_id', 'count'),
        pf=('realized_pnl_$', get_pf),
        avg_pnl=('realized_pnl_$', 'mean')
    )
    sg_valid = sg_grp[sg_grp['n'] >= 20].sort_values('pf', ascending=False)
    
    print("Top 5 by PF:")
    print(sg_valid.head(5))
    print("\nBottom 5 by PF:")
    print(sg_valid.tail(5))
    
    # Flag A+ trades losing money
    ap_losers = sg_grp.loc[(slice(None), 'A+'), :]
    ap_losers = ap_losers[ap_losers['avg_pnl'] < 0]
    if not ap_losers.empty:
        print("\n[FLAG] Strongly Broken: A+ trades losing money for these sources:")
        print(ap_losers)

    # [SAVED]
    print("\n[SAVED] Saving results...")
    f_csv = artifacts_dir / "source_audit.csv"
    source_stats.to_csv(f_csv)
    print(f"  CSV: {f_csv}")
    
    f_md = artifacts_dir / "source_audit.md"
    with open(f_md, 'w') as f:
        f.write("# Source and Grade Audit Summary\n\n")
        f.write("## Source Performance\n\n")
        f.write(source_stats.to_csv(sep='|', index=True)) # Pseudo-markdown
        f.write("\n\n## Grade Performance\n\n")
        f.write(grade_stats.to_csv(sep='|', index=True))
        f.write(f"\n\nVERDICT: {verdict}\n")
    print(f"  Markdown: {f_md}")

if __name__ == "__main__":
    main()
