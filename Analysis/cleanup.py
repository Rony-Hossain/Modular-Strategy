# -*- coding: utf-8 -*-
import os

path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\HostStrategy.cs'
with open(path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

# Find the finally block end
target_line = -1
for i, line in enumerate(lines):
    if 'b.V_Sweep, b.V_Brick, b.SimPnl, b.FirstHit);' in line:
        # The finally block usually ends a few lines later
        # Let's find the closing brace of the foreach, then the finally
        if i+2 < len(lines) and '}' in lines[i+1] and '}' in lines[i+2]:
            target_line = i + 3
            break

if target_line != -1:
    # Check if we have the 'else {' mess after it
    if 'else {' in lines[target_line] or '} else {' in lines[target_line]:
        # Search for the end of this mess
        # It should end before the next method 'if (_ui != null && _srEngine != null)'
        mess_end = -1
        for j in range(target_line, len(lines)):
            if 'if (_ui != null && _srEngine != null)' in lines[j]:
                mess_end = j
                break
        
        if mess_end != -1:
            print(f"Deleting lines {target_line} to {mess_end-1}")
            new_lines = lines[:target_line] + lines[mess_end:]
            with open(path, 'w', encoding='utf-8') as f:
                f.writelines(new_lines)
            print("Cleanup done.")
        else:
            print("Could not find end of mess.")
else:
    print("Could not find start of mess.")
