# -*- coding: utf-8 -*-
import os

path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\HostStrategy.cs'
with open(path, 'r', encoding='utf-8') as f:
    text = f.read()

# 1. Ensure buffer exists
if 'private readonly System.Collections.Generic.List<EvalRowBuffer> _evalBuffer = new System.Collections.Generic.List<EvalRowBuffer>();' not in text:
    text = text.replace(
        'private readonly System.Collections.Generic.List<RawDecision> _filteredCandidates = new System.Collections.Generic.List<RawDecision>(16);',
        'private readonly System.Collections.Generic.List<RawDecision> _filteredCandidates = new System.Collections.Generic.List<RawDecision>(16);\n        private readonly System.Collections.Generic.List<EvalRowBuffer> _evalBuffer = new System.Collections.Generic.List<EvalRowBuffer>();'
    )

# 2. Fix the try/finally block
# We use a broad match for the try/finally section to replace it reliably.
import re
pattern = re.compile(r'try\s*\{.*?\}\s*catch\s*\(Exception ex\)\s*\{.*?\}\s*finally\s*\{.*?\}', re.DOTALL)

new_block = '''try
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
            }'''

text = pattern.sub(new_block, text, count=1)

with open(path, 'w', encoding='utf-8') as f:
    f.write(text)
print("HostStrategy.cs final patch applied")
