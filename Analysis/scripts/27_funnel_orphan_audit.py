import pandas as pd
import numpy as np
import re
from pathlib import Path

def normalize_trade_id(csid):
    if not isinstance(csid, str):
        return csid
    # Drop _HHMM
    return re.sub(r'_\d{4}', '', csid)

def parse_rank_weak(detail):
    # RANK_WEAK [module_id] A=int B=int C=int D=int Pen=int Net=int Mult=float
    pattern = r"A=(-?\d+) B=(-?\d+) C=(-?\d+) D=(-?\d+) Pen=(-?\d+) Net=(-?\d+)"
    match = re.search(pattern, detail)
    if match:
        return {
            'A': int(match.group(1)),
            'B': int(match.group(2)),
            'C': int(match.group(3)),
            'D': int(match.group(4)),
            'Pen': int(match.group(5)),
            'Net': int(match.group(6))
        }
    return None

def main():
    repo_root = Path(".")
    log_path = repo_root / "backtest/Log.csv"
    tl_path = repo_root / "Analysis/artifacts/trade_lifecycle.parquet"
    
    if not log_path.exists():
        print(f"ERROR: {log_path} not found.")
        return

    print(f"Loading {log_path} (this may take a moment)...")
    log = pd.read_csv(log_path)
    # Ensure naive timestamps for comparison
    log['Timestamp'] = pd.to_datetime(log['Timestamp'], errors='coerce').dt.tz_localize(None)
    
    # Filter for session open to help with timestamp sanity
    session_opens = log[log['Tag'] == 'SESSION']
    session_opens = session_opens[session_opens['Detail'].str.contains('OPEN', na=False)]
    
    # Define signal_id for join (Bar, Source, ConditionSetId, Direction)
    # Note: Direction might be empty on some tags, so we use it where available.
    
    ### SECTION 1 — Funnel counts
    print("\n### SECTION 1 — Funnel counts")
    funnel_stats = []
    sources = log['Source'].dropna().unique()
    
    for src in sources:
        src_log = log[log['Source'] == src]
        
        n_evals = len(src_log[src_log['Tag'] == 'EVAL'])
        # Filter WARN for RANK_WEAK
        n_rank_weak = len(src_log[(src_log['Tag'] == 'WARN') & (src_log['Detail'].str.contains('RANK_WEAK', na=False))])
        
        n_rejected = len(src_log[src_log['Tag'] == 'SIGNAL_REJECTED'])
        n_accepted = len(src_log[src_log['Tag'] == 'SIGNAL_ACCEPTED'])
        n_rank_passed = n_rejected + n_accepted
        
        n_ordered = len(src_log[src_log['Tag'] == 'ORDER_LMT'])
        
        funnel_stats.append({
            'Source': src,
            'Evals': n_evals,
            'Rank_Weak': n_rank_weak,
            'Rank_Passed': n_rank_passed,
            'Rejected': n_rejected,
            'Accepted': n_accepted,
            'Ordered': n_ordered
        })
    
    # To get Filled and Resolved, we need to join or use trade_lifecycle
    if tl_path.exists():
        tl = pd.read_parquet(tl_path)
        # Ensure tl timestamps are naive
        tl['entry_timestamp'] = pd.to_datetime(tl['entry_timestamp']).dt.tz_localize(None)
        tl['exit_timestamp'] = pd.to_datetime(tl['exit_timestamp']).dt.tz_localize(None)
        
        tl_counts = tl.groupby('source').size().reset_index(name='Filled')
        # Resolved: has exit_subtype and is not 'open'
        tl_resolved = tl[tl['exit_subtype'].notna() & (tl['exit_subtype'] != 'open')].groupby('source').size().reset_index(name='Resolved')
        
        funnel_df = pd.DataFrame(funnel_stats)
        funnel_df = funnel_df.merge(tl_counts, left_on='Source', right_on='source', how='left').drop(columns='source')
        funnel_df = funnel_df.merge(tl_resolved, left_on='Source', right_on='source', how='left').drop(columns='source')
        funnel_df = funnel_df.fillna(0)
    else:
        funnel_df = pd.DataFrame(funnel_stats)
        funnel_df['Filled'] = 0
        funnel_df['Resolved'] = 0

    print(funnel_df.to_string(index=False))
    
    funnel_breaks = []
    for _, row in funnel_df.iterrows():
        # n_evals >= n_rank_passed (rank filter only subtracts)
        # However, some evals might not even reach rank if they fail earlier logic? 
        # But usually Evals is the start.
        if row['Evals'] < row['Rank_Passed']: funnel_breaks.append(f"{row['Source']}: Evals < Rank_Passed")
        # n_rank_passed == n_rejected + n_accepted
        # (This is true by our definition above, but let's check if the log itself agrees)
        
        # n_accepted == n_ordered
        if row['Accepted'] != row['Ordered']: funnel_breaks.append(f"{row['Source']}: Accepted ({row['Accepted']}) != Ordered ({row['Ordered']})")
        # n_filled <= n_ordered
        if row['Filled'] > row['Ordered']: funnel_breaks.append(f"{row['Source']}: Filled ({row['Filled']}) > Ordered ({row['Ordered']})")
        # n_resolved <= n_filled
        if row['Resolved'] > row['Filled']: funnel_breaks.append(f"{row['Source']}: Resolved ({row['Resolved']}) > Filled ({row['Filled']})")

    ### SECTION 2 — Orphans
    print("\n### SECTION 2 — Orphans")
    orphans = {}
    
    # 1. EVAL orphans: EVAL rows with no follow-up
    # followups: SIGNAL_ACCEPTED, SIGNAL_REJECTED, and RANK_WEAK (which is in WARN)
    # Match by Bar and Source and ConditionSetId
    evals = log[log['Tag'] == 'EVAL'].copy()
    followups = log[log['Tag'].isin(['SIGNAL_ACCEPTED', 'SIGNAL_REJECTED'])]
    rank_weaks = log[(log['Tag'] == 'WARN') & (log['Detail'].str.contains('RANK_WEAK', na=False))]
    
    # Combine followup identifiers
    follow_ids = pd.concat([
        followups[['Bar', 'Source', 'ConditionSetId']],
        rank_weaks[['Bar', 'Source']] # RANK_WEAK might have module_id in detail but ConditionSetId col is usually empty
    ]).drop_duplicates()
    
    # For RANK_WEAK specifically, we need to parse module_id from Detail if we want exact match
    def get_rank_module(detail):
        match = re.search(r'RANK_WEAK \[([^\]]+)\]', detail)
        return match.group(1) if match else None
    
    rank_ids = rank_weaks.copy()
    rank_ids['ConditionSetId'] = rank_ids['Detail'].apply(get_rank_module)
    follow_ids = pd.concat([
        followups[['Bar', 'Source', 'ConditionSetId']],
        rank_ids[['Bar', 'Source', 'ConditionSetId']]
    ]).drop_duplicates()
    
    eval_ids_only = evals[['Bar', 'Source', 'ConditionSetId']].drop_duplicates()
    eval_join = eval_ids_only.merge(follow_ids, on=['Bar', 'Source', 'ConditionSetId'], how='left', indicator=True)
    n_eval_orphans = len(eval_join[eval_join['_merge'] == 'left_only'])
    orphans['EVAL_orphans'] = n_eval_orphans
    
    # 2. SIGNAL_ACCEPTED with no ORDER_LMT
    accepted = log[log['Tag'] == 'SIGNAL_ACCEPTED'].copy()
    ordered = log[log['Tag'] == 'ORDER_LMT'].copy()
    
    acc_ids = accepted[['Bar', 'Source', 'ConditionSetId', 'Direction']].drop_duplicates()
    ord_ids = ordered[['Bar', 'Source', 'ConditionSetId', 'Direction']].drop_duplicates()
    
    acc_ord_join = acc_ids.merge(ord_ids, on=['Bar', 'Source', 'ConditionSetId', 'Direction'], how='left', indicator=True)
    orphans['SIGNAL_ACCEPTED_orphans'] = len(acc_ord_join[acc_ord_join['_merge'] == 'left_only'])
    
    # 3. ORDER_LMT with no ENTRY_FILL
    if tl_path.exists():
        # Use trade_lifecycle to find filled trades
        filled_tl = tl[['condition_set_id', 'source', 'direction', 'entry_timestamp']].copy()
        # Proximity match is safest as Bar index isn't always on ORDER_LMT? 
        # Actually Schema says Bar=0 on ORDER_LMT? Let's check.
        # "0 on lifecycle events that aren't bar-aligned (STOP_MOVE, ENTRY_FILL, ... ORDER_LMT, SIGNAL_ACCEPTED ...)"
        # So we use (Source, ConditionSetId, Direction) + Time window
        
        ord_orphans = 0
        for _, o in ordered.iterrows():
            # Match within 5 min
            matches = tl[(tl['source'] == o['Source']) & 
                         (tl['condition_set_id'] == o['ConditionSetId']) & 
                         (tl['direction'] == o['Direction']) &
                         (tl['entry_timestamp'] >= o['Timestamp']) &
                         (tl['entry_timestamp'] <= o['Timestamp'] + pd.Timedelta(minutes=5))]
            if matches.empty:
                ord_orphans += 1
        orphans['ORDER_LMT_orphans'] = ord_orphans
    
    # 4. T1/T2/STOP_MOVE orphans
    if tl_path.exists():
        tl_windows = tl[['trade_id', 'entry_timestamp', 'exit_timestamp']].copy()
        
        stop_moves = log[log['Tag'] == 'STOP_MOVE'].copy()
        sm_orphans = 0
        for _, sm in stop_moves.iterrows():
            mask = (tl_windows['entry_timestamp'] <= sm['Timestamp']) & (tl_windows['exit_timestamp'] >= sm['Timestamp'])
            if not mask.any():
                sm_orphans += 1
        orphans['STOP_MOVE_orphans'] = sm_orphans
        
        t_hits = log[log['Tag'].isin(['T1_HIT', 'T2_HIT'])].copy()
        th_orphans = 0
        for _, th in t_hits.iterrows():
            mask = (tl_windows['entry_timestamp'] <= th['Timestamp']) & (tl_windows['exit_timestamp'] >= th['Timestamp'])
            if not mask.any():
                th_orphans += 1
        orphans['T_HIT_orphans'] = th_orphans
        
        # TA_DECISION orphans
        ta_decisions = log[log['Tag'] == 'TA_DECISION'].copy()
        ta_decisions['trade_id'] = ta_decisions['ConditionSetId'].apply(normalize_trade_id)
        ta_orphans = ta_decisions[~ta_decisions['trade_id'].isin(tl['trade_id'])]
        orphans['TA_DECISION_orphans'] = len(ta_orphans)
        
        # TRADE_RESET orphans
        trade_resets = log[log['Tag'] == 'TRADE_RESET'].copy()
        # Reset has Source, CSID, Direction. Match proximity to exit_timestamp.
        tr_orphans = 0
        for _, tr in trade_resets.iterrows():
            mask = (tl['source'] == tr['Source']) & \
                   (tl['condition_set_id'] == tr['ConditionSetId']) & \
                   (tl['direction'] == tr['Direction']) & \
                   (tl['exit_timestamp'] >= tr['Timestamp'] - pd.Timedelta(minutes=1)) & \
                   (tl['exit_timestamp'] <= tr['Timestamp'] + pd.Timedelta(minutes=1))
            if not mask.any():
                tr_orphans += 1
        orphans['TRADE_RESET_orphans'] = tr_orphans

    for k, v in orphans.items():
        print(f"{k}: {v}")

    ### SECTION 3 — Timestamp sanity
    print("\n### SECTION 3 — Timestamp sanity")
    # Events with timestamp=0001-01-01 that weren't WARN or ZONE_MITIGATED.
    null_ts = log[log['Timestamp'] == pd.Timestamp('0001-01-01')]
    invalid_null_ts = null_ts[~null_ts['Tag'].isin(['WARN', 'ZONE_MITIGATED'])]
    print(f"Events with 0001-01-01 timestamp (excluding WARN/ZONE): {len(invalid_null_ts)}")
    
    if tl_path.exists():
        tl_time_sanity = tl[tl['exit_timestamp'] < tl['entry_timestamp']]
        print(f"Trades with exit_timestamp < entry_timestamp: {len(tl_time_sanity)}")

    ### SECTION 4 — Score consistency
    print("\n### SECTION 4 — Score consistency")
    eval_scores = log[log['Tag'] == 'EVAL'][['Bar', 'Source', 'ConditionSetId', 'Direction', 'Score']].rename(columns={'Score': 'EVAL_Score'})
    acc_scores = log[log['Tag'] == 'SIGNAL_ACCEPTED'][['Bar', 'Source', 'ConditionSetId', 'Direction', 'Score']].rename(columns={'Score': 'ACC_Score'})
    
    score_join = eval_scores.merge(acc_scores, on=['Bar', 'Source', 'ConditionSetId', 'Direction'])
    mismatches = score_join[score_join['EVAL_Score'] != score_join['ACC_Score']]
    print(f"Score mismatches between EVAL and ACCEPTED: {len(mismatches)}")

    if len(mismatches) > 0:
        print(mismatches.head())

    ### SECTION 5 — RANK_WEAK Net = A+B+C+D−Pen
    print("\n### SECTION 5 — RANK_WEAK Net calculation check")
    rank_weak_rows = log[(log['Tag'] == 'WARN') & (log['Detail'].str.contains('RANK_WEAK', na=False))].copy()
    
    net_mismatches = 0
    for _, row in rank_weak_rows.iterrows():
        p = parse_rank_weak(row['Detail'])
        if p:
            expected_net = p['A'] + p['B'] + p['C'] + p['D'] - p['Pen']
            if expected_net != p['Net']:
                net_mismatches += 1
                
    print(f"RANK_WEAK Net mismatches: {net_mismatches}")

    ### SECTION 6 — Same bar, duplicate accepts
    print("\n### SECTION 6 — Duplicate accepts same bar")
    dupes = log[log['Tag'] == 'SIGNAL_ACCEPTED'].groupby(['Bar', 'Source']).size().reset_index(name='count')
    dupes = dupes[dupes['count'] > 1]
    print(f"Cases of multiple SIGNAL_ACCEPTED on same Bar/Source: {len(dupes)}")

    ### REPORT GENERATION
    output_md = repo_root / "funnel_orphan_audit.md"
    with open(output_md, 'w', encoding='utf-8') as f:
        f.write("# Funnel & Orphan Audit Report\n\n")
        f.write("## Section 1: Funnel Invariants\n")
        f.write(funnel_df.to_string(index=False) + "\n\n")
        if funnel_breaks:
            f.write("### Breaks Found:\n")
            for b in funnel_breaks:
                f.write(f"- {b}\n")
        else:
            f.write("All invariants passed.\n\n")
            
        f.write("## Section 2: Orphans\n")
        for k, v in orphans.items():
            f.write(f"- {k}: {v}\n")
        f.write("\n")
        
        f.write("## Section 5: RANK_WEAK consistency\n")
        f.write(f"- Net mismatches: {net_mismatches}\n")
        if net_mismatches > 0:
            f.write("BLOCKER: Weight optimization (Phase 13b) cannot proceed until Net calculation is fixed in StrategyLogger.cs.\n")
        
        f.write("\n## Logging gaps to close\n")
        if orphans['EVAL_orphans'] > 0:
            f.write("- EVAL orphans detected. Check `StrategyEngine.cs` for silent filter stages.\n")
        if orphans['STOP_MOVE_orphans'] > 0:
            f.write("- STOP_MOVE orphans detected. Check `OrderManager.cs` for stray stop updates.\n")
        if net_mismatches > 0:
            f.write("- RANK_WEAK Net mismatch. Fix arithmetic in `StrategyLogger.cs`.\n")

    print(f"\nSaved report to {output_md}")

if __name__ == "__main__":
    main()
