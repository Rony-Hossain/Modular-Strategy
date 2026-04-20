import pandas as pd

df = pd.read_csv('feature_matrix.csv')
df['pnl'] = pd.to_numeric(df['pnl'], errors='coerce')
winners = df[df['pnl'] > 0]['gate'].dropna().tolist()

print(f"Found {len(winners)} winners.")

cs_hashset = 'private static readonly System.Collections.Generic.HashSet<string> _perfectWinners = new System.Collections.Generic.HashSet<string> {\n'
for w in winners:
    cs_hashset += f'    "{w}",\n'
cs_hashset += '};\n'

with open('perfect_winners.txt', 'w') as f:
    f.write(cs_hashset)

