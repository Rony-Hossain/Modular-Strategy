import pandas as pd
import numpy as np

# Load the trades
df = pd.read_csv('backtest/Trades.csv')

# Clean up Profit, MAE, MFE columns
def clean_currency(x):
    if isinstance(x, str):
        x = x.replace('$', '').replace(',', '')
        if '(' in x and ')' in x:
            x = '-' + x.replace('(', '').replace(')', '')
    try:
        return float(x)
    except:
        return 0.0

df['Profit'] = df['Profit'].apply(clean_currency)
df['MAE'] = df['MAE'].apply(clean_currency)
df['MFE'] = df['MFE'].apply(clean_currency)

# Group by Entry name
stats = df.groupby('Entry name').agg({
    'Profit': ['count', 'sum', 'mean', 'std', 'min', 'max'],
    'MAE': 'mean',
    'MFE': 'mean'
})

# Calculate Win Rate
win_rate = df.groupby('Entry name')['Profit'].apply(lambda x: (x > 0).sum() / len(x))
stats['Win Rate'] = win_rate

# Calculate Profit Factor per group
def calc_pf(x):
    pos = x[x > 0].sum()
    neg = abs(x[x < 0].sum())
    return pos / neg if neg != 0 else np.inf

stats['Profit Factor'] = df.groupby('Entry name')['Profit'].apply(calc_pf)

print(stats.to_string())

# Find the largest losers
print("\nTop 10 Largest Losers:")
print(df.sort_values('Profit').head(10)[['Trade number', 'Entry name', 'Entry price', 'Exit price', 'Profit', 'MAE', 'MFE']])
