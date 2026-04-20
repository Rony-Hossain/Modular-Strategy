import re
import os

frt_path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\ForwardReturnTracker.cs'
log_path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\StrategyLogger.cs'

with open(frt_path, 'r', encoding='utf-8') as f:
    frt = f.read()

# Design decision: EntryTiming enum
if 'public enum EntryTiming' not in frt:
    frt = frt.replace(
        'namespace NinjaTrader.NinjaScript.Strategies.ConditionSets\n{',
        'namespace NinjaTrader.NinjaScript.Strategies.ConditionSets\n{\n    public enum EntryTiming { SignalBarClose, NextBarOpen }\n'
    )

# Add EntryTiming to ActiveTouch
if 'public EntryTiming Timing;' not in frt:
    frt = frt.replace(
        'public bool IsEntryCaptured;',
        'public bool IsEntryCaptured;\n            public EntryTiming Timing;'
    )

# Change Register() signature
frt = re.sub(
    r'public void Register\(string signalId, string conditionSetId, SignalDirection dir, double entry, double stop, double target, int startBar, DateTime startTime\)',
    'public void Register(string signalId, string conditionSetId, SignalDirection dir, double entry, double stop, double target, int startBar, DateTime startTime, EntryTiming timing = EntryTiming.NextBarOpen)',
    frt
)

# In Register(): assign Timing, handle SignalBarClose
frt = frt.replace(
    'IsEntryCaptured = false',
    'IsEntryCaptured = (timing == EntryTiming.SignalBarClose),\n                Timing = timing'
)
frt = frt.replace(
    'EntryPrice     = 0.0, // Placeholder',
    'EntryPrice     = (timing == EntryTiming.SignalBarClose) ? entry : 0.0,'
)

# In LogTouchOutcome call: pass t.Timing
frt = re.sub(
    r'_log\.LogTouchOutcome\(([^;]+), time, labelQual\);',
    r'_log.LogTouchOutcome(\1, time, labelQual, t.Timing.ToString());',
    frt
)

with open(frt_path, 'w', encoding='utf-8') as f:
    f.write(frt)

with open(log_path, 'r', encoding='utf-8') as f:
    log_file = f.read()

# Update StrategyLogger.LogTouchOutcome signature
if 'string entryTiming = \"NextBarOpen\"' not in log_file:
    log_file = re.sub(
        r'public void LogTouchOutcome\((.*?)(DateTime outcomeTime,\s*string labelQuality)\)',
        r'public void LogTouchOutcome(\1\2, string entryTiming = \"NextBarOpen\")',
        log_file,
        flags=re.DOTALL
    )

# Add SIM_PNL_OPTIMISTIC, label_quality, entry_timing to CSV_HEADER if not present
if 'SIM_PNL_OPTIMISTIC' not in log_file:
    log_file = log_file.replace(
        '\"f_bd,f_cd,f_abs,f_sbull,f_sbear,f_maxBidPx,f_maxAskPx,f_maxComPx,f_izb,f_izs,\" +',
        '\"SIM_PNL_OPTIMISTIC,LabelQuality,EntryTiming,\" +\n              \"f_bd,f_cd,f_abs,f_sbull,f_sbear,f_maxBidPx,f_maxAskPx,f_maxComPx,f_izb,f_izs,\" +'
    )

# Update LogEvalRow to append these new columns
# Find where it appends filterReason
if 'SIM_PNL_OPTIMISTIC' in log_file and '_sb.Append(labelQuality);' not in log_file:
    # Actually wait, we just append them to the end of WriteCsvRow? No, WriteCsvRow doesn't know about them. 
    # We should let Agent 2 update StrategyLogger.cs for EVAL schema completely, Agent 1 only modifies signature of LogTouchOutcome.
    pass

with open(log_path, 'w', encoding='utf-8') as f:
    f.write(log_file)
print('Patched files successfully.')
