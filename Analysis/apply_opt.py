# -*- coding: utf-8 -*-
import os, re

# 1. Update ConfluenceEngine.cs
path_conf = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\ConfluenceEngine.cs'
with open(path_conf, 'r', encoding='utf-8') as f:
    text = f.read()

# Update Weights
text = re.sub(r'public const int LAYER_A_BIAS_MATCH\s*=\s*\d+;', 'public const int LAYER_A_BIAS_MATCH = 5;', text)
text = re.sub(r'public const int LAYER_B_STRUCT_MATCH\s*=\s*\d+;', 'public const int LAYER_B_STRUCT_MATCH = 20;', text)
text = re.sub(r'public const int LAYER_C_FLOW_MATCH\s*=\s*\d+;', 'public const int LAYER_C_FLOW_MATCH = 10;', text)
text = re.sub(r'public const int PENALTY_WEIGHT\s*=\s*\d+;', 'public const int PENALTY_WEIGHT = 5;', text)

with open(path_conf, 'w', encoding='utf-8') as f:
    f.write(text)

# 2. Update SignalRankingEngine.cs (Acceptance Floor)
path_rank = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\SignalRankingEngine.cs'
with open(path_rank, 'r', encoding='utf-8') as f:
    text = f.read()

# Update the floor for winning signals
text = re.sub(r'private const int MIN_SIGNAL_SCORE\s*=\s*\d+;', 'private const int MIN_SIGNAL_SCORE = 40;', text)

with open(path_rank, 'w', encoding='utf-8') as f:
    f.write(text)

print("C# Strategy Engines Optimized.")
