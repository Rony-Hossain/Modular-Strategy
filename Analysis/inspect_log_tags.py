"""
inspect_log_tags.py  —  Show all Tag types in Log.csv and one sample row each.
Run:  python inspect_log_tags.py
"""
import sys, pandas as pd
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

LOG = '../backtest/Log.csv'

df = pd.read_csv(LOG, dtype=str)
df.columns = [c.strip() for c in df.columns]

print('=== TAG COUNTS ===')
print(df['Tag'].value_counts().to_string())
print()

print('=== SAMPLE ROW PER TAG ===')
for tag in df['Tag'].dropna().unique():
    row = df[df['Tag'] == tag].iloc[0]
    print(f'--- {tag} ---')
    for c in df.columns:
        v = str(row.get(c, '')).strip()
        if v and v.lower() != 'nan':
            print(f'  {c:<18}: {v[:120]}')
    print()
