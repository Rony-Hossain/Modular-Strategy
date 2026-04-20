"""
analyze_winners_vs_losers.py  —  Find what separates winners from losers.

Uses TOUCH_OUTCOME (accepted signals only) joined with SIGNAL_ACCEPTED snap
features to build a feature matrix, then:
  1. Shows mean feature values: winners vs losers side-by-side
  2. Trains a Random Forest — prints feature importances
  3. Finds the best single-threshold cut per feature
  4. Builds a simple rule set and shows what win-rate it achieves
  5. Prints per-source win rates + score distributions

Run:  python analyze_winners_vs_losers.py
"""

import sys, re
import numpy as np
import pandas as pd
from pathlib import Path

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

LOG_PATH = Path(__file__).parent.parent / 'backtest' / 'Log.csv'

# ── Regex for SNAP features in Detail field ───────────────────────────────────
_SNAP = re.compile(
    r'SNAP_BD=([-\d.]+).*?SNAP_CD=([-\d.]+).*?SNAP_ABS=([-\d.]+)'
    r'.*?SNAP_H1=([-\d.]+).*?SNAP_H2=([-\d.]+).*?SNAP_H4=([-\d.]+)'
    r'.*?SNAP_REG=([-\d.]+).*?SNAP_STR=([-\d.]+).*?SNAP_DEX=([-\d.]+)'
    r'.*?SNAP_BDIV=([-\d.]+).*?SNAP_BERDIV=([-\d.]+)'
    r'.*?SNAP_SW=([-\d.]+).*?SNAP_TRD=([-\d.]+).*?SNAP_ATR=([-\d.]+)'
)
_PNL_RE = re.compile(r'SIM_PNL=([-\d.]+)')
_MFE_RE = re.compile(r'MFE=([\d.]+)')
_MAE_RE = re.compile(r'MAE=([\d.]+)')
_GNM_RE = re.compile(r':(\d+)(?::REJ)?$')

SNAP_COLS = ['BD','CD','ABS','H1','H2','H4','REG','STR','DEX','BDIV','BERDIV','SW','TRD','ATR']


# ─────────────────────────────────────────────────────────────────────────────
def build_feature_matrix():
    df = pd.read_csv(LOG_PATH, dtype=str)
    df.columns = [c.strip() for c in df.columns]
    df['Timestamp'] = pd.to_datetime(df['Timestamp'], errors='coerce')

    def _f(row, col, default=0.0):
        try: return float(str(row.get(col, '')).strip())
        except: return default

    # Build signal map
    sig_map = {}
    for _, r in df[df['Tag'] == 'SIGNAL_ACCEPTED'].iterrows():
        bar  = int(_f(r, 'Bar'))
        cond = str(r.get('ConditionSetId', '')).strip()
        sig_map[f"{cond}:{bar}"] = dict(
            time      = r['Timestamp'],
            direction = str(r.get('Direction', '')).strip(),
            source    = str(r.get('Source', '')).strip(),
            score     = _f(r, 'Score'),
            entry     = _f(r, 'EntryPrice'),
            stop      = _f(r, 'StopPrice'),
            t1        = _f(r, 'T1Price'),
        )

    rows = []
    for _, r in df[df['Tag'] == 'TOUCH_OUTCOME'].iterrows():
        gate  = str(r.get('GateReason', ''))
        if ':REJ' in gate:
            continue
        label   = str(r.get('Label', '')).strip()
        outcome = 1 if label == 'TARGET' else 0 if label == 'STOP' else -1
        if outcome == -1:
            continue

        gm = _GNM_RE.search(gate)
        if not gm: continue
        src_bar  = int(gm[1])
        cond_key = gate.split(':')[0]
        key      = f"{cond_key}:{src_bar}"
        if key not in sig_map:
            key = next((k for k in sig_map if k.endswith(f':{src_bar}')), None)
        if not key or key not in sig_map: continue

        s   = sig_map[key]
        det = str(r.get('Detail', ''))

        snap = _SNAP.search(det)
        snap_vals = {c: float(v) for c, v in zip(SNAP_COLS, snap.groups())} \
                    if snap else {c: np.nan for c in SNAP_COLS}

        pm  = _PNL_RE.search(det); mfe = _MFE_RE.search(det); mae = _MAE_RE.search(det)

        row = dict(
            outcome   = outcome,
            source    = s['source'],
            direction = s['direction'],
            score     = s['score'],
            entry     = s['entry'],
            stop      = s['stop'],
            t1        = s['t1'],
            sim_pnl   = float(pm[1])  if pm  else 0.0,
            mfe       = float(mfe[1]) if mfe else 0.0,
            mae       = float(mae[1]) if mae else 0.0,
        )
        row.update({f'snap_{c.lower()}': v for c, v in snap_vals.items()})
        rows.append(row)

    return pd.DataFrame(rows)


# ─────────────────────────────────────────────────────────────────────────────
def main():
    print("Building feature matrix ...")
    df = build_feature_matrix()
    print(f"  {len(df)} trades  ({df['outcome'].sum()} wins, {(df['outcome']==0).sum()} losses)\n")

    wins   = df[df['outcome'] == 1]
    losses = df[df['outcome'] == 0]
    feat_cols = ['score'] + [f'snap_{c.lower()}' for c in SNAP_COLS]

    # ── 1. Feature means: winners vs losers ──────────────────────────────────
    print("=" * 65)
    print(f"{'FEATURE':<18} {'WINNERS':>10} {'LOSERS':>10} {'DIFF':>10} {'EDGE':>8}")
    print("=" * 65)
    rows_out = []
    for c in feat_cols:
        wm = wins[c].mean()
        lm = losses[c].mean()
        diff = wm - lm
        rows_out.append((c, wm, lm, diff))

    rows_out.sort(key=lambda x: abs(x[3]), reverse=True)
    for c, wm, lm, diff in rows_out:
        edge = '+' if diff > 0 else '-'
        print(f"{c:<18} {wm:>10.2f} {lm:>10.2f} {diff:>+10.2f} {edge:>8}")

    # ── 2. Random Forest importance ───────────────────────────────────────────
    print()
    try:
        from sklearn.ensemble import RandomForestClassifier, GradientBoostingClassifier
        from sklearn.model_selection import cross_val_score
        from sklearn.preprocessing import LabelEncoder

        X = df[feat_cols + ['source', 'direction']].copy()
        X['source_enc']    = LabelEncoder().fit_transform(X['source'].fillna(''))
        X['direction_enc'] = LabelEncoder().fit_transform(X['direction'].fillna(''))
        X = X.drop(columns=['source', 'direction'])
        X = X.fillna(X.median())
        y = df['outcome']

        rf = RandomForestClassifier(n_estimators=300, max_depth=6,
                                    class_weight='balanced', random_state=42)
        scores = cross_val_score(rf, X, y, cv=5, scoring='roc_auc')
        print(f"Random Forest  AUC: {scores.mean():.3f} (+/- {scores.std():.3f})")

        rf.fit(X, y)
        imp = sorted(zip(X.columns, rf.feature_importances_), key=lambda x: -x[1])
        print("\nTop feature importances:")
        for feat, imp_val in imp[:10]:
            bar = '#' * int(imp_val * 200)
            print(f"  {feat:<20} {imp_val:.4f}  {bar}")

    except ImportError:
        print("(sklearn not available — skipping RF)")

    # ── 3. Best single-threshold cuts ─────────────────────────────────────────
    print()
    print("=" * 65)
    print("BEST SINGLE-THRESHOLD FILTERS  (min 20 trades each side)")
    print("=" * 65)
    results = []
    for c in feat_cols:
        col = df[c].dropna()
        if col.nunique() < 3:
            continue
        for thresh in np.percentile(col, [20, 30, 40, 50, 60, 70, 80]):
            above = df[df[c] >= thresh]
            below = df[df[c] <  thresh]
            for side, sub in [('>=', above), ('<', below)]:
                if len(sub) < 20: continue
                wr = sub['outcome'].mean()
                n  = len(sub)
                net = sub['sim_pnl'].sum()
                results.append((wr, n, net, c, side, thresh))

    results.sort(reverse=True)
    print(f"\n{'FEATURE':<18} {'CUT':<4} {'THRESH':>8} {'WIN%':>6} {'N':>5} {'NET PNL':>10}")
    for wr, n, net, c, side, thresh in results[:20]:
        print(f"{c:<18} {side:<4} {thresh:>8.1f} {wr*100:>5.1f}% {n:>5} ${net:>9,.0f}")

    # ── 4. Per-source win rates ───────────────────────────────────────────────
    print()
    print("=" * 65)
    print("WIN RATE BY SOURCE")
    print("=" * 65)
    src_stats = df.groupby('source').agg(
        wins  = ('outcome', 'sum'),
        total = ('outcome', 'count'),
        net   = ('sim_pnl', 'sum'),
        avg_score = ('score', 'mean'),
    ).assign(win_pct = lambda x: x['wins']/x['total']*100)
    src_stats = src_stats.sort_values('win_pct', ascending=False)
    print(f"\n{'SOURCE':<22} {'W':>5} {'L':>5} {'WIN%':>6} {'NET':>10} {'AVG SCORE':>10}")
    for src, row in src_stats.iterrows():
        l = row['total'] - row['wins']
        print(f"{src:<22} {int(row['wins']):>5} {int(l):>5} "
              f"{row['win_pct']:>5.1f}% ${row['net']:>9,.0f} {row['avg_score']:>10.1f}")

    # ── 5. Score distribution by outcome ─────────────────────────────────────
    print()
    print("=" * 65)
    print("SCORE DISTRIBUTION BY OUTCOME")
    print("=" * 65)
    bins = list(range(50, 101, 5))
    df['score_bin'] = pd.cut(df['score'], bins=bins)
    score_dist = df.groupby('score_bin', observed=True).agg(
        wins  = ('outcome', 'sum'),
        total = ('outcome', 'count'),
        net   = ('sim_pnl', 'sum'),
    ).assign(win_pct = lambda x: x['wins']/x['total']*100)
    print(f"\n{'SCORE RANGE':<15} {'W':>5} {'L':>5} {'WIN%':>6} {'NET':>10}")
    for rng, row in score_dist.iterrows():
        l = row['total'] - row['wins']
        print(f"{str(rng):<15} {int(row['wins']):>5} {int(l):>5} "
              f"{row['win_pct']:>5.1f}% ${row['net']:>9,.0f}")

    # ── 6. H4 bias breakdown ─────────────────────────────────────────────────
    print()
    print("=" * 65)
    print("H4 BIAS BREAKDOWN  (snap_h4: >0=bull, <0=bear, 0=neutral)")
    print("=" * 65)
    df['h4_cat'] = df['snap_h4'].apply(
        lambda x: 'bull' if x > 0 else ('bear' if x < 0 else 'neutral'))
    df['aligned'] = df.apply(
        lambda r: (r['direction']=='Long' and r['h4_cat']=='bull') or
                  (r['direction']=='Short' and r['h4_cat']=='bear'), axis=1)
    for cat, sub in df.groupby('aligned'):
        lbl = 'H4 ALIGNED' if cat else 'H4 AGAINST'
        wr  = sub['outcome'].mean()*100
        net = sub['sim_pnl'].sum()
        print(f"  {lbl:<15}: {len(sub):>4} trades  win%={wr:.1f}%  net=${net:,.0f}")


if __name__ == '__main__':
    main()
