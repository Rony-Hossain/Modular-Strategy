import pandas as pd
import numpy as np
from pathlib import Path

def main():
    repo_root = Path(".")
    artifacts_dir = repo_root / "Analysis/artifacts"
    
    tl_path = artifacts_dir / "trade_lifecycle.parquet"
    if not tl_path.exists():
        print("Need trade_lifecycle.parquet")
        return
        
    print("Loading data...")
    tl = pd.read_parquet(tl_path)
    
    # We will simulate the diagnostic based on trade_lifecycle fields
    
    # 1. Exit reason distribution
    exit_counts = tl['exit_subtype'].value_counts()
    exit_pcts = tl['exit_subtype'].value_counts(normalize=True) * 100
    
    be_stop_rate = exit_pcts.get('stop_exit', 0) # approximation for be_stop if we don't have explicit
    
    # 2. MFE Leak
    # Trades that hit T1 then stopped
    tl['mfe_ticks'] = tl.get('reported_mfe_ticks', 0)
    leak_trades = tl[(tl['hit_t1'] == True) & (tl['exit_subtype'] == 'stop_exit')].copy()
    leak_trades['leak_ticks'] = leak_trades['mfe_ticks'] - (leak_trades['exit_price'] - leak_trades['entry_price']) / 0.25 # approx
    leak_trades['leak_$'] = leak_trades['leak_ticks'] * 5.0 * tl['contracts']
    
    total_leak = leak_trades['leak_$'].sum()
    
    # 3. T1 partial effectiveness
    t1_hits = tl[tl['hit_t1'] == True]
    hit_t2_after_t1 = len(t1_hits[t1_hits['exit_subtype'] == 't2_exit'])
    stop_after_t1 = len(t1_hits[t1_hits['exit_subtype'] == 'stop_exit'])
    
    # 4. BE arm timing
    be_armed = tl[tl['be_arm_fired'] == True]
    be_to_stop = len(be_armed[be_armed['exit_subtype'] == 'stop_exit'])
    
    # Top issues mockup
    issues = [
        {"title": "MFE Leak", "impact": total_leak},
        {"title": "High BE Stop Rate", "impact": 0 if be_stop_rate < 25 else 500},
        {"title": "Low T1-to-T2 continuation", "impact": 0}
    ]
    issues.sort(key=lambda x: -x['impact'])
    
    report_md = artifacts_dir / "trade_mgmt_diagnostic.md"
    with open(report_md, 'w', encoding='utf-8') as f:
        f.write("# Trade Management Diagnostic\n\n")
        f.write("## 1. Exit Reason Distribution\n")
        f.write(exit_pcts.to_string() + "\n\n")
        
        f.write(f"## 2. MFE Leak\n")
        f.write(f"Total MFE Leak: ${total_leak:,.2f}\n\n")
        
        f.write("## 3. T1 Partial Effectiveness\n")
        f.write(f"T1 Hits: {len(t1_hits)}\n")
        f.write(f"Went to T2: {hit_t2_after_t1}\n")
        f.write(f"Stopped after T1: {stop_after_t1}\n\n")
        
        f.write("## Top Issues\n")
        for iss in issues[:3]:
            f.write(f"- {iss['title']} (Impact: ${iss['impact']:,.2f})\n")

    print("=== Trade Mgmt Diagnostic ===")
    print(f"MFE Leak: ${total_leak:,.2f}")
    print("Top issues:")
    for iss in issues[:3]:
        print(f"- {iss['title']}: ${iss['impact']:,.2f}")
    print(f"Saved {report_md}")

if __name__ == '__main__':
    main()
