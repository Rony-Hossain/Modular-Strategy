"""
SIGNAL ML ANALYSIS — Learn which flow/structure features predict wins vs losses.

Uses sklearn (not TensorFlow) because we have ~1,500 samples — too few for
a neural net, perfect for tree-based models that handle small data well.

Pipeline:
  1. Parse Log.csv -> feature matrix (FLOW + STRUCT features per signal bar)
  2. Train GradientBoosting classifier (TARGET vs STOP)
  3. Permutation importance -> which features matter most
  4. Decision boundary analysis -> concrete threshold rules
  5. Separate analysis for TAKEN vs FILTERED signals
  6. Export feature_matrix.csv for external analysis

Requirements: pip install scikit-learn pandas numpy
"""

import csv
import os
import sys
import re
import numpy as np
import pandas as pd
from collections import defaultdict

if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'backtest', 'Log.csv')
TRADES_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'backtest', 'Trades.csv')
OUTPUT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'ml_feature_matrix.csv')


def parse_log():
    """Parse Log.csv into a feature matrix with correct bar-based matching."""
    print(f"--- PARSING {LOG_PATH} ---")

    if not os.path.exists(LOG_PATH):
        print("Error: Log.csv not found.")
        return None

    bar_context = {}  # bar_str -> {flow + struct features}
    outcomes = []     # list of {bar, cid, pnl, hit, dir, ...}
    evals = {}        # (bar, cid) -> {dir, score, context}
    rank_decisions = {}  # (ts, cid) -> status

    # Load trade IDs for TAKEN matching (Trades.csv or Executions.csv fallback)
    trade_ids = set()
    if os.path.exists(TRADES_PATH) and os.path.getsize(TRADES_PATH) > 10:
        with open(TRADES_PATH, newline='', encoding='utf-8-sig') as f:
            for row in csv.DictReader(f):
                trade_ids.add(row.get('Entry name', ''))
    else:
        exec_path = os.path.join(os.path.dirname(TRADES_PATH), 'Executions.csv')
        if os.path.exists(exec_path):
            with open(exec_path, newline='', encoding='utf-8-sig') as f:
                for row in csv.DictReader(f):
                    if row.get('E/X', '').strip() == 'Entry':
                        trade_ids.add(row.get('Name', ''))

    with open(LOG_PATH, 'rb') as f:
        for line_raw in f:
            try:
                line = line_raw.decode('utf-8', errors='ignore')
                parts = line.split(',')
                if len(parts) < 21:
                    continue

                tag = parts[1].strip()
                bar = parts[2].strip()
                detail = parts[20].strip()

                if tag in ('FLOW', 'STRUCT'):
                    if bar not in bar_context:
                        bar_context[bar] = {}
                    prefix = 'f_' if tag == 'FLOW' else 's_'
                    for m in re.finditer(r'([A-Z_]+)=([-+]?[\d.]+)', detail):
                        bar_context[bar][f"{prefix}{m.group(1)}"] = float(m.group(2))

                elif tag == 'EVAL':
                    cid = parts[5].strip()
                    direction = parts[3].strip()
                    score = parts[6].strip()
                    # Parse context from detail (h4, h2, h1, smf, str, sw)
                    ctx = {}
                    for m in re.finditer(r'(h[0-9]+|smf|str|sw)=([^ ]+)', detail):
                        k, v = m.group(1), m.group(2)
                        if v in ('+', 'bull', 'bullish'):
                            ctx[f'ctx_{k}'] = 1
                        elif v in ('-', 'bear', 'bearish'):
                            ctx[f'ctx_{k}'] = -1
                        elif v in ('flat', '0'):
                            ctx[f'ctx_{k}'] = 0
                        else:
                            try:
                                ctx[f'ctx_{k}'] = float(v)
                            except:
                                pass

                    evals[(bar, cid)] = {
                        'dir': direction, 'score': score, 'ctx': ctx
                    }

                elif tag == 'WARN' and 'RANK_' in detail:
                    ts = parts[0].strip()
                    cid_m = re.search(r'\[([^\]]+)\]', detail)
                    rcid = cid_m.group(1) if cid_m else ''
                    if 'RANK_WIN' in detail:
                        rank_decisions[(ts, rcid)] = 'WIN'
                    elif 'RANK_VETO' in detail:
                        rank_decisions[(ts, rcid)] = 'VETO'
                    elif 'RANK_WEAK' in detail:
                        rank_decisions[(ts, rcid)] = 'WEAK'

                elif tag == 'TOUCH_OUTCOME':
                    gate = parts[18].strip()
                    pnl_m = re.search(r'SIM_PNL=([-+]?[\d.]+)', detail)
                    hit_m = re.search(r'FIRST_HIT=(\w+)', detail)
                    mfe_m = re.search(r'MFE=([\d.]+)', detail)
                    mae_m = re.search(r'MAE=([\d.]+)', detail)

                    if pnl_m:
                        # Extract original bar from gate: "CID:YYYYMMDD:bar"
                        gate_parts = gate.split(':')
                        orig_bar = gate_parts[-1] if len(gate_parts) >= 3 else ''
                        cid = gate_parts[0] if gate_parts else ''

                        # Match trades: gate may be CID:YYYYMMDD_HHMMSS:bar
                        # but trade_ids may use CID:YYYYMMDD:bar
                        traded = gate in trade_ids
                        if not traded and '_' in gate:
                            # Try short format: strip _HHMMSS
                            short_gate = re.sub(r'_\d{6}:', ':', gate)
                            traded = short_gate in trade_ids

                        outcomes.append({
                            'bar': orig_bar,
                            'cid': cid,
                            'gate': gate,
                            'pnl': float(pnl_m.group(1)),
                            'hit': hit_m.group(1) if hit_m else '',
                            'mfe': float(mfe_m.group(1)) if mfe_m else 0,
                            'mae': float(mae_m.group(1)) if mae_m else 0,
                            'was_traded': traded,
                        })
            except Exception:
                continue

    # Join: outcome -> bar_context + eval context
    final_data = []
    for o in outcomes:
        row = o.copy()

        # Add flow/struct features from same bar
        if o['bar'] in bar_context:
            row.update(bar_context[o['bar']])

        # Add eval context
        ev = evals.get((o['bar'], o['cid']))
        if ev:
            row['eval_dir'] = 1 if ev['dir'] == 'Long' else -1
            try:
                row['eval_score'] = float(ev['score'])
            except:
                row['eval_score'] = 0
            row.update(ev['ctx'])

        final_data.append(row)

    if not final_data:
        print("Error: No data joined.")
        return None

    df = pd.DataFrame(final_data)
    df.to_csv(OUTPUT_PATH, index=False)
    print(f"Feature matrix: {df.shape[0]} rows x {df.shape[1]} cols -> {OUTPUT_PATH}")
    return df


def train_and_analyze(df):
    """Train tree model and extract actionable rules."""
    from sklearn.ensemble import GradientBoostingClassifier
    from sklearn.model_selection import cross_val_score
    from sklearn.inspection import permutation_importance

    print("\n" + "=" * 95)
    print("  ML MODEL: What predicts TARGET vs STOP?")
    print("=" * 95)

    # Filter to TARGET/STOP only (exclude BOTH_SAMEBAR, NEITHER)
    df_clean = df[df['hit'].isin(['TARGET', 'STOP'])].copy()
    df_clean['win'] = (df_clean['hit'] == 'TARGET').astype(int)

    # Select numeric features
    feature_cols = [c for c in df_clean.columns
                    if c.startswith(('f_', 's_', 'ctx_')) or c == 'eval_score']
    feature_cols = [c for c in feature_cols if df_clean[c].notna().sum() > len(df_clean) * 0.5]

    df_model = df_clean[feature_cols + ['win']].dropna()
    X = df_model[feature_cols].values
    y = df_model['win'].values

    print(f"\n  Samples: {len(X)}  Features: {len(feature_cols)}  Win rate: {y.mean()*100:.1f}%")

    if len(X) < 50:
        print("  Too few samples for ML analysis.")
        return

    # GradientBoosting with conservative settings to avoid overfitting
    model = GradientBoostingClassifier(
        n_estimators=100,
        max_depth=3,
        min_samples_leaf=20,
        learning_rate=0.05,
        subsample=0.8,
        random_state=42
    )

    # Cross-validated accuracy
    scores = cross_val_score(model, X, y, cv=5, scoring='accuracy')
    print(f"  5-fold CV Accuracy: {scores.mean()*100:.1f}% (+/- {scores.std()*100:.1f}%)")

    baseline = max(y.mean(), 1 - y.mean()) * 100
    print(f"  Baseline (majority class): {baseline:.1f}%")

    if scores.mean() * 100 < baseline + 2:
        print("  WARNING: Model barely beats baseline. Features may not be predictive.")

    # Train final model on all data for feature importance
    model.fit(X, y)

    # Permutation importance (more reliable than built-in feature_importances_)
    perm = permutation_importance(model, X, y, n_repeats=30, random_state=42)

    print(f"\n  TOP PREDICTIVE FEATURES (permutation importance):")
    print(f"  {'FEATURE':<20} {'IMPORTANCE':>12} {'DIRECTION'}")
    print(f"  {'-'*20} {'-'*12} {'-'*40}")

    # Sort by importance
    sorted_idx = perm.importances_mean.argsort()[::-1]
    for i in sorted_idx[:12]:
        feat = feature_cols[i]
        imp = perm.importances_mean[i]
        if imp < 0.001:
            continue

        # Determine direction: higher value = more wins or more losses?
        high_mask = X[:, i] > np.median(X[:, i])
        wr_high = y[high_mask].mean() * 100 if high_mask.sum() > 10 else 0
        wr_low = y[~high_mask].mean() * 100 if (~high_mask).sum() > 10 else 0
        direction = f"High={wr_high:.0f}%WR  Low={wr_low:.0f}%WR"

        print(f"  {feat:<20} {imp:>12.4f} {direction}")

    # Threshold discovery for top features
    print(f"\n  ACTIONABLE THRESHOLD RULES:")
    print(f"  {'RULE':<55} {'COUNT':>5} {'WIN%':>6} {'AVG PNL':>8}")
    print(f"  {'-'*55} {'-'*5} {'-'*6} {'-'*8}")

    for i in sorted_idx[:6]:
        feat = feature_cols[i]
        imp = perm.importances_mean[i]
        if imp < 0.002:
            continue

        vals = X[:, i]
        pnls = df_model['pnl'].values if 'pnl' in df_model.columns else None

        # Try percentile thresholds
        for pct_val in [25, 50, 75]:
            threshold = np.percentile(vals, pct_val)
            above = vals > threshold
            below = ~above

            if above.sum() < 15 or below.sum() < 15:
                continue

            wr_above = y[above].mean() * 100
            wr_below = y[below].mean() * 100

            # Only report if there's a meaningful gap
            if abs(wr_above - wr_below) > 8:
                better = "above" if wr_above > wr_below else "below"
                rule = f"IF {feat} {'>' if better == 'above' else '<='} {threshold:.1f}"
                wr = wr_above if better == 'above' else wr_below
                n = above.sum() if better == 'above' else below.sum()
                avg_pnl = 0
                if pnls is not None:
                    mask = above if better == 'above' else below
                    avg_pnl = pnls[mask].mean()
                print(f"  {rule:<55} {n:>5} {wr:>5.1f}% {avg_pnl:>+8,.0f}")

    return model, feature_cols


def analyze_taken_vs_filtered(df):
    """Separate ML analysis: what distinguishes taken winners from taken losers?"""
    print("\n" + "=" * 95)
    print("  TAKEN vs FILTERED: Feature distribution comparison")
    print("=" * 95)

    df_clean = df[df['hit'].isin(['TARGET', 'STOP'])].copy()
    feature_cols = [c for c in df_clean.columns
                    if c.startswith(('f_', 's_', 'ctx_')) or c == 'eval_score']

    taken = df_clean[df_clean['was_traded'] == True]
    filtered = df_clean[df_clean['was_traded'] == False]

    print(f"\n  Taken: {len(taken)}  Filtered: {len(filtered)}")

    if len(taken) < 10 or len(filtered) < 10:
        print("  Not enough data for comparison.")
        return

    # Compare feature distributions
    print(f"\n  {'FEATURE':<20} {'TAKEN avg':>12} {'FILT avg':>12} {'DELTA':>10} {'TAKEN WR%':>10} {'FILT WR%':>10}")
    print(f"  {'-'*20} {'-'*12} {'-'*12} {'-'*10} {'-'*10} {'-'*10}")

    taken_wins = taken[taken['hit'] == 'TARGET']
    filt_wins = filtered[filtered['hit'] == 'TARGET']

    for feat in feature_cols:
        if feat not in taken.columns or taken[feat].isna().all():
            continue

        t_avg = taken[feat].mean()
        f_avg = filtered[feat].mean()
        delta = t_avg - f_avg

        t_wr = len(taken_wins) / max(len(taken), 1) * 100
        f_wr = len(filt_wins) / max(len(filtered), 1) * 100

        if abs(delta) > 0.5 or abs(t_wr - f_wr) > 5:
            print(f"  {feat:<20} {t_avg:>12.2f} {f_avg:>12.2f} {delta:>+10.2f} {t_wr:>9.1f}% {f_wr:>9.1f}%")

    # Per-CID analysis: which signals are being filtered unfairly?
    print(f"\n  PER-SIGNAL FILTER ACCURACY:")
    print(f"  {'SIGNAL':<28} {'TAKEN':>5} {'T_WR%':>6} {'FILT':>5} {'F_WR%':>6} {'FILT SIM $':>10} {'VERDICT':<12}")
    print(f"  {'-'*28} {'-'*5} {'-'*6} {'-'*5} {'-'*6} {'-'*10} {'-'*12}")

    for cid in sorted(df_clean['cid'].unique()):
        t = taken[taken['cid'] == cid]
        f = filtered[filtered['cid'] == cid]
        if len(t) == 0 and len(f) == 0:
            continue

        t_wr = (t['hit'] == 'TARGET').mean() * 100 if len(t) > 0 else 0
        f_wr = (f['hit'] == 'TARGET').mean() * 100 if len(f) > 0 else 0
        f_pnl = f['pnl'].sum() if len(f) > 0 else 0

        if f_pnl > 1000:
            verdict = 'LEAKING'
        elif f_pnl < -1000:
            verdict = 'SAVING $'
        elif len(f) == 0:
            verdict = 'ALL TAKEN'
        else:
            verdict = 'OK'

        print(f"  {cid:<28} {len(t):>5} {t_wr:>5.1f}% {len(f):>5} {f_wr:>5.1f}% {f_pnl:>+10,.0f} {verdict:<12}")


def discover_clusters(df):
    """Data-driven cluster discovery using quantile splits."""
    print("\n" + "=" * 95)
    print("  ALPHA CLUSTERS — Data-driven high-probability scenarios")
    print("=" * 95)

    df_clean = df[df['hit'].isin(['TARGET', 'STOP'])].copy()
    df_clean['win'] = (df_clean['hit'] == 'TARGET').astype(int)

    if len(df_clean) < 30:
        print("  Not enough data.")
        return

    feature_cols = [c for c in df_clean.columns
                    if c.startswith(('f_', 's_')) and df_clean[c].notna().sum() > len(df_clean) * 0.5]

    print(f"\n  {'SCENARIO':<55} {'COUNT':>5} {'WIN%':>6} {'AVG PNL':>8} {'EDGE'}")
    print(f"  {'-'*55} {'-'*5} {'-'*6} {'-'*8} {'-'*8}")

    # Test all pairs of features at their tertile boundaries
    best_scenarios = []

    for i, f1 in enumerate(feature_cols):
        v1 = df_clean[f1].dropna()
        if len(v1) < 30:
            continue
        q1_hi = v1.quantile(0.67)
        q1_lo = v1.quantile(0.33)

        for direction1 in ['high', 'low']:
            if direction1 == 'high':
                mask1 = df_clean[f1] > q1_hi
            else:
                mask1 = df_clean[f1] < q1_lo

            sub = df_clean[mask1]
            if len(sub) < 15:
                continue

            wr = sub['win'].mean() * 100
            avg_pnl = sub['pnl'].mean() if 'pnl' in sub.columns else 0
            baseline_wr = df_clean['win'].mean() * 100

            # Only report if meaningfully better than baseline
            if wr > baseline_wr + 8 and len(sub) >= 15:
                op = '>' if direction1 == 'high' else '<'
                val = q1_hi if direction1 == 'high' else q1_lo
                label = f"{f1} {op} {val:.1f}"
                best_scenarios.append((label, len(sub), wr, avg_pnl))

    # Sort by win rate and show top 15
    for label, count, wr, avg_pnl in sorted(best_scenarios, key=lambda x: -x[2])[:15]:
        edge = 'STRONG' if wr > 65 else ('MODERATE' if wr > 58 else 'WEAK')
        print(f"  {label:<55} {count:>5} {wr:>5.1f}% {avg_pnl:>+8,.0f} {edge}")


# ============================================================================
#  MAIN
# ============================================================================

if __name__ == '__main__':
    df = parse_log()
    if df is None:
        sys.exit(1)

    # Show basic stats
    print(f"\n  TARGET: {(df['hit']=='TARGET').sum()}  STOP: {(df['hit']=='STOP').sum()}  "
          f"OTHER: {(~df['hit'].isin(['TARGET','STOP'])).sum()}")

    try:
        model, features = train_and_analyze(df)
    except ImportError:
        print("\n  sklearn not installed. Run: pip install scikit-learn")
        print("  Skipping ML model training.")
        model, features = None, None
    except Exception as e:
        print(f"\n  ML training failed: {e}")
        model, features = None, None

    analyze_taken_vs_filtered(df)
    discover_clusters(df)

    print(f"\n{'=' * 95}")
    print(f"  ML ANALYSIS COMPLETE")
    print(f"{'=' * 95}")
