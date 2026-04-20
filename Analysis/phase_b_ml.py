import pandas as pd
import numpy as np
from sklearn.ensemble import RandomForestClassifier
from sklearn.model_selection import TimeSeriesSplit
from sklearn.metrics import classification_report, accuracy_score, precision_score
import os

FEATURE_FILE = 'feature_matrix.csv'

def run_ml_pipeline():
    print("--- ML AGENT: PHASE B PIPELINE ---")
    
    if not os.path.exists(FEATURE_FILE):
        print(f"Error: {FEATURE_FILE} not found. Ensure harvester.py has run.")
        return

    # Load data
    df = pd.read_csv(FEATURE_FILE)
    print(f"Loaded {len(df)} initial rows.")

    # 1. Target Definition (1 = Winner, 0 = Loser)
    # We drop non-simulated runs
    df = df[~df['hit'].isin(['NOT_SIMULATED', ''])]
    df = df.dropna(subset=['pnl'])
    
    # Clean PnL column if it's string
    if df['pnl'].dtype == object:
        df['pnl'] = pd.to_numeric(df['pnl'].str.replace('$', '').str.replace(',', ''), errors='coerce')
    
    df = df.dropna(subset=['pnl'])
    df['target'] = (df['pnl'] > 0).astype(int)
    
    print(f"Post-cleaning: {len(df)} simulated rows.")
    if len(df) == 0:
        print("No simulated data available for training.")
        return

    print(f"Class Balance: Winners={df['target'].sum()} Losers={len(df) - df['target'].sum()}")

    # 2. Feature Selection
    # Drop outcome-leaking columns and metadata
    drop_cols = ['ts', 'cid', 'bar', 'pnl', 'hit', 'mae', 'gate', 'target', 'eval_dir']
    features = [c for c in df.columns if c not in drop_cols]
    
    X = df[features].fillna(0) # Simple imputation for missing features
    y = df['target']

    print(f"Training on {len(features)} structural and order-flow features...")

    # 3. Model Training (Walk-Forward Validation to prevent leakage)
    tscv = TimeSeriesSplit(n_splits=5)
    model = RandomForestClassifier(n_estimators=200, max_depth=7, class_weight='balanced', random_state=42)

    fold = 1
    precisions = []
    
    print("\n--- WALK-FORWARD VALIDATION RESULTS ---")
    for train_index, test_index in tscv.split(X):
        X_train, X_test = X.iloc[train_index], X.iloc[test_index]
        y_train, y_test = y.iloc[train_index], y.iloc[test_index]
        
        model.fit(X_train, y_train)
        
        # We want to capture 100% precision (only trade absolute winners)
        # So we adjust the probability threshold manually
        y_prob = model.predict_proba(X_test)[:, 1]
        
        # Extremely strict threshold to filter out ALL losers
        STRICT_THRESHOLD = 0.70 
        y_pred_strict = (y_prob >= STRICT_THRESHOLD).astype(int)
        
        test_precision = precision_score(y_test, y_pred_strict, zero_division=0)
        precisions.append(test_precision)
        
        trades_taken = sum(y_pred_strict)
        actual_winners = sum((y_pred_strict == 1) & (y_test == 1))
        
        print(f"Fold {fold}: Precision={test_precision:.1%} | Trades Taken={trades_taken} (Actual Winners={actual_winners})")
        fold += 1

    print(f"\nAverage Strict Precision across folds: {np.mean(precisions):.1%}")
    print("-------------------------------------------------------------------------")
    print("Note: Forcing a '100% score' threshold drastically reduces trade frequency.")
    print("The model prefers to take NO trades rather than risk a loser under these strict constraints.")

if __name__ == "__main__":
    run_ml_pipeline()