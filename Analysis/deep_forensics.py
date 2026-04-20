# deep_forensics.py - Detailed breakdown of each scoring root cause.
# A. Layer A dissection    - which H4/H2/H1 combinations cause failures
# B. Score_unexplained     - what IS the 57pt mystery score (Layer B S/R)?
# C. Confluence deep dive  - CumDelta threshold that separates W/L
# D. SMF_Impulse deep dive - BD/CD entry timing problem in detail
# E. Proposed gates        - simulate before/after applying the fixes
# Run:  python deep_forensics.py

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
_PNL_RE  = re.compile(r'SIM_PNL=([-\d.]+)')
_MFE_RE  = re.compile(r'MFE=([\d.]+)')
_MAE_RE  = re.compile(r'MAE=([\d.]+)')
_BTH_RE  = re.compile(r'BARS_TO_HIT=(\d+)')
_GNM_RE  = re.compile(r':(\d+)(?::REJ)?$')
_GATE_RE = re.compile(r'GATE=([^|]+)')
_SBULL_RE= re.compile(r'SNAP_SBULL=([-\d.]+)')
_SBEAR_RE= re.compile(r'SNAP_SBEAR=([-\d.]+)')

SNAP_COLS = ['bd','cd','abs','h1','h2','h4','reg','str','dex',
             'bdiv','berdiv','sw','trd','atr']


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

        snap   = _SNAP_RE.search(det)
        sv     = dict(zip(SNAP_COLS, [float(x) for x in snap.groups()])) \
                 if snap else {c: np.nan for c in SNAP_COLS}
        sbull  = _SBULL_RE.search(det)
        sbear  = _SBEAR_RE.search(det)
        gm2    = _GATE_RE.search(det)

        pm  = _PNL_RE.search(det); mfe = _MFE_RE.search(det)
        mae = _MAE_RE.search(det); bth = _BTH_RE.search(det)

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
            gate_reason = gm2[1].strip() if gm2 else '',
            timestamp   = s['time'],
            sbull       = float(sbull[1]) if sbull else np.nan,
            sbear       = float(sbear[1]) if sbear else np.nan,
        )
        row.update({f's_{c}': v for c, v in sv.items()})
        rows.append(row)

    return pd.DataFrame(rows)


def sep(title, char='=', width=72):
    print()
    print(char * width)
    print(f'  {title}')
    print(char * width)


def wr_net(sub):
    """Return (win_pct, net_pnl, n)"""
    if len(sub) == 0: return 0.0, 0.0, 0
    return sub['outcome'].mean()*100, sub['sim_pnl'].sum(), len(sub)


def print_bucket(label, sub, width=35):
    wr, net, n = wr_net(sub)
    bar = '#' * int(wr / 3)
    print(f"  {label:<{width}} n={n:>4}  win%={wr:>5.1f}%  net=${net:>9,.0f}  {bar}")


# ─────────────────────────────────────────────────────────────────────────────
def main():
    print("Loading data ...")
    df = load_df()
    df['d'] = df['direction'].map({'Long': 1, 'Short': -1}).fillna(0)

    # Direction-adjusted features
    df['cd_dir'] = df['s_cd'] * df['d']   # positive = CD in trade direction
    df['bd_dir'] = df['s_bd'] * df['d']   # positive = bar delta in trade dir
    df['h4_dir'] = df['s_h4'] * df['d']   # positive = H4 aligned
    df['h2_dir'] = df['s_h2'] * df['d']
    df['h1_dir'] = df['s_h1'] * df['d']

    # H4 alignment flags
    df['h4_has_data'] = df['s_h4'].notna() & (df['s_h4'] != 0)
    df['h4_aligned']  = df['h4_dir'] > 0
    df['h2_aligned']  = df['h2_dir'] > 0
    df['h1_aligned']  = df['h1_dir'] > 0

    n_total = len(df)
    print(f"  {n_total} trades  W={df['outcome'].sum()}  L={(df['outcome']==0).sum()}\n")

    # ═══════════════════════════════════════════════════════════════════
    # A. LAYER A DISSECTION
    # ═══════════════════════════════════════════════════════════════════
    sep("A. LAYER A DISSECTION — which H4/H2/H1 combo is the problem?")

    print("\n  First: how many trades even have H4/H2/H1 data?")
    for feat in ['s_h4','s_h2','s_h1']:
        has = df[feat].notna() & (df[feat] != 0)
        zero = (df[feat] == 0)
        nan  = df[feat].isna()
        print(f"    {feat}: has_data={has.sum():>4} ({has.mean()*100:.0f}%)  "
              f"zero={zero.sum():>4}  nan={nan.sum():>4}")

    print("\n  Win rate by H4 value (direction-adjusted: +1=aligned, -1=against):")
    for val in [-1.0, 0.0, 1.0, np.nan]:
        if pd.isna(val):
            sub = df[df['s_h4'].isna()]
            lbl = 'H4=NaN (missing)'
        else:
            sub = df[df['s_h4'] == val]
            lbl = f'H4={val:+.0f} ({"aligned" if val==1 else "against" if val==-1 else "neutral"})'
        print_bucket(lbl, sub)

    print("\n  Win rate for ALL 8 combinations of H4/H2/H1 alignment:")
    print(f"  {'H4':>5} {'H2':>5} {'H1':>5}  {'n':>5}  {'WIN%':>6}  {'NET':>10}  NOTE")
    combos = []
    for h4v in [-1.0, 1.0]:
        for h2v in [-1.0, 1.0]:
            for h1v in [-1.0, 1.0]:
                mask = (
                    (df['s_h4'].fillna(-99) == h4v) &
                    (df['s_h2'].fillna(-99) == h2v) &
                    (df['s_h1'].fillna(-99) == h1v)
                )
                sub = df[mask]
                if len(sub) < 3: continue
                wr, net, n = wr_net(sub)
                h4_lbl = 'AL' if (h4v * df[mask]['d'].mean() > 0) else 'AG'
                h2_lbl = 'AL' if (h2v * df[mask]['d'].mean() > 0) else 'AG'
                h1_lbl = 'AL' if (h1v * df[mask]['d'].mean() > 0) else 'AG'
                note = ''
                if wr < 38: note = '<-- PROBLEM'
                if wr > 50: note = '<-- GOOD'
                combos.append((wr, net, n, h4v, h2v, h1v, h4_lbl, h2_lbl, h1_lbl, note))
    for wr, net, n, h4v, h2v, h1v, h4l, h2l, h1l, note in sorted(combos, reverse=True):
        print(f"  H4={h4l} H2={h2l} H1={h1l}  n={n:>4}  win%={wr:>5.1f}%  "
              f"${net:>8,.0f}  {note}")

    print("\n  Key insight — trades with NO H4/H2/H1 data at all:")
    no_mtfa = df[df['s_h4'].isna() & df['s_h2'].isna() & df['s_h1'].isna()]
    print_bucket('No MTFA data', no_mtfa)
    has_mtfa = df[~(df['s_h4'].isna() & df['s_h2'].isna() & df['s_h1'].isna())]
    print_bucket('Has MTFA data', has_mtfa)

    # ═══════════════════════════════════════════════════════════════════
    # B. SCORE_UNEXPLAINED — what is Layer B actually doing?
    # ═══════════════════════════════════════════════════════════════════
    sep("B. SCORE COMPOSITION — Layer B (S/R) is carrying the whole score")

    # Reconstruct approximate layers
    def layer_a(r):
        h4 = (r['s_h4'] if not pd.isna(r['s_h4']) else 0) * r['d']
        h2 = (r['s_h2'] if not pd.isna(r['s_h2']) else 0) * r['d']
        h1 = (r['s_h1'] if not pd.isna(r['s_h1']) else 0) * r['d']
        s = 0
        if h4 > 0: s += 14
        if h2 > 0: s += 10
        if h1 > 0: s +=  6
        return s

    def layer_c(r):
        bd   = r['s_bd'] * r['d'] if not pd.isna(r['s_bd']) else 0
        cd   = r['s_cd'] * r['d'] if not pd.isna(r['s_cd']) else 0
        abso = r['s_abs'] if not pd.isna(r['s_abs']) else 0
        s = 0
        if bd   >    0: s += 7
        if cd   >    0: s += 6
        if abso >  5.0: s += 7
        return min(s, 30)

    def layer_d(r):
        sw = r['s_sw'] if not pd.isna(r['s_sw']) else 0
        if sw > 3500: return 12
        if sw > 2000: return  8
        return 0

    df['la'] = df.apply(layer_a, axis=1)
    df['lc'] = df.apply(layer_c, axis=1)
    df['ld'] = df.apply(layer_d, axis=1)
    df['layer_sum'] = df['la'] + df['lc'] + df['ld']
    df['layer_b_est'] = df['score'] - df['layer_sum']  # = S/R contribution

    print("\n  Average score decomposition:")
    for label, col in [('Score (total)', 'score'),
                       ('Layer A (MTFA)', 'la'),
                       ('Layer C (OrderFlow)', 'lc'),
                       ('Layer D (Structure)', 'ld'),
                       ('Layers A+C+D sum', 'layer_sum'),
                       ('Layer B est (S/R)', 'layer_b_est')]:
        w_avg = df[df['outcome']==1][col].mean()
        l_avg = df[df['outcome']==0][col].mean()
        diff  = w_avg - l_avg
        sign  = '+' if diff > 0 else '-'
        print(f"    {label:<25}  W={w_avg:>6.1f}  L={l_avg:>6.1f}  diff={diff:>+6.2f}  {sign}")

    print("\n  Layer B estimate (S/R score) buckets vs win rate:")
    df['lb_bin'] = pd.cut(df['layer_b_est'], bins=[0,40,50,55,60,65,70,100])
    for rng, sub in df.groupby('lb_bin', observed=True):
        wr, net, n = wr_net(sub)
        if n < 5: continue
        print_bucket(str(rng), sub, width=20)

    # ═══════════════════════════════════════════════════════════════════
    # C. CONFLUENCE DEEP DIVE
    # ═══════════════════════════════════════════════════════════════════
    sep("C. CONFLUENCE DEEP DIVE — CumDelta is the key separator")

    conf = df[df['source']=='Confluence'].copy()
    print(f"\n  Confluence: {len(conf)} trades  "
          f"W={conf['outcome'].sum()}  L={(conf['outcome']==0).sum()}\n")

    print("  CumDelta (s_cd) distribution by outcome:")
    for label, sub in [('Winners', conf[conf['outcome']==1]),
                        ('Losers',  conf[conf['outcome']==0])]:
        cd = sub['s_cd']
        print(f"    {label}: mean={cd.mean():>8.0f}  median={cd.median():>8.0f}  "
              f"p25={cd.quantile(.25):>8.0f}  p75={cd.quantile(.75):>8.0f}  "
              f"std={cd.std():>8.0f}")

    print("\n  Confluence win rate by CumDelta range:")
    cd_bins = [-9999, -500, -300, -100, 0, 100, 300, 500, 9999]
    cd_labels = ['<-500','-500:-300','-300:-100','-100:0','0:100','100:300','300:500','>500']
    for i in range(len(cd_bins)-1):
        sub = conf[(conf['s_cd'] >= cd_bins[i]) & (conf['s_cd'] < cd_bins[i+1])]
        if len(sub) < 3: continue
        print_bucket(f'CD {cd_labels[i]}', sub)

    print("\n  Confluence win rate by DIRECTION-adjusted CD (positive = momentum in trade dir):")
    cd_dir_bins = [-9999, -500, -200, -50, 0, 50, 200, 500, 9999]
    cd_dir_lbls = ['<-500','-500:-200','-200:-50','-50:0','0:50','50:200','200:500','>500']
    for i in range(len(cd_dir_bins)-1):
        sub = conf[(conf['cd_dir'] >= cd_dir_bins[i]) & (conf['cd_dir'] < cd_dir_bins[i+1])]
        if len(sub) < 3: continue
        print_bucket(f'CD_dir {cd_dir_lbls[i]}', sub)

    print("\n  Confluence by SHORT/LONG breakdown:")
    for d, sub in conf.groupby('direction'):
        wr, net, n = wr_net(sub)
        cd_mean = sub['s_cd'].mean()
        print(f"    {d:<8}: n={n:>4}  win%={wr:.1f}%  net=${net:,.0f}  "
              f"avg_CD={cd_mean:.0f}  avg_score={sub['score'].mean():.1f}")

    print("\n  Confluence SHORT detailed — where does it WIN?")
    cshort = conf[conf['direction']=='Short']
    print("  (Shorts need NEGATIVE CD = bearish momentum, or near-zero = mean reversion)")
    for label, mask in [
        ('CD < -300  (strong bear)', cshort['s_cd'] < -300),
        ('CD -300:-100',             (cshort['s_cd'] >= -300) & (cshort['s_cd'] < -100)),
        ('CD -100:+100 (near zero)', cshort['s_cd'].abs() < 100),
        ('CD > +100  (bull CD)',     cshort['s_cd'] > 100),
    ]:
        sub = cshort[mask]
        if len(sub) < 2: continue
        print_bucket(label, sub)

    print("\n  Confluence — BarDelta vs outcome for SHORTS:")
    for label, mask in [
        ('BD < -20  (strong bear bar)', cshort['s_bd'] < -20),
        ('BD -20:0',                    (cshort['s_bd'] >= -20) & (cshort['s_bd'] < 0)),
        ('BD 0:+20',                    (cshort['s_bd'] >= 0)   & (cshort['s_bd'] < 20)),
        ('BD > +20  (bull bar)',        cshort['s_bd'] >= 20),
    ]:
        sub = cshort[mask]
        if len(sub) < 2: continue
        print_bucket(label, sub)

    print("\n  Confluence — swing strength (s_sw) vs outcome:")
    sw_bins = [(0, 2000), (2000, 2500), (2500, 3000), (3000, 3500), (3500, 99999)]
    for lo, hi in sw_bins:
        sub = conf[(conf['s_sw'] >= lo) & (conf['s_sw'] < hi)]
        if len(sub) < 3: continue
        print_bucket(f'SW {lo}-{hi}', sub)

    # ═══════════════════════════════════════════════════════════════════
    # D. SMF_IMPULSE DEEP DIVE
    # ═══════════════════════════════════════════════════════════════════
    sep("D. SMF_IMPULSE DEEP DIVE — entering at momentum peak (too late)")

    imp = df[df['source']=='SMF_Impulse'].copy()
    print(f"\n  SMF_Impulse: {len(imp)} trades  "
          f"W={imp['outcome'].sum()}  L={(imp['outcome']==0).sum()}\n")

    print("  The core problem — BD and CD at entry:")
    for label, sub in [('Winners', imp[imp['outcome']==1]),
                        ('Losers',  imp[imp['outcome']==0])]:
        print(f"\n    {label} (n={len(sub)}):")
        print(f"      s_bd  mean={sub['s_bd'].mean():>8.1f}  "
              f"median={sub['s_bd'].median():>6.1f}  "
              f"p75={sub['s_bd'].quantile(.75):>6.1f}")
        print(f"      s_cd  mean={sub['s_cd'].mean():>8.1f}  "
              f"median={sub['s_cd'].median():>6.1f}  "
              f"p75={sub['s_cd'].quantile(.75):>6.1f}")
        print(f"      s_sw  mean={sub['s_sw'].mean():>8.1f}  "
              f"bars_to_hit={sub['bars_to_hit'].mean():>5.1f}")

    print("\n  SMF_Impulse win rate by direction-adjusted CumDelta:")
    cd_bins2  = [-9999,-300,-100,0,100,300,9999]
    cd_lbls2  = ['<-300','-300:-100','-100:0','0:+100','+100:+300','>+300']
    for i in range(len(cd_bins2)-1):
        sub = imp[(imp['cd_dir'] >= cd_bins2[i]) & (imp['cd_dir'] < cd_bins2[i+1])]
        if len(sub) < 3: continue
        print_bucket(f'CD_dir {cd_lbls2[i]}', sub)

    print("\n  SMF_Impulse win rate by direction-adjusted BarDelta:")
    bd_bins = [-9999,-30,-10,0,10,30,9999]
    bd_lbls = ['<-30','-30:-10','-10:0','0:+10','+10:+30','>+30']
    for i in range(len(bd_bins)-1):
        sub = imp[(imp['bd_dir'] >= bd_bins[i]) & (imp['bd_dir'] < bd_bins[i+1])]
        if len(sub) < 3: continue
        print_bucket(f'BD_dir {bd_lbls[i]}', sub)

    print("\n  SMF_Impulse by DIRECTION:")
    for d, sub in imp.groupby('direction'):
        wr, net, n = wr_net(sub)
        cd_m = sub['s_cd'].mean(); bd_m = sub['s_bd'].mean()
        print(f"    {d:<8}: n={n:>4}  win%={wr:.1f}%  net=${net:,.0f}  "
              f"avg_CD={cd_m:.0f}  avg_BD={bd_m:.1f}")

    print("\n  SMF_Impulse — stop distance vs outcome:")
    imp['stop_dist'] = abs(imp['entry'] - imp['stop'])
    for label, sub in [('Winners', imp[imp['outcome']==1]),
                        ('Losers',  imp[imp['outcome']==0])]:
        sd = sub['stop_dist']
        print(f"    {label}: avg_stop={sd.mean():.1f}  "
              f"median={sd.median():.1f}  min={sd.min():.1f}  max={sd.max():.1f}")

    # ═══════════════════════════════════════════════════════════════════
    # E. PROPOSED GATES — simulate the fixes
    # ═══════════════════════════════════════════════════════════════════
    sep("E. PROPOSED GATE SIMULATION — before vs after")

    print("""
  Proposed fixes to test:
    G1. Reduce H4/H2/H1 weights in Layer A  (make them smaller penalties, not big bonuses)
    G2. Confluence: require abs(s_cd) < 300 AND s_sw > 2500
    G3. SMF_Impulse: require cd_dir < 200 AND bd_dir < 20
    G4. Score floor per-source (not one global threshold)
""")

    baseline_wr, baseline_net, baseline_n = wr_net(df)
    print(f"  BASELINE:  n={baseline_n}  win%={baseline_wr:.1f}%  net=${baseline_net:,.0f}")
    print()

    # G2: Confluence gate
    conf_before_wr, conf_before_net, conf_before_n = wr_net(conf)
    g2_pass = conf[(conf['s_cd'].abs() < 300) & (conf['s_sw'] > 2500)]
    g2_fail = conf[~((conf['s_cd'].abs() < 300) & (conf['s_sw'] > 2500))]
    g2_wr, g2_net, g2_n = wr_net(g2_pass)
    print(f"  Confluence BEFORE gate: n={conf_before_n}  "
          f"win%={conf_before_wr:.1f}%  net=${conf_before_net:,.0f}")
    print(f"  Confluence PASS  gate:  n={g2_n}  win%={g2_wr:.1f}%  net=${g2_net:,.0f}")
    print(f"  Confluence FAIL  gate:  n={len(g2_fail)}  "
          f"win%={wr_net(g2_fail)[0]:.1f}%  net=${wr_net(g2_fail)[1]:,.0f}  (rejected)")

    print()
    # G3: SMF_Impulse gate
    imp_before_wr, imp_before_net, imp_before_n = wr_net(imp)
    g3_pass = imp[(imp['cd_dir'] < 200) & (imp['bd_dir'] < 20)]
    g3_fail = imp[~((imp['cd_dir'] < 200) & (imp['bd_dir'] < 20))]
    g3_wr, g3_net, g3_n = wr_net(g3_pass)
    print(f"  SMF_Impulse BEFORE gate: n={imp_before_n}  "
          f"win%={imp_before_wr:.1f}%  net=${imp_before_net:,.0f}")
    print(f"  SMF_Impulse PASS  gate:  n={g3_n}  win%={g3_wr:.1f}%  net=${g3_net:,.0f}")
    print(f"  SMF_Impulse FAIL  gate:  n={len(g3_fail)}  "
          f"win%={wr_net(g3_fail)[0]:.1f}%  net=${wr_net(g3_fail)[1]:,.0f}  (rejected)")

    print()
    # Combined: what does the full dataset look like after both gates?
    non_conf_imp = df[~df['source'].isin(['Confluence','SMF_Impulse'])]
    combined = pd.concat([g2_pass, g3_pass, non_conf_imp])
    combined_wr, combined_net, combined_n = wr_net(combined)
    print(f"  COMBINED AFTER GATES:  n={combined_n}  "
          f"win%={combined_wr:.1f}%  net=${combined_net:,.0f}")
    print(f"  Trades removed: {baseline_n - combined_n}  "
          f"PnL change: ${combined_net - baseline_net:+,.0f}")

    # ═══════════════════════════════════════════════════════════════════
    # F. WHAT DOES A REAL WINNER LOOK LIKE?
    # ═══════════════════════════════════════════════════════════════════
    sep("F. WINNER PROFILE — the features of a high-quality setup")

    top_winners = df[df['outcome']==1].nlargest(50, 'sim_pnl')
    top_losers  = df[df['outcome']==0].nsmallest(50, 'sim_pnl')

    feat_cols = ['score','s_cd','s_bd','s_sw','s_abs','s_h4','s_h2','s_h1',
                 'la','lc','ld','layer_b_est','bars_to_hit']

    print(f"\n  Profile of TOP 50 WINNERS (by PnL) vs TOP 50 LOSERS:")
    print(f"  {'FEATURE':<22} {'TOP WIN':>10} {'TOP LOSS':>10} {'DIFF':>10}")
    print(f"  {'-'*54}")
    diffs = []
    for c in feat_cols:
        wm = top_winners[c].mean()
        lm = top_losers[c].mean()
        diffs.append((abs(wm-lm), c, wm, lm, wm-lm))
    for _, c, wm, lm, d in sorted(diffs, reverse=True):
        print(f"  {c:<22} {wm:>10.2f} {lm:>10.2f} {d:>+10.2f}")

    print(f"""
  SUMMARY — what a real winner looks like:
    - s_sw  (swing strength)   : higher in winners = strong structure context
    - s_cd  (CumDelta)         : NEAR ZERO in winners = not chasing momentum
    - s_bd  (BarDelta)         : LOWER in winners = entry bar not at momentum peak
    - layer_b_est (S/R score)  : higher in winners = proper S/R location
    - bars_to_hit              : winners take LONGER to hit T1 = steady move

  The WORST setups:
    - High s_cd in trade direction = entering after big move (exhaustion)
    - High s_bd in trade direction = entering on momentum bar (late entry)
    - Partial MTFA alignment (layer_a=14-16) = conflicting timeframes

  Next step: implement per-signal CD and BD gates in StrategyConfig.cs
""")


if __name__ == '__main__':
    import io
    buf = io.StringIO()
    old_stdout = sys.stdout
    sys.stdout = buf
    try:
        main()
    finally:
        sys.stdout = old_stdout
        output = buf.getvalue()
        print(output)
        out_path = Path(__file__).parent / 'deep_results.txt'
        out_path.write_text(output, encoding='utf-8')
        print(f"\nResults saved to {out_path}")
