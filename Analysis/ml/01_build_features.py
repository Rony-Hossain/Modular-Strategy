"""
ML Step 1 — Build Feature Matrix
Joins signals + outcomes + rank_scores + context into one ML-ready table.
Outputs: Analysis/ml/feature_matrix.parquet

Run after: 01_parse_events.py (needs fresh artifacts from post-fix backtest)
"""

import pandas as pd
import numpy as np
from pathlib import Path

np.random.seed(42)

ARTIFACTS = Path(__file__).resolve().parent.parent / "artifacts"
OUTPUT    = Path(__file__).resolve().parent / "feature_matrix.parquet"

def main():
    print("[1/5] Loading artifacts...")
    signals   = pd.read_parquet(ARTIFACTS / "signals.parquet")
    outcomes  = pd.read_parquet(ARTIFACTS / "outcomes.parquet")
    rank_win  = pd.read_parquet(ARTIFACTS / "rank_win.parquet")
    flow      = pd.read_parquet(ARTIFACTS / "flow_bars.parquet")
    struct    = pd.read_parquet(ARTIFACTS / "struct_bars.parquet")

    print(f"  signals:  {len(signals)}")
    print(f"  outcomes: {len(outcomes)}")
    print(f"  rank_win: {len(rank_win)}")

    # ── We want ALL signals that have an outcome, not just traded ones ──
    # Join signals with outcomes first
    df = signals.merge(outcomes, on="trade_id", how="inner", suffixes=("", "_out"))
    print(f"  after outcome join: {len(df)}")

    # ── Join rank_win for layer scores ──
    print("[3/5] Joining layer scores...")
    rank_cols = ["bar", "conditionsetid", "raw_score", "layer_a", "layer_b",
                 "layer_c", "layer_d", "penalty", "net_score", "mult",
                 "final_score", "a_tokens", "b_tokens", "c_tokens", "d_tokens"]
    # Handle potential 'bonus' column from bug-001 fix
    if "bonus" in rank_win.columns:
        rank_cols.insert(rank_cols.index("penalty"), "bonus")

    available_cols = [c for c in rank_cols if c in rank_win.columns]
    df = df.merge(
        rank_win[available_cols],
        left_on=["bar", "conditionsetid"],
        right_on=["bar", "conditionsetid"],
        how="left",
        suffixes=("", "_rank")
    )

    # ── Join flow context (nearest bar) ──
    print("[4/5] Joining flow context...")
    if not flow.empty and "bar" in flow.columns:
        flow_cols = [c for c in flow.columns if c != "timestamp"]
        df = df.merge(flow[flow_cols], on="bar", how="left", suffixes=("", "_flow"))

    # ── Join struct context (nearest bar) ──
    if not struct.empty and "bar" in struct.columns:
        struct_cols = [c for c in struct.columns if c != "timestamp"]
        df = df.merge(struct[struct_cols], on="bar", how="left", suffixes=("", "_struct"))

    # ── Derive ML targets ──
    print("[5/5] Building targets...")
    df["is_win"] = (df["sim_pnl"].fillna(0) > 0).astype(int)
    df["pnl"] = df["sim_pnl"].fillna(0)

    # MFE/MAE ratio (quality of the trade)
    mfe = df["mfe"].fillna(0).clip(lower=0)
    mae = df["mae"].fillna(0).clip(lower=0)
    df["mfe_mae_ratio"] = np.where(mae > 0, mfe / mae, np.where(mfe > 0, 10.0, 0.0))

    # Encode categoricals
    df["source_enc"] = df["source"].astype("category").cat.codes
    df["direction_enc"] = (df["direction"] == "L").astype(int)
    df["grade_enc"] = df["grade"].map({"A+": 3, "A": 2, "B": 1, "C": 0}).fillna(-1).astype(int)

    # Encode context flags
    for col in ["ctx_h4", "ctx_h2", "ctx_h1"]:
        if col in df.columns:
            df[f"{col}_enc"] = df[col].map({"+": 1, "-": -1, "0": 0}).fillna(0).astype(int)
    if "ctx_smf" in df.columns:
        df["ctx_smf_enc"] = df["ctx_smf"].map({"bull": 1, "bear": -1, "neutral": 0, "flat": 0}).fillna(0).astype(int)

    # ── Select final columns ──
    feature_cols = [
        # IDs
        "signal_id", "trade_id", "timestamp", "bar", "source", "conditionsetid",
        "direction", "grade",
        # Scores
        "score", "raw_score", "layer_a", "layer_b", "layer_c", "layer_d",
        "penalty", "net_score", "mult", "final_score",
        # Context
        "ctx_h4_enc", "ctx_h2_enc", "ctx_h1_enc", "ctx_smf_enc", "ctx_str", "ctx_sw",
        # Encoded
        "source_enc", "direction_enc", "grade_enc",
        # Targets
        "pnl", "is_win", "mfe", "mae", "mfe_mae_ratio",
        # Tokens (for explainability)
        "a_tokens", "b_tokens", "c_tokens", "d_tokens",
    ]

    # Add bonus if present
    if "bonus" in df.columns:
        feature_cols.insert(feature_cols.index("penalty"), "bonus")

    # Add flow/struct columns that made it through
    flow_struct_cols = [c for c in df.columns if c.startswith(("regime", "bd", "cd", "dsl", "dsh",
                        "poc", "vah", "val", "near_sup", "near_res", "skew", "pp"))]
    feature_cols.extend(flow_struct_cols)

    # Only keep columns that exist
    final_cols = [c for c in feature_cols if c in df.columns]
    df_out = df[final_cols].copy()

    print(f"\n[OUTPUT] {len(df_out)} rows × {len(final_cols)} cols → {OUTPUT}")
    df_out.to_parquet(OUTPUT, index=False)
    print("[CHECK] Column list:")
    for c in final_cols:
        dtype = df_out[c].dtype
        nulls = df_out[c].isna().sum()
        print(f"  {c:<25} {str(dtype):<12} nulls={nulls}")

if __name__ == "__main__":
    main()
