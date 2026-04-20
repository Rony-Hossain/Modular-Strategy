import re

path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\SignalGenerator.cs'
with open(path, 'r', encoding='utf-8') as f:
    text = f.read()

# Replace the G3.5:ThinMarket block
text = re.sub(r'\"G3.5:ThinMarket\(vt=\{0:F0\}<\{1:F0\}=\{2:F1\}.*?\{3:F0\}\)\",\s+volTrades, avgTrades \* THIN_MARKET_RATIO, THIN_MARKET_RATIO, avgTrades', '\"G3.5_ThinMarket\"', text, flags=re.DOTALL)

with open(path, 'w', encoding='utf-8') as f:
    f.write(text)
print("SignalGenerator.cs G3.5 fix applied")
