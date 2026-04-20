"""
score_forensics.py  —  Deep diagnostic: why is the score miscalibrated?

Investigates:
  1. Score non-monotonicity  — plots score bucket vs win rate
  2. Layer decomposition     — reconstructs Layer A/C/D from SNAP features,
                               compares each layer's contribution for W vs L
  3. H4 deep dive            — distribution of snap_h4 values, why alignment = 0 edge
  4. Confluence forensics    — what sub-signals fire when Confluence wins vs loses
  5. SMF_Impulse forensics   — timing, direction, H4 context for wins vs losses
  6. Score vs actual PnL     — is score at all predictive of magnitude?
  7. Weight calibration      — what weights WOULD make score predictive

Run:  python score_forensics.py
"""

import sys, re
import numpy as np
import pandas as pd
from pathlib import Path

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

LOG_PATH = Path(__file__).parent.parent / 'backtest' / 'Log.csv'

_SNAP_RE = re.compile(
    r'SNAP_BD=([-\d.]+).*?SNAP_CD=([-\d.]+).*?SNAP_ABS=([-\d.]+)'
    r'.*?SNAP_H1=([-\d.]+).*?SNAP_H2=([-\d.]+).*?SNAP_H4=([-\d.]+)'
    r'.*?SNAP_REG=([-\d.]+).*?SNAP_STR=([-\d.]+).*?SNAP_DEX=([-\d.]+)'
    r'.*?SNAP_BDIV=([-\d.]+).*?SNAP_BERDIV=([-\d.]+)'
    r'.*?SNAP_SW=([-\d.]+).*?SNAP_TRD=([-\d.]+).*?SNAP_ATR=([-\d.]+)'
)
_PNL_RE = re.compile(r'SIM_PNL=([-\d.]+)')
_MFE_RE = re.compile(r'MFE=([\d.]+)')
_MAE_RE = re.compile(r'MAE=([\d.]+)')
_BTH_RE = re.compile(r'BARS_TO_HIT=(\d+)')
_GNM_RE = re.compile(r':(\d+)(?::REJ)?$')
_GATE_RE = re.compile(r'GATE=([^|]+)')

SNAP_COLS = ['bd','cd','abs','h1','h2','h4','reg','str','dex','bdiv','berdiv','sw','trd','atr']


# ─────────────────────────────────────────────────────────────────────────────
def load_df():
    df = pd.read_csv(LOG_PATH, dtype=str)
    df.columns = [c.strip() for c in df.columns]
    df['Timestamp'] = pd.to_datetime(df['Timestamp'], errors='coerce')

    def _f(r, c, d=0.0):
        try: return float(str(r.get(c,'')).strip())
        except: return d

    sig_map = {}
    for _, r in df[df['Tag']=='SIGNAL_ACCEPTED'].iterrows():
        bar  = int(_f(r,'Bar'))
        cond = str(r.get('ConditionSetId','')).strip()
        sig_map[f"{cond}:{bar}"] = dict(
            time      = r['Timestamp'],
            direction = str(r.get('Direction','')).strip(),
            source    = str(r.get('Source','')).strip(),
            cond_set  = cond,
            score     = _f(r,'Score'),
            entry     = _f(r,'EntryPrice'),
            stop      = _f(r,'StopPrice'),
            t1        = _f(r,'T1Price'),
        )

    rows = []
    for _, r in df[df['Tag']=='TOUCH_OUTCOME'].iterrows():
        gate  = str(r.get('GateReason',''))
        if ':REJ' in gate: continue
        label   = str(r.get('Label','')).strip()
        outcome = 1 if label=='TARGET' else 0 if label=='STOP' else -1
        if outcome == -1: continue

        gm = _GNM_RE.search(gate)
        if not gm: continue
        src_bar  = int(gm[1])
        cond_key = gate.split(':')[0]
        key      = f"{cond_key}:{src_bar}"
        if key not in sig_map:
            key = next((k for k in sig_map if k.endswith(f':{src_bar}')), None)
        if not key or key not in sig_map: continue

        s   = sig_map[key]
        det = str(r.get('Detail',''))

        snap = _SNAP_RE.search(det)
        sv   = dict(zip(SNAP_COLS, [float(x) for x in snap.groups()])) \
               if snap else {c: np.nan for c in SNAP_COLS}

        pm  = _PNL_RE.search(det); mfe = _MFE_RE.search(det)
        mae = _MAE_RE.search(det); bth = _BTH_RE.search(det)
        gm2 = _GATE_RE.search(det)
        gate_reason = gm2[1].strip() if gm2 else ''

        row = dict(
            outcome     = outcome,
            source      = s['source'],
            direction   = s['direction'],
            score       = s['score'],
            entry       = s['entry'],
            stop        = s['stop'],
            t1          = s['t1'],
            sim_pnl     = float(pm[1])  if pm  else 0.0,
            mfe         = float(mfe[1]) if mfe else 0.0,
            mae         = float(mae[1]) if mae else 0.0,
            bars_to_hit = int(bth[1])   if bth else 0,
            gate_reason = gate_reason,
            timestamp   = s['time'],
        )
        row.update({f's_{c}': v for c, v in sv.items()})
        rows.append(row)

    return pd.DataFrame(rows)


# ─────────────────────────────────────────────────────────────────────────────
def sep(title):
    print()
    print('=' * 70)
    print(f'  {title}')
    print('=' * 70)


def compare_wl(df, cols, label=''):
    w = df[df['outcome']==1]
    l = df[df['outcome']==0]
    if label:
        print(f'\n  {label}  (W={len(w)}  L={len(l)})')
    print(f'  {"FEATURE":<22} {"WINNERS":>10} {"LOSERS":>10} {"DIFF":>10}')
    print(f'  {"-"*54}')
    diffs = []
    for c in cols:
        wm = w[c].mean(); lm = l[c].mean()
        diffs.append((abs(wm-lm), c, wm, lm, wm-lm))
    for _, c, wm, lm, d in sorted(diffs, reverse=True):
        print(f'  {c:<22} {wm:>10.3f} {lm:>10.3f} {d:>+10.3f}')


# ─────────────────────────────────────────────────────────────────────────────
def main():
    print("Loading data ...")
    df = load_df()
    W  = df[df['outcome']==1]
    L  = df[df['outcome']==0]
    snap_cols = [f's_{c}' for c in SNAP_COLS]
    print(f"  {len(df)} trades  W={len(W)}  L={len(L)}")

    # ── 1. Score non-monotonicity ─────────────────────────────────────────────
    sep("1. SCORE VS WIN RATE  — is score actually predictive?")
    df['score_bin'] = pd.cut(df['score'], bins=range(50,106,5))
    tbl = df.groupby('score_bin', observed=True).agg(
        W=('outcome','sum'), N=('outcome','count'), net=('sim_pnl','sum')
    ).assign(win_pct=lambda x: x['W']/x['N']*100)
    print(f"\n  {'SCORE':>12}  {'W':>5}  {'L':>5}  {'WIN%':>6}  {'NET':>10}  NOTE")
    prev_wr = None
    for rng, row in tbl.iterrows():
        L_cnt = row['N'] - row['W']
        note  = ''
        if prev_wr is not None:
            if row['win_pct'] < prev_wr - 5:
                note = '<-- DROPS (weight inflation?)'
            elif row['win_pct'] > prev_wr + 5:
                note = '<-- JUMPS'
        print(f"  {str(rng):>12}  {int(row['W']):>5}  {int(L_cnt):>5}  "
              f"{row['win_pct']:>5.1f}%  ${row['net']:>8,.0f}  {note}")
        if row['N'] >= 5:
            prev_wr = row['win_pct']

    # ── 2. Layer decomposition ────────────────────────────────────────────────
    sep("2. LAYER CONTRIBUTION  — which layer adds score to losers?")

    # Reconstruct approximate layer scores from SNAP features
    # Layer A: H4/H2/H1 alignment with direction
    def layer_a(row):
        d  = 1 if row['direction'] == 'Long' else -1
        h4 = row['s_h4'] * d   # positive = aligned
        h2 = row['s_h2'] * d
        h1 = row['s_h1'] * d
        # Using StrategyConfig weights: H4=14, H2=10, H1=6
        score = 0.0
        if h4 > 0: score += 14
        if h2 > 0: score += 10
        if h1 > 0: score += 6
        return score

    # Layer C: OrderFlow (BD, CD, ABS, BDIV, BERDIV)
    def layer_c(row):
        d     = 1 if row['direction'] == 'Long' else -1
        score = 0.0
        bd    = row['s_bd'] * d
        cd    = row['s_cd'] * d
        abso  = row['s_abs']
        bdiv  = row['s_bdiv'] * d
        berdiv= row['s_berdiv'] * d
        if bd  >  0: score += 7    # PROXY_BAR_DELTA
        if cd  >  0: score += 6    # PROXY_REGIME / CVD side
        if abso > 5: score += 7    # LAYER_C_ABS_MAX
        if bdiv > 0: score += 15   # LAYER_C_DIVERGENCE
        if berdiv>0: score += 8    # LAYER_C_DELTA_EXHST
        return min(score, 30)      # capped at 30

    # Layer D: Structure (SW, STR)
    def layer_d(row):
        sw  = row['s_sw']
        # Swing strength > 3000 roughly = full structure
        if sw > 3500: return 12    # LAYER_D_FULL_STRUCT
        if sw > 2000: return 8     # LAYER_D_TREND_ONLY
        return 0

    df['layer_a'] = df.apply(layer_a, axis=1)
    df['layer_c'] = df.apply(layer_c, axis=1)
    df['layer_d'] = df.apply(layer_d, axis=1)
    df['layers_sum'] = df['layer_a'] + df['layer_c'] + df['layer_d']
    df['score_unexplained'] = df['score'] - df['layers_sum']

    compare_wl(df, ['layer_a','layer_c','layer_d','layers_sum','score_unexplained','score'])

    print("\n  KEY QUESTION: does layers_sum predict outcome better than score?")
    from sklearn.metrics import roc_auc_score
    valid = df.dropna(subset=['layer_a','layer_c','layer_d','score'])
    for col in ['score','layers_sum','layer_a','layer_c','layer_d']:
        try:
            auc = roc_auc_score(valid['outcome'], valid[col])
            print(f"    AUC({col:<22}): {auc:.3f}")
        except Exception as e:
            print(f"    AUC({col}): error {e}")

    # ── 3. H4 deep dive ───────────────────────────────────────────────────────
    sep("3. H4 DEEP DIVE  — why does H4 alignment give zero edge?")

    print("\n  Distribution of snap_h4 values:")
    h4_dist = df['s_h4'].value_counts().sort_index()
    for val, cnt in h4_dist.items():
        pct = cnt/len(df)*100
        print(f"    snap_h4={val:+.1f}  count={cnt:>4}  ({pct:.1f}%)")

    print("\n  H4 alignment breakdown by direction:")
    df['h4_aligned'] = df.apply(
        lambda r: (r['direction']=='Long' and r['s_h4']>0) or
                  (r['direction']=='Short' and r['s_h4']<0), axis=1)
    df['h4_neutral'] = df['s_h4'] == 0

    for cat, mask in [
        ('H4 Aligned (trade WITH H4)',  df['h4_aligned'] & ~df['h4_neutral']),
        ('H4 Against (trade vs H4)',    ~df['h4_aligned'] & ~df['h4_neutral']),
        ('H4 Neutral (H4=0)',           df['h4_neutral']),
    ]:
        sub = df[mask]
        wr  = sub['outcome'].mean()*100
        net = sub['sim_pnl'].sum()
        print(f"    {cat:<35}: n={len(sub):>4}  win%={wr:.1f}%  net=${net:,.0f}")

    print("\n  H4=0 breakdown — what direction is the trade?")
    h4_zero = df[df['s_h4']==0]
    for d, sub in h4_zero.groupby('direction'):
        wr = sub['outcome'].mean()*100
        print(f"    H4=0 + {d:<8}: n={len(sub):>4}  win%={wr:.1f}%  "
              f"avg_score={sub['score'].mean():.1f}")

    print("\n  H4 layer score added vs outcome:")
    for la_val in sorted(df['layer_a'].unique()):
        sub = df[df['layer_a']==la_val]
        if len(sub) < 10: continue
        wr  = sub['outcome'].mean()*100
        net = sub['sim_pnl'].sum()
        print(f"    layer_a={la_val:.0f}  n={len(sub):>4}  win%={wr:.1f}%  net=${net:,.0f}")

    # ── 4. Confluence forensics ────────────────────────────────────────────────
    sep("4. CONFLUENCE FORENSICS  — why 38% win rate?")
    conf = df[df['source']=='Confluence']
    cW   = conf[conf['outcome']==1]
    cL   = conf[conf['outcome']==0]
    print(f"\n  Confluence: {len(conf)} trades  W={len(cW)}  L={len(cL)}  "
          f"win%={len(cW)/max(len(conf),1)*100:.1f}%")

    compare_wl(conf, snap_cols + ['score','layer_a','layer_c','layer_d'],
               'Confluence W vs L')

    print("\n  Confluence — H4 alignment breakdown:")
    for cat, mask in [
        ('H4 Aligned', conf['h4_aligned'] & ~conf['h4_neutral']),
        ('H4 Against', ~conf['h4_aligned'] & ~conf['h4_neutral']),
        ('H4 Neutral', conf['h4_neutral']),
    ]:
        sub = conf[mask]
        if len(sub) == 0: continue
        wr  = sub['outcome'].mean()*100
        print(f"    {cat:<20}: n={len(sub):>4}  win%={wr:.1f}%  "
              f"avg_score={sub['score'].mean():.1f}")

    print("\n  Confluence — gate reasons on LOSSES (what score criteria was close?):")
    loss_gates = cL['gate_reason'].value_counts().head(10)
    for gate, cnt in loss_gates.items():
        print(f"    {cnt:>4}x  {gate[:60]}")

    print("\n  Confluence — direction breakdown:")
    for d, sub in conf.groupby('direction'):
        wr = sub['outcome'].mean()*100
        net = sub['sim_pnl'].sum()
        print(f"    {d:<10}: n={len(sub):>4}  win%={wr:.1f}%  net=${net:,.0f}  "
              f"avg_score={sub['score'].mean():.1f}")

    # ── 5. SMF_Impulse forensics ───────────────────────────────────────────────
    sep("5. SMF_IMPULSE FORENSICS  — why 34.8% win rate?")
    imp = df[df['source']=='SMF_Impulse']
    iW  = imp[imp['outcome']==1]
    iL  = imp[imp['outcome']==0]
    print(f"\n  SMF_Impulse: {len(imp)} trades  W={len(iW)}  L={len(iL)}  "
          f"win%={len(iW)/max(len(imp),1)*100:.1f}%")

    compare_wl(imp, snap_cols + ['score','layer_a','layer_c','layer_d','bars_to_hit'],
               'SMF_Impulse W vs L')

    print("\n  SMF_Impulse — H4 alignment breakdown:")
    for cat, mask in [
        ('H4 Aligned', imp['h4_aligned'] & ~imp['h4_neutral']),
        ('H4 Against', ~imp['h4_aligned'] & ~imp['h4_neutral']),
        ('H4 Neutral', imp['h4_neutral']),
    ]:
        sub = imp[mask]
        if len(sub) == 0: continue
        wr  = sub['outcome'].mean()*100
        net = sub['sim_pnl'].sum()
        print(f"    {cat:<20}: n={len(sub):>4}  win%={wr:.1f}%  net=${net:,.0f}")

    print("\n  SMF_Impulse — bars_to_hit distribution (winners vs losers):")
    for outcome_val, label in [(1,'WIN'),(0,'LOSS')]:
        sub = imp[imp['outcome']==outcome_val]
        bth = sub['bars_to_hit']
        print(f"    {label}: mean={bth.mean():.1f}  median={bth.median():.0f}  "
              f"p25={bth.quantile(.25):.0f}  p75={bth.quantile(.75):.0f}")

    print("\n  SMF_Impulse — entry vs T1 distance (RR ratio):")
    for outcome_val, label in [(1,'WIN'),(0,'LOSS')]:
        sub = imp[imp['outcome']==outcome_val]
        sub = sub[(sub['t1']>0) & (sub['stop']>0)]
        if sub.empty: continue
        rr = abs(sub['t1'] - sub['entry']) / abs(sub['entry'] - sub['stop'])
        print(f"    {label}: avg_RR={rr.mean():.2f}  "
              f"avg_entry={sub['entry'].mean():.2f}  "
              f"avg_stop_dist={abs(sub['entry']-sub['stop']).mean():.1f}")

    # ── 6. Score vs actual magnitude ──────────────────────────────────────────
    sep("6. SCORE VS PNL MAGNITUDE  — does high score = bigger wins?")
    df['score_q'] = pd.qcut(df['score'], q=5, labels=['Q1','Q2','Q3','Q4','Q5'],
                             duplicates='drop')
    tbl2 = df.groupby('score_q', observed=True).agg(
        win_pct = ('outcome', 'mean'),
        avg_pnl = ('sim_pnl', 'mean'),
        avg_mfe = ('mfe', 'mean'),
        avg_mae = ('mae', 'mean'),
        n       = ('outcome', 'count'),
    )
    print(f"\n  {'QUINTILE':<10} {'WIN%':>6} {'AVG PNL':>10} {'AVG MFE':>10} "
          f"{'AVG MAE':>10} {'N':>5}")
    for q, row in tbl2.iterrows():
        print(f"  {str(q):<10} {row['win_pct']*100:>5.1f}% "
              f"${row['avg_pnl']:>8.0f}  {row['avg_mfe']:>10.0f}  "
              f"{row['avg_mae']:>10.0f}  {int(row['n']):>5}")

    # ── 7. Weight calibration hypothesis ─────────────────────────────────────
    sep("7. WEIGHT CALIBRATION HYPOTHESIS")
    print("""
  Current problem pattern:
    - Score 65-80 underperforms 55-65      → MIDDLE BAND is INFLATED
    - H4 alignment adds 0 edge             → LAYER_A weights are adding noise
    - Confluence 38% despite high scores   → LAYER_B (S/R) may be dominating
                                              and overriding bad setups
    - SMF_Impulse 35% despite structure    → LAYER_D may fire on weak structure

  What to investigate next:
    A. Do winners have DIFFERENT LAYERS contributing than losers?
       (i.e., a win from layer_c conviction vs a win from layer_a bias?)
    B. Are there phantom score sources? (B layer S/R bonuses stacking
       without real conviction in layer C?)
    C. Is the score FLOOR too low? (min_net=40 lets in too many weak trades)
    D. For Confluence: does it fire when ALL layers are weak but barely pass,
       rather than when at least ONE layer is strong?
    E. For SMF_Impulse: is it firing AFTER the impulse (entry too late)?
""")

    # Correlation matrix of layers vs outcome
    print("  Layer correlation with outcome:")
    for col in ['score','layer_a','layer_c','layer_d','layers_sum',
                's_sw','s_cd','s_bd','s_abs']:
        corr = df[col].corr(df['outcome'])
        bar  = '#' * int(abs(corr) * 100)
        sign = '+' if corr >= 0 else '-'
        print(f"    {col:<22}: {corr:>+.4f}  {sign}{bar}")


if __name__ == '__main__':
    main()
