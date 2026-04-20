# -*- coding: utf-8 -*-
path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\HostStrategy.cs'
with open(path, 'r', encoding='utf-8') as f: text = f.read()

if 'struct EvalRowBuffer' not in text:
    struct_code = '''
    public struct EvalRowBuffer {
        public MathLogic.Strategy.RawDecision Decision;
        public string FilterReason;
        public bool WasRankedWinner;
        public bool WasTakenLive;
        public int RawScore;
        public int FinalScore;
        public double ConfMult;
        public int RankScore;
        public int NumCands;
        public bool V_Smf;
        public bool V_Div;
        public bool V_Imbal;
        public bool V_Exh;
        public bool V_Trap;
        public bool V_Ice;
        public bool V_Sweep;
        public bool V_Brick;
        public string FirstHit;
        public double SimPnl;
    }
'''
    text = text.replace('public class ModularStrategy : Strategy\n    {', 'public class ModularStrategy : Strategy\n    {' + struct_code)

if '_evalBuffer = new System.Collections.Generic.List<EvalRowBuffer>()' not in text:
    text = text.replace(
        'private readonly System.Collections.Generic.List<RawDecision> _filteredCandidates = new System.Collections.Generic.List<RawDecision>(16);',
        'private readonly System.Collections.Generic.List<RawDecision> _filteredCandidates = new System.Collections.Generic.List<RawDecision>(16);\n        private readonly System.Collections.Generic.List<EvalRowBuffer> _evalBuffer = new System.Collections.Generic.List<EvalRowBuffer>();'
    )

text = text.replace('if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)', '_evalBuffer.Clear();\n            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)')

try_finally_old = '''            try
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
                }
                else if (earlyVetoReason == "V_TIME_BLOCK")
                {
                    if (_orders.HasOpenPosition || _orders.HasPendingEntry)
                        _orderManager.ForceFlatten("NoOvernight");
                }
            }
            catch (Exception ex) { logStatus = "PARTIAL_ON_EXCEPTION"; _log?.Warn(Time[0], "OnBarUpdate CRITICAL ERROR: {0}", ex.Message); }
            finally
            {
                int minSinceOpen = snapshot.IsValid ? ((DataFeed)_feed).BarsSinceOpen * 5 : 0;
                if (earlyVetoReason != null) {
                    _log?.LogEvalRow(Time[0], RawDecision.None, snapshot, earlyVetoReason, false, false, 0, 0, 1.0, 0, 0, double.NaN, snapshot.Primary.Session.ToString(), minSinceOpen, "NOT_SIMULATED", false, false, false, false, false, false, false, false);
                } else {
                    foreach (var r in _rankingEngine.AllResults) {
                        string finalReason = r.FilterReason; bool takenLive = false;
                        if (r.WasRankedWinner) {
                            if (_activeSignal != null && _activeSignal.ConditionSetId == r.Decision.ConditionSetId) { takenLive = true; finalReason = "ACCEPTED"; }
                            else { finalReason = _signalGen.LastRejectReason; }
                        }
                        var c = r.Confluence;
                        _log?.LogEvalRow(Time[0], r.Decision, snapshot, finalReason, r.WasRankedWinner, takenLive, r.RawScore, (int)c.NetScore, c.Multiplier, r.FinalScore, _rankingEngine.AllResults.Count, double.NaN, snapshot.Primary.Session.ToString(), minSinceOpen, logStatus, c.V_SmfNonConf, c.V_Divergence, c.V_ImbalZone, c.V_ExhaustedCD, c.V_Trapped, c.V_Iceberg, c.V_Sweep, c.V_Brickwall);
                    }
                    foreach (var f in _filteredCandidates) _log?.LogEvalRow(Time[0], f, snapshot, "FP_VETO", false, false, f.RawScore, f.RawScore, 1.0, 0, 0, double.NaN, snapshot.Primary.Session.ToString(), minSinceOpen, logStatus, false, false, false, false, false, false, false, false);
                }
            }'''

try_finally_new = '''            try
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
                        _evalBuffer.Add(new EvalRowBuffer { Decision = r.Decision, FilterReason = finalReason, WasRankedWinner = r.WasRankedWinner, WasTakenLive = takenLive, RawScore = r.RawScore, FinalScore = (int)c.NetScore, ConfMult = c.Multiplier, RankScore = r.FinalScore, NumCands = _rankingEngine.AllResults.Count, V_Smf = c.V_SmfNonConf, V_Div = c.V_Divergence, V_Imbal = c.V_ImbalZone, V_Exh = c.V_ExhaustedCD, V_Trap = c.V_Trapped, V_Ice = c.V_Iceberg, V_Sweep = c.V_Sweep, V_Brick = c.V_Brickwall, SimPnl = 0, FirstHit = null });
                    }
                    foreach (var f in _filteredCandidates) {
                        _evalBuffer.Add(new EvalRowBuffer { Decision = f, FilterReason = "FP_VETO", WasRankedWinner = false, WasTakenLive = false, RawScore = f.RawScore, FinalScore = f.RawScore, ConfMult = 1.0, RankScore = 0, NumCands = 0, SimPnl = 0, FirstHit = null });
                    }
                }
                else if (earlyVetoReason == "V_TIME_BLOCK")
                {
                    if (_orders.HasOpenPosition || _orders.HasPendingEntry)
                        _orderManager.ForceFlatten("NoOvernight");
                    _evalBuffer.Add(new EvalRowBuffer { Decision = RawDecision.None, FilterReason = earlyVetoReason, SimPnl = double.NaN, FirstHit = "NOT_SIMULATED", ConfMult = 1.0 });
                }
                else 
                {
                    _evalBuffer.Add(new EvalRowBuffer { Decision = RawDecision.None, FilterReason = earlyVetoReason, SimPnl = double.NaN, FirstHit = "NOT_SIMULATED", ConfMult = 1.0 });
                }
            }
            catch (Exception ex) { logStatus = "PARTIAL_ON_EXCEPTION"; _log?.Warn(Time[0], "OnBarUpdate CRITICAL ERROR: {0}", ex.Message); }
            finally
            {
                int minSinceOpen = snapshot.IsValid ? ((DataFeed)_feed).BarsSinceOpen * 5 : 0;
                foreach (var b in _evalBuffer) {
                    _log?.LogEvalRow(Time[0], b.Decision, snapshot, b.FilterReason, b.WasRankedWinner, b.WasTakenLive, b.RawScore, b.FinalScore, b.ConfMult, b.RankScore, b.NumCands, double.NaN, snapshot.Primary.Session.ToString(), minSinceOpen, logStatus, b.V_Smf, b.V_Div, b.V_Imbal, b.V_Exh, b.V_Trap, b.V_Ice, b.V_Sweep, b.V_Brick, b.SimPnl, b.FirstHit);
                }
            }'''

if 'catch (Exception ex) { logStatus = "PARTIAL_ON_EXCEPTION";' in text and '_evalBuffer.Add' not in text:
    text = text.replace(try_finally_old, try_finally_new)

with open(path, 'w', encoding='utf-8') as f: f.write(text)
print("HostStrategy.cs patched successfully")
