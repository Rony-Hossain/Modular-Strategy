# -*- coding: utf-8 -*-
import os, re

path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\StrategyLogger.cs'
with open(path, 'r', encoding='utf-8') as f:
    text = f.read()

# 1. Delete EvalDecision method
# It starts around line 310 and ends before SignalAccepted.
pattern_eval_decision = re.compile(r'public void EvalDecision\(.*?\}\s+public void SignalAccepted', re.DOTALL)
text = pattern_eval_decision.sub('public void SignalAccepted', text)

# 2. Fix LogEvalRow to use simPnl and firstHit
# Find the Append sequence
# simPnl goes into column 17
# firstHit goes into column 20

# Search for the block of empty appends
old_sequence = '''                  _sb.Append(""); _sb.Append(\',\');
                  _sb.Append(""); _sb.Append(\',\');
                  _sb.Append(""); _sb.Append(\',\');
                  _sb.Append(""); _sb.Append(\',\');
                  _sb.Append(CsvQuote(d.SignalId)); _sb.Append(\',\');
                  _sb.Append(CsvQuote(d.Label)); _sb.Append(\',\');'''

new_sequence = '''                  _sb.Append(""); _sb.Append(',');
                  _sb.Append(double.IsNaN(simPnl) ? "NaN" : (simPnl != 0 ? simPnl.ToString("F2") : "")); _sb.Append(',');
                  _sb.Append(""); _sb.Append(',');
                  _sb.Append(CsvQuote(d.SignalId)); _sb.Append(',');
                  _sb.Append(CsvQuote(firstHit != null ? firstHit : d.Label)); _sb.Append(',');'''

# Note: The whitespace in the file might be different. Let's use regex or find more carefully.
# In previous Select-String I saw:
#                   _sb.Append(""); _sb.Append(',');
#                   _sb.Append(""); _sb.Append(',');
#                   _sb.Append(""); _sb.Append(',');
#                   _sb.Append(""); _sb.Append(',');
#                   _sb.Append(CsvQuote(d.SignalId)); _sb.Append(',');
#                   _sb.Append(CsvQuote(d.Label)); _sb.Append(',');

# Re-read part of the file to be sure
start_marker = '_sb.Append(d.Target2Price.ToString(\"F2\")); _sb.Append(\',\');'
pos = text.find(start_marker)
if pos != -1:
    end_marker = '_sb.Append(CsvQuote(\"\"));'
    end_pos = text.find(end_marker, pos)
    if end_pos != -1:
        # We replace the middle part
        # Target2 is col 14. 15, 16, 17, 18 are empty.
        # Then SignalId (19), Label (20), Detail (21)
        
        middle_part = '''
                  _sb.Append(""); _sb.Append(',');
                  _sb.Append(""); _sb.Append(',');
                  _sb.Append(double.IsNaN(simPnl) ? "NaN" : (simPnl != 0 ? simPnl.ToString("F2") : "")); _sb.Append(',');
                  _sb.Append(""); _sb.Append(',');
                  _sb.Append(CsvQuote(d.SignalId)); _sb.Append(',');
                  _sb.Append(CsvQuote(firstHit != null ? firstHit : d.Label)); _sb.Append(',');'''
        
        # We need to find exactly how many empty appends there are.
        # Header: ...,T2Price(14),RRRatio(15),ExitPrice(16),PnL(17),SessionPnL(18),GateReason(19),Label(20),Detail(21)
        # 14 is T2Price.
        # 15 is RRRatio -> empty
        # 16 is ExitPrice -> empty
        # 17 is PnL -> simPnl
        # 18 is SessionPnL -> empty
        # 19 is GateReason -> SignalId
        # 20 is Label -> firstHit
        # 21 is Detail -> empty
        
        # Let's replace the whole LogEvalRow body to be safe.
        pass

# Actually, let's just do a clean replacement of LogEvalRow
log_eval_row_new = '''public void LogEvalRow(DateTime time, RawDecision d, MarketSnapshot snap, string filterReason, bool wasRankedWinner, bool wasTakenLive, int rawScore, int finalScore, double confMult, double rankScore, int numCands, double spread, string sessionName, int minSinceOpen, string logStatus, bool v_smf, bool v_div, bool v_imbal, bool v_exh, bool v_trap, bool v_ice, bool v_sweep, bool v_brick, double simPnl = 0, string firstHit = null)
        {
            if (!WriteCsv || _writer == null) return;

            try
            {
                _sb.Clear();
                _sb.Append(time.ToString("yyyy-MM-dd HH:mm:ss")); _sb.Append(',');
                _sb.Append("EVAL"); _sb.Append(',');
                _sb.Append(CurrentBar); _sb.Append(',');
                _sb.Append(d.Direction.ToString()); _sb.Append(',');
                _sb.Append(d.Source.ToString()); _sb.Append(',');
                _sb.Append(d.ConditionSetId); _sb.Append(',');
                _sb.Append(finalScore); _sb.Append(',');
                _sb.Append(""); _sb.Append(',');
                _sb.Append(""); _sb.Append(',');
                _sb.Append(d.EntryPrice.ToString("F2")); _sb.Append(',');
                _sb.Append(d.StopPrice.ToString("F2")); _sb.Append(',');
                _sb.Append(""); _sb.Append(',');
                _sb.Append(d.TargetPrice.ToString("F2")); _sb.Append(',');
                _sb.Append(d.Target2Price.ToString("F2")); _sb.Append(',');
                _sb.Append(""); _sb.Append(','); // RR
                _sb.Append(""); _sb.Append(','); // ExitPrice
                _sb.Append(double.IsNaN(simPnl) ? "NaN" : (simPnl != 0 ? simPnl.ToString("F2") : "")); _sb.Append(','); // PnL
                _sb.Append(""); _sb.Append(','); // SessionPnL
                _sb.Append(CsvQuote(d.SignalId)); _sb.Append(','); // GateReason
                _sb.Append(CsvQuote(firstHit != null ? firstHit : d.Label)); _sb.Append(','); // Label
                _sb.Append(CsvQuote("")); // Detail

                _sb.Append(\',\'); _sb.Append(filterReason);
                _sb.Append(\',\'); _sb.Append(wasRankedWinner ? \"1\" : \"0\");
                _sb.Append(\',\'); _sb.Append(wasTakenLive ? \"1\" : \"0\");
                _sb.Append(\',\'); _sb.Append(rawScore);
                _sb.Append(\',\'); _sb.Append(finalScore);
                _sb.Append(\',\'); _sb.Append(confMult.ToString(\"F2\"));
                _sb.Append(\',\'); _sb.Append(rankScore.ToString(\"F2\"));
                _sb.Append(\',\'); _sb.Append(numCands);
                _sb.Append(\',\'); _sb.Append(snap.IsValid ? snap.Primary.Volume : 0);
                _sb.Append(\',\'); _sb.Append(snap.IsValid ? snap.Get(SnapKeys.AvgTrades).ToString(\"F0\") : \"0\");
                _sb.Append(\',\'); _sb.Append(double.IsNaN(spread) ? \"NaN\" : spread.ToString(\"F2\"));
                _sb.Append(\',\'); _sb.Append(sessionName);
                _sb.Append(\',\'); _sb.Append(minSinceOpen);
                _sb.Append(\',\'); _sb.Append(logStatus);

                _sb.Append(\',\'); _sb.Append(v_smf ? \"1\" : \"0\");
                _sb.Append(\',\'); _sb.Append(v_div ? \"1\" : \"0\");
                _sb.Append(\',\'); _sb.Append(v_imbal ? \"1\" : \"0\");
                _sb.Append(\',\'); _sb.Append(v_exh ? \"1\" : \"0\");
                _sb.Append(\',\'); _sb.Append(v_trap ? \"1\" : \"0\");
                _sb.Append(\',\'); _sb.Append(v_ice ? \"1\" : \"0\");
                _sb.Append(\',\'); _sb.Append(v_sweep ? \"1\" : \"0\");
                _sb.Append(\',\'); _sb.Append(v_brick ? \"1\" : \"0\");

                _writer.WriteLine(_sb.ToString());
                _writer.Flush();
            }
            catch (Exception ex)
            {
                try { Print(\"[WARN] EVAL CSV write error: {0}\", ex.Message); } catch { }
            }
        }'''

# Replace LogEvalRow
pattern_log_eval = re.compile(r'public void LogEvalRow\(.*?\}\s+catch\s+\(Exception ex\)\s+\{.*?\}\s+\}', re.DOTALL)
text = pattern_log_eval.sub(log_eval_row_new, text)

with open(path, 'w', encoding='utf-8') as f:
    f.write(text)

print("StrategyLogger.cs updated successfully.")
