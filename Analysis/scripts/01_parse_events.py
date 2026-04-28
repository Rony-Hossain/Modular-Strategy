import pandas as pd
import numpy as np
import re
from pathlib import Path
import sys

# Set seed for reproducibility
np.random.seed(42)

# Instrument constants per schema §4
INSTRUMENTS = {
    'MNQ': {'tick_size': 0.25, 'tick_value': 0.50, 'point_value': 2.00},
    'NQ':  {'tick_size': 0.25, 'tick_value': 5.00, 'point_value': 20.00},
    'ES':  {'tick_size': 0.25, 'tick_value': 12.50, 'point_value': 50.00},
    'MES': {'tick_size': 0.25, 'tick_value': 1.25, 'point_value': 5.00},
}

def parse_kv(s):
    """Parses space-separated K=V pairs into a dict, handling both numeric and string values."""
    if pd.isna(s) or s == "": return {}
    res = {}
    # Handle the '|' separator by replacing it with space for KVP extraction
    # and replace comma with space
    s_clean = s.replace('|', ' ').replace(',', ' ')
    for match in re.finditer(r'([A-Za-z0-9_]+)=([^ |]+)', s_clean):
        k, v = match.groups()
        try:
            if '.' in v:
                res[k] = float(v)
            else:
                res[k] = int(v)
        except ValueError:
            res[k] = v
    return res

def parse_context(detail):
    """Parses context string: h4=[+|-|0] h2=[+|-|0] h1=[+|-|0] smf=[bull|bear|neutral|flat] str=<f> sw=<i>"""
    if pd.isna(detail) or detail == "": return {}
    res = {}
    for h in ['h4', 'h2', 'h1']:
        m = re.search(fr'{h}=([+\-0])', detail)
        res[f'ctx_{h}'] = m.group(1) if m else np.nan
    m = re.search(r'smf=(bull|bear|neutral|flat)', detail)
    res['ctx_smf'] = m.group(1) if m else np.nan
    m = re.search(r'str=([-+]?[\d\.]+)', detail)
    res['ctx_str'] = float(m.group(1)) if m else np.nan
    m = re.search(r'sw=(\d+)', detail)
    res['ctx_sw'] = int(m.group(1)) if m else np.nan
    return res

def normalize_trade_id(signal_id):
    if not isinstance(signal_id, str): return signal_id
    # Remove _HHMM part: regex replace _\d{4} → ""
    return re.sub(r'_\d{4}', '', signal_id)

def main():
    repo_root = Path(".")
    log_path = repo_root / "backtest/Log.csv"
    output_dir = repo_root / "Analysis/artifacts"
    output_dir.mkdir(parents=True, exist_ok=True)

    # [INPUT]
    print("[INPUT] Loading Log.csv...")
    if not log_path.exists():
        print(f"ERROR: {log_path} not found.")
        sys.exit(1)
        
    df = pd.read_csv(log_path, low_memory=False)
    print(f"  Loaded Log.csv: {len(df)} rows")
    
    tag_counts = df['Tag'].value_counts()
    print("  Tag breakdown:")
    for tag, count in tag_counts.items():
        print(f"    {tag:<20}: {count}")

    # [PARSE]
    print("[PARSE] Cleaning timestamps and parsing details...")
    df['Timestamp'] = pd.to_datetime(df['Timestamp'], utc=True, errors='raise')
    
    # Carry forward last valid timestamp for 0001-01-01 rows
    is_invalid_ts = df['Timestamp'].dt.year == 1
    df.loc[is_invalid_ts, 'Timestamp'] = pd.NaT
    df['Timestamp'] = df['Timestamp'].ffill()
    
    if df['Timestamp'].isna().any():
        drop_count = df['Timestamp'].isna().sum()
        print(f"  Dropping {drop_count} rows with no valid leading timestamp")
        df = df.dropna(subset=['Timestamp'])

    # Helper to generate signal_id
    def make_signal_id(row):
        ts_str = row['Timestamp'].strftime('%Y%m%d_%H%M')
        base = f"{row['ConditionSetId']}:{ts_str}:{row['Bar']}"
        if row['Tag'] == 'SIGNAL_REJECTED':
            return f"{base}:REJ"
        return base

    # Normalize external signal IDs (from Label columns)
    def normalize_signal_id(sid):
        if not isinstance(sid, str): return sid
        # Handle formats like CSID:YYYYMMDD:BAR:REJ or CSID:YYYYMMDD_HHMM:BAR
        # Convert all to CSID:YYYYMMDD:BAR[:REJ] (Drop HHMM for max compatibility)
        parts = sid.split(':')
        if len(parts) >= 3:
            csid = parts[0]
            date_part = parts[1].split('_')[0] # Drop _HHMM if present
            bar = parts[2]
            suffix = ":REJ" if (len(parts) > 3 and 'REJ' in parts[3]) or ('REJ' in parts[1]) else ""
            return f"{csid}:{date_part}:{bar}{suffix}"
        return sid

    # TABLE A: signals
    print("  Processing Table A: signals.parquet")
    sig_tags = ['SIGNAL_ACCEPTED', 'SIGNAL_REJECTED']
    signals_df = df[df['Tag'].isin(sig_tags)].copy()
    signals_df['signal_id_raw'] = signals_df.apply(make_signal_id, axis=1)
    signals_df['signal_id'] = signals_df['signal_id_raw'].apply(normalize_signal_id)
    signals_df['trade_id'] = signals_df['signal_id'].apply(lambda x: x.split(':REJ')[0])
    signals_df['traded'] = signals_df['Tag'] == 'SIGNAL_ACCEPTED'
    
    ctx_parsed = signals_df['Detail'].apply(parse_context).apply(pd.Series)
    signals_df = pd.concat([signals_df, ctx_parsed], axis=1)
    
    def parse_gate(reason):
        if pd.isna(reason) or reason == "": return pd.Series({'gate_id': np.nan, 'gate_name': np.nan, 'gate_expression': np.nan})
        m = re.match(r'G([\d\.]+):([^\(]+)\((.*)\)', reason)
        if m:
            return pd.Series({'gate_id': m.group(1), 'gate_name': m.group(2), 'gate_expression': m.group(3)})
        return pd.Series({'gate_id': np.nan, 'gate_name': np.nan, 'gate_expression': np.nan})
    
    gate_df = signals_df['GateReason'].apply(parse_gate)
    signals_df = pd.concat([signals_df, gate_df], axis=1)
    
    cols_a = ['signal_id', 'trade_id', 'Timestamp', 'Bar', 'Direction', 'Source', 'ConditionSetId', 
              'Score', 'Grade', 'Contracts', 'EntryPrice', 'StopPrice', 'StopTicks', 
              'T1Price', 'T2Price', 'RRRatio', 'traded', 'Label', 
              'ctx_h4', 'ctx_h2', 'ctx_h1', 'ctx_smf', 'ctx_str', 'ctx_sw',
              'gate_id', 'gate_name', 'gate_expression']
    signals_df = signals_df.rename(columns={c: c.lower() for c in signals_df.columns if c in ['Timestamp', 'Bar', 'Direction', 'Source', 'ConditionSetId', 'Score', 'Grade', 'Contracts', 'EntryPrice', 'StopPrice', 'StopTicks', 'T1Price', 'T2Price', 'RRRatio', 'Label']})
    cols_a = [c.lower() if c in ['Timestamp', 'Bar', 'Direction', 'Source', 'ConditionSetId', 'Score', 'Grade', 'Contracts', 'EntryPrice', 'StopPrice', 'StopTicks', 'T1Price', 'T2Price', 'RRRatio', 'Label'] else c for c in cols_a]
    signals_df = signals_df[cols_a]

    # TABLE B: signal_touch
    print("  Processing Table B: signal_touch.parquet")
    touch_df = df[df['Tag'] == 'TOUCH'].copy()
    touch_feats = touch_df['Detail'].apply(parse_kv).apply(pd.Series)
    touch_df = pd.concat([touch_df, touch_feats], axis=1)
    touch_rename = {
        'Timestamp': 'touch_timestamp',
        'ZONE_TYPE': 'zone_type', 'ZONE_LO': 'zone_lo', 'ZONE_HI': 'zone_hi',
        'TRD': 'touch_trd', 'BOS_L': 'bos_l',
        'FVG_BL': 'fvg_bl', 'FVG_BL_LO': 'fvg_bl_lo', 'FVG_BL_HI': 'fvg_bl_hi',
        'BD': 'touch_bd', 'CD': 'touch_cd', 'ABS': 'touch_abs', 'SBULL': 'touch_sbull',
        'POC': 'touch_poc', 'VAH': 'touch_vah',
        'H4B': 'h4b', 'BDIV': 'touch_bdiv', 'REGIME': 'touch_regime', 'ATR': 'atr', 'HASVOL': 'touch_hasvol'
    }
    touch_df = touch_df.rename(columns=touch_rename)
    signal_touch = signals_df.merge(
        touch_df[['Bar', 'Source', 'ConditionSetId'] + [c for c in touch_rename.values() if c in touch_df.columns]],
        left_on=['bar', 'source', 'conditionsetid'],
        right_on=['Bar', 'Source', 'ConditionSetId'],
        how='left'
    )
    cols_b = ['signal_id', 'trade_id'] + [c for c in touch_rename.values() if c in signal_touch.columns]
    signal_touch_final = signal_touch[cols_b]

    # TABLE C: outcomes
    print("  Processing Table C: outcomes.parquet (handling column shift)")
    outcome_raw = df[df['Tag'] == 'TOUCH_OUTCOME'].copy()
    
    def parse_outcome_row(row):
        # Handle logger shift: if Label is 'STOP' or 'TARGET', then GateReason holds the real Label
        label_val = str(row['Label'])
        gate_val = str(row['GateReason'])
        detail_val = str(row['Detail'])
        
        if label_val in ['STOP', 'TARGET', 'NONE']:
            # Shifted: Real Label is in GateReason, Detail is Label, Detail KVPs are in a virtual column or appended
            # pd.read_csv usually puts the overflow into 'Detail' or creates unnamed columns.
            # But based on our check, it was: GateReason=SignalID, Label=STOP, Detail=MFE=...
            real_sid = gate_val
            first_hit = label_val
            kvp_str = detail_val
        else:
            real_sid = label_val
            first_hit = detail_val.split(',')[0] if ',' in detail_val else 'UNKNOWN'
            kvp_str = detail_val
            
        res = parse_kv(kvp_str)
        res['signal_id'] = normalize_signal_id(real_sid)
        res['first_hit'] = first_hit
        res['outcome_timestamp'] = row['Timestamp']
        res['outcome_bar'] = row['Bar']
        return pd.Series(res)

    outcome_df = outcome_raw.apply(parse_outcome_row, axis=1)
    outcome_df['trade_id'] = outcome_df['signal_id'].apply(lambda x: x.split(':REJ')[0] if isinstance(x, str) else x)
    
    # Ensure all required columns exist
    for c in ['sim_pnl', 'mfe', 'mae', 'hit_stop', 'hit_target', 'bars_to_hit', 'close_end', 'window_bars']:
        if c.upper() in outcome_df.columns: outcome_df[c] = outcome_df[c.upper()]
        if c not in outcome_df.columns: outcome_df[c] = np.nan
        
    cols_c = ['signal_id', 'trade_id', 'outcome_timestamp', 'outcome_bar', 'sim_pnl', 'mfe', 'mae', 'hit_stop', 'hit_target', 'first_hit', 'bars_to_hit', 'close_end', 'window_bars']
    outcome_final = outcome_df[cols_c]

    # TABLE D/E: Context/Forward Bars (v5.2 fix)
    print("  Processing Tables D & E: context/forward bars (v5.2 delimiter='|')")
    def parse_bars_v52(detail):
        if pd.isna(detail) or detail == "": return []
        segments = detail.split('|') # v5.2 FIX: No spaces
        return [parse_kv(seg) for seg in segments]

    # D: context_bars
    ctx_bars_raw = df[df['Tag'] == 'BAR_CONTEXT'].copy()
    ctx_data = []
    for _, row in ctx_bars_raw.iterrows():
        bars = parse_bars_v52(row['Detail'])
        matched_sigs = signals_df[signals_df['bar'] == row['Bar']]
        for _, sig in matched_sigs.iterrows():
            for i, b in enumerate(bars):
                offset = -5 + i
                ctx_data.append({
                    'signal_id': sig['signal_id'], 'trade_id': sig['trade_id'], 'bar_offset': offset,
                    'open': b.get('O'), 'high': b.get('H'), 'low': b.get('L'), 'close': b.get('C'), 'volume': b.get('V'), 'delta': b.get('D')
                })
    ctx_bars_final = pd.DataFrame(ctx_data)

    # E: forward_bars
    fwd_bars_raw = df[df['Tag'] == 'BAR_FORWARD'].copy()
    fwd_data = []
    for _, row in fwd_bars_raw.iterrows():
        bars = parse_bars_v52(row['Detail'])
        # v5.2 FIX: BAR_FORWARD Bar is 5 bars AFTER signal Bar
        matched_sigs = signals_df[signals_df['bar'] == row['Bar'] - 5]
        # Also match on ConditionSetId if possible (row['Source'] contains it)
        if not matched_sigs.empty:
            cid = str(row['Source']).split(':')[0]
            matched_sigs = matched_sigs[matched_sigs['conditionsetid'] == cid]
            
        for _, sig in matched_sigs.iterrows():
            for i, b in enumerate(bars):
                offset = 1 + i
                fwd_data.append({
                    'signal_id': sig['signal_id'], 'trade_id': sig['trade_id'], 'bar_offset': offset,
                    'bar_timestamp': b.get('T'), # ISO timestamp in Forward Bars
                    'open': b.get('O'), 'high': b.get('H'), 'low': b.get('L'), 'close': b.get('C'), 'volume': b.get('V'), 'delta': b.get('D')
                })
    fwd_bars_final = pd.DataFrame(fwd_data)

    # TABLE F: flow_bars
    flow_raw = df[df['Tag'] == 'FLOW'].copy()
    flow_feats = flow_raw['Detail'].apply(parse_kv).apply(pd.Series)
    flow_final = pd.concat([flow_raw[['Timestamp', 'Bar']], flow_feats], axis=1).rename(columns={
        'Timestamp': 'timestamp', 'Bar': 'bar', 'REGIME': 'regime', 'STR': 'str_val',
        'BD': 'bd', 'CD': 'cd', 'DSL': 'dsl', 'DSH': 'dsh', 'DEX': 'dex', 'BDIV': 'bdiv', 'BERDIV': 'berdiv',
        'ABS': 'abs_score', 'SBULL': 'sbull', 'SBEAR': 'sbear', 'IZB': 'izb', 'IZS': 'izs', 'HASVOL': 'hasvol',
        'SW': 'sw_val', 'TRD': 'trd', 'CH_L': 'ch_l', 'CH_S': 'ch_s', 'BOS_L': 'bos_l', 'BOS_S': 'bos_s'
    })

    # TABLE G: struct_bars
    struct_raw = df[df['Tag'] == 'STRUCT'].copy()
    def parse_struct_v52(detail):
        if pd.isna(detail) or detail == "": return {}
        res = {}
        parts = [p.strip() for p in detail.split('|')]
        p1 = parse_kv(parts[0])
        for k in ['POC', 'VAH', 'VAL']:
            val = p1.get(k, 0.0)
            res[k.lower()] = np.nan if val == 0.0 else val
        if len(parts) > 1:
            for side in ['SUP', 'RES']:
                m = re.search(fr'NEAR_{side}=([\d\.]+)\(([\d\.-]+)t\)', parts[1])
                if m:
                    res[f'near_{side.lower()}_price'] = float(m.group(1))
                    res[f'near_{side.lower()}_ticks'] = float(m.group(2))
        if len(parts) > 2:
            for h in ['H4', 'H1']:
                m = re.search(fr'{h}=([\d\.]+)/([\d\.]+)', parts[2])
                if m:
                    res[f'{h.lower()}_high'] = float(m.group(1))
                    res[f'{h.lower()}_low'] = float(m.group(2))
        if len(parts) > 3:
            p4 = parse_kv(parts[3])
            res['pp'] = p4.get('PP')
            res['skew'] = p4.get('SKEW')
        return res
    struct_feats = struct_raw['Detail'].apply(parse_struct_v52).apply(pd.Series)
    struct_final = pd.concat([struct_raw[['Timestamp', 'Bar']], struct_feats], axis=1).rename(columns={'Timestamp': 'timestamp', 'Bar': 'bar'})

    # TABLE H: evals
    eval_raw = df[df['Tag'] == 'EVAL'].copy()
    eval_raw['eval_id'] = eval_raw.apply(lambda r: f"{r['ConditionSetId']}:{r['Timestamp'].strftime('%Y%m%d_%H%M')}:{r['Bar']}", axis=1)
    eval_ctx = eval_raw['Detail'].apply(parse_context).apply(pd.Series)
    eval_final = pd.concat([eval_raw, eval_ctx], axis=1).rename(columns={c: c.lower() for c in eval_raw.columns if c in ['Timestamp', 'Bar', 'Direction', 'Source', 'ConditionSetId', 'Score', 'EntryPrice', 'StopPrice', 'T1Price', 'T2Price', 'Label']})
    cols_h = ['eval_id', 'timestamp', 'bar', 'direction', 'source', 'conditionsetid', 'score', 'entryprice', 'stopprice', 't1price', 't2price', 'label', 'ctx_h4', 'ctx_h2', 'ctx_h1', 'ctx_smf', 'ctx_str', 'ctx_sw']
    eval_final = eval_final[cols_h]

    # TABLE I: rank_scores (Renamed/Split in v5.2, but keep for legacy if needed. We'll split into rank_win/rank_veto as requested)
    # v5.2: Separate rank_win.parquet and rank_veto.parquet. Also keep rank_scores.parquet (RANK_WEAK).
    print("  Processing rank parquets (WEAK, WIN, VETO)")
    warn_raw = df[df['Tag'] == 'WARN'].copy()
    
    # rank_scores.parquet (RANK_WEAK legacy)
    weak_raw = warn_raw[warn_raw['Detail'].str.startswith('RANK_WEAK', na=False)].copy()
    def parse_rank_weak(detail):
        m = re.search(r'RANK_WEAK \[([^\]]+)\] (.*)', detail)
        if not m: return {}
        cid = m.group(1)
        kv = parse_kv(m.group(2))
        return {'condition_set_id': cid, 'layer_a': kv.get('A'), 'layer_b': kv.get('B'), 'layer_c': kv.get('C'), 'layer_d': kv.get('D'), 'bonus': kv.get('Bon', 0), 'penalty': kv.get('Pen'), 'net_score': kv.get('Net'), 'mult': kv.get('Mult')}
    weak_feats = weak_raw['Detail'].apply(parse_rank_weak).apply(pd.Series)
    rank_weak_final = pd.concat([weak_raw[['Timestamp', 'Bar']], weak_feats], axis=1)
    rank_scores_final = eval_final.merge(rank_weak_final.drop(columns=['Bar']), left_on=['timestamp', 'conditionsetid'], right_on=['Timestamp', 'condition_set_id'], how='inner')
    rank_scores_final = rank_scores_final[['eval_id', 'timestamp', 'bar', 'conditionsetid', 'layer_a', 'layer_b', 'layer_c', 'layer_d', 'bonus', 'penalty', 'net_score', 'mult']]

    # rank_win
    win_raw = warn_raw[warn_raw['Detail'].str.startswith('RANK_WIN', na=False)].copy()
    def parse_rank_win(detail):
        # RANK_WIN [VWAP_RTH_v1] Raw=59 A=14 B=0 C=16 D=12 Pen=0 Net=42 Mult=0.92 | A:h4+ B:none C:bd+vwap+bp+vel+vel-tice+ D:choch Final=54.3
        m = re.search(r'RANK_WIN \[([^\]]+)\] (.*?) \| (.*)', detail)
        if not m: return {}
        cid = m.group(1)
        kv = parse_kv(m.group(2))
        tokens = m.group(3)
        res = {'condition_set_id': cid, 'raw_score': kv.get('Raw'), 'layer_a': kv.get('A'), 'layer_b': kv.get('B'), 'layer_c': kv.get('C'), 'layer_d': kv.get('D'), 'bonus': kv.get('Bon', 0), 'penalty': kv.get('Pen'), 'net_score': kv.get('Net'), 'mult': kv.get('Mult'), 'final_score': kv.get('Final')}
        for t in ['A', 'B', 'C', 'D']:
            tm = re.search(fr'{t}:([^ ]+)', tokens)
            res[f'{t.lower()}_tokens'] = tm.group(1) if tm else np.nan
        return res
    win_feats = win_raw['Detail'].apply(parse_rank_win).apply(pd.Series)
    rank_win_final = pd.concat([win_raw[['Timestamp', 'Bar']], win_feats], axis=1)
    # Join with EVAL to get eval_id
    rank_win_final = eval_final.merge(rank_win_final.drop(columns=['Bar']), left_on=['timestamp', 'conditionsetid'], right_on=['Timestamp', 'condition_set_id'], how='inner')
    rank_win_final = rank_win_final[['eval_id', 'timestamp', 'bar', 'conditionsetid', 'raw_score', 'layer_a', 'layer_b', 'layer_c', 'layer_d', 'bonus', 'penalty', 'net_score', 'mult', 'final_score', 'a_tokens', 'b_tokens', 'c_tokens', 'd_tokens']]

    # rank_veto
    veto_raw = warn_raw[warn_raw['Detail'].str.startswith('RANK_VETO', na=False)].copy()
    def parse_rank_veto(detail):
        # RANK_VETO [VWAP_RTH_v1] S conf=A=14 B=17 C=16 D=0 Pen=0 Net=47 Mult=0.00 VETOED
        m = re.search(r'RANK_VETO \[([^\]]+)\] ([LS]) conf=(.*)', detail)
        if not m: return {}
        cid = m.group(1)
        dir_sign = m.group(2)
        kv = parse_kv(m.group(3))
        return {'condition_set_id': cid, 'direction': dir_sign, 'layer_a': kv.get('A'), 'layer_b': kv.get('B'), 'layer_c': kv.get('C'), 'layer_d': kv.get('D'), 'bonus': kv.get('Bon', 0), 'penalty': kv.get('Pen'), 'net_score': kv.get('Net')}
    veto_feats = veto_raw['Detail'].apply(parse_rank_veto).apply(pd.Series)
    rank_veto_final = pd.concat([veto_raw[['Timestamp', 'Bar']], veto_feats], axis=1)
    # Join with EVAL to get eval_id
    rank_veto_final = eval_final.merge(rank_veto_final.drop(columns=['Bar']), left_on=['timestamp', 'conditionsetid'], right_on=['Timestamp', 'condition_set_id'], how='inner', suffixes=('', '_veto'))
    # Use direction_veto or similar if name collision occurred, but here we explicitly select
    rank_veto_final = rank_veto_final[['eval_id', 'timestamp', 'bar', 'conditionsetid', 'direction', 'layer_a', 'layer_b', 'layer_c', 'layer_d', 'bonus', 'penalty', 'net_score']]

    # SLIP_EVENTS
    print("  Processing slip_events.parquet")
    slip_raw = warn_raw[warn_raw['Detail'].str.startswith('SLIP', na=False)].copy()
    def parse_slip(detail):
        # SLIP entry=4.0t exit=2.5t SessionAware
        m = re.search(r'entry=([\d\.-]+)t exit=([\d\.-]+)t', detail)
        if m: return {'entry_slip_ticks': float(m.group(1)), 'exit_slip_ticks': float(m.group(2))}
        return {}
    slip_feats = slip_raw['Detail'].apply(parse_slip).apply(pd.Series)
    slip_events = pd.concat([slip_raw[['Timestamp']], slip_feats], axis=1)
    
    # DYNAMIC_BE
    print("  Processing dynamic_be.parquet")
    be_raw = warn_raw[warn_raw['Detail'].str.startswith('DynamicBE', na=False)].copy()
    def parse_be(detail):
        # DynamicBE:T1_Prox(MFE=100.0t>=70%xT1=86.5t)  entry=24594.75
        m = re.search(r'DynamicBE:([^(\s]+)(?:\(([^)]+)\))?\s+entry=([\d\.]+)', detail)
        if not m: return {}
        mode = m.group(1)
        expr = m.group(2) or ""
        entry_px = float(m.group(3))
        res = {'be_mode': mode, 'entry_price': entry_px}
        # MFE=100.0t>=70%xT1=86.5t
        em = re.search(r'MFE=([\d\.]+)t>=(\d+)%xT1=([\d\.]+)t', expr)
        if em:
            res.update({'mfe_ticks_at_trigger': float(em.group(1)), 't1_pct_threshold': float(em.group(2)), 't1_ticks': float(em.group(3))})
        return res
    be_feats = be_raw['Detail'].apply(parse_be).apply(pd.Series)
    dynamic_be = pd.concat([be_raw[['Timestamp']], be_feats], axis=1)

    # TA_SHADOWS
    print("  Processing ta_shadows.parquet")
    sh_raw = warn_raw[warn_raw['Detail'].str.contains('TA_TIGHTEN_SHADOW|TA_EXIT_SHADOW', na=False)].copy()
    def parse_shadow(row):
        dtype = 'tighten' if 'TIGHTEN' in row['Detail'] else 'exit'
        sm = re.search(r'sid=([^ ]+)', row['Detail'])
        return pd.Series({'trade_id': normalize_trade_id(sm.group(1)) if sm else np.nan, 'shadow_type': dtype, 'raw_detail': row['Detail']})
    ta_shadows = pd.concat([sh_raw[['Timestamp']], sh_raw.apply(parse_shadow, axis=1)], axis=1).rename(columns={'Timestamp': 'timestamp'})

    # TABLE J: zone_lifecycle
    zone_raw = df[df['Tag'] == 'ZONE_MITIGATED'].copy()
    def parse_zone_v52(detail):
        if pd.isna(detail) or detail == "": return {}
        m = re.search(r'close=([\d\.]+) zone=([\d\.]+)-([\d\.]+)', detail)
        return {'close_price': float(m.group(1)), 'zone_lo': float(m.group(2)), 'zone_hi': float(m.group(3))} if m else {}
    zone_feats = zone_raw['Detail'].apply(parse_zone_v52).apply(pd.Series)
    zone_final = pd.concat([zone_raw[['Timestamp', 'Direction', 'Label']], zone_feats], axis=1).rename(columns={'Timestamp': 'timestamp', 'Direction': 'direction', 'Label': 'zone_side'})
    zone_final['zone_width'] = zone_final['zone_hi'] - zone_final['zone_lo']

    # TABLE K: trade_lifecycle (v5.2 updates)
    print("  Processing Table K: trade_lifecycle.parquet (v5.2 updates)")
    entry_regex = r'^[A-Za-z_]+_v\d+:\d{8}(?:_\d{4})?:\d+$'
    def get_fill_subtype(label):
        if re.match(entry_regex, str(label)): return 'entry'
        if label in ['Stop', 'T2', 'NoOvernight', 'SessionClose']: return {'Stop':'stop_exit', 'T2':'t2_exit', 'NoOvernight':'forced_exit', 'SessionClose':'forced_exit'}[label]
        return 'unknown'
    fills = df[df['Tag'] == 'ENTRY_FILL'].copy()
    fills['event_subtype'] = fills['Label'].apply(get_fill_subtype)
    
    entries = fills[fills['event_subtype'] == 'entry'].copy()
    trades_data = []
    inst = INSTRUMENTS['MNQ']

    for _, entry in entries.iterrows():
        tid = normalize_trade_id(entry['Label'])
        sig = signals_df[signals_df['trade_id'] == tid]
        if sig.empty: continue
        sig = sig.iloc[0]
        
        # Chronological range: [entry_ts, next TRADE_RESET]
        next_resets = df[(df['Tag'] == 'TRADE_RESET') & (df['Timestamp'] >= entry['Timestamp']) & (df['ConditionSetId'] == sig['conditionsetid']) & (df['Direction'] == sig['direction'])]
        reset_row = next_resets.iloc[0] if not next_resets.empty else None
        reset_ts = reset_row['Timestamp'] if reset_row is not None else df['Timestamp'].max()
        range_events = df[(df['Timestamp'] >= entry['Timestamp']) & (df['Timestamp'] <= reset_ts)]
        
        # Existing logic for Table K...
        stop_moves = range_events[range_events['Tag'] == 'STOP_MOVE']
        sm_parsed = stop_moves['Detail'].apply(parse_kv).apply(pd.Series)
        t1_hits = range_events[range_events['Tag'] == 'T1_HIT']
        t1_hit = not t1_hits.empty
        t1_row = t1_hits.iloc[0] if t1_hit else None
        t1_detail = parse_kv(t1_row['Detail']) if t1_hit else {}
        exit_fills = fills[(fills['Timestamp'] >= entry['Timestamp']) & (fills['Timestamp'] <= reset_ts) & (fills['event_subtype'].isin(['stop_exit', 't2_exit', 'forced_exit']))]
        exit_event = exit_fills.iloc[0] if not exit_fills.empty else None
        
        # Realized P&L
        side_sign = 1 if sig['direction'] == 'Long' else -1
        exit_px = exit_event['EntryPrice'] if exit_event is not None else entry['EntryPrice']
        t1_px = t1_row['ExitPrice'] if t1_hit else np.nan
        t1_rem = t1_detail.get('remaining', 0) if t1_hit else 0
        if t1_hit:
            p_con, r_con = sig['contracts'] - t1_rem, t1_rem
            t1_pnl = (t1_px - entry['EntryPrice']) * side_sign / inst['tick_size']
            r_pnl = (exit_px - entry['EntryPrice']) * side_sign / inst['tick_size']
            rp_ticks = (t1_pnl * p_con + r_pnl * r_con) / sig['contracts']
        else:
            rp_ticks = (exit_px - entry['EntryPrice']) * side_sign / inst['tick_size']

        # v5.2 NEW COLUMNS: SLIP, DynamicBE, TA_SHADOWS
        # 1. SLIP: Match by proximity to entry
        slip_match = slip_events[(slip_events['Timestamp'] >= entry['Timestamp'] - pd.Timedelta(seconds=1)) & (slip_events['Timestamp'] <= entry['Timestamp'] + pd.Timedelta(seconds=1))]
        slip_row = slip_match.iloc[0] if not slip_match.empty else None
        
        # 2. DynamicBE: Match by entry_price
        be_row = None
        if not dynamic_be.empty:
            be_match = dynamic_be[(dynamic_be['Timestamp'] >= entry['Timestamp']) & (dynamic_be['Timestamp'] <= reset_ts) & (np.isclose(dynamic_be['entry_price'], entry['EntryPrice']))]
            be_row = be_match.iloc[0] if not be_match.empty else None
        
        # 3. TA_SHADOWS: Count by trade_id
        trade_shadows = ta_shadows[ta_shadows['trade_id'] == tid]
        
        trades_data.append({
            'trade_id': tid, 'entry_timestamp': entry['Timestamp'], 'entry_price': entry['EntryPrice'],
            'direction': sig['direction'], 'source': sig['source'], 'condition_set_id': sig['conditionsetid'],
            'score': sig['score'], 'grade': sig['grade'], 'contracts': sig['contracts'],
            'stop_original': sig['stopprice'], 'stop_ticks': sig['stopticks'], 't1_price': sig['t1price'], 't2_price': sig['t2price'], 'rr_ratio': sig['rrratio'], 'intended_entry': sig['entryprice'],
            'slippage_ticks': (entry['EntryPrice'] - sig['entryprice']) * side_sign / inst['tick_size'],
            'num_stop_moves': len(stop_moves), 'first_stop_move_ts': stop_moves['Timestamp'].min() if not stop_moves.empty else pd.NaT,
            'final_stop_price': stop_moves.iloc[-1]['StopPrice'] if not stop_moves.empty else sig['stopprice'],
            'be_arm_fired': not sm_parsed[sm_parsed['afterT1'] == False].empty if not sm_parsed.empty else False,
            'trail_fired': not sm_parsed[sm_parsed['afterT1'] == True].empty if not sm_parsed.empty else False,
            'hit_t1': t1_hit, 't1_timestamp': t1_row['Timestamp'] if t1_hit else pd.NaT, 't1_exit_price': t1_px, 't1_remaining': t1_rem,
            'exit_subtype': exit_event['event_subtype'] if exit_event is not None else 'open', 'exit_price': exit_px, 'exit_timestamp': exit_event['Timestamp'] if exit_event is not None else pd.NaT,
            'trade_duration_min': (exit_event['Timestamp'] - entry['Timestamp']).total_seconds() / 60 if exit_event is not None else 0,
            'realized_pnl_ticks': rp_ticks, 'realized_pnl_$': rp_ticks * inst['tick_size'] * inst['point_value'] * sig['contracts'],
            # v5.2 NEW COLS - Safely access using .get() or checking membership
            'entry_slip_ticks': slip_row['entry_slip_ticks'] if slip_row is not None and 'entry_slip_ticks' in slip_row else np.nan,
            'exit_slip_ticks': slip_row['exit_slip_ticks'] if slip_row is not None and 'exit_slip_ticks' in slip_row else np.nan,
            'be_arm_reason': be_row['be_mode'] if be_row is not None and 'be_mode' in be_row else np.nan,
            'be_arm_mfe_ticks': be_row['mfe_ticks_at_trigger'] if be_row is not None and 'mfe_ticks_at_trigger' in be_row else np.nan,
            'num_ta_shadow_tighten': len(trade_shadows[trade_shadows['shadow_type'] == 'tighten']),
            'num_ta_shadow_exit': len(trade_shadows[trade_shadows['shadow_type'] == 'exit'])
        })
    trade_lifecycle_final = pd.DataFrame(trades_data)

    # [CHECK] v5.2 Validation
    print("[CHECK] v5.2 Validations (18-22)")
    # Re-calculate needed counts for coverage
    v_n_evals = len(eval_final)
    v_n_accepted = len(signals_df[signals_df['traded'] == True])
    v_n_rejected = len(signals_df[signals_df['traded'] == False])
    
    # 18. BAR_FORWARD row count check (1,239 * 5 = 6,195)
    v18 = len(fwd_bars_final) == len(fwd_bars_raw) * 5
    print(f"  #18: BAR_FORWARD 5-segment match ({len(fwd_bars_final)}): {'PASS' if v18 else 'FAIL'}")
    # 19. SLIP match rate
    v19 = trade_lifecycle_final['entry_slip_ticks'].notna().mean()
    print(f"  #19: SLIP match rate ({v19:.1%}): {'PASS' if v19 > 0.95 else 'FAIL'}")
    # 20. DynamicBE match rate
    v20 = trade_lifecycle_final['be_arm_reason'].notna().sum()
    print(f"  #20: DynamicBE events found: {v20}")
    # 21. TA_SHADOW match rate
    v21 = (trade_lifecycle_final['num_ta_shadow_tighten'] + trade_lifecycle_final['num_ta_shadow_exit']).sum()
    print(f"  #21: TA_SHADOW total counts: {v21}")
    # 22. RANK coverage (Include WEAK + WIN + VETO)
    v22 = (len(rank_win_final) + len(rank_veto_final) + len(rank_scores_final)) / (v_n_evals - v_n_accepted - v_n_rejected) if (v_n_evals - v_n_accepted - v_n_rejected) > 0 else 1.0
    print(f"  #22: RANK coverage vs non-signal evals ({v22:.1%}): {'PASS' if v22 > 0.3 else 'FAIL'}")

    # [RESULT]
    tables = {
        'signals.parquet': signals_df, 'signal_touch.parquet': signal_touch_final, 'outcomes.parquet': outcome_final,
        'context_bars.parquet': ctx_bars_final, 'forward_bars.parquet': fwd_bars_final, 'flow_bars.parquet': flow_final,
        'struct_bars.parquet': struct_final, 'evals.parquet': eval_final, 'zone_lifecycle.parquet': zone_final, 'trade_lifecycle.parquet': trade_lifecycle_final,
        'rank_scores.parquet': rank_scores_final, 'rank_win.parquet': rank_win_final, 'rank_veto.parquet': rank_veto_final, 'slip_events.parquet': slip_events, 'dynamic_be.parquet': dynamic_be, 'ta_shadows.parquet': ta_shadows
    }
    print("[RESULT] Table summaries:")
    for name, t in tables.items():
        print(f"  {name}: shape={t.shape}")

    # [SAVED]
    for name, t in tables.items():
        f_path = output_dir / name
        t.to_parquet(f_path, index=False, compression='snappy')
        print(f"[SAVED] {f_path}")

if __name__ == "__main__":
    main()
