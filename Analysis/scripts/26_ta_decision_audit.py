import pandas as pd
import numpy as np
import re
from pathlib import Path
import os

def parse_kvp(kvp_str):
    if not isinstance(kvp_str, str):
        return {}
    # Split by spaces
    parts = kvp_str.strip().split(' ')
    d = {}
    for p in parts:
        if '=' in p:
            try:
                k, v = p.split('=', 1)
                d[k] = v
            except ValueError:
                continue
    return d

def normalize_trade_id(csid):
    if not isinstance(csid, str):
        return csid
    return re.sub(r'_\d{4}', '', csid)

def main():
    repo_root = Path(".")
    log_path = repo_root / "backtest/Log.csv"
    tl_path = repo_root / "Analysis/artifacts/trade_lifecycle.parquet"
    
    if not log_path.exists():
        print(f"ERROR: {log_path} not found.")
        return
    if not tl_path.exists():
        print(f"ERROR: {tl_path} not found.")
        return

    print(f"Loading {tl_path}...")
    tl = pd.read_parquet(tl_path)
    
    print(f"Loading {log_path} (this may take a moment)...")
    # Only read columns we need to save memory
    usecols = ['Timestamp', 'Tag', 'ConditionSetId', 'Label', 'Detail', 'Bar']
    log = pd.read_csv(log_path, usecols=usecols)
    log['Timestamp'] = pd.to_datetime(log['Timestamp'])
    
    # Filter TA_DECISION rows
    ta = log[log['Tag'] == 'TA_DECISION'].copy()
    ta['trade_id'] = ta['ConditionSetId'].apply(normalize_trade_id)
    
    # Parse Details
    print("Parsing TA_DECISION details...")
    def parse_ta_detail(row):
        detail = row['Detail']
        # Format: <family>,<kvp_payload>
        if ',' in detail:
            family, kvp_str = detail.split(',', 1)
        else:
            family, kvp_str = 'Unknown', detail
        
        kvp = parse_kvp(kvp_str)
        kvp['ta_family'] = family
        return kvp

    parsed_details = ta.apply(parse_ta_detail, axis=1)
    ta_details_df = pd.DataFrame(parsed_details.tolist(), index=ta.index)
    
    # Combine
    ta = pd.concat([ta.drop(columns=['Detail']), ta_details_df], axis=1)
    
    # Convert numeric fields
    numeric_fields = ['sev', 'hard', 'adv', 'dir', 'ext', 'abs', 'stk', 'slopeScore', 
                      'pers', 'stage', 'bias', 'bpct', 'bd', 'cd', 'slope', 'dsh', 'dsl', 
                      'buy', 'sell', 'absv', 'hist', 't1', 'pft', 'mfe']
    for f in numeric_fields:
        if f in ta.columns:
            ta[f] = pd.to_numeric(ta[f], errors='coerce')
            
    # Split thr
    if 'thr' in ta.columns:
        ta[['thr_lo', 'thr_hi']] = ta['thr'].str.split('/', expand=True).astype(float)

    # Join with trade_lifecycle
    print("Joining with trade_lifecycle...")
    # trade_lifecycle has outcome info
    # We want: realized_pnl_$, realized_pnl_ticks, exit_subtype
    tl_subset = tl[['trade_id', 'realized_pnl_$', 'realized_pnl_ticks', 'exit_subtype', 'entry_timestamp', 'exit_timestamp']].copy()
    tl_subset['is_winner'] = tl_subset['realized_pnl_$'] > 0
    
    df = ta.merge(tl_subset, on='trade_id', how='inner')
    
    # Sort for sequence analysis
    df = df.sort_values(['trade_id', 'Timestamp'])

    # --- SECTION 1: Decision-outcome correlation ---
    print("\n### SECTION 1: Decision-outcome correlation")
    # Fraction of bars per decision
    decision_counts = df.groupby(['trade_id', 'Label']).size().unstack(fill_value=0)
    total_decisions = decision_counts.sum(axis=1)
    decision_frac = decision_counts.div(total_decisions, axis=0)
    
    # Merge back winner info
    decision_analysis = decision_frac.merge(tl_subset[['trade_id', 'is_winner']], on='trade_id')
    
    winners = decision_analysis[decision_analysis['is_winner']]
    losers = decision_analysis[~decision_analysis['is_winner']]
    
    sec1_results = []
    # Map observed labels to analysis categories
    # TA_HOLD, TA_GESTATION -> 'Hold'
    # TA_TIGHTEN, TA_TIGHTEN_PROTECT -> 'Tighten'
    # TA_EXIT -> 'Exit'
    
    cat_map = {
        'TA_HOLD': 'Hold',
        'TA_GESTATION': 'Hold',
        'TA_TIGHTEN': 'Tighten',
        'TA_TIGHTEN_PROTECT': 'Tighten',
        'TA_EXIT': 'Exit'
    }
    
    # Create categorized fractions
    cat_frac = pd.DataFrame(index=decision_analysis.index)
    cat_frac['is_winner'] = decision_analysis['is_winner']
    for cat in ['Hold', 'Tighten', 'Exit']:
        cols = [c for c, mapped in cat_map.items() if mapped == cat and c in decision_analysis.columns]
        if cols:
            cat_frac[cat] = decision_analysis[cols].sum(axis=1)
        else:
            cat_frac[cat] = 0.0

    winners_cat = cat_frac[cat_frac['is_winner']]
    losers_cat = cat_frac[~cat_frac['is_winner']]
    
    for cat in ['Hold', 'Tighten', 'Exit']:
        w_mean = winners_cat[cat].mean()
        l_mean = losers_cat[cat].mean()
        sec1_results.append({
            'Category': cat,
            'Winner_Frac': w_mean,
            'Loser_Frac': l_mean,
            'Diff': w_mean - l_mean
        })
    
    sec1_df = pd.DataFrame(sec1_results)
    print(sec1_df.to_string(index=False))
    
    # Sequence analysis
    def get_seq(group):
        return " -> ".join(group['Label'].tolist())
    
    sequences = df.groupby('trade_id').apply(get_seq, include_groups=False).reset_index(name='sequence')
    sequences = sequences.merge(tl_subset[['trade_id', 'is_winner']], on='trade_id')
    
    print("\nTop 5 sequences for Winners:")
    print(sequences[sequences['is_winner']]['sequence'].value_counts().head(5))
    print("\nTop 5 sequences for Losers:")
    print(sequences[~sequences['is_winner']]['sequence'].value_counts().head(5))

    # --- SECTION 2: Severity threshold check ---
    print("\n### SECTION 2: Severity threshold check")
    # sev vs thr
    df['sev_above_inner'] = df['sev'] >= df['thr_lo']
    df['sev_above_outer'] = df['sev'] >= df['thr_hi']
    
    sev_stats = df.groupby(['sev_above_inner', 'sev_above_outer', 'Label']).size().unstack(fill_value=0)
    print("Decisions taken when severity exceeds thresholds:")
    print(sev_stats)
    
    # Outcome when sev > outer_thr
    outer_hit = df[df['sev_above_outer']]
    if not outer_hit.empty:
        outer_outcome = outer_hit.groupby('trade_id')['is_winner'].first().value_counts(normalize=True)
        print("\nOutcome when severity exceeded outer threshold (at least once):")
        print(outer_outcome)
    else:
        print("\nSeverity never exceeded outer threshold in this dataset.")

    # --- SECTION 3: Action-result alignment ---
    print("\n### SECTION 3: Action-result alignment")
    # Find STOP_MOVE and ENTRY_FILL (exit) rows
    stop_moves = log[log['Tag'] == 'STOP_MOVE'].copy()
    exits = log[(log['Tag'] == 'ENTRY_FILL') & (log['Label'].isin(['Stop', 'T2', 'NoOvernight']))].copy()
    
    # For Tighten
    tighten_labels = ['TA_TIGHTEN', 'TA_TIGHTEN_PROTECT']
    tighten_decisions = df[df['Label'].isin(tighten_labels)].copy()
    tighten_verified = 0
    for _, d in tighten_decisions.iterrows():
        # Look for STOP_MOVE within 11 minutes
        matched = stop_moves[(stop_moves['Timestamp'] >= d['Timestamp']) & 
                            (stop_moves['Timestamp'] <= d['Timestamp'] + pd.Timedelta(minutes=11))]
        if not matched.empty:
            tighten_verified += 1
            
    # For Exit
    exit_labels = ['TA_EXIT']
    exit_decisions = df[df['Label'].isin(exit_labels)].copy()
    exit_verified = 0
    for _, d in exit_decisions.iterrows():
        matched = exits[(exits['Timestamp'] >= d['Timestamp']) & 
                        (exits['Timestamp'] <= d['Timestamp'] + pd.Timedelta(minutes=11))]
        if not matched.empty:
            exit_verified += 1
            
    n_tighten = len(tighten_decisions)
    n_exit = len(exit_decisions)
    
    tighten_exec_rate = (tighten_verified / n_tighten) if n_tighten > 0 else 0
    exit_exec_rate = (exit_verified / n_exit) if n_exit > 0 else 0
    
    print(f"Tighten decisions: {n_tighten}, executed: {tighten_verified} ({tighten_exec_rate:.1%})")
    print(f"Exit decisions:    {n_exit}, executed: {exit_verified} ({exit_exec_rate:.1%})")
    
    gap_tighten = 1 - tighten_exec_rate
    gap_exit = 1 - exit_exec_rate
    
    # --- SECTION 4: Stage / persistence patterns ---
    print("\n### SECTION 4: Stage / persistence patterns")
    stage_stats = df.groupby('trade_id').agg({
        'stage': 'max',
        'pers': 'max',
        'is_winner': 'first'
    })
    
    print("Outcome by max stage reached:")
    print(stage_stats.groupby('stage')['is_winner'].mean())
    print("\nOutcome by max persistence reached:")
    print(stage_stats.groupby('pers')['is_winner'].mean())

    # --- SECTION 5: CVD slope confirmation ---
    print("\n### SECTION 5: CVD slope confirmation")
    # Identify trades where slope flipped sign
    # We need to see if slope was positive then negative or vice versa
    def check_slope_flip(group):
        slopes = group['slope'].dropna()
        if len(slopes) < 2: return False
        return (slopes.max() > 0) and (slopes.min() < 0)
    
    flip_trades = df.groupby('trade_id').filter(check_slope_flip)
    num_flip = flip_trades['trade_id'].nunique()
    
    print(f"Trades with CVD slope flip: {num_flip}")
    if num_flip > 0:
        flip_actions = flip_trades.groupby('trade_id')['Label'].unique()
        # Count how many of these trades took a Tighten or Exit decision
        reacted = flip_actions.apply(lambda x: 'Tighten' in x or 'Exit' in x).sum()
        print(f"System reacted (Tighten/Exit) in {reacted} flip trades ({reacted/num_flip:.1%})")

    # --- SECTION 6: Decision volume per trade ---
    print("\n### SECTION 6: Decision volume per trade")
    counts = df.groupby('trade_id').size()
    counts_df = counts.reset_index(name='num_decisions')
    counts_df = counts_df.merge(tl_subset[['trade_id', 'is_winner']], on='trade_id')
    
    print("Mean decisions per trade (Winners):", counts_df[counts_df['is_winner']]['num_decisions'].mean())
    print("Mean decisions per trade (Losers): ", counts_df[~counts_df['is_winner']]['num_decisions'].mean())
    
    # --- SAVE RESULTS ---
    output_csv = repo_root / "ta_decision_audit.csv"
    df.to_csv(output_csv, index=False)
    print(f"\nSaved full audit data to {output_csv}")
    
    output_md = repo_root / "ta_decision_audit.md"
    with open(output_md, 'w') as f:
        f.write("# TA_DECISION Audit Report\n\n")
        f.write("## Section 1: Decision-outcome correlation\n")
        f.write(sec1_df.to_string(index=False) + "\n\n")
        f.write("## Section 3: Action-result alignment\n")
        f.write(f"- Tighten gap: {gap_tighten:.1%}\n")
        f.write(f"- Exit gap: {gap_exit:.1%}\n\n")
        f.write("## Section 6: Decision volume\n")
        f.write(f"- Mean decisions (Winners): {counts_df[counts_df['is_winner']]['num_decisions'].mean():.2f}\n")
        f.write(f"- Mean decisions (Losers): {counts_df[~counts_df['is_winner']]['num_decisions'].mean():.2f}\n")

    print(f"Saved report to {output_md}")

if __name__ == "__main__":
    main()
