import pandas as pd
import numpy as np
import tensorflow as tf
from tensorflow import keras
from sklearn.model_selection import train_test_split
from sklearn.preprocessing import StandardScaler
import os
import re

# --- CONFIGURATION ---
LOG_PATH = "D:/Ninjatrader-Modular-Startegy/backtest/Log.csv"
OUTPUT_PATH = "ml_feature_matrix.csv"

def parse_log_for_ml():
    """
    Parses Log.csv using integer bar matching and joins data.
    """
    print(f"--- HARVESTING DATA FOR DEEP LEARNING FROM {LOG_PATH} ---")
    
    if not os.path.exists(LOG_PATH):
        print("Error: Log.csv not found.")
        return None

    bar_context = {} 
    outcomes = []    

    try:
        with open(LOG_PATH, 'rb') as f:
            for line_raw in f:
                if b'FLOW' in line_raw or b'STRUCT' in line_raw:
                    line = line_raw.decode('utf-8', errors='ignore')
                    parts = line.split(',')
                    if len(parts) < 21: continue
                    tag = 'FLOW' if 'FLOW' in parts[1] else 'STRUCT'
                    try:
                        bar_id = int(parts[2].strip())
                        detail = parts[20].strip()
                        if bar_id not in bar_context: bar_context[bar_id] = {}
                        matches = re.findall(r'([A-Z0-9_]+)=([-+]?[\d\.]+)', detail)
                        prefix = 'f_' if tag == 'FLOW' else 's_'
                        for k, v in matches:
                            bar_context[bar_id][f"{prefix}{k}"] = float(v)
                    except: continue

                elif b'TOUCH_OUTCOME' in line_raw:
                    line = line_raw.decode('utf-8', errors='ignore')
                    parts = line.split(',')
                    if len(parts) < 21: continue
                    sid = parts[18].strip() 
                    hit = parts[19].strip()
                    detail = parts[20].strip()
                    
                    try:
                        pnl_m = re.search(r'SIM_PNL=([-+]?[\d\.]+)', detail)
                        pnl = float(pnl_m.group(1)) if pnl_m else 0.0
                        id_parts = sid.split(':')
                        if len(id_parts) >= 2:
                            orig_bar = int(id_parts[-1].strip())
                            outcomes.append({
                                'bar': orig_bar,
                                'pnl': pnl,
                                'hit': hit
                            })
                    except: continue

    except Exception as e:
        print(f"Error during parsing: {e}")
        return None

    final_data = []
    for o in outcomes:
        bar_id = o['bar']
        if bar_id in bar_context:
            row = o.copy()
            row.update(bar_context[bar_id])
            final_data.append(row)

    if not final_data:
        print("Error: No data joined.")
        return None

    df = pd.DataFrame(final_data)
    df.to_csv(OUTPUT_PATH, index=False)
    print(f"Feature matrix created: {df.shape}")
    return df

def train_and_audit(df):
    """
    Trains NN and performs feature interaction audit.
    """
    print("--- TRAINING AND AUDITING TENSORFLOW MODEL ---")
    df = df[df['hit'].isin(['TARGET', 'STOP'])].copy()
    features = [c for c in df.columns if c.startswith('f_') or c.startswith('s_')]
    df = df.dropna(subset=features)
    
    y = (df['hit'] == 'TARGET').astype(int).values
    X_raw = df[features].values
    
    scaler = StandardScaler()
    X = scaler.fit_transform(X_raw)
    
    X_train, X_test, y_train, y_test = train_test_split(X, y, test_size=0.2, random_state=42)

    model = keras.Sequential([
        keras.Input(shape=(X.shape[1],)),
        keras.layers.Dense(64, activation='relu'),
        keras.layers.Dense(32, activation='relu'),
        keras.layers.Dense(1, activation='sigmoid')
    ])

    model.compile(optimizer='adam', loss='binary_crossentropy', metrics=['accuracy'])
    model.fit(X_train, y_train, epochs=100, batch_size=32, verbose=0)
    
    # 1. Permutation Importance
    base_preds = model.predict(X_test, verbose=0).flatten()
    base_error = np.mean(np.abs(y_test - base_preds))
    
    importances = []
    for i, feat in enumerate(features):
        X_shuff = X_test.copy()
        np.random.shuffle(X_shuff[:, i])
        shuff_preds = model.predict(X_shuff, verbose=0).flatten()
        shuff_error = np.mean(np.abs(y_test - shuff_preds))
        importances.append((feat, shuff_error - base_error))
    
    print("\n=== TOP ALPHA FACTORS (TAPES & LEVELS) ===")
    for feat, imp in sorted(importances, key=lambda x: x[1], reverse=True)[:8]:
        print(f"  {feat:<18} | Conviction Impact: {imp:.4f}")

    # 2. Extract Alpha Rules
    print("\n=== GENERATING PREDICTIVE C# POLICY ===")
    top_feat = sorted(importances, key=lambda x: x[1], reverse=True)[0][0]
    # Simple threshold discovery
    vals = df[top_feat].unique()
    best_thr = 0; best_wr = 0
    for v in np.percentile(df[top_feat], [25, 50, 75]):
        wr = (df[df[top_feat] > v]['hit'] == 'TARGET').mean() * 100
        if wr > best_wr:
            best_wr = wr; best_thr = v
            
    print(f"// AI SUGGESTION: If {top_feat} > {best_thr:.2f}, Win Rate -> {best_wr:.1f}%")

if __name__ == "__main__":
    df = parse_log_for_ml()
    if df is not None:
        train_and_audit(df)
