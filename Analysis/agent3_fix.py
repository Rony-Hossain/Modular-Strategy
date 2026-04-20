# -*- coding: utf-8 -*-
path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\HostStrategy.cs'
with open(path, 'r', encoding='utf-8') as f: text = f.read()

# Fix for Agent 3 Q4
text = text.replace('FinalScore = (int)c.NetScore', 'FinalScore = r.FinalScore')

with open(path, 'w', encoding='utf-8') as f: f.write(text)
print('Agent 3 fix applied to HostStrategy.cs')
