# -*- coding: utf-8 -*-
import os

path = r'D:\Ninjatrader-Modular-Startegy\ModularStrategy\HostStrategy.cs'
with open(path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

# Find the start of the mess (where OnBarUpdate should be or where it starts)
# Let's find OnStateChange end.
osc_end = -1
for i, line in enumerate(lines):
    if 'else if (State == State.Terminated) { _ui?.DisposeResources(); _log?.Dispose(); }' in line:
        # Find next closing brace
        for k in range(i, len(lines)):
            if '}' in lines[k]:
                osc_end = k
                break
        break

# Find OnMarketData start
omd_start = -1
for i, line in enumerate(lines):
    if 'protected override void OnMarketData' in line:
        omd_start = i
        break

if osc_end != -1 and omd_start != -1:
    new_obu = '''
        protected override void OnBarUpdate()
        {
            _evalBuffer.Clear();
            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)
            {
                _tape?.OnSessionOpen(Time[0]); _tapeBid = 0; _tapeAsk = 0;
                _bigPrint?.OnSessionOpen(); _velocity?.OnSessionOpen(); _sweep?.OnSessionOpen(); _tapeIceberg?.OnSessionOpen();
                _feed.OnSessionOpen(); _signalGen.OnSessionOpen(); _orders.OnSessionOpen();
                _activeSignal = null; _lastSignalBar = -1;
                System.Array.Clear(_tradesRingBuffer, 0, AVG_TRADES_PERIOD); _tradesRingIndex = 0; _tradesRingFilled = 0; _tradesRingSum = 0.0;
                System.Array.Clear(_cvdRingBuffer, 0, CVD_DIVERGENCE_PERIOD); System.Array.Clear(_priceRingBuffer, 0, CVD_DIVERGENCE_PERIOD);
                _cvdRingIndex = 0; _cvdRingFilled = false;
                _imbalZones?.OnSessionOpen(); _fvgZones?.OnSessionOpen(); _obZones?.OnSessionOpen();
                _orbProcessor?.Reset(); _lastFpResult = FootprintResult.Zero; _lastSrResult = SupportResistanceResult.Empty;
                _entryAdvisor?.OnSessionOpen(); _srEngine?.OnSessionOpen();
            }

            if (BarsInProgress != VOLUMETRIC_BAR_INDEX) _feed.OnBarUpdate(BarsInProgress);
            _filteredCandidates.Clear();

            if (BarsInProgress == 2 && CurrentBars[2] >= 1) UpdateHigherTFEma(Close[0], ref _ema50H1Prev, _h1PriceRing, ref _h1PriceRingIdx, ref _h1PriceRingCount, ref _h1EmaBias);
            else if (BarsInProgress == 4 && CurrentBars[4] >= 1) UpdateHigherTFEma(Close[0], ref _ema50H2hrPrev, _h2hrPriceRing, ref _h2hrPriceRingIdx, ref _h2hrPriceRingCount, ref _h2hrEmaBias);
            else if (BarsInProgress == 5 && CurrentBars[5] >= 1) UpdateHigherTFEma(Close[0], ref _ema50H4hrPrev, _h4hrPriceRing, ref _h4hrPriceRingIdx, ref _h4hrPriceRingCount, ref _h4hrEmaBias);

            if (_volBarsType == null && BarsArray.Length > VOLUMETRIC_BAR_INDEX && BarsArray[VOLUMETRIC_BAR_INDEX] != null)
                _volBarsType = BarsArray[VOLUMETRIC_BAR_INDEX].BarsType as NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType;

            if (BarsInProgress != 0 || !_feed.IsReady) return;
            if (CurrentBars[0] < BarsRequiredToTrade) return;

            FootprintResult fpResult = FootprintResult.Zero;
            if (_fpCore != null && _fpCore.IsReady && _volBarsType != null && CurrentBars[VOLUMETRIC_BAR_INDEX] >= 0)
            {
                _fpCore.TryComputeCurrentBar(_volBarsType, BarsArray[VOLUMETRIC_BAR_INDEX], CurrentBars[VOLUMETRIC_BAR_INDEX], BarsArray[0].BarsPeriod, Time[1], Time[0], Open[0], High[0], Low[0], Close[0], FootprintAssemblyMode.CompletedPrimaryBarOnly, out fpResult);
                _lastFpResult = fpResult; _entryAdvisor?.OnNewFootprint(in _lastFpResult);
                if (fpResult.IsValid) ((DataFeed)_feed).SetVolumetricData(fpResult.TotalBuyVol, fpResult.TotalSellVol, fpResult.BarDelta);
            }

            var snapshot = _feed.GetSnapshot();
            if (_srEngine != null && snapshot.IsValid && !_srBootstrapped) { _srEngine.Bootstrap(ref snapshot); _srBootstrapped = true; }
            if (_log != null) _log.CurrentBar = CurrentBar;
            if (_imbalZones != null) _imbalZones.Update(snapshot.Primary, in _lastFpResult);
            if (_fvgZones != null) _fvgZones.Update(snapshot.Primary, snapshot.ATR, CurrentBar);
            if (_lastFpResult.IsValid && snapshot.IsValid) _divTracker.OnBar(_lastFpResult, snapshot.ATR, CurrentBar);
            if (snapshot.IsValid) _structLabeler.Update(ref snapshot, snapshot.Primary, CurrentBar);
            if (_obZones != null && snapshot.IsValid) _obZones.Update(ref snapshot, snapshot.Primary, CurrentBar);
            OnPopulateIndicatorBag(ref snapshot);
            _forwardTracker?.OnBar(Open[0], High[0], Low[0], Close[0], Time[0], CurrentBar, Bars.IsFirstBarOfSession);
            _log?.BarContext_Tick(snapshot, CurrentBar); _log?.OrderFlowBar(snapshot.Primary.Time, snapshot); _log?.StructuralBar(snapshot.Primary.Time, in _lastSrResult);

            if (Bars.IsFirstBarOfSession) { _tracker?.EmitSessionSummary(Time[0], _log); _tracker?.ResetSession(); _divTracker.Reset(); _tape.OnSessionOpen(Time[0]); _logic.OnSessionOpen(snapshot); _log?.SessionOpen(Time[0], snapshot.VWAP, snapshot.ATR); }
            _log?.FlushTicks();

            string logStatus = "COMPLETE";
            string earlyVetoReason = null;

            if (IsEntryBlocked(Time[0])) earlyVetoReason = "V_TIME_BLOCK";
            else if (_orders.HasOpenPosition || _orders.HasPendingEntry) { _orderManager.ManagePosition(snapshot, _activeSignal, in _lastFpResult, in _lastSrResult); return; }
            else if (CurrentBar == _lastSignalBar) earlyVetoReason = "HOST_SAMEBAR_DUP";
            else if (_signalGen.IsBlocked) earlyVetoReason = "G1_CircuitBreaker";

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
        }

        private static readonly TimeSpan _entryBlockStart = new TimeSpan(15, 45, 0);
        private static readonly TimeSpan _entryBlockEnd   = new TimeSpan(18,  0, 0);
        private static bool IsEntryBlocked(DateTime t) { TimeSpan tod = t.TimeOfDay; return tod >= _entryBlockStart && tod < _entryBlockEnd; }

'''
    new_content = lines[:osc_end+1] + [new_obu] + lines[omd_start:]
    with open(path, 'w', encoding='utf-8') as f:
        f.writelines(new_content)
    print("HostStrategy.cs fixed with total precision.")
else:
    print(f"Indices not found: osc_end={osc_end}, omd_start={omd_start}")
