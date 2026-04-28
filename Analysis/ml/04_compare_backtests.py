"""
ML Step 4 — Compare baseline vs ML-optimized backtest results.
Reads two feature matrices and produces a side-by-side report.

Usage:
  1. Before applying weights: cp Analysis/ml/feature_matrix.parquet Analysis/ml/baseline.parquet
  2. Apply weights, re-run backtest, re-run 01_parse + 01_build_features
  3. python Analysis/ml/04_compare_backtests.py

Outputs: Analysis/ml/comparison_report.md
"""

import pandas as pd
import numpy as np
from pathlib import Path

ML_DIR   = Path(__file__).resolve().parent
BASELINE = ML_DIR / "baseline.parquet"
CURRENT  = ML_DIR / "feature_matrix.parquet"
OUTPUT   = ML_DIR / "comparison_report.md"


def compute_stats(df, label):
    """Compute key trading statistics."""
    n = len(df)
    pnl = df["pnl"].sum()
    wins = (df["pnl"] > 0).sum()
    losses = (df["pnl"] < 0).sum()
    scratches = (df["pnl"] == 0).sum()
    wr = wins / n if n > 0 else 0
    avg_win = df.loc[df["pnl"] > 0, "pnl"].mean() if wins > 0 else 0
    avg_loss = df.loc[df["pnl"] < 0, "pnl"].mean() if losses > 0 else 0
    pf = abs(df.loc[df["pnl"] > 0, "pnl"].sum() / df.loc[df["pnl"] < 0, "pnl"].sum()) if losses > 0 else float("inf")
    expectancy = pnl / n if n > 0 else 0

    return {
        "label": label,
        "trades": n,
        "net_pnl": round(pnl, 2),
        "win_rate": round(wr * 100, 1),
        "profit_factor": round(pf, 3),
        "avg_win": round(avg_win, 2),
        "avg_loss": round(avg_loss, 2),
        "expectancy": round(expectancy, 2),
        "wins": wins,
        "losses": losses,
        "scratches": scratches,
    }


def grade_stats(df):
    """Per-grade breakdown."""
    rows = []
    for grade in ["A+", "A", "B", "C"]:
        g = df[df["grade"] == grade]
        if len(g) == 0:
            continue
        rows.append({
            "grade": grade,
            "n": len(g),
            "wr": round((g["pnl"] > 0).mean() * 100, 1),
            "pf": round(abs(g.loc[g["pnl"] > 0, "pnl"].sum() / g.loc[g["pnl"] < 0, "pnl"].sum()), 2) if (g["pnl"] < 0).any() else float("inf"),
            "avg_pnl": round(g["pnl"].mean(), 2),
        })
    return pd.DataFrame(rows)


def source_stats(df):
    """Per-source breakdown."""
    rows = []
    for src in sorted(df["source"].unique()):
        g = df[df["source"] == src]
        rows.append({
            "source": src,
            "n": len(g),
            "pnl": round(g["pnl"].sum(), 2),
            "wr": round((g["pnl"] > 0).mean() * 100, 1),
            "pf": round(abs(g.loc[g["pnl"] > 0, "pnl"].sum() / g.loc[g["pnl"] < 0, "pnl"].sum()), 2) if (g["pnl"] < 0).any() else float("inf"),
        })
    return pd.DataFrame(rows).sort_values("pnl", ascending=False)


def main():
    if not BASELINE.exists():
        print(f"ERROR: {BASELINE} not found.")
        print("Before applying ML weights, save the current feature_matrix:")
        print("  cp Analysis/ml/feature_matrix.parquet Analysis/ml/baseline.parquet")
        return

    if not CURRENT.exists():
        print(f"ERROR: {CURRENT} not found. Run 01_build_features.py first.")
        return

    base = pd.read_parquet(BASELINE)
    curr = pd.read_parquet(CURRENT)

    s_base = compute_stats(base, "Baseline")
    s_curr = compute_stats(curr, "ML-Optimized")

    lines = [
        "# Backtest Comparison: Baseline vs ML-Optimized",
        "",
        "## Summary",
        "",
        "| Metric | Baseline | ML-Optimized | Delta |",
        "| --- | --- | --- | --- |",
    ]

    for key in ["trades", "net_pnl", "win_rate", "profit_factor", "expectancy", "avg_win", "avg_loss"]:
        bv = s_base[key]
        cv = s_curr[key]
        delta = cv - bv if isinstance(cv, (int, float)) else "N/A"
        if isinstance(delta, float):
            delta = f"{delta:+.2f}"
        lines.append(f"| {key} | {bv} | {cv} | {delta} |")

    # Grade calibration
    lines.extend(["", "## Grade Calibration", ""])

    g_base = grade_stats(base)
    g_curr = grade_stats(curr)

    lines.extend(["### Baseline", "",
                   "| Grade | N | WR% | PF | Avg PnL |",
                   "| --- | --- | --- | --- | --- |"])
    for _, r in g_base.iterrows():
        lines.append(f"| {r['grade']} | {r['n']} | {r['wr']} | {r['pf']} | {r['avg_pnl']} |")

    lines.extend(["", "### ML-Optimized", "",
                   "| Grade | N | WR% | PF | Avg PnL |",
                   "| --- | --- | --- | --- | --- |"])
    for _, r in g_curr.iterrows():
        lines.append(f"| {r['grade']} | {r['n']} | {r['wr']} | {r['pf']} | {r['avg_pnl']} |")

    # Check monotonicity
    if len(g_curr) >= 3:
        pfs = g_curr.sort_values("grade", ascending=False)["pf"].values
        is_monotone = all(pfs[i] >= pfs[i+1] for i in range(len(pfs)-1))
        verdict = "PASS (monotone descending)" if is_monotone else "FAIL (not monotone)"
        lines.append(f"\n**Grade calibration verdict:** {verdict}")

    # Source comparison
    lines.extend(["", "## Source Performance", ""])

    s_base_src = source_stats(base)
    s_curr_src = source_stats(curr)

    lines.extend(["### Baseline", "",
                   "| Source | N | PnL | WR% | PF |",
                   "| --- | --- | --- | --- | --- |"])
    for _, r in s_base_src.iterrows():
        lines.append(f"| {r['source']} | {r['n']} | {r['pnl']} | {r['wr']} | {r['pf']} |")

    lines.extend(["", "### ML-Optimized", "",
                   "| Source | N | PnL | WR% | PF |",
                   "| --- | --- | --- | --- | --- |"])
    for _, r in s_curr_src.iterrows():
        lines.append(f"| {r['source']} | {r['n']} | {r['pnl']} | {r['wr']} | {r['pf']} |")

    OUTPUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"[DONE] → {OUTPUT}")
    print(f"\n  Baseline:     {s_base['net_pnl']:>10} ({s_base['trades']} trades, PF={s_base['profit_factor']})")
    print(f"  ML-Optimized: {s_curr['net_pnl']:>10} ({s_curr['trades']} trades, PF={s_curr['profit_factor']})")
    print(f"  Delta PnL:    {s_curr['net_pnl'] - s_base['net_pnl']:>+10.2f}")


if __name__ == "__main__":
    main()
