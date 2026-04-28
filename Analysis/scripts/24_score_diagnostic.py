import pandas as pd
import numpy as np
from pathlib import Path
from scipy.stats import spearmanr
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

def main():
    repo_root = Path(".")
    artifacts_dir = repo_root / "Analysis/artifacts"
    
    # [INPUT]
    print("[INPUT] Loading artifacts...")
    files = {
        'rw': 'rank_win.parquet',
        'tl': 'trade_lifecycle.parquet',
        'sig': 'signals.parquet'
    }
    
    data = {}
    for k, v in files.items():
        p = artifacts_dir / v
        if not p.exists():
            print(f"ERROR: {p} not found.")
            sys.exit(1)
        data[k] = pd.read_parquet(p)
        print(f"  Loaded {v}: {len(data[k])} rows")

    rw = data['rw']
    tl = data['tl']
    sig = data['sig']

    # [PARSE]
    print("[PARSE] Joining datasets...")
    # Join rank_win → signals on (conditionsetid, bar), then → trade_lifecycle on trade_id
    traded = sig[sig['traded'] == True][['signal_id', 'trade_id', 'conditionsetid', 'bar']].copy()
    rw_linked = rw.merge(traded, on=['conditionsetid', 'bar'], how='inner')
    df = rw_linked.merge(tl[['trade_id', 'realized_pnl_$', 'exit_subtype']], on='trade_id', how='inner')
    df['is_win'] = (df['realized_pnl_$'] > 5).astype(int)
    print(f"  Join complete. Analyzing {len(df)} trades with score data.")

    # [RESULT] 1. Layer Correlation
    print("[RESULT] 1. Layer Correlation Analysis")
    layers = ['layer_a', 'layer_b', 'layer_c', 'layer_d', 'penalty']
    layer_results = []
    for layer in layers:
        if layer not in df.columns: continue
        rho_pnl, p_pnl = spearmanr(df[layer], df['realized_pnl_$'], nan_policy='omit')
        rho_win, p_win = spearmanr(df[layer], df['is_win'], nan_policy='omit')
        layer_results.append({
            'layer': layer, 'rho_pnl': rho_pnl, 'p_pnl': p_pnl, 'rho_win': rho_win, 'p_win': p_win, 'n': len(df[df[layer].notna()])
        })
    layer_df = pd.DataFrame(layer_results)
    print(layer_df)

    # 2. Score Stages
    print("\n[RESULT] 2. Score Stage Analysis")
    stages = ['raw_score', 'net_score', 'final_score']
    stage_results = []
    for stage in stages:
        if stage not in df.columns: continue
        rho_pnl, p_pnl = spearmanr(df[stage], df['realized_pnl_$'], nan_policy='omit')
        rho_win, p_win = spearmanr(df[stage], df['is_win'], nan_policy='omit')
        stage_results.append({
            'stage': stage, 'rho_pnl': rho_pnl, 'p_pnl': p_pnl, 'rho_win': rho_win, 'p_win': p_win
        })
    stage_df = pd.DataFrame(stage_results)
    print(stage_df)

    # 3. Token Breakdown
    print("\n[RESULT] 3. Token Impact Analysis")
    token_cols = ['a_tokens', 'b_tokens', 'c_tokens', 'd_tokens']
    all_tokens = []
    for col in token_cols:
        if col in df.columns:
            for entry in df[col].dropna():
                if entry.lower() == 'none': continue
                cleaned = entry.replace('+', ' ').split()
                all_tokens.extend(cleaned)
    
    unique_tokens = pd.Series(all_tokens).value_counts()
    top_tokens = unique_tokens.head(20).index.tolist()
    token_impact = []
    for token in top_tokens:
        present_mask = df[token_cols].apply(lambda x: x.str.contains(token, na=False, regex=False)).any(axis=1)
        avg_pnl_present = df[present_mask]['realized_pnl_$'].mean()
        avg_pnl_absent = df[~present_mask]['realized_pnl_$'].mean()
        token_impact.append({
            'token': token, 'count': present_mask.sum(), 'avg_pnl_present': avg_pnl_present, 'avg_pnl_absent': avg_pnl_absent, 'impact': avg_pnl_present - avg_pnl_absent
        })
    impact_df = pd.DataFrame(token_impact)
    if not impact_df.empty and 'impact' in impact_df.columns:
        impact_df = impact_df.sort_values('impact', ascending=False)
        print("Top 10 Good Tokens:")
        print(impact_df.head(10))
        print("\nTop 10 Bad Tokens:")
        print(impact_df.tail(10))
    else:
        print("  No token data available (0 matched trades).")

    # [CHECK]
    neg_layers = layer_df[layer_df['rho_pnl'] < 0]['layer'].tolist()
    if neg_layers:
        print(f"\n[FLAG] WARNING: Layers with NEGATIVE correlation to PnL: {neg_layers}")

    # [SAVED]
    print("\n[SAVED] Saving results...")
    f_md = artifacts_dir / "score_diagnostic.md"
    with open(f_md, 'w') as f:
        f.write("# Scoring System Diagnostic\n\n")
        f.write("## Layer Correlation (Spearman)\n\n" + to_md_table(layer_df) + "\n\n")
        f.write("## Score Stage Correlation\n\n" + to_md_table(stage_df) + "\n\n")
        f.write("## Token Impact Analysis\n\n" + to_md_table(impact_df) + "\n\n")
        if neg_layers:
            f.write(f"**WARNING**: Layers with NEGATIVE correlation to PnL: {', '.join(neg_layers)}\n")
    print(f"  Markdown: {f_md}")

if __name__ == "__main__":
    main()
