# -*- coding: utf-8 -*-
import os, re

# --- Fix StrategyLogger.cs ---
path_log = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\StrategyLogger.cs'
with open(path_log, 'r', encoding='utf-8') as f:
    text_log = f.read()

# Fix the escaped quotes in default parameter
text_log = text_log.replace('\\"NextBarOpen\\"', '"NextBarOpen"')

with open(path_log, 'w', encoding='utf-8') as f:
    f.write(text_log)

# --- Fix HostStrategy.cs ---
path_host = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\HostStrategy.cs'
with open(path_host, 'r', encoding='utf-8') as f:
    text_host = f.read()

# The regex replacement messed up OnStateChange. 
# Let's restore OnStateChange's try/catch and fix OnBarUpdate.

# 1. Restore OnStateChange try/catch
# It was: try { AddVolumetric(...) } catch (Exception ex) { ... }
# My script replaced it with a huge block.
# Let's look for the misplaced block starting with "try { if (earlyVetoReason == null)"

misplaced_start = 'try\\s*\\{\\s*if \\(earlyVetoReason == null\\)'
# Actually, let's just use a very specific string to find the start in OnStateChange
on_state_change_marker = 'AddDataSeries\\(Data.BarsPeriodType.Minute, 240\\);'
replacement_on_state_change = '''AddDataSeries(Data.BarsPeriodType.Minute, 240);
                try { AddVolumetric(Instrument.FullName, Data.BarsPeriodType.Minute, 1, Data.VolumetricDeltaType.BidAsk, 1); }
                catch (Exception ex) { Print("AddVolumetric FAILED: " + ex.Message); }
            }'''

text_host = re.sub(on_state_change_marker + r'.*?\}\s*else if \(State == State.DataLoaded\)', replacement_on_state_change + '\n            else if (State == State.DataLoaded)', text_host, flags=re.DOTALL)

# 2. Fix OnBarUpdate
# Find where the old logic is and replace it properly.
# The previous script left a mess in OnBarUpdate too.
# Let's find the start of OnBarUpdate logic and replace until the end of the method.

on_bar_update_start = 'else if (_signalGen.IsBlocked) earlyVetoReason = \"G1_CircuitBreaker\";'

new_on_bar_update_block = '''else if (_signalGen.IsBlocked) earlyVetoReason = "G1_CircuitBreaker";

            try
            {
                if (earlyVetoReason == null)
                {
                    _engine.Evaluate(snapshot);
                    int candidateCountForRanking = ApplyFootprintEntryAdvisor(Time[0]);
                    _rankingEngine.SetVolumetricMode(snapshot.GetFlag(SnapKeys.HasVolumetric));
                    RawDecision decision = _rankingEngine.Rank(_engine.CandidateBuffer, candidateCountForRanking, snapshot, in _lastSrResult);

                    if (decision.IsValid)
                    {
                        SignalObject signal = _signalGen.Process(decision, snapshot, _rankingEngine.LastWinnerDetail);
                        if (signal != null) { _orders.SubmitEntry(signal); _activeSignal = signal; _lastSignalClosed = signal; _lastSignalBar = CurrentBar; _ui?.AddSignal(signal); }
                        else if (_signalGen.LastRejectedSignal != null) { _ui?.AddSignal(_signalGen.LastRejectedSignal); }
                    }
                    
                    foreach (var r in _rankingEngine.AllResults) {
                        string finalReason = r.FilterReason; bool takenLive = false;
                        if (r.WasRankedWinner) {
                            if (_activeSignal != null && _activeSignal.ConditionSetId == r.Decision.ConditionSetId) { takenLive = true; finalReason = "ACCEPTED"; }
                            else { finalReason = _signalGen.LastRejectReason; }
                        }
                        var c = r.Confluence;
                        _evalBuffer.Add(new EvalRowBuffer { 
                            Decision = r.Decision, FilterReason = finalReason, 
                            WasRankedWinner = r.WasRankedWinner, WasTakenLive = takenLive, 
                            RawScore = r.RawScore, FinalScore = (int)c.NetScore, 
                            ConfMult = c.Multiplier, RankScore = (int)r.FinalScore, 
                            NumCands = _rankingEngine.AllResults.Count, 
                            V_Smf = c.V_SmfNonConf, V_Div = c.V_Divergence, 
                            V_Imbal = c.V_ImbalZone, V_Exh = c.V_ExhaustedCD, 
                            V_Trap = c.V_Trapped, V_Ice = c.V_Iceberg, 
                            V_Sweep = c.V_Sweep, V_Brick = c.V_Brickwall, 
                            SimPnl = 0, FirstHit = null 
                        });
                    }
                    foreach (var f in _filteredCandidates) {
                        _evalBuffer.Add(new EvalRowBuffer { 
                            Decision = f, FilterReason = "FP_VETO", 
                            WasRankedWinner = false, WasTakenLive = false, 
                            RawScore = f.RawScore, FinalScore = f.RawScore, 
                            ConfMult = 1.0, RankScore = 0, NumCands = 0, 
                            SimPnl = 0, FirstHit = null 
                        });
                    }
                }
                else 
                {
                    if (earlyVetoReason == "V_TIME_BLOCK" && (_orders.HasOpenPosition || _orders.HasPendingEntry))
                        _orderManager.ForceFlatten("NoOvernight");
                    
                    _evalBuffer.Add(new EvalRowBuffer { 
                        Decision = RawDecision.None, FilterReason = earlyVetoReason, 
                        SimPnl = double.NaN, FirstHit = "NOT_SIMULATED", 
                        ConfMult = 1.0 
                    });
                }
            }
            catch (Exception ex) { logStatus = "PARTIAL_ON_EXCEPTION"; _log?.Warn(Time[0], "OnBarUpdate CRITICAL ERROR: {0}", ex.Message); }
            finally
            {
                int minSinceOpen = snapshot.IsValid ? ((DataFeed)_feed).BarsSinceOpen * 5 : 0;
                foreach (var b in _evalBuffer) {
                    _log?.LogEvalRow(Time[0], b.Decision, snapshot, b.FilterReason, 
                        b.WasRankedWinner, b.WasTakenLive, b.RawScore, b.FinalScore, 
                        b.ConfMult, b.RankScore, b.NumCands, double.NaN, 
                        snapshot.Primary.Session.ToString(), minSinceOpen, logStatus, 
                        b.V_Smf, b.V_Div, b.V_Imbal, b.V_Exh, b.V_Trap, b.V_Ice, 
                        b.V_Sweep, b.V_Brick, b.SimPnl, b.FirstHit);
                }
            }

            if (_ui != null && _srEngine != null) { int zoneCount = _srEngine.GetZoneDTOs(_uiZoneBuffer); _ui.SetZones(_uiZoneBuffer, zoneCount); }
        }'''

# Replace until the end of OnBarUpdate
text_host = re.sub(on_bar_update_start + r'.*?if \(_ui != null && _srEngine != null\)', new_on_bar_update_block, text_host, flags=re.DOTALL)

with open(path_host, 'w', encoding='utf-8') as f:
    f.write(text_host)

print("Emergency fixes applied.")
