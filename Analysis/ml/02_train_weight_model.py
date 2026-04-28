"""
ML Step 2 — Train model to predict trade quality from layer scores + context.
Outputs feature importances and SHAP values → weight recommendations.

Outputs:
  - Analysis/ml/model_importance.csv     (feature importance rankings)
  - Analysis/ml/shap_summary.csv         (mean |SHAP| per feature)
  - Analysis/ml/weight_proposal.json     (proposed StrategyConfig constants)
  - Analysis/ml/training_report.md       (human-readable report)

Requirements: pip install lightgbm shap scikit-learn
"""

import json
import warnings
import numpy as np
import pandas as pd
from pathlib import Path
from sklearn.model_selection import TimeSeriesSplit, cross_val_score
from sklearn.metrics import classification_report, roc_auc_score
import lightgbm as lgb

warnings.filterwarnings("ignore")
np.random.seed(42)

ML_DIR   = Path(__file__).resolve().parent
DATA     = ML_DIR / "feature_matrix.parquet"

# ── Current weights from StrategyConfig ──
CURRENT_WEIGHTS = {
    "LAYER_A_H4": 14, "LAYER_A_H2": 10, "LAYER_A_H1": 6,
    "LAYER_B_MAX_CAP": 40,
    "PROXY_BAR_DELTA": 7, "PROXY_REGIME": 6, "PROXY_VWAP_SIDE": 5,
    "PROXY_H1_BAR_DIR": 4, "LAYER_C_DIVERGENCE": 15,
    "LAYER_D_FULL_STRUCT": 12, "LAYER_D_TREND_ONLY": 8,
    "PENALTY_H4": 8, "PENALTY_H2": 5, "PENALTY_BOTH_EXTRA": 5,
    "PENALTY_ABOVE_FAIR": 15, "BONUS_DEEP_DISCOUNT": 10,
}


def load_and_prepare():
    df = pd.read_parquet(DATA)
    print(f"[DATA] {len(df)} trades loaded")

    # ── Feature columns (numeric only for tree model) ──
    layer_features = ["layer_a", "layer_b", "layer_c", "layer_d", "penalty"]
    if "bonus" in df.columns:
        layer_features.append("bonus")

    context_features = ["ctx_h4_enc", "ctx_h2_enc", "ctx_h1_enc", "ctx_smf_enc",
                        "ctx_str", "ctx_sw", "direction_enc", "source_enc"]

    flow_features = [c for c in df.columns if c in ["regime", "bd", "cd", "dsl", "dsh",
                     "poc", "vah", "val", "skew", "pp"]]

    all_features = layer_features + context_features + flow_features
    available = [f for f in all_features if f in df.columns]

    # Drop rows where all layer scores are NaN (no rank data)
    mask = df[["layer_a", "layer_b", "layer_c", "layer_d"]].notna().any(axis=1)
    df = df[mask].copy()
    print(f"  after dropping no-rank rows: {len(df)}")

    X = df[available].fillna(0)
    y_cls = df["is_win"]
    y_reg = df["pnl"]

    return df, X, y_cls, y_reg, available


def train_classifier(X, y, features):
    """Binary classifier: predict is_win."""
    print("\n[MODEL] Training LightGBM classifier (is_win)...")

    model = lgb.LGBMClassifier(
        n_estimators=300,
        max_depth=5,
        learning_rate=0.05,
        num_leaves=31,
        min_child_samples=20,
        subsample=0.8,
        colsample_bytree=0.8,
        reg_alpha=1.0,
        reg_lambda=1.0,
        random_state=42,
        verbose=-1,
    )

    # Time-series cross-validation (no future leak)
    tscv = TimeSeriesSplit(n_splits=5)
    scores = cross_val_score(model, X, y, cv=tscv, scoring="roc_auc")
    print(f"  CV ROC-AUC: {scores.mean():.4f} ± {scores.std():.4f}")
    print(f"  Per-fold:   {[f'{s:.4f}' for s in scores]}")

    # Train on full data for importance
    model.fit(X, y)
    y_pred = model.predict(X)
    y_prob = model.predict_proba(X)[:, 1]
    print(f"  Train AUC:  {roc_auc_score(y, y_prob):.4f}")
    print(classification_report(y, y_pred, target_names=["Loss", "Win"]))

    # Feature importance
    importance = pd.DataFrame({
        "feature": features,
        "importance": model.feature_importances_,
        "importance_pct": model.feature_importances_ / model.feature_importances_.sum() * 100
    }).sort_values("importance", ascending=False)

    return model, importance, scores


def compute_shap(model, X, features):
    """SHAP values for explainability."""
    try:
        import shap
        print("[SHAP] Computing SHAP values...")
        explainer = shap.TreeExplainer(model)
        shap_values = explainer.shap_values(X)

        # For binary classifier, shap_values may be [neg_class, pos_class]
        if isinstance(shap_values, list):
            sv = shap_values[1]  # positive class
        else:
            sv = shap_values

        shap_df = pd.DataFrame({
            "feature": features,
            "mean_abs_shap": np.abs(sv).mean(axis=0),
            "mean_shap": sv.mean(axis=0),
        }).sort_values("mean_abs_shap", ascending=False)

        return shap_df
    except ImportError:
        print("[SHAP] shap not installed — skipping. pip install shap")
        return None


def propose_weights(importance, shap_df):
    """Map feature importances back to StrategyConfig weight recommendations."""
    print("\n[PROPOSAL] Generating weight recommendations...")

    # Layer importance mapping
    layer_map = {
        "layer_a": ["LAYER_A_H4", "LAYER_A_H2", "LAYER_A_H1"],
        "layer_b": ["LAYER_B_MAX_CAP"],
        "layer_c": ["PROXY_BAR_DELTA", "PROXY_REGIME", "PROXY_VWAP_SIDE",
                     "PROXY_H1_BAR_DIR", "LAYER_C_DIVERGENCE"],
        "layer_d": ["LAYER_D_FULL_STRUCT", "LAYER_D_TREND_ONLY"],
        "penalty": ["PENALTY_H4", "PENALTY_H2", "PENALTY_BOTH_EXTRA",
                     "PENALTY_ABOVE_FAIR"],
    }

    # Get layer importances
    imp_dict = dict(zip(importance["feature"], importance["importance_pct"]))
    layer_imp = {}
    for layer in ["layer_a", "layer_b", "layer_c", "layer_d", "penalty"]:
        layer_imp[layer] = imp_dict.get(layer, 0)

    # SHAP direction (positive = helps wins, negative = hurts)
    shap_dir = {}
    if shap_df is not None:
        for _, row in shap_df.iterrows():
            shap_dir[row["feature"]] = row["mean_shap"]

    # Build proposal
    proposal = {}
    total_layer_imp = sum(layer_imp.get(l, 0) for l in ["layer_a", "layer_b", "layer_c", "layer_d"])
    if total_layer_imp == 0:
        total_layer_imp = 1  # avoid div/0

    for layer, keys in layer_map.items():
        if layer == "penalty":
            continue
        rel_imp = layer_imp.get(layer, 0) / total_layer_imp
        direction = shap_dir.get(layer, 0)

        for key in keys:
            current = CURRENT_WEIGHTS.get(key, 0)
            if direction < -0.01:
                # Negative SHAP → this layer hurts, reduce weight
                proposed = max(0, int(current * 0.5))
                reason = f"negative SHAP ({direction:.4f}), halving"
            elif rel_imp < 0.05:
                # Very low importance → reduce
                proposed = max(0, int(current * 0.6))
                reason = f"low importance ({rel_imp:.1%}), reducing"
            elif rel_imp > 0.30:
                # High importance → increase
                proposed = min(current + 5, int(current * 1.3))
                reason = f"high importance ({rel_imp:.1%}), increasing"
            else:
                proposed = current
                reason = f"importance OK ({rel_imp:.1%}), keeping"

            proposal[key] = {
                "current": current,
                "proposed": proposed,
                "importance_pct": round(rel_imp * 100, 1),
                "shap_direction": round(direction, 4) if layer in shap_dir else None,
                "reason": reason,
            }

    return proposal


def write_report(importance, shap_df, proposal, cv_scores):
    """Write human-readable report."""
    lines = [
        "# ML Weight Optimization Report",
        "",
        f"**Date:** {pd.Timestamp.now().strftime('%Y-%m-%d %H:%M')}",
        f"**CV ROC-AUC:** {cv_scores.mean():.4f} ± {cv_scores.std():.4f}",
        "",
        "## Feature Importance (LightGBM)",
        "",
        "| Rank | Feature | Importance % |",
        "| --- | --- | --- |",
    ]
    for i, (_, row) in enumerate(importance.iterrows(), 1):
        lines.append(f"| {i} | {row['feature']} | {row['importance_pct']:.1f}% |")

    if shap_df is not None:
        lines.extend([
            "", "## SHAP Values (mean |SHAP| per feature)", "",
            "| Feature | mean |SHAP| | mean SHAP (direction) |",
            "| --- | --- | --- |",
        ])
        for _, row in shap_df.iterrows():
            sign = "+" if row["mean_shap"] > 0 else ""
            lines.append(f"| {row['feature']} | {row['mean_abs_shap']:.4f} | {sign}{row['mean_shap']:.4f} |")

    lines.extend(["", "## Weight Proposals", "",
                   "| Config Key | Current | Proposed | Importance | SHAP | Reason |",
                   "| --- | --- | --- | --- | --- | --- |"])
    for key, p in proposal.items():
        shap_str = f"{p['shap_direction']:.4f}" if p["shap_direction"] is not None else "N/A"
        changed = " **CHANGED**" if p["current"] != p["proposed"] else ""
        lines.append(f"| {key} | {p['current']} | {p['proposed']} | {p['importance_pct']}% | {shap_str} | {p['reason']}{changed} |")

    lines.extend([
        "", "## How to Apply", "",
        "1. Review proposed weights above",
        "2. Update values in `ModularStrategy/StrategyConfig.cs` → `Confluence` class",
        "3. Re-run backtest",
        "4. Re-run analysis pipeline (scripts 23-28)",
        "5. Compare grade calibration and source performance vs baseline",
        "", "If CV AUC < 0.52, the model has weak predictive power — weights may not",
        "improve results significantly. Focus on feature engineering or more data first.",
    ])

    report_path = ML_DIR / "training_report.md"
    report_path.write_text("\n".join(lines), encoding="utf-8")
    print(f"\n[REPORT] → {report_path}")


def main():
    df, X, y_cls, y_reg, features = load_and_prepare()

    # ── Train classifier ──
    model, importance, cv_scores = train_classifier(X, y_cls, features)
    importance.to_csv(ML_DIR / "model_importance.csv", index=False)
    print(f"[SAVE] → model_importance.csv")

    # ── SHAP ──
    shap_df = compute_shap(model, X, features)
    if shap_df is not None:
        shap_df.to_csv(ML_DIR / "shap_summary.csv", index=False)
        print(f"[SAVE] → shap_summary.csv")

    # ── Propose new weights ──
    proposal = propose_weights(importance, shap_df)
    with open(ML_DIR / "weight_proposal.json", "w") as f:
        json.dump(proposal, f, indent=2)
    print(f"[SAVE] → weight_proposal.json")

    # ── Write report ──
    write_report(importance, shap_df, proposal, cv_scores)

    print("\n[DONE] Next steps:")
    print("  1. Review training_report.md")
    print("  2. If AUC > 0.55, apply weight_proposal.json to StrategyConfig.cs")
    print("  3. Re-run backtest and compare")


if __name__ == "__main__":
    main()
