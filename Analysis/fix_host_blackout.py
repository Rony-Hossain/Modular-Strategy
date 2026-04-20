# -*- coding: utf-8 -*-
import os

path_host = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\HostStrategy.cs'
with open(path_host, 'r', encoding='utf-8') as f:
    lines = f.readlines()

# Define the missing code
blackout_code = [
    '\n',
    '        private static readonly TimeSpan _entryBlockStart = new TimeSpan(15, 45, 0);\n',
    '        private static readonly TimeSpan _entryBlockEnd   = new TimeSpan(18,  0, 0);\n',
    '        private static bool IsEntryBlocked(DateTime t) { TimeSpan tod = t.TimeOfDay; return tod >= _entryBlockStart && tod < _entryBlockEnd; }\n',
    '\n'
]

# Find the end of the class (before the last two closing braces)
# The class usually ends with CreateLogic method.
insertion_idx = -1
for i in range(len(lines)-1, -1, -1):
    if 'protected virtual IStrategyLogic CreateLogic' in lines[i]:
        # Find the end of this method
        for j in range(i, len(lines)):
            if '}' in lines[j]:
                # Found the closing brace of the method, insert after it.
                insertion_idx = j + 1
                break
        break

if insertion_idx != -1:
    new_lines = lines[:insertion_idx] + blackout_code + lines[insertion_idx:]
    with open(path_host, 'w', encoding='utf-8') as f:
        f.writelines(new_lines)
    print("HostStrategy.cs fixed: blackout logic re-inserted.")
else:
    print("Could not find insertion point in HostStrategy.cs")
