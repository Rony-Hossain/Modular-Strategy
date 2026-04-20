# -*- coding: utf-8 -*-
import os, re

# --- Fix SignalGenerator.cs ---
path_siggen = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\SignalGenerator.cs'
with open(path_siggen, 'r', encoding='utf-8') as f:
    text_siggen = f.read()

# The HashSet is currently ABOVE the class. Move it INSIDE.
# Find the start of the HashSet and the end.
hashset_start = text_siggen.find('private static readonly System.Collections.Generic.HashSet<string> _perfectWinners')
hashset_end = text_siggen.find('};', hashset_start) + 2

if hashset_start != -1 and hashset_end != -1:
    hashset_block = text_siggen[hashset_start:hashset_end]
    # Remove it from outside
    text_siggen = text_siggen[:hashset_start] + text_siggen[hashset_end:]
    
    # Inject it after the opening brace of the class
    class_brace = text_siggen.find('public class SignalGenerator : ISignalGenerator')
    class_brace = text_siggen.find('{', class_brace) + 1
    text_siggen = text_siggen[:class_brace] + '\n        ' + hashset_block + '\n' + text_siggen[class_brace:]

with open(path_siggen, 'w', encoding='utf-8') as f:
    f.write(text_siggen)

# --- Fix StrategyLogger.cs (Check if extra brace still exists) ---
path_log = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\StrategyLogger.cs'
with open(path_log, 'r', encoding='utf-8') as f:
    text_log = f.read()

# I noticed in the previous read that there were two closing braces before public int CurrentBar
# But let's look for a very specific block.
if '        }\\n        }\\n\\n        public int CurrentBar' in text_log:
    text_log = text_log.replace('        }\\n        }\\n\\n        public int CurrentBar', '        }\\n\\n        public int CurrentBar')

with open(path_log, 'w', encoding='utf-8') as f:
    f.write(text_log)

print("Precision fixes applied.")
