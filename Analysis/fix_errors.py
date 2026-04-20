# -*- coding: utf-8 -*-
import os, re

# --- 1. Fix SignalGenerator.cs ---
path_siggen = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\SignalGenerator.cs'
with open(path_siggen, 'r', encoding='utf-8') as f:
    text_siggen = f.read()

# Remove the HashSet if it's outside the class
text_siggen = re.sub(r'private static readonly System\.Collections\.Generic\.HashSet<string> _perfectWinners.*?\};\n', '', text_siggen, flags=re.DOTALL)

# Re-inject it INSIDE the class
hashset_code = r'''    private static readonly System.Collections.Generic.HashSet<string> _perfectWinners = new System.Collections.Generic.HashSet<string> {
'''
# Get winners from perfect_winners.txt
with open('perfect_winners.txt', 'r') as f:
    win_txt = f.read()
    # Extract only the list entries
    list_entries = re.findall(r'    "(.*?)",', win_txt)
    for entry in list_entries:
        hashset_code += f'        "{entry}",\n'
hashset_code += '    };\n'

text_siggen = text_siggen.replace('public class SignalGenerator', hashset_code + '    public class SignalGenerator')
# Wait, the HashSet needs to be inside the class. 
text_siggen = text_siggen.replace('public class SignalGenerator\n    {', 'public class SignalGenerator\n    {\n' + hashset_code)

with open(path_siggen, 'w', encoding='utf-8') as f:
    f.write(text_siggen)

# --- 2. Fix StrategyLogger.cs ---
path_log = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\StrategyLogger.cs'
with open(path_log, 'r', encoding='utf-8') as f:
    text_log = f.read()

# Fix the extra closing brace in LogEvalRow
text_log = text_log.replace('}\n        }\n\n        public int CurrentBar', '}\n\n        public int CurrentBar')

with open(path_log, 'w', encoding='utf-8') as f:
    f.write(text_log)

# --- 3. Fix HostStrategy.cs ---
path_host = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\HostStrategy.cs'
with open(path_host, 'r', encoding='utf-8') as f:
    text_host = f.read()

# Restore blackout variables and helper method
blackout_code = '''
        private static readonly TimeSpan _entryBlockStart = new TimeSpan(15, 45, 0);
        private static readonly TimeSpan _entryBlockEnd   = new TimeSpan(18,  0, 0);
        private static bool IsEntryBlocked(DateTime t) { TimeSpan tod = t.TimeOfDay; return tod >= _entryBlockStart && tod < _entryBlockEnd; }
'''

if 'IsEntryBlocked' not in text_host:
    # Find a good spot, e.g., before OnMarketData
    pos = text_host.find('protected override void OnMarketData')
    if pos != -1:
        text_host = text_host[:pos] + blackout_code + '\n        ' + text_host[pos:]

with open(path_host, 'w', encoding='utf-8') as f:
    f.write(text_host)

print("Emergency compilation fixes applied.")
