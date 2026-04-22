import pandas as pd
import numpy as np
from pathlib import Path
import re
import sys

# Set seed for reproducibility
np.random.seed(42)

def parse_detail(detail_str):
    """Parses detail strings of format K1=V1 K2=V2 or K1:V1 etc."""
    if not isinstance(detail_str, str) or detail_str == "":
        return {}
    
    # Handle the ' | ' separator first
    detail_str = detail_str.replace('|', ' ')
    
    # Use regex to find key-value pairs
    data = {}
    for match in re.finditer(r'([A-Z_a-z0-9]+)=([-+]?[\d.]+)', detail_str):
        k, v = match.groups()
        try:
            data[k] = float(v) if '.' in v else int(v)
        except ValueError:
            data[k] = v
            
    # Handle MTFA slopes in Context string (h4=+, h2=-, h1=0)
    for match in re.finditer(r'(h[124])=([+\-0])', detail_str):
        k, v = match.groups()
        data[k] = 1 if v == '+' else (-1 if v == '-' else 0)
        
    return data

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
        
    df_raw = pd.read_csv(log_path, low_memory=False)
    print(f"  Loaded Log.csv: {len(df_raw)} rows")

    # [PARSE]
    print("[PARSE] Cleaning timestamps and parsing details...")
    df_raw['Timestamp'] = pd.to_datetime(df_raw['Timestamp'], utc=True, errors='coerce')
    df_raw.loc[df_raw['Timestamp'].dt.year < 1970, 'Timestamp'] = pd.NaT
    df_raw['Timestamp'] = df_raw['Timestamp'].ffill()
    df_raw = df_raw.dropna(subset=['Timestamp'])

    def get_tag_features(tag_name):
        tag_df = df_raw[df_raw['Tag'] == tag_name].copy()
        if tag_df.empty: return pd.DataFrame()
        features = tag_df['Detail'].apply(parse_detail).apply(pd.Series)
        return pd.concat([tag_df.drop(columns=['Detail']), features], axis=1)

    print("  Extracting features per tag and joining...")
    
    # Anchor: EVAL signals
    eval_df = get_tag_features('EVAL')
    if eval_df.empty:
        print("ERROR: No EVAL rows found.")
        sys.exit(1)
        
    signals = eval_df.copy()
    signals['signal_id'] = signals.apply(lambda r: f"{r['ConditionSetId']}:{r['Timestamp'].strftime('%Y%m%d_%H%M')}:{r['Bar']}", axis=1)
    
    # Status
    outcome_status_rows = df_raw[df_raw['Tag'].isin(['SIGNAL_ACCEPTED', 'SIGNAL_REJECTED'])].copy()
    outcome_status_rows['signal_id'] = outcome_status_rows.apply(lambda r: f"{r['ConditionSetId']}:{r['Timestamp'].strftime('%Y%m%d_%H%M')}:{r['Bar']}", axis=1)
    
    signals = signals.merge(
        outcome_status_rows[['signal_id', 'Tag', 'GateReason']].rename(columns={'Tag': 'Status'}),
        on='signal_id', how='left'
    )
    signals['Status'] = signals['Status'].fillna('RANK_KILLED')
    
    # Join TOUCH
    touch_df = get_tag_features('TOUCH')
    if not touch_df.empty:
        touch_df['signal_id'] = touch_df.apply(lambda r: f"{r['ConditionSetId']}:{r['Timestamp'].strftime('%Y%m%d_%H%M')}:{r['Bar']}", axis=1)
        raw_cols = set(df_raw.columns)
        touch_feats = [c for c in touch_df.columns if c not in raw_cols and c != 'signal_id']
        touch_subset = touch_df[['signal_id'] + touch_feats].rename(columns={c: f"t_{c}" for c in touch_feats})
        signals = signals.merge(touch_subset, on='signal_id', how='left')
    
    # Join FLOW
    flow_df = get_tag_features('FLOW')
    if not flow_df.empty:
        raw_cols = set(df_raw.columns)
        flow_feats = [c for c in flow_df.columns if c not in raw_cols]
        flow_subset = flow_df[['Bar'] + flow_feats].groupby('Bar').first().reset_index()
        flow_subset = flow_subset.rename(columns={c: f"f_{c}" for c in flow_feats})
        signals = signals.merge(flow_subset, on='Bar', how='left')
    
    # Join TOUCH_OUTCOME
    outcome_df = get_tag_features('TOUCH_OUTCOME')
    if not outcome_df.empty:
        # Match via match_key: CID:YYYYMMDD:BAR
        def make_match_key(sid):
            if not isinstance(sid, str): return None
            parts = sid.replace(':REJ:', ':').split(':')
            if len(parts) >= 3:
                date_part = parts[1].split('_')[0]
                return f"{parts[0]}:{date_part}:{parts[-1]}"
            return None

        # GateReason is usually where the signal ID is for outcomes
        outcome_df['match_key'] = outcome_df['GateReason'].apply(make_match_key)
        signals['match_key'] = signals['signal_id'].apply(make_match_key)
        
        raw_cols = set(df_raw.columns)
        outcome_feats = [c for c in outcome_df.columns if c not in raw_cols and c != 'match_key']
        outcome_subset = outcome_df[['match_key'] + outcome_feats].groupby('match_key').first().reset_index()
        signals = signals.merge(outcome_subset, on='match_key', how='left')
    
    # [CHECK]
    print("[CHECK] Validating dtypes...")
    if 'SIM_PNL' not in signals.columns:
        print(f"  [WARN] 'SIM_PNL' column missing. Columns: {signals.columns.tolist()[:10]}...")
        valid_signals = pd.DataFrame()
    else:
        valid_signals = signals.dropna(subset=['SIM_PNL']).copy()
        print(f"  Signals with outcomes: {len(valid_signals)}")

    # [RESULT]
    print("[RESULT] Filter Audit")
    if not valid_signals.empty:
        # cd_col
        cd_col = 't_CD' if 't_CD' in valid_signals.columns else ('f_CD' if 'f_CD' in valid_signals.columns else None)
        if cd_col:
            high_cd = valid_signals[valid_signals[cd_col].abs() > 1200]
            low_cd = valid_signals[valid_signals[cd_col].abs() <= 1200]
            print(f"\n  CD Threshold Audit (>1200):")
            for name, g in [("High", high_cd), ("Normal", low_cd)]:
                print(f"    {name:<10}: n={len(g):>4}, Win%={(g['SIM_PNL']>0).mean():>6.1%}, PnL={g['SIM_PNL'].sum():>+10.0f}")

        if 'f_DSH' in valid_signals.columns:
            low_dsh = valid_signals[valid_signals['f_DSH'] < 3.0]
            high_dsh = valid_signals[valid_signals['f_DSH'] >= 3.0]
            print(f"\n  DSH Threshold Audit (<3.0):")
            for name, g in [("Low", low_dsh), ("High", high_dsh)]:
                print(f"    {name:<10}: n={len(g):>4}, Win%={(g['SIM_PNL']>0).mean():>6.1%}, PnL={g['SIM_PNL'].sum():>+10.0f}")

    # [SAVED]
    if not valid_signals.empty:
        f_out = output_dir / "filter_audit_matrix.parquet"
        valid_signals.to_parquet(f_out, index=False)
        print(f"\n[SAVED] Matrix: {f_out}")
        
        f_sum = output_dir / "filter_audit_summary.csv"
        valid_signals.groupby('Status')['SIM_PNL'].agg(['count', 'mean', 'sum']).to_csv(f_sum)
        print(f"[SAVED] Summary: {f_sum}")

if __name__ == "__main__":
    main()
