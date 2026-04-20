# -*- coding: utf-8 -*-
import re

# 1. StrategyEngine.cs
path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\StrategyEngine.cs'
with open(path, 'r', encoding='utf-8') as f: text = f.read()

if 'if (d.Direction != SignalDirection.None && d.EntryPrice > 0 && d.StopPrice > 0 && d.TargetPrice > 0)' not in text:
    text = text.replace(
        '_log?.Tracker?.Register(d.SignalId, d.ConditionSetId, d.Direction,\n                    d.EntryPrice, d.StopPrice, d.TargetPrice, p.CurrentBar, p.Time);',
        'if (d.Direction != SignalDirection.None && d.EntryPrice > 0 && d.StopPrice > 0 && d.TargetPrice > 0)\n                {\n                    _log?.Tracker?.Register(d.SignalId, d.ConditionSetId, d.Direction,\n                        d.EntryPrice, d.StopPrice, d.TargetPrice, p.CurrentBar, p.Time);\n                }'
    )
with open(path, 'w', encoding='utf-8') as f: f.write(text)

# 2. ConfluenceEngine.cs
path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\ConfluenceEngine.cs'
with open(path, 'r', encoding='utf-8') as f: text = f.read()
text = text.replace('V_SMF_NONCONF', 'V_NONCONF')
with open(path, 'w', encoding='utf-8') as f: f.write(text)

# 3. SignalGenerator.cs
path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\SignalGenerator.cs'
with open(path, 'r', encoding='utf-8') as f: text = f.read()

mapping = {
    'G1:CircuitBreaker': 'G1_CircuitBreaker',
    'G1:DecisionInvalid': 'G1_DecisionInvalid',
    'G1:DirectionNone': 'G1_DirectionNone',
    'G1:SnapshotInvalid': 'G1_SnapshotInvalid',
    'G2:StopZero': 'G2_StopZero',
    '\"G3:Score({decision.RawScore}<{_minScore})\"': '\"G3_Score\"',
    '\"G2:StopTooTight({stopTicks:F1}ticks<{RiskDefaults.MIN_STOP_TICKS}) [post-slip]\"': '\"G2_StopTooTight\"',
    '\"G3.5:ThinMarket(vt={0:F0}<{1:F0}={2:F1}×{3:F0})\",\\n                        volTrades, avgTrades * THIN_MARKET_RATIO, THIN_MARKET_RATIO, avgTrades': '\"G3.5_ThinMarket\"',
    '\"G3.5:ThinMarket(vt={0:F0}<{1:F0}={2:F1}Ã—{3:F0})\",\n                        volTrades, avgTrades * THIN_MARKET_RATIO, THIN_MARKET_RATIO, avgTrades': '\"G3.5_ThinMarket\"'
}

for k, v in mapping.items():
    text = text.replace(k, v)

with open(path, 'w', encoding='utf-8') as f: f.write(text)

# 4. StrategyLogger.cs & HostStrategy.cs logic
path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\StrategyLogger.cs'
with open(path, 'r', encoding='utf-8') as f: text = f.read()
if 'double simPnl = 0, string firstHit = null' not in text:
    text = re.sub(
        r'public void LogEvalRow\(DateTime time, RawDecision d, MarketSnapshot snap, string filterReason, bool wasRankedWinner, bool wasTakenLive, int rawScore, int finalScore, double confMult, double rankScore, int numCands, double spread, string sessionName, int minSinceOpen, string logStatus, bool v_smf, bool v_div, bool v_imbal, bool v_exh, bool v_trap, bool v_ice, bool v_sweep, bool v_brick\)',
        'public void LogEvalRow(DateTime time, RawDecision d, MarketSnapshot snap, string filterReason, bool wasRankedWinner, bool wasTakenLive, int rawScore, int finalScore, double confMult, double rankScore, int numCands, double spread, string sessionName, int minSinceOpen, string logStatus, bool v_smf, bool v_div, bool v_imbal, bool v_exh, bool v_trap, bool v_ice, bool v_sweep, bool v_brick, double simPnl = 0, string firstHit = null)',
        text
    )

    # To write SIM_PNL and FIRST_HIT to CSV for EVAL row, we can just append them if provided.
    # But wait, EVAL rows share the TOUCH_OUTCOME columns in the downstream Python?
    # Downstream Python reads SIM_PNL from the PnL column and FIRST_HIT from the Label column?
    # No, Agent 4 says "HOST_SAMEBAR_DUP, V_TIME_BLOCK, G1_CircuitBreaker emit EVAL rows with SIM_PNL=NaN, FIRST_HIT=NOT_SIMULATED."
    # If the user means we should just put NaN in pnl column and NOT_SIMULATED in label column of the CSV row:
    text = text.replace(
        '_sb.Append(pnl != 0 ? pnl.ToString("F2") : ""); _sb.Append(\',\');',
        '_sb.Append(double.IsNaN(pnl) ? "NaN" : (pnl != 0 ? pnl.ToString("F2") : "")); _sb.Append(\',\');'
    )
    text = text.replace(
        'd.TargetPrice, d.Target2Price, 0,\n                0, 0, 0,\n                "", d.Label, detail);',
        'd.TargetPrice, d.Target2Price, 0,\n                0, simPnl, 0,\n                "", firstHit != null ? firstHit : d.Label, detail);'
    )

with open(path, 'w', encoding='utf-8') as f: f.write(text)

# 5. OrderManager.cs
path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\OrderManager.cs'
with open(path, 'r', encoding='utf-8') as f: text = f.read()

if '_activeSignal.Direction.ToString()' not in text:
    text = text.replace(
        'string.Format("{0}_{1}", _activeSignal.Source, _activeSignal.BarIndex)',
        'string.Format("{0}_{1}_{2}", _activeSignal.Source, _activeSignal.Direction.ToString(), _activeSignal.BarIndex)'
    )
with open(path, 'w', encoding='utf-8') as f: f.write(text)

print("Agent 2 Python script executed successfully")
