#!/usr/bin/env python3
"""
optimizer.py  —  Autonomous Strategy Optimizer
================================================
Reads Log.csv + BacktestResults (if present), analyzes trades, runs ML and
Optuna to find the best StrategyConfig, then writes results back to
strategy_config.py and prints a full report.

Usage:
    python optimizer.py                          # full pipeline
    python optimizer.py --phase ml               # ML feature analysis only
    python optimizer.py --phase threshold        # score threshold search only
    python optimizer.py --phase monte_carlo      # stress tests only
    python optimizer.py --trials 500             # Optuna trial count

Outputs:
    Analysis/optimizer_report.txt               # human-readable report
    Analysis/strategy_config.py  OPTIMIZED={}  # updated weights block
"""

import sys, re, math, argparse, json, warnings
from pathlib import Path
from datetime import datetime

import numpy as np
import pandas as pd
from scipy import stats

import optuna

optuna.logging.set_verbosity(optuna.logging.WARNING)

from sklearn.ensemble import RandomForestClassifier, GradientBoostingClassifier
from sklearn.model_selection import StratifiedKFold, cross_val_score
from sklearn.preprocessing import StandardScaler
from sklearn.metrics import classification_report

warnings.filterwarnings("ignore")

# ─── Paths ────────────────────────────────────────────────────────────────────
ROOT = Path(__file__).parent.parent
LOG_PATH = ROOT / "backtest" / "Log.csv"
CFG_PATH = Path(__file__).parent / "strategy_config.py"
RPT_PATH = Path(__file__).parent / "optimizer_report.txt"

# ─── Regex ────────────────────────────────────────────────────────────────────
_SNAP_RE = re.compile(r"SNAP_(\w+)=([-+\d.]+)")
_PNL_RE = re.compile(r"SIM_PNL=([-\d.]+)")
_MFE_RE = re.compile(r"MFE=([\d.]+)")
_MAE_RE = re.compile(r"MAE=([\d.]+)")
_BTH_RE = re.compile(r"BARS_TO_HIT=(\d+)")
_GNM_RE = re.compile(r":(\d+)(?::REJ)?$")

REPORT_LINES = []


def log(msg=""):
    print(msg)
    REPORT_LINES.append(msg)


# ═══════════════════════════════════════════════════════════════════════════════
# PHASE 1  —  DATA INGESTION
# ═══════════════════════════════════════════════════════════════════════════════


def load_trades(log_path: Path) -> pd.DataFrame:
    """Parse Log.csv into one row per TOUCH_OUTCOME with full feature set."""
    log(f"Loading {log_path} ...")
    raw = pd.read_csv(log_path, dtype=str)
    raw.columns = [c.strip() for c in raw.columns]
    raw["Timestamp"] = pd.to_datetime(raw["Timestamp"], errors="coerce")

    # ── SIGNAL rows (accepted + rejected) → score lookup ─────────────────────
    sig = raw[raw["Tag"].isin(["SIGNAL_ACCEPTED", "SIGNAL_REJECTED"])].copy()
    sig["_score"] = pd.to_numeric(sig["Score"], errors="coerce").fillna(0).astype(int)
    sig["_grade"] = sig["Grade"].fillna("").str.strip()
    sig["_bar"] = pd.to_numeric(sig["Bar"], errors="coerce").fillna(0).astype(int)
    sig["_accepted"] = sig["Tag"] == "SIGNAL_ACCEPTED"
    sig["_cond"] = sig["ConditionSetId"].fillna("").str.strip()
    sig["_src"] = sig["Source"].fillna("").str.strip()
    sig["_dir"] = sig["Direction"].fillna("").str.strip()
    # key = cond:bar
    sig["_key"] = sig["_cond"] + ":" + sig["_bar"].astype(str)
    score_map = sig.set_index("_key")[
        ["_score", "_grade", "_accepted", "_src"]
    ].to_dict("index")

    # ── TOUCH_OUTCOME rows ────────────────────────────────────────────────────
    touch = raw[raw["Tag"] == "TOUCH_OUTCOME"].copy()
    log(
        f"  SIGNAL_ACCEPTED: {sig['_accepted'].sum()}  "
        f"SIGNAL_REJECTED: {(~sig['_accepted']).sum()}  "
        f"TOUCH_OUTCOME: {len(touch)}"
    )

    rows = []
    for _, r in touch.iterrows():
        det = str(r.get("Detail", ""))
        gate = str(r.get("GateReason", ""))
        cond = str(r.get("ConditionSetId", "")).strip()
        direct = str(r.get("Direction", "")).strip()
        label = str(r.get("Label", "")).strip()
        is_rej = ":REJ" in gate

        # Extract bar from gate reason
        gm = _GNM_RE.search(gate)
        src_bar = int(gm[1]) if gm else int(float(str(r.get("Bar", 0))))

        # Snap features
        snap = {m[1]: float(m[2]) for m in _SNAP_RE.finditer(det)}

        # Outcome values
        pm = _PNL_RE.search(det)
        mfe = _MFE_RE.search(det)
        mae = _MAE_RE.search(det)
        bth = _BTH_RE.search(det)

        sim_pnl = float(pm[1]) if pm else 0.0
        mfe_val = float(mfe[1]) if mfe else 0.0
        mae_val = float(mae[1]) if mae else 0.0
        bars_to_hit = int(bth[1]) if bth else 0

        # Score lookup
        key = f"{cond}:{src_bar}"
        sm = score_map.get(key, {})
        score = sm.get("_score", 0)
        grade = sm.get("_grade", "")
        src = sm.get("_src", cond)

        # Direction alignment features (long=1 / short=-1)
        is_long = direct in ("L", "Long", "long")
        h4 = snap.get("H4", 0.0)
        h2 = snap.get("H2", 0.0)
        h1 = snap.get("H1", 0.0)
        bd = snap.get("BD", 0.0)
        cd = snap.get("CD", 0.0)

        row = dict(
            timestamp=r["Timestamp"],
            bar=src_bar,
            source=src or cond,
            cond_set=cond,
            direction="Long" if is_long else "Short",
            label=label,
            win=1 if label == "TARGET" else 0,
            rejected=int(is_rej),
            score=score,
            grade=grade,
            sim_pnl=sim_pnl,
            mfe=mfe_val,
            mae=mae_val,
            bars_to_hit=bars_to_hit,
            # raw snap
            snap_BD=bd,
            snap_CD=cd,
            snap_ABS=snap.get("ABS", 0.0),
            snap_SBULL=snap.get("SBULL", 0.0),
            snap_SBEAR=snap.get("SBEAR", 0.0),
            snap_H1=h1,
            snap_H2=h2,
            snap_H4=h4,
            snap_REG=snap.get("REG", 0.0),
            snap_STR=snap.get("STR", 0.0),
            snap_DEX=snap.get("DEX", 0.0),
            snap_BDIV=snap.get("BDIV", 0.0),
            snap_BERDIV=snap.get("BERDIV", 0.0),
            snap_SW=snap.get("SW", 0.0),
            snap_TRD=snap.get("TRD", 0.0),
            snap_ATR=snap.get("ATR", 0.0),
            snap_HASVOL=snap.get("HASVOL", 0.0),
            # derived alignment features
            h4_aligned=(
                1
                if (is_long and h4 > 0) or (not is_long and h4 < 0)
                else -1 if (is_long and h4 < 0) or (not is_long and h4 > 0) else 0
            ),
            h2_aligned=(
                1
                if (is_long and h2 > 0) or (not is_long and h2 < 0)
                else -1 if (is_long and h2 < 0) or (not is_long and h2 > 0) else 0
            ),
            h1_aligned=(
                1
                if (is_long and h1 > 0) or (not is_long and h1 < 0)
                else -1 if (is_long and h1 < 0) or (not is_long and h1 > 0) else 0
            ),
            bd_aligned=1 if (is_long and bd > 0) or (not is_long and bd < 0) else 0,
            cd_aligned=1 if (is_long and cd > 0) or (not is_long and cd < 0) else 0,
        )
        rows.append(row)

    df = pd.DataFrame(rows)
    df["timestamp"] = pd.to_datetime(df["timestamp"], errors="coerce")
    df = df.sort_values("timestamp").reset_index(drop=True)
    log(
        f"  Parsed {len(df)} outcome rows  "
        f"({df['win'].sum()} wins / {(df['win']==0).sum()} losses)  "
        f"win%={df['win'].mean()*100:.1f}%"
    )
    log(
        f"  Accepted: {(df['rejected']==0).sum()}  Rejected (simulated): {df['rejected'].sum()}"
    )
    log(
        f"  Net simulated PnL (all): ${df['sim_pnl'].sum():,.0f}  "
        f"(accepted only): ${df[df['rejected']==0]['sim_pnl'].sum():,.0f}"
    )
    return df


# ═══════════════════════════════════════════════════════════════════════════════
# PHASE 2  —  ML FEATURE IMPORTANCE
# ═══════════════════════════════════════════════════════════════════════════════

SNAP_FEATURES = [
    "snap_BD",
    "snap_CD",
    "snap_ABS",
    "snap_SBULL",
    "snap_SBEAR",
    "snap_H1",
    "snap_H2",
    "snap_H4",
    "snap_REG",
    "snap_STR",
    "snap_DEX",
    "snap_BDIV",
    "snap_BERDIV",
    "snap_SW",
    "snap_TRD",
    "snap_ATR",
    "snap_HASVOL",
    "h4_aligned",
    "h2_aligned",
    "h1_aligned",
    "bd_aligned",
    "cd_aligned",
]


def ml_feature_analysis(df: pd.DataFrame) -> dict:
    log("\n" + "=" * 60)
    log("PHASE 2 — ML Feature Importance")
    log("=" * 60)

    X = df[SNAP_FEATURES].fillna(0).values
    y = df["win"].values

    # Random Forest
    rf = RandomForestClassifier(
        n_estimators=300,
        max_depth=6,
        class_weight="balanced",
        random_state=42,
        n_jobs=-1,
    )
    cv_scores = cross_val_score(
        rf,
        X,
        y,
        cv=StratifiedKFold(5, shuffle=True, random_state=42),
        scoring="roc_auc",
    )
    log(f"  Random Forest CV AUC: {cv_scores.mean():.3f} +/- {cv_scores.std():.3f}")

    rf.fit(X, y)
    importances = sorted(
        zip(SNAP_FEATURES, rf.feature_importances_), key=lambda x: -x[1]
    )

    log("\n  Feature Importances (top 10):")
    log(f"  {'Feature':<18}  {'Importance':>10}  {'Direction'}")
    for feat, imp in importances[:10]:
        # Determine whether high value of this feature is bullish for WIN
        vals = df[feat].values
        corr = np.corrcoef(vals, y)[0, 1]
        direction = (
            "-> WIN" if corr > 0.05 else "<- LOSS" if corr < -0.05 else "neutral"
        )
        log(f"  {feat:<18}  {imp:>10.4f}  {direction}  (corr={corr:+.3f})")

    # Gradient Boosting for prediction quality
    gb = GradientBoostingClassifier(
        n_estimators=200, max_depth=3, learning_rate=0.05, random_state=42
    )
    gb_scores = cross_val_score(
        gb,
        X,
        y,
        cv=StratifiedKFold(5, shuffle=True, random_state=42),
        scoring="roc_auc",
    )
    log(
        f"\n  GradientBoosting CV AUC: {gb_scores.mean():.3f} +/- {gb_scores.std():.3f}"
    )

    # Per-source analysis
    log("\n  Per-Source Performance:")
    log(
        f"  {'Source':<25} {'N':>5} {'Win%':>6} {'AvgPnL':>8} {'AvgMFE':>8} {'AvgMAE':>8} {'EV':>8}"
    )
    source_stats = {}
    for src, g in df[df["rejected"] == 0].groupby("source"):
        n = len(g)
        wr = g["win"].mean() * 100
        apnl = g["sim_pnl"].mean()
        amfe = g["mfe"].mean()
        amae = g["mae"].mean()
        ev = g["sim_pnl"].sum()
        source_stats[src] = dict(
            n=n, win_rate=wr, avg_pnl=apnl, mfe=amfe, mae=amae, ev=ev
        )
        log(
            f"  {src:<25} {n:>5} {wr:>5.1f}% {apnl:>8.1f} {amfe:>8.0f} {amae:>8.0f} {ev:>8.0f}"
        )

    return dict(
        importances=importances, source_stats=source_stats, rf_auc=cv_scores.mean()
    )


# ═══════════════════════════════════════════════════════════════════════════════
# PHASE 3  —  SCORE THRESHOLD OPTIMIZATION
# ═══════════════════════════════════════════════════════════════════════════════


def _simulate(
    df: pd.DataFrame,
    thresholds: dict,
    global_thresh: int,
    min_rr: float = 1.0,
    require_h4: bool = False,
) -> dict:
    """Simulate which trades would be taken given thresholds; return metrics."""
    mask = pd.Series(True, index=df.index)

    # Global score threshold (for trades that have a score)
    scored = df["score"] > 0
    mask[scored] = df.loc[scored, "score"] >= global_thresh

    # Per-source overrides
    for src, thr in thresholds.items():
        src_mask = df["source"] == src
        mask[src_mask] = df.loc[src_mask, "score"] >= thr

    # H4 alignment filter
    if require_h4:
        mask &= df["h4_aligned"] >= 0  # allow neutral and aligned, block counter

    taken = df[mask]
    if len(taken) == 0:
        return dict(n=0, pnl=0, win_rate=0, sharpe=-99, max_dd=0)

    pnls = taken["sim_pnl"].values
    total = pnls.sum()
    win_rate = taken["win"].mean()
    eq = np.cumsum(pnls)
    peak = np.maximum.accumulate(eq)
    dd = peak - eq
    max_dd = dd.max() if len(dd) > 0 else 0
    sharpe = (pnls.mean() / (pnls.std() + 1e-9)) * math.sqrt(252)

    return dict(
        n=len(taken),
        pnl=total,
        win_rate=win_rate,
        sharpe=sharpe,
        max_dd=max_dd,
        avg_pnl=pnls.mean(),
    )


def optimize_thresholds(df: pd.DataFrame, n_trials: int = 300) -> dict:
    log("\n" + "=" * 60)
    log("PHASE 3 — Score Threshold Optimization (Optuna)")
    log("=" * 60)

    sources = df["source"].unique().tolist()
    baseline = _simulate(df, {}, 60)
    log(
        f"  Baseline (threshold=60, all accepted): "
        f"n={baseline['n']}  PnL=${baseline['pnl']:,.0f}  "
        f"win={baseline['win_rate']*100:.1f}%  Sharpe={baseline['sharpe']:.2f}  "
        f"MaxDD=${baseline['max_dd']:,.0f}"
    )

    def objective(trial):
        global_t = trial.suggest_int("global_thresh", 55, 85)
        require_h4 = trial.suggest_categorical("require_h4", [False, True])
        per_src = {}
        for src in sources:
            per_src[src] = trial.suggest_int(f"thresh_{src}", 50, 95)
        m = _simulate(df, per_src, global_t, require_h4=require_h4)
        if m["n"] < 30:
            return -9999  # too few trades is not useful
        # Objective: maximize Sharpe, penalize max drawdown
        return m["sharpe"] - (m["max_dd"] / max(abs(m["pnl"]), 1)) * 0.5

    study = optuna.create_study(
        direction="maximize", sampler=optuna.samplers.TPESampler(seed=42)
    )
    study.optimize(objective, n_trials=n_trials, show_progress_bar=False)

    best = study.best_params
    per_src = {src: best[f"thresh_{src}"] for src in sources}
    optimized = _simulate(
        df, per_src, best["global_thresh"], require_h4=best["require_h4"]
    )

    log(f"\n  Optimized result:")
    log(f"    Global threshold  : {best['global_thresh']}")
    log(f"    Require H4 aligned: {best['require_h4']}")
    log(f"    Per-source thresholds:")
    for src in sorted(per_src):
        log(f"      {src:<25}: {per_src[src]}")
    log(
        f"\n    n={optimized['n']}  PnL=${optimized['pnl']:,.0f}  "
        f"win={optimized['win_rate']*100:.1f}%  "
        f"Sharpe={optimized['sharpe']:.2f}  MaxDD=${optimized['max_dd']:,.0f}"
    )

    improvement = optimized["pnl"] - baseline["pnl"]
    log(f"\n    PnL improvement vs baseline: ${improvement:+,.0f}")

    return dict(
        global_thresh=best["global_thresh"],
        require_h4=best["require_h4"],
        per_source=per_src,
        metrics=optimized,
        baseline=baseline,
    )


# ═══════════════════════════════════════════════════════════════════════════════
# PHASE 4  —  CONFLUENCE WEIGHT OPTIMIZATION
# ═══════════════════════════════════════════════════════════════════════════════


def _approx_score(snap: dict, weights: dict, is_long: bool) -> float:
    """Approximate the ConfluenceEngine score from SNAP features + weights."""
    h4 = snap.get("snap_H4", 0)
    h2 = snap.get("snap_H2", 0)
    h1 = snap.get("snap_H1", 0)
    bd = snap.get("snap_BD", 0)
    cd = snap.get("snap_CD", 0)
    abs_ = snap.get("snap_ABS", 0)
    trd = snap.get("snap_TRD", 0)
    reg = snap.get("snap_REG", 0)
    div = snap.get("snap_BDIV", 0) if is_long else snap.get("snap_BERDIV", 0)
    opp_div = snap.get("snap_BERDIV", 0) if is_long else snap.get("snap_BDIV", 0)

    score = 0.0

    # Layer A: MTFA
    if (is_long and h4 > 0) or (not is_long and h4 < 0):
        score += weights["LAYER_A_H4"]
    if (is_long and h2 > 0) or (not is_long and h2 < 0):
        score += weights["LAYER_A_H2"]
    if (is_long and h1 > 0) or (not is_long and h1 < 0):
        score += weights["LAYER_A_H1"]

    # Penalties for counter-trend
    if (is_long and h4 < 0) or (not is_long and h4 > 0):
        score -= weights["PENALTY_H4"]
    if (is_long and h2 < 0) or (not is_long and h2 > 0):
        score -= weights["PENALTY_H2"]
    if ((is_long and h4 < 0) and (is_long and h2 < 0)) or (
        (not is_long and h4 > 0) and (not is_long and h2 > 0)
    ):
        score -= weights["PENALTY_BOTH_EXTRA"]

    # Layer C: OrderFlow proxies
    if (is_long and bd > 0) or (not is_long and bd < 0):
        score += weights["PROXY_BAR_DELTA"]
    if (is_long and reg > 0) or (not is_long and reg < 0):
        score += weights["PROXY_REGIME"]
    if opp_div > 0:
        score += weights["LAYER_C_DIVERGENCE"]

    # Absorption
    score += min(abs_ * weights["LAYER_C_ABS_MAX"] / 10.0, weights["LAYER_C_ABS_MAX"])

    # Layer D: Structure
    if (is_long and trd > 0) or (not is_long and trd < 0):
        score += weights["LAYER_D_FULL_STRUCT"]
    elif trd != 0:
        score += weights["LAYER_D_TREND_ONLY"]

    return score


def optimize_confluence_weights(df: pd.DataFrame, n_trials: int = 200) -> dict:
    log("\n" + "=" * 60)
    log("PHASE 4 — Confluence Weight Optimization")
    log("=" * 60)

    snaps = df[SNAP_FEATURES + ["direction", "win", "sim_pnl", "rejected"]].copy()
    snaps["is_long"] = snaps["direction"] == "Long"

    def objective(trial):
        w = dict(
            LAYER_A_H4=trial.suggest_int("LAYER_A_H4", 5, 20),
            LAYER_A_H2=trial.suggest_int("LAYER_A_H2", 3, 15),
            LAYER_A_H1=trial.suggest_int("LAYER_A_H1", 2, 10),
            PENALTY_H4=trial.suggest_int("PENALTY_H4", 3, 15),
            PENALTY_H2=trial.suggest_int("PENALTY_H2", 2, 10),
            PENALTY_BOTH_EXTRA=trial.suggest_int("PENALTY_BOTH_EXTRA", 2, 10),
            PROXY_BAR_DELTA=trial.suggest_int("PROXY_BAR_DELTA", 3, 12),
            PROXY_REGIME=trial.suggest_int("PROXY_REGIME", 2, 10),
            LAYER_C_DIVERGENCE=trial.suggest_int("LAYER_C_DIVERGENCE", 8, 22),
            LAYER_C_ABS_MAX=trial.suggest_int("LAYER_C_ABS_MAX", 3, 12),
            LAYER_D_FULL_STRUCT=trial.suggest_int("LAYER_D_FULL_STRUCT", 6, 18),
            LAYER_D_TREND_ONLY=trial.suggest_int("LAYER_D_TREND_ONLY", 3, 12),
        )
        reject_thr = trial.suggest_int("reject_thresh", 55, 80)

        pnls = []
        for _, row in snaps.iterrows():
            snap_d = {k: row[k] for k in SNAP_FEATURES}
            s = _approx_score(snap_d, w, bool(row["is_long"]))
            if s >= reject_thr:
                pnls.append(row["sim_pnl"])

        if len(pnls) < 30:
            return -9999
        pnls = np.array(pnls)
        sharpe = (pnls.mean() / (pnls.std() + 1e-9)) * math.sqrt(252)
        eq = np.cumsum(pnls)
        peak = np.maximum.accumulate(eq)
        max_dd = (peak - eq).max()
        return sharpe - (max_dd / max(abs(pnls.sum()), 1)) * 0.5

    study2 = optuna.create_study(
        direction="maximize", sampler=optuna.samplers.TPESampler(seed=99)
    )
    study2.optimize(objective, n_trials=n_trials, show_progress_bar=False)

    best = study2.best_params
    log("\n  Best confluence weights:")
    for k, v in sorted(best.items()):
        log(f"    {k:<28}: {v}")

    # Evaluate
    pnls = []
    for _, row in snaps.iterrows():
        snap_d = {k: row[k] for k in SNAP_FEATURES}
        s = _approx_score(snap_d, best, bool(row["is_long"]))
        if s >= best.get("reject_thresh", 60):
            pnls.append(row["sim_pnl"])
    pnls = np.array(pnls) if pnls else np.array([0])
    log(
        f"\n  Result: n={len(pnls)}  PnL=${pnls.sum():,.0f}  "
        f"win={((pnls>0).mean()*100):.1f}%  "
        f"Sharpe={(pnls.mean()/(pnls.std()+1e-9)*math.sqrt(252)):.2f}"
    )

    return best


# ═══════════════════════════════════════════════════════════════════════════════
# PHASE 5  —  MONTE CARLO STRESS TESTS
# ═══════════════════════════════════════════════════════════════════════════════


def monte_carlo(df: pd.DataFrame, n_sims: int = 10_000) -> dict:
    log("\n" + "=" * 60)
    log("PHASE 5 — Monte Carlo Stress Tests")
    log("=" * 60)

    pnls = df[df["rejected"] == 0]["sim_pnl"].values
    n = len(pnls)
    log(f"  Trade sample: {n} accepted trades  total PnL=${pnls.sum():,.0f}")

    # Bootstrap resampling
    finals, max_dds, sharpes = [], [], []
    for _ in range(n_sims):
        sample = np.random.choice(pnls, size=n, replace=True)
        eq = np.cumsum(sample)
        peak = np.maximum.accumulate(eq)
        dd = (peak - eq).max()
        sh = (sample.mean() / (sample.std() + 1e-9)) * math.sqrt(252)
        finals.append(eq[-1])
        max_dds.append(dd)
        sharpes.append(sh)

    finals = np.array(finals)
    max_dds = np.array(max_dds)
    sharpes = np.array(sharpes)

    log(f"\n  Equity (final) distribution ({n_sims:,} simulations):")
    for pct in [5, 25, 50, 75, 95]:
        log(f"    P{pct:2d}: ${np.percentile(finals, pct):>10,.0f}")
    log(f"    Mean: ${finals.mean():>10,.0f}   Std: ${finals.std():>10,.0f}")

    ruin_prob = (finals <= -1000).mean() * 100
    log(f"\n  Probability of ruin (equity < -$1,000): {ruin_prob:.1f}%")

    log(f"\n  Max Drawdown distribution:")
    for pct in [50, 75, 90, 95, 99]:
        log(f"    P{pct:2d}: ${np.percentile(max_dds, pct):>8,.0f}")

    log(
        f"\n  Sharpe distribution: "
        f"P25={np.percentile(sharpes,25):.2f}  "
        f"P50={np.percentile(sharpes,50):.2f}  "
        f"P75={np.percentile(sharpes,75):.2f}"
    )

    # Win-rate sensitivity
    log(f"\n  Win-Rate Sensitivity (fixed trade PnL magnitudes, vary win%):")
    log(f"  {'Win%':>6}  {'Avg PnL':>9}  {'Sharpe':>7}  {'Total PnL':>11}")
    avg_win = pnls[pnls > 0].mean() if (pnls > 0).any() else 0
    avg_loss = abs(pnls[pnls < 0].mean()) if (pnls < 0).any() else 0
    for wr in [0.35, 0.40, 0.45, 0.48, 0.50, 0.55, 0.60]:
        synth = np.where(np.random.rand(n) < wr, avg_win, -avg_loss)
        avg_p = synth.mean()
        sh = (avg_p / (synth.std() + 1e-9)) * math.sqrt(252)
        total = synth.sum()
        log(f"  {wr*100:>5.0f}%  {avg_p:>9.1f}  {sh:>7.2f}  {total:>11,.0f}")

    # Consecutive loss streaks
    log(f"\n  Consecutive Loss Streak Analysis (from actual sequence):")
    max_streak = cur_streak = 0
    for p in pnls:
        if p < 0:
            cur_streak += 1
            max_streak = max(max_streak, cur_streak)
        else:
            cur_streak = 0
    log(f"    Max observed streak: {max_streak}")
    wr_actual = (pnls > 0).mean()
    expected_max_streak = (
        math.log(n) / math.log(1 / (1 - wr_actual)) if wr_actual < 1 else 0
    )
    log(f"    Expected max streak (theory): {expected_max_streak:.1f}")

    return dict(
        ruin_prob=ruin_prob,
        p50_equity=np.percentile(finals, 50),
        p10_equity=np.percentile(finals, 10),
        p90_equity=np.percentile(finals, 90),
        p90_max_dd=np.percentile(max_dds, 90),
        p50_sharpe=np.percentile(sharpes, 50),
    )


# ═══════════════════════════════════════════════════════════════════════════════
# PHASE 6  —  WRITE BACK TO strategy_config.py
# ═══════════════════════════════════════════════════════════════════════════════


def write_optimized_config(
    cfg_path: Path, threshold_result: dict, weight_result: dict
) -> None:
    log("\n" + "=" * 60)
    log("PHASE 6 — Writing optimized config to strategy_config.py")
    log("=" * 60)

    optimized = {
        "SCORE_REJECT": threshold_result.get("global_thresh", 60),
        "REQUIRE_H4_ALIGNED": threshold_result.get("require_h4", False),
        "PER_SOURCE_THRESHOLDS": threshold_result.get("per_source", {}),
        "OPT_TIMESTAMP": datetime.now().isoformat(timespec="seconds"),
    }
    # Confluence weights from Phase 4
    for k in [
        "LAYER_A_H4",
        "LAYER_A_H2",
        "LAYER_A_H1",
        "PENALTY_H4",
        "PENALTY_H2",
        "PENALTY_BOTH_EXTRA",
        "PROXY_BAR_DELTA",
        "PROXY_REGIME",
        "LAYER_C_DIVERGENCE",
        "LAYER_C_ABS_MAX",
        "LAYER_D_FULL_STRUCT",
        "LAYER_D_TREND_ONLY",
    ]:
        if k in weight_result:
            optimized[k] = weight_result[k]

    text = cfg_path.read_text(encoding="utf-8")
    # Replace the OPTIMIZED = {...} block
    new_block = "OPTIMIZED = " + json.dumps(optimized, indent=4)
    text = re.sub(r"OPTIMIZED\s*=\s*\{[^}]*\}", new_block, text, flags=re.DOTALL)
    cfg_path.write_text(text, encoding="utf-8")
    log(f"  Written to {cfg_path}")
    log(f"  Key changes:")
    log(f"    SCORE_REJECT        : {optimized['SCORE_REJECT']}")
    log(f"    REQUIRE_H4_ALIGNED  : {optimized['REQUIRE_H4_ALIGNED']}")
    for k in ["LAYER_A_H4", "PENALTY_H4", "LAYER_C_DIVERGENCE", "LAYER_D_FULL_STRUCT"]:
        if k in optimized:
            log(f"    {k:<28}: {optimized[k]}")


# ═══════════════════════════════════════════════════════════════════════════════
# MAIN
# ═══════════════════════════════════════════════════════════════════════════════


def main():
    ap = argparse.ArgumentParser(description="Strategy Optimizer")
    ap.add_argument("--log", default=str(LOG_PATH), help="Path to Log.csv")
    ap.add_argument(
        "--phase",
        default="all",
        choices=["all", "ml", "threshold", "weights", "monte_carlo"],
    )
    ap.add_argument(
        "--trials", type=int, default=300, help="Optuna trials per optimization phase"
    )
    ap.add_argument(
        "--sims", type=int, default=10_000, help="Monte Carlo simulation count"
    )
    ap.add_argument(
        "--no-write", action="store_true", help="Do not update strategy_config.py"
    )
    args = ap.parse_args()

    log("=" * 60)
    log("STRATEGY OPTIMIZER  —  " + datetime.now().strftime("%Y-%m-%d %H:%M"))
    log("=" * 60)

    # Phase 1 always runs
    df = load_trades(Path(args.log))

    ml_result = {}
    thr_result = {}
    wgt_result = {}
    mc_result = {}

    if args.phase in ("all", "ml"):
        ml_result = ml_feature_analysis(df)

    if args.phase in ("all", "threshold"):
        thr_result = optimize_thresholds(df, n_trials=args.trials)

    if args.phase in ("all", "weights"):
        wgt_result = optimize_confluence_weights(df, n_trials=args.trials // 2)

    if args.phase in ("all", "monte_carlo"):
        mc_result = monte_carlo(df, n_sims=args.sims)

    # ── Summary ───────────────────────────────────────────────────────────────
    log("\n" + "=" * 60)
    log("SUMMARY & RECOMMENDATIONS")
    log("=" * 60)

    if mc_result:
        log(f"  Ruin probability      : {mc_result.get('ruin_prob', 0):.1f}%")
        log(f"  Median final equity   : ${mc_result.get('p50_equity', 0):,.0f}")
        log(f"  P90 max drawdown      : ${mc_result.get('p90_max_dd', 0):,.0f}")
        log(f"  Median Sharpe         : {mc_result.get('p50_sharpe', 0):.2f}")

    if thr_result:
        baseline = thr_result.get("baseline", {})
        optimized_m = thr_result.get("metrics", {})
        improvement = optimized_m.get("pnl", 0) - baseline.get("pnl", 0)
        log(f"\n  Threshold optimized PnL improvement: ${improvement:+,.0f}")
        log(f"  Recommended SCORE_REJECT: {thr_result.get('global_thresh', 60)}")

    if ml_result.get("source_stats"):
        log("\n  Weakest sources (by avg PnL of accepted trades):")
        sorted_src = sorted(
            ml_result["source_stats"].items(), key=lambda x: x[1]["avg_pnl"]
        )
        for src, s in sorted_src[:3]:
            log(
                f"    {src:<25}: win={s['win_rate']:.1f}%  avg=${s['avg_pnl']:.1f}  "
                f"consider raising threshold or removing"
            )
        log("\n  Strongest sources:")
        for src, s in sorted(
            ml_result["source_stats"].items(), key=lambda x: -x[1]["avg_pnl"]
        )[:3]:
            log(
                f"    {src:<25}: win={s['win_rate']:.1f}%  avg=${s['avg_pnl']:.1f}  keep/lower threshold"
            )

    # Write back
    if not args.no_write and thr_result:
        write_optimized_config(CFG_PATH, thr_result, wgt_result)

    # Save report
    RPT_PATH.write_text("\n".join(REPORT_LINES), encoding="utf-8")
    log(f"\nReport saved to {RPT_PATH}")


if __name__ == "__main__":
    main()
