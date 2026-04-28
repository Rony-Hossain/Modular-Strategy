import pandas as pd
import numpy as np
import re
from pathlib import Path
import ast
import operator
import sys

# Attempt to import strategy_config, handle path
sys.path.append(str(Path("Analysis").resolve()))
try:
    from strategy_config import POLICY, VETOES, FLOORS, MODULES, PENALTIES, CONFLUENCE, OPTIMIZED
except ImportError:
    print("WARNING: Could not import strategy_config. Using mock config for testing.")
    POLICY = {}
    VETOES = {}
    FLOORS = {}
    MODULES = {}
    PENALTIES = {}
    CONFLUENCE = {}
    OPTIMIZED = {}

def load_config(overrides: dict = None) -> dict:
    """Return merged config = base dicts overlaid by OPTIMIZED then overrides."""
    # Start with flat dictionary of all base config
    config = {}
    config.update(POLICY)
    config.update(VETOES)
    config.update(FLOORS)
    config.update(MODULES)
    config.update(PENALTIES)
    config.update(CONFLUENCE)
    
    # Overlay OPTIMIZED
    if OPTIMIZED and OPTIMIZED.get("OPT_STATUS") != "PENDING_REBUILD":
        config.update(OPTIMIZED)
        
    # Overlay overrides
    if overrides:
        config.update(overrides)
        
    return config

def safe_eval(expr, locals_dict):
    """Safely evaluate simple comparison expressions."""
    try:
        # Very basic safe eval for ops like `<, >, ==, <=, >=`
        # e.g., expr might be "150 < 400"
        # We'll use ast.literal_eval if possible, but for comparisons we might need eval with empty globals
        allowed_names = {}
        return eval(expr, {"__builtins__": {}}, locals_dict)
    except Exception as e:
        # print(f"eval failed for {expr}: {e}")
        return None

def parse_gate_expression(expr_str):
    """Parse format like {var=value}{op}{expr} or vt=150<400=0.40×1000"""
    if not isinstance(expr_str, str):
        return None, None, None
        
    # Example: vt=150<400=0.40x1000
    # Try to extract the left side, operator, and the base of the right side if it's a threshold multiple
    
    match = re.search(r'([a-zA-Z0-9_]+)=([0-9.-]+)\s*([<>=!]+)\s*([0-9.-]+)', expr_str)
    if match:
        var_name = match.group(1)
        val = float(match.group(2))
        op = match.group(3)
        thresh_val = float(match.group(4))
        
        # See if there's a multiple
        mult_match = re.search(r'([0-9.-]+)[x×*]([0-9.-]+)', expr_str)
        if mult_match:
            mult = float(mult_match.group(1))
            base = float(mult_match.group(2))
            return var_name, val, op, mult, base
        return var_name, val, op, thresh_val, None
    return None, None, None, None, None

def decide_accept(signal_row, config) -> tuple[bool, str]:
    """Return (accepted, reason). Reason is 'accepted' or a rejection code."""
    score = signal_row.get('score', 0)
    source = signal_row.get('source', '')
    
    # 1. Score floor
    score_reject = config.get('SCORE_REJECT', 60)
    if score < score_reject:
        return False, 'below_score_floor'
        
    # 2. Per-source threshold
    per_source = config.get('PER_SOURCE_THRESHOLDS', {})
    if source in per_source and score < per_source[source]:
        return False, 'below_source_threshold'
        
    # 3. H4 alignment
    if config.get('REQUIRE_H4_ALIGNED', False):
        h4 = signal_row.get('ctx_h4')
        direction = signal_row.get('direction', '')
        if (direction == 'Long' and h4 != '+') or (direction == 'Short' and h4 != '-'):
            return False, 'h4_misaligned'
            
    # 4. Active veto replay
    # If the signal was originally rejected by a gate, try to replay it
    gate_reason = signal_row.get('gate_reason')
    if pd.notna(gate_reason) and gate_reason != 'accepted':
        # E.g. G3.5:ThinMarket(vt=150<400=0.40×1000)
        match = re.search(r'G[0-9.]+:([a-zA-Z0-9_]+)\((.*)\)', gate_reason)
        if match:
            gate_name = match.group(1)
            expr_str = match.group(2)
            
            # Example mapping: ThinMarket -> CUMDELTA_EXHAUSTED or similar
            # This requires knowing the config key mapping to the gate name.
            # We'll do a best-effort replay based on parsed threshold multiple if available.
            var_name, val, op, mult_or_thresh, base = parse_gate_expression(expr_str)
            
            if gate_name == 'ThinMarket' and 'THIN_MARKET_MULT' in config and base is not None:
                new_thresh = config['THIN_MARKET_MULT'] * base
                new_expr = f"{val} {op} {new_thresh}"
                if safe_eval(new_expr, {}):
                    return False, f"veto_replayed_{gate_name}"
            # Add other known gates here
            else:
                # Sticky veto - couldn't replay
                return False, 'sticky_veto'
        else:
            return False, 'sticky_veto'
            
    # 5. Conviction floors (simplified)
    if config.get('MIN_RR_RATIO') and signal_row.get('rrratio', 0) < config['MIN_RR_RATIO']:
        return False, 'min_rr_ratio'

    return True, 'accepted'

def replay(config, features_df) -> pd.DataFrame:
    """Walk features_df in timestamp order. Apply decide_accept. For
    accepted signals, draw sim_pnl from outcomes. Apply session/daily
    state (MAX_DAILY_LOSS, MAX_CONSECUTIVE_LOSS) as circuit breakers.
    """
    if features_df.empty:
        return pd.DataFrame()
        
    df = features_df.sort_values('timestamp').copy()
    
    results = []
    
    # State tracking
    current_day = None
    daily_pnl = 0.0
    consecutive_losses = 0
    halted = False
    
    max_daily_loss = config.get('MAX_DAILY_LOSS', 500)
    max_consec_loss = config.get('MAX_CONSECUTIVE_LOSS', 4)
    
    sticky_veto_count = 0
    
    for idx, row in df.iterrows():
        ts = row['timestamp']
        day = ts.date() if pd.notna(ts) else None
        
        # Reset state on new day
        if day != current_day:
            current_day = day
            daily_pnl = 0.0
            consecutive_losses = 0
            halted = False
            
        if halted:
            results.append({
                'signal_id': row.get('signal_id'),
                'timestamp': ts,
                'accept_under_new_config': False,
                'reject_reason': 'halted_daily_limit',
                'realized_pnl': 0.0,
                'daily_pnl': daily_pnl,
                'consecutive_losses': consecutive_losses,
                'halted_flag': True
            })
            continue
            
        accepted, reason = decide_accept(row, config)
        
        if reason == 'sticky_veto':
            sticky_veto_count += 1
            
        pnl = 0.0
        if accepted:
            pnl = row.get('sim_pnl', 0.0)
            if pd.isna(pnl): pnl = 0.0
            
            daily_pnl += pnl
            if pnl < 0:
                consecutive_losses += 1
            elif pnl > 0:
                consecutive_losses = 0
                
            if daily_pnl <= -max_daily_loss or consecutive_losses >= max_consec_loss:
                halted = True
                
        results.append({
            'signal_id': row.get('signal_id'),
            'timestamp': ts,
            'accept_under_new_config': accepted,
            'reject_reason': reason,
            'realized_pnl': pnl,
            'daily_pnl': daily_pnl,
            'consecutive_losses': consecutive_losses,
            'halted_flag': halted
        })
        
    res_df = pd.DataFrame(results)
    res_df['cum_pnl'] = res_df['realized_pnl'].cumsum()
    
    # Store global stats for metrics access
    res_df.attrs['sticky_veto_count'] = sticky_veto_count
    
    return res_df

def metrics(replay_df) -> dict:
    """Return dict of performance metrics."""
    if replay_df.empty:
        return {}
        
    accepted = replay_df[replay_df['accept_under_new_config']]
    total_pnl = accepted['realized_pnl'].sum()
    n_trades = len(accepted)
    wins = len(accepted[accepted['realized_pnl'] > 0])
    win_rate = wins / n_trades if n_trades > 0 else 0
    avg_pnl = total_pnl / n_trades if n_trades > 0 else 0
    std_pnl = accepted['realized_pnl'].std() if n_trades > 1 else 0
    
    # Sharpe (approx)
    sharpe_daily = 0
    if n_trades > 1 and std_pnl > 0:
        # Rough daily sharpe approx assuming trades spread over time
        daily_pnls = accepted.groupby(accepted['timestamp'].dt.date)['realized_pnl'].sum()
        if len(daily_pnls) > 1 and daily_pnls.std() > 0:
            sharpe_daily = (daily_pnls.mean() / daily_pnls.std()) * np.sqrt(252)
            
    # Max drawdown
    cum_max = replay_df['cum_pnl'].cummax()
    drawdown = cum_max - replay_df['cum_pnl']
    max_drawdown = drawdown.max()
    
    max_consec_losses = replay_df['consecutive_losses'].max()
    halted_days_count = replay_df[replay_df['halted_flag']]['timestamp'].dt.date.nunique()
    
    return {
        'total_pnl': total_pnl,
        'n_trades': n_trades,
        'win_rate': win_rate,
        'avg_pnl': avg_pnl,
        'std_pnl': std_pnl,
        'sharpe_daily': sharpe_daily,
        'max_drawdown': max_drawdown,
        'max_consecutive_losses': max_consec_losses,
        'halted_days_count': halted_days_count
    }

if __name__ == '__main__':
    np.random.seed(42)
    repo_root = Path(".")
    artifacts_dir = repo_root / "Analysis/artifacts"
    
    # Try to load features + outcomes
    fm_path = artifacts_dir / "feature_matrix.parquet"
    outcomes_path = artifacts_dir / "outcomes.parquet"
    signals_path = artifacts_dir / "signals.parquet"
    
    if not signals_path.exists() or not outcomes_path.exists():
        print(f"Missing input parquets in {artifacts_dir}. Need signals.parquet and outcomes.parquet.")
        # Create dummy data for testing if files don't exist
        print("Creating dummy data for testing...")
        dates = pd.date_range('2026-01-01', periods=100, freq='H')
        df = pd.DataFrame({
            'signal_id': [f"sig_{i}" for i in range(100)],
            'timestamp': dates,
            'score': np.random.randint(40, 100, 100),
            'source': np.random.choice(['A', 'B', 'C'], 100),
            'gate_reason': ['accepted']*80 + ['G1:ThinMarket(vt=150<400=0.40x1000)']*20,
            'sim_pnl': np.random.normal(5, 50, 100)
        })
    else:
        # Load actual data
        signals = pd.read_parquet(signals_path)
        outcomes = pd.read_parquet(outcomes_path)
        # Merge to get features
        df = signals.merge(outcomes[['signal_id', 'sim_pnl']], on='signal_id', how='left')
        df['gate_reason'] = df.get('gate_expression', 'accepted') # Using expression or name
        df['gate_reason'] = df['gate_reason'].fillna('accepted')

    config = load_config()
    print("Running replay harness...")
    res = replay(config, df)
    
    if not res.empty:
        m = metrics(res)
        print("\n=== Baseline Metrics ===")
        for k, v in m.items():
            print(f"{k}: {v}")
            
        print(f"\nSticky veto count: {res.attrs.get('sticky_veto_count', 0)}")
        
        out_path = artifacts_dir / "replay_baseline.parquet"
        res.to_parquet(out_path)
        print(f"\nSaved {out_path}")
        print(res.head(10))
        
        # Check a halted day if any
        halted = res[res['halted_flag']]
        if not halted.empty:
            sample_day = halted['timestamp'].dt.date.iloc[0]
            print(f"\nSample halted day: {sample_day}")
            print(res[res['timestamp'].dt.date == sample_day][['timestamp', 'accept_under_new_config', 'reject_reason', 'daily_pnl', 'halted_flag']])
    else:
        print("Replay dataframe is empty.")
