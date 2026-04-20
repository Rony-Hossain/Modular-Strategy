# -*- coding: utf-8 -*-
with open(r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\StrategyLogger.cs', 'r', encoding='utf-8') as f:
    text = f.read()

open_braces = text.count('{')
close_braces = text.count('}')
print(f"Open: {open_braces}, Close: {close_braces}")
