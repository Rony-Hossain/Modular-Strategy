"""
count_outcomes.py  —  Count winners/losers across all TOUCH_OUTCOME signals.
Breaks down by accepted vs rejected (filtered out by the strategy gate).

Run:  python count_outcomes.py
"""
import sys, pandas as pd
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

LOG = '../backtest/Log.csv'

df = pd.read_csv(LOG, dtype=str)
df.columns = [c.strip() for c in df.columns]

to = df[df['Tag'] == 'TOUCH_OUTCOME'].copy()
label = to['Label'].str.strip()

print(f'Total TOUCH_OUTCOME rows : {len(to)}')
print(f'Winners (TARGET)         : {(label == "TARGET").sum()}')
print(f'Losers  (STOP)           : {(label == "STOP").sum()}')
print(f'Other                    : {(~label.isin(["TARGET","STOP"])).sum()}')
total_wl = (label == "TARGET").sum() + (label == "STOP").sum()
print(f'Overall win rate         : {(label=="TARGET").sum() / max(total_wl,1)*100:.1f}%')
print()

accepted = to[~to['GateReason'].str.contains(':REJ', na=False)]
rejected = to[ to['GateReason'].str.contains(':REJ', na=False)]

for name, subset in [('Accepted (strategy took)', accepted),
                      ('Rejected (filter blocked)', rejected)]:
    lbl = subset['Label'].str.strip()
    w = (lbl == 'TARGET').sum()
    l = (lbl == 'STOP').sum()
    wr = w / max(w + l, 1) * 100
    print(f'{name}:')
    print(f'  Winners: {w}  Losers: {l}  Win rate: {wr:.1f}%')
