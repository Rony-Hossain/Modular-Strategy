#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// HOST STRATEGY — NT8 shell. Pattern B plug-and-play.
    /// </summary>
    public class ModularStrategy : Strategy
    {
        // ===================================================================
        // USER-FACING PARAMETERS
        // ===================================================================

        [NinjaScriptProperty]
        [Display(Name = "Account size ($)", GroupName = "Risk", Order = 0)]
        public double AccountSize { get; set; }

        [NinjaScriptProperty]
        [Range(0.001, 0.05)]
        [Display(Name = "Risk per trade (%)", GroupName = "Risk", Order = 1)]
        public double RiskPct { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max contracts", GroupName = "Risk", Order = 2)]
        public int MaxContracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max daily loss ($)", GroupName = "Risk", Order = 3)]
        public double MaxDailyLoss { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP", GroupName = "Display", Order = 0)]
        public bool ShowVWAP { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show signal bubbles", GroupName = "Display", Order = 1)]
        public bool ShowSignals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show order blocks", GroupName = "Display", Order = 2)]
        public bool ShowOBZones { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Trend Length", GroupName = "SMF Settings", Order = 0)]
        public int SmfTrendLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trend Engine", GroupName = "SMF Settings", Order = 1)]
        public SMF_TrendEngineType SmfTrendEngine { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name = "Flow Window", GroupName = "SMF Settings", Order = 2)]
        public int SmfFlowWindow { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Flow Smoothing", GroupName = "SMF Settings", Order = 3)]
        public int SmfFlowSmoothing { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Retest Cooldown (bars)", GroupName = "SMF Settings", Order = 4)]
        public int SmfDotCooldown { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Impulse Threshold", GroupName = "SMF Settings", Order = 5)]
        public double SmfImpulseThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log level", GroupName = "Diagnostics", Order = 0)]
        public LogLevel LogLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Write CSV log", GroupName = "Diagnostics", Order = 1)]
        public bool WriteCsvLog { get; set; }

        // ===================================================================
        // MODULES
        // ===================================================================

        protected IDataFeed        _feed;
        protected IStrategyLogic   _logic;
        protected ISignalGenerator _signalGen;
        protected IOrderManager    _orders;
        private   OrderManager     _orderManager;
        protected IUIRenderer      _ui;
        protected StrategyLogger   _log;
        private ConditionSets.ForwardReturnTracker _forwardTracker;

        private StrategyEngine            _engine;
        private SignalRankingEngine       _rankingEngine;
        private PerSetPerformanceTracker  _tracker;
        private SupportResistanceEngine   _srEngine;
        private bool                      _srBootstrapped = false;
        private bool                      _srPublishing = true;
        private ConditionSets.StructuralLabeler _structLabeler = new ConditionSets.StructuralLabeler();

        private FootprintAssembler       _fpAssembler;
        private FootprintCore            _fpCore;
        private TapeRecorder             _tape;
        private double                   _tapeBid;
        private double                   _tapeAsk;
        private FootprintEntryAdvisor    _entryAdvisor;
        private VolumeProfileProcessor   _orbProcessor;
        private readonly FootprintDivergenceTracker _divTracker = new FootprintDivergenceTracker();

        private FootprintResult          _lastFpResult = FootprintResult.Zero;
        private SupportResistanceResult  _lastSrResult = SupportResistanceResult.Empty;
        private ImbalanceZoneRegistry    _imbalZones;
        private FvgZoneRegistry          _fvgZones;
        private ObZoneRegistry           _obZones;
        private SmartMoneyFlowCloudBOSWaves _smf;

        private const int VOLUMETRIC_BAR_INDEX = 6;
        private const bool LOG_FOOTPRINT_PIPELINE = true;
        private NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType _volBarsType;

        private const int AVG_TRADES_PERIOD = 20;
        private readonly double[] _tradesRingBuffer = new double[AVG_TRADES_PERIOD];
        private int    _tradesRingIndex  = 0;
        private int    _tradesRingFilled = 0;
        private double _tradesRingSum    = 0.0;

        private const int CVD_DIVERGENCE_PERIOD = 10;
        private readonly double[] _cvdRingBuffer = new double[CVD_DIVERGENCE_PERIOD];
        private readonly double[] _priceRingBuffer = new double[CVD_DIVERGENCE_PERIOD];
        private int _cvdRingIndex = 0;
        private bool _cvdRingFilled = false;

        private const int EMA50_PERIOD   = 50;
        private double _ema50H1Prev       = double.NaN;
        private readonly double[] _h1PriceRing = new double[(EMA50_PERIOD - 1) / 2 + 1];
        private int    _h1PriceRingIdx    = 0;
        private int    _h1PriceRingCount  = 0;

        private double _ema50H2hrPrev      = double.NaN;
        private readonly double[] _h2hrPriceRing = new double[(EMA50_PERIOD - 1) / 2 + 1];
        private int    _h2hrPriceRingIdx   = 0;
        private int    _h2hrPriceRingCount = 0;

        private double _ema50H4hrPrev      = double.NaN;
        private readonly double[] _h4hrPriceRing = new double[(EMA50_PERIOD - 1) / 2 + 1];
        private int    _h4hrPriceRingIdx   = 0;
        private int    _h4hrPriceRingCount = 0;

        private double _h1EmaBias   = 0.0;
        private double _h2hrEmaBias = 0.0;
        private double _h4hrEmaBias = 0.0;

        protected SignalObject _activeSignal;
        protected SignalObject _lastSignalClosed; // FIX (#N13): Persistent metadata for async logging
        protected int _lastSignalBar = -1;
        private int _tradeCountAtEntry = -1;

        // ===================================================================
        // NT8 LIFECYCLE
        // ===================================================================

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "ModularStrategy";
                Description      = "Plug-and-play modular strategy: SMC + VWAP + ORB + EMA/ADX";
                Calculate        = Calculate.OnBarClose;
                IsOverlay        = true;
                IsAutoScale      = false;
                BarsRequiredToTrade = 12; // 1 hour of 5-min bars. Strategy awake by 10:30 AM.
                IsExitOnSessionCloseStrategy = true;
                IncludeTradeHistoryInBacktest = true;

                SmfTrendLength      = 34;
                SmfTrendEngine      = SMF_TrendEngineType.EMA;
                SmfFlowWindow       = 24;
                SmfFlowSmoothing    = 5;
                SmfDotCooldown      = 12;
                SmfImpulseThreshold = 0.5;

                LogLevel         = LogLevel.Normal;
                WriteCsvLog      = true;
                AccountSize      = 25000;
                RiskPct          = RiskDefaults.RISK_PCT;
                MaxContracts     = RiskDefaults.MAX_CONTRACTS;
                MaxDailyLoss     = 0;
                ShowVWAP         = true;
                ShowSignals      = false;
                ShowOBZones      = true;
            }
            else if (State == State.Configure)
            {
                if (BarsPeriods[0].BarsPeriodType != Data.BarsPeriodType.Minute || BarsPeriods[0].Value != 5)
                {
                    throw new Exception(string.Format(
                        "HostStrategy requires 5-minute primary bars. Current primary: {0} {1}",
                        BarsPeriods[0].BarsPeriodType, BarsPeriods[0].Value));
                }

                AddDataSeries(Data.BarsPeriodType.Minute, 15);
                AddDataSeries(Data.BarsPeriodType.Minute, 60);
                AddDataSeries(Data.BarsPeriodType.Day,    1);
                AddDataSeries(Data.BarsPeriodType.Minute, 120);
                AddDataSeries(Data.BarsPeriodType.Minute, 240);
                try { AddVolumetric(Instrument.FullName, Data.BarsPeriodType.Minute, 1, Data.VolumetricDeltaType.BidAsk, 1); }
                catch (Exception ex) { Print("AddVolumetric FAILED: " + ex.Message); }
            }
            else if (State == State.DataLoaded)
            {
                InstrumentKind inst = InstrumentSpecs.Resolve(Instrument.MasterInstrument.Name);
                if (MaxDailyLoss <= 0) MaxDailyLoss = AccountSize * 0.02;

                _log       = new StrategyLogger(this, LogLevel) { WriteCsv = WriteCsvLog };
                _forwardTracker = new ConditionSets.ForwardReturnTracker(
                    _log,
                    windowBars: 60,
                    pointValue: Instrument.MasterInstrument.PointValue);
                _log.Tracker = _forwardTracker;
                _feed      = new DataFeed(this, inst, 0, 1, 2, 3, 4, 5);
                _engine    = (StrategyEngine)CreateLogic(inst);
                _logic     = _engine;
                _rankingEngine = new SignalRankingEngine(_log);
                _tracker       = new PerSetPerformanceTracker();
                _srEngine      = new SupportResistanceEngine(SupportResistanceCoreConfig.ForInstrument(inst), _log);
                _imbalZones    = new ImbalanceZoneRegistry(_log);
                _fvgZones      = new FvgZoneRegistry();
                _obZones       = new ObZoneRegistry();
                _fpAssembler = new FootprintAssembler();
                _fpCore      = new FootprintCore(_fpAssembler, FootprintCoreConfig.Default);
                _fpCore.Initialize(Instrument.MasterInstrument.TickSize, 600, Data.BarsPeriodType.Minute, 1);
                _tape        = new TapeRecorder();
                _entryAdvisor = new FootprintEntryAdvisor(FootprintEntryAdvisorConfig.Default);
                _orbProcessor = new VolumeProfileProcessor(Instrument.MasterInstrument.TickSize);
                _signalGen = new SignalGenerator(
                    instrument:      inst, 
                    accountSize:     AccountSize, 
                    riskPctPerTrade: RiskPct, 
                    maxContracts:    MaxContracts, 
                    maxDailyLoss:    MaxDailyLoss, 
                    minRRRatio:      RiskDefaults.MIN_RR_RATIO, 
                    minScore:        GradeThresholds.REJECT, 
                    logger:          _log, 
                    slippageModel:   new ScoreSessionSlippageModel(inst));
                _orders       = new OrderManager(this, _log, new ScoreSessionSlippageModel(inst));
                _orderManager = (OrderManager)_orders;
                _ui           = new UIRenderer { ShowVWAP=ShowVWAP, ShowSignalBubbles=ShowSignals, ShowOBZones=ShowOBZones, ShowSRLevels=true, ShowORBLines=true };
                _logic.Initialize(inst, Instrument.MasterInstrument.TickSize, Instrument.MasterInstrument.TickSize * Instrument.MasterInstrument.PointValue);
            }
            else if (State == State.Terminated) { _ui?.DisposeResources(); _log?.Dispose(); }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)
            {
                _tape?.OnSessionOpen(Time[0]); _tapeBid = 0; _tapeAsk = 0;
                _feed.OnSessionOpen(); _signalGen.OnSessionOpen(); _orders.OnSessionOpen();
                _activeSignal = null; _lastSignalBar = -1;
                System.Array.Clear(_tradesRingBuffer, 0, AVG_TRADES_PERIOD);
                _tradesRingIndex = 0; _tradesRingFilled = 0; _tradesRingSum = 0.0;
                System.Array.Clear(_cvdRingBuffer, 0, CVD_DIVERGENCE_PERIOD);
                System.Array.Clear(_priceRingBuffer, 0, CVD_DIVERGENCE_PERIOD);
                _cvdRingIndex = 0; _cvdRingFilled = false;
                _imbalZones?.OnSessionOpen();
                _fvgZones?.OnSessionOpen();
                _obZones?.OnSessionOpen();
                _orbProcessor?.Reset();
                _lastFpResult = FootprintResult.Zero; _lastSrResult = SupportResistanceResult.Empty;
                _entryAdvisor?.OnSessionOpen(); _srEngine?.OnSessionOpen();
            }

            if (BarsInProgress != VOLUMETRIC_BAR_INDEX) _feed.OnBarUpdate(BarsInProgress);

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
                _lastFpResult = fpResult;
                _entryAdvisor?.OnNewFootprint(in _lastFpResult);
                if (fpResult.IsValid) ((DataFeed)_feed).SetVolumetricData(fpResult.TotalBuyVol, fpResult.TotalSellVol, fpResult.BarDelta);
            }

            var snapshot = _feed.GetSnapshot();
            if (_srEngine != null && snapshot.IsValid && !_srBootstrapped) { _srEngine.Bootstrap(ref snapshot); _srBootstrapped = true; }
            if (_log != null) _log.CurrentBar = CurrentBar;
            if (_imbalZones != null) _imbalZones.Update(snapshot.Primary, in _lastFpResult);
            if (_fvgZones != null) _fvgZones.Update(snapshot.Primary, snapshot.ATR, CurrentBar);

            if (_lastFpResult.IsValid && snapshot.IsValid)
                _divTracker.OnBar(_lastFpResult, snapshot.ATR, CurrentBar);

            if (snapshot.IsValid)
                _structLabeler.Update(ref snapshot, snapshot.Primary, CurrentBar);

            if (_obZones != null && snapshot.IsValid)
                _obZones.Update(ref snapshot, snapshot.Primary, CurrentBar);

            OnPopulateIndicatorBag(ref snapshot);
            _forwardTracker?.OnBar(
                High[0], Low[0], Close[0], Time[0],
                CurrentBar, Bars.IsFirstBarOfSession);
            _log?.BarContext_Tick(snapshot, CurrentBar);
            _log?.OrderFlowBar(snapshot.Primary.Time, snapshot); // RESTORED
            _log?.StructuralBar(snapshot.Primary.Time, in _lastSrResult);

            // ── ORB Volume Accumulation (High-Fidelity) ──
            // Driven from BarsInProgress == 0. We walk the 1-min volumetric series
            // for any bars that closed inside the 09:30–10:00 ET window and
            // accumulate price-level volume into the ORB profile.
            if (snapshot.IsValid && !snapshot.ORBComplete && _orbProcessor != null && !_orbProcessor.IsLocked
                && _volBarsType != null && CurrentBars[VOLUMETRIC_BAR_INDEX] >= 0)
            {
                var volBarsObj = BarsArray[VOLUMETRIC_BAR_INDEX];
                var volBars    = _volBarsType;
                int volIdx     = CurrentBars[VOLUMETRIC_BAR_INDEX];
                DateTime primaryBarStart = Time[1];   // close of previous primary bar = start of current
                DateTime primaryBarEnd   = Time[0];

                double tickSize = Instrument.MasterInstrument.TickSize;

                // Walk backward through volumetric bars that closed within this primary bar's window
                for (int i = volIdx; i >= 0; i--)
                {
                    DateTime volBarTime = volBarsObj.GetTime(i);
                    if (volBarTime <= primaryBarStart) break;
                    if (volBarTime > primaryBarEnd)    continue;

                    TimeSpan tod = volBarTime.TimeOfDay;
                    if (tod < SessionTimes.REGULAR_OPEN || tod >= SessionTimes.ORB_END) continue;

                    var v = volBars.Volumes[i];
                    if (v == null) continue;

                    double volBarHigh = volBarsObj.GetHigh(i);
                    double volBarLow  = volBarsObj.GetLow(i);
                    int    levels     = 0;
                    double accumVol   = 0;

                    for (double price = volBarLow; price <= volBarHigh + (tickSize / 2.0); price += tickSize)
                    {
                        double totalVol = (double)v.GetTotalVolumeForPrice(price);
                        if (totalVol > 0) 
                        { 
                            _orbProcessor.AddVolume(price, totalVol); 
                            levels++; 
                            accumVol += totalVol;
                        }
                    }
                    if (levels > 0) 
                    {
                        _orbProcessor.FinalizeBar();
                        _log?.Warn(Time[0], "ORB_VP_DIAG: bar accumulated levels={0} vol={1:F0} time={2}", levels, accumVol, volBarTime);
                    }
                }
            }

            // Lock as soon as the range completes and at least one bar has been accumulated
            if (snapshot.IsValid && _orbProcessor != null && !_orbProcessor.IsLocked && snapshot.ORBComplete)
            {
                if (_orbProcessor.IsReady)
                {
                    _orbProcessor.Lock();
                    _log?.Warn(Time[0], "ORB_VP LOCKED (RTH): POC={0:F2} VAH={1:F2} VAL={2:F2}",
                        _orbProcessor.POC, _orbProcessor.VAHigh, _orbProcessor.VALow);
                }
                else
                {
                    // Rate-limit this so it doesn't spam — log once per session
                    if (Bars.IsFirstBarOfSession || (CurrentBar % 100 == 0))
                        _log?.Warn(Time[0], "ORB_VP_DIAG: ORBComplete=TRUE but processor NOT READY (no bars accumulated)");
                }
            }

            // Re-publish ORB levels after lock so Evaluate sees them on the lock bar
            if (_orbProcessor != null && _orbProcessor.IsLocked)
            {
                snapshot.Set(SnapKeys.ORBPoc,    _orbProcessor.POC);
                snapshot.Set(SnapKeys.ORBVaHigh, _orbProcessor.VAHigh);
                snapshot.Set(SnapKeys.ORBVaLow,  _orbProcessor.VALow);
            }

            if (Bars.IsFirstBarOfSession)
            {
                _tracker?.EmitSessionSummary(Time[0], _log); _tracker?.ResetSession();
                _divTracker.Reset();
                _logic.OnSessionOpen(snapshot); _log?.SessionOpen(Time[0], snapshot.VWAP, snapshot.ATR);
            }

            if (_orders.HasOpenPosition || _orders.HasPendingEntry)
            {
                _orderManager.ManagePosition(snapshot, _activeSignal, in _lastFpResult, in _lastSrResult);
                return;
            }

            if (CurrentBar == _lastSignalBar) return;
            if (_signalGen.IsBlocked) return;

            _engine.Evaluate(snapshot);

            // ── ORB Diagnostics ──
            if (snapshot.ORBComplete && (snapshot.Primary.Session == SessionPhase.EarlySession || snapshot.Primary.Session == SessionPhase.MidSession))
            {
                var orbSet = _engine.GetSet("ORB_Value_v2");
                if (orbSet != null)
                    _log?.Warn(Time[0], "ORB_DIAG: {0}", orbSet.LastDiagnostic);
            }
            int candidateCountForRanking = ApplyFootprintEntryAdvisor(Time[0]);
            _rankingEngine.SetVolumetricMode(snapshot.GetFlag(SnapKeys.HasVolumetric));
            RawDecision decision = _rankingEngine.Rank(_engine.CandidateBuffer, candidateCountForRanking, snapshot, in _lastSrResult);
            
            if (decision.IsValid)
            {
                SignalObject signal = _signalGen.Process(decision, snapshot);
                if (signal != null)
                {
                    _orders.SubmitEntry(signal);
                    _activeSignal = signal;
                    _lastSignalClosed = signal; // Keep reference for async logging
                    _lastSignalBar = CurrentBar;
                    _ui.AddSignal(signal);
                }
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (_tape == null) return;
            if (e.MarketDataType == MarketDataType.Bid)  { _tapeBid = e.Price; return; }
            if (e.MarketDataType == MarketDataType.Ask)  { _tapeAsk = e.Price; return; }
            if (e.MarketDataType != MarketDataType.Last) return;
            if (_tapeBid > 0 && _tapeAsk > 0)
                _tape.OnBbo(_tapeBid, _tapeAsk);
            _tape.OnTick(e.Time, e.Price, e.Volume, _tapeBid, _tapeAsk);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            bool wasOpen = _orders.HasOpenPosition;
            _orders.OnOrderUpdate(order.Name, orderState, averageFillPrice, filled);

            if ((orderState == OrderState.Filled || orderState == OrderState.PartFilled) && _activeSignal != null && !_activeSignal.IsFilled)
            {
                if (_tradeCountAtEntry == -1 || !wasOpen) _tradeCountAtEntry = SystemPerformance.AllTrades.Count;
                if (orderState == OrderState.Filled)
                {
                    _log?.OrderFilled(Time[0], order.Name, _activeSignal.Direction, filled, averageFillPrice, true);
                    _signalGen.OnFill(_activeSignal, averageFillPrice);
                    _logic.OnFill(_activeSignal, averageFillPrice);
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition pos, string orderId, DateTime time)
        {
            // FIX (#N13): Use _lastSignalClosed if _activeSignal was already nulled by async reset
            SignalObject sig = _activeSignal ?? _lastSignalClosed;
            if (sig == null) return;

            if (pos == MarketPosition.Flat)
            {
                double pnl = 0.0;
                int tradeCount = SystemPerformance.AllTrades.Count;
                if (_tradeCountAtEntry >= 0)
                {
                    for (int i = _tradeCountAtEntry; i < tradeCount; i++)
                        pnl += SystemPerformance.AllTrades[i].ProfitCurrency;
                }

                _log?.OrderFilled(time, executionId, sig.Direction, quantity, price, false);
                _log?.PositionClosed(time, sig, price, pnl, orderId);

                SessionPhase closePhase = SessionPhase.MidSession;
                if (_feed != null) { var snap = _feed.GetSnapshot(); if (snap.IsValid) closePhase = snap.Primary.Session; }
                _tracker?.RecordTrade(sig, pnl, Instrument.MasterInstrument.TickSize * Instrument.MasterInstrument.PointValue, closePhase);

                _signalGen.OnClose(sig, pnl, time);
                _logic.OnClose(sig, price, pnl);
                
                _activeSignal = null;
                _tradeCountAtEntry = -1;
            }
        }

        private int ApplyFootprintEntryAdvisor(DateTime time)
        {
            if (_entryAdvisor == null || _engine == null) return _engine != null ? _engine.CandidateCount : 0;
            int fpWrite = 0;
            for (int i = 0; i < _engine.CandidateCount; i++)
            {
                RawDecision c = _engine.CandidateBuffer[i];
                if (!c.IsValid) continue;
                
                // Bypass FootprintEntryAdvisor for ORB signals — they have their own delta confirmation
                if (c.ConditionSetId != null && c.ConditionSetId.StartsWith("ORB_"))
                {
                    _engine.CandidateBuffer[fpWrite++] = c;
                    continue;
                }
                
                FootprintEntryDecision ea = _entryAdvisor.Evaluate(c.Direction, c.ConditionSetId ?? string.Empty);
                if (ea.IsVetoed) continue;
                c.RawScore = (int)Math.Round(c.RawScore * ea.Multiplier, MidpointRounding.AwayFromZero);
                _engine.CandidateBuffer[fpWrite++] = c;
            }
            return fpWrite;
        }

        protected virtual void OnPopulateIndicatorBag(ref MarketSnapshot snapshot)
        {
            if (CurrentBar < 1) return;
            if (_smf != null)
            {
                snapshot.Set(SnapKeys.Regime, _smf.LastSignalSeries[0]);
                snapshot.Set(SnapKeys.MfSm, _smf.MfSmSeries[0]);
                snapshot.Set(SnapKeys.NonConfLong, _smf.NonConfirmationLongSeries[0] > 0.5 ? 1.0 : 0.0);
                snapshot.Set(SnapKeys.NonConfShort, _smf.NonConfirmationShortSeries[0] > 0.5 ? 1.0 : 0.0);
                snapshot.Set(SnapKeys.Basis, _smf.BasisClose[0]);
                snapshot.Set(SnapKeys.Upper, _smf.UpperBand[0]);
                snapshot.Set(SnapKeys.Lower, _smf.LowerBand[0]);
            }
            bool hasVol = _lastFpResult.IsValid && (_lastFpResult.TotalBuyVol + _lastFpResult.TotalSellVol) > 0.0;
            snapshot.Set(SnapKeys.HasVolumetric, hasVol ? 1.0 : 0.0);
            snapshot.Set(SnapKeys.CumDelta, _lastFpResult.CumDelta);
            snapshot.Set(SnapKeys.BarDelta, _lastFpResult.IsValid ? _lastFpResult.BarDelta : snapshot.Primary.BarDelta);
            snapshot.Set(SnapKeys.VolDeltaSh, _lastFpResult.DeltaSh);
            snapshot.Set(SnapKeys.VolDeltaSl, _lastFpResult.DeltaSl);
            snapshot.Set(SnapKeys.VolMaxSeenDelta, _lastFpResult.MaxSeenDelta);
            snapshot.Set(SnapKeys.VolMinSeenDelta, _lastFpResult.MinSeenDelta);
            snapshot.Set(SnapKeys.VolBuyVol, _lastFpResult.TotalBuyVol);
            snapshot.Set(SnapKeys.VolSellVol, _lastFpResult.TotalSellVol);
            snapshot.Set(SnapKeys.VolTrades, _lastFpResult.Trades);
            _imbalZones?.PublishToSnap(ref snapshot);
            _fvgZones?.PublishToSnap(ref snapshot);
            _obZones?.PublishToSnap(ref snapshot);
            snapshot.Set(SnapKeys.AbsorptionScore, _lastFpResult.AbsorptionScore);
            snapshot.Set(SnapKeys.StackedImbalanceBull, _lastFpResult.StackedBullRun);
            snapshot.Set(SnapKeys.StackedImbalanceBear, _lastFpResult.StackedBearRun);
            snapshot.Set(SnapKeys.HasBullStack, _lastFpResult.HasBullStack ? 1.0 : 0.0);
            snapshot.Set(SnapKeys.HasBearStack, _lastFpResult.HasBearStack ? 1.0 : 0.0);
            // Location-aware footprint fields (for consumers that need to 
            // know WHERE the heavy volume or stacked imbalances occurred,
            // not just whether they were present)
            if (_lastFpResult.IsValid)
            {
                snapshot.Set(SnapKeys.MaxBidVolPrice, _lastFpResult.MaxBidVolPrice);
                snapshot.Set(SnapKeys.MaxAskVolPrice, _lastFpResult.MaxAskVolPrice);
                snapshot.Set(SnapKeys.MaxCombinedVolPrice, _lastFpResult.MaxCombinedVolPrice);
                snapshot.Set(SnapKeys.BullStackLow, _lastFpResult.BullStackLow);
                snapshot.Set(SnapKeys.BullStackHigh, _lastFpResult.BullStackHigh);
                snapshot.Set(SnapKeys.BearStackLow, _lastFpResult.BearStackLow);
                snapshot.Set(SnapKeys.BearStackHigh, _lastFpResult.BearStackHigh);
            }
            else
            {
                snapshot.Set(SnapKeys.MaxBidVolPrice, 0.0);
                snapshot.Set(SnapKeys.MaxAskVolPrice, 0.0);
                snapshot.Set(SnapKeys.MaxCombinedVolPrice, 0.0);
                snapshot.Set(SnapKeys.BullStackLow, 0.0);
                snapshot.Set(SnapKeys.BullStackHigh, 0.0);
                snapshot.Set(SnapKeys.BearStackLow, 0.0);
                snapshot.Set(SnapKeys.BearStackHigh, 0.0);
            }

            // Phase 2.7 — Trapped Traders
            if (_lastFpResult.IsValid)
            {
                snapshot.Set(SnapKeys.TrappedLongs, _lastFpResult.TrappedLongs ? 1.0 : 0.0);
                snapshot.Set(SnapKeys.TrappedShorts, _lastFpResult.TrappedShorts ? 1.0 : 0.0);
                snapshot.Set(SnapKeys.TrapLevel, _lastFpResult.TrapLevel);
            }
            else
            {
                snapshot.Set(SnapKeys.TrappedLongs, 0.0);
                snapshot.Set(SnapKeys.TrappedShorts, 0.0);
                snapshot.Set(SnapKeys.TrapLevel, 0.0);
            }

            // Phase 2.8 — Iceberg
            if (_lastFpResult.IsValid)
            {
                snapshot.Set(SnapKeys.BullIceberg, _lastFpResult.BullIceberg ? 1.0 : 0.0);
                snapshot.Set(SnapKeys.BearIceberg, _lastFpResult.BearIceberg ? 1.0 : 0.0);
                snapshot.Set(SnapKeys.IcebergPrice, _lastFpResult.IcebergPrice);
            }
            else
            {
                snapshot.Set(SnapKeys.BullIceberg, 0.0);
                snapshot.Set(SnapKeys.BearIceberg, 0.0);
                snapshot.Set(SnapKeys.IcebergPrice, 0.0);
            }

            // Phase 2.9 — Exhaustion + Unfinished Auction
            if (_lastFpResult.IsValid)
            {
                snapshot.Set(SnapKeys.BullExhaustion,   _lastFpResult.BullExhaustion   ? 1.0 : 0.0);
                snapshot.Set(SnapKeys.BearExhaustion,   _lastFpResult.BearExhaustion   ? 1.0 : 0.0);
                snapshot.Set(SnapKeys.UnfinishedTop,    _lastFpResult.UnfinishedTop    ? 1.0 : 0.0);
                snapshot.Set(SnapKeys.UnfinishedBottom, _lastFpResult.UnfinishedBottom ? 1.0 : 0.0);
            }
            else
            {
                snapshot.Set(SnapKeys.BullExhaustion,   0.0);
                snapshot.Set(SnapKeys.BearExhaustion,   0.0);
                snapshot.Set(SnapKeys.UnfinishedTop,    0.0);
                snapshot.Set(SnapKeys.UnfinishedBottom, 0.0);
            }

            snapshot.Set(SnapKeys.BullDivergence, _divTracker.IsBullDivergenceActive(CurrentBar) ? 1.0 : 0.0);
            snapshot.Set(SnapKeys.BearDivergence, _divTracker.IsBearDivergenceActive(CurrentBar) ? 1.0 : 0.0);
            snapshot.Set(SnapKeys.H1EmaBias, _h1EmaBias);
            snapshot.Set(SnapKeys.H2HrEmaBias, _h2hrEmaBias);
            snapshot.Set(SnapKeys.H4HrEmaBias, _h4hrEmaBias);

            if (_orbProcessor != null && _orbProcessor.IsLocked)
            {
                snapshot.Set(SnapKeys.ORBPoc,    _orbProcessor.POC);
                snapshot.Set(SnapKeys.ORBVaHigh, _orbProcessor.VAHigh);
                snapshot.Set(SnapKeys.ORBVaLow,  _orbProcessor.VALow);
            }

            _lastSrResult = SupportResistanceResult.Empty;
            if (_srEngine != null && snapshot.IsValid && _srBootstrapped) _lastSrResult = _srEngine.Update(ref snapshot);
            if (_srPublishing && _lastSrResult.IsValid)
            {
                snapshot.Set(SnapKeys.POC, _lastSrResult.POC); snapshot.Set(SnapKeys.VAHigh, _lastSrResult.VAHigh); snapshot.Set(SnapKeys.VALow, _lastSrResult.VALow);
                snapshot.Set(SnapKeys.POCSkew, _lastSrResult.POCSkew); snapshot.Set(SnapKeys.H1SwingHigh, _lastSrResult.SwingHighH1); snapshot.Set(SnapKeys.H1SwingLow, _lastSrResult.SwingLowH1);
                snapshot.Set(SnapKeys.PivotPP, _lastSrResult.PivotPP);
            }
        }

        private void UpdateHigherTFEma(double close, ref double emaPrev, double[] ring, ref int idx, ref int count, ref double bias)
        {
            ring[idx] = close; idx = (idx + 1) % ring.Length; if (count < ring.Length) count++;
            double lag = (count >= ring.Length) ? ring[idx] : close;
            double newEma = MathFlow.ZLEmaStep(close, lag, emaPrev, EMA50_PERIOD); emaPrev = newEma;
            if (count >= ring.Length / 2 && !double.IsNaN(newEma) && newEma > 0) bias = close > newEma ? 1.0 : (close < newEma ? -1.0 : 0.0);
            else bias = 0.0;
        }

        protected virtual IStrategyLogic CreateLogic(InstrumentKind inst) {
            var smfEngine = new ConditionSets.SMFNativeEngine();
            return new StrategyEngine(_log, 
                // new ConditionSets.SMF_Native_Impulse(smfEngine), 
                // new ConditionSets.SMF_Native_BandReclaim(smfEngine), 
                // new ConditionSets.SMF_Native_Retest(smfEngine), 
                // new ConditionSets.SMC_BOS(), 
                // new ConditionSets.SMC_OB(), 
                // new ConditionSets.SMC_FVG_Retest(), 
                // new ConditionSets.SMC_IFVG(), 
                // new ConditionSets.SMC_Liquidity_Sweep(), 
                // new ConditionSets.SMC_Session_Sweep(), 
                // new ConditionSets.Wyckoff_Spring(), 
                // new ConditionSets.Wyckoff_Upthrust(), 
                // new ConditionSets.FailedAuction(), 
                // new ConditionSets.EMA_Cross(), 
                // new ConditionSets.ADX_Trend(), 
                new ConditionSets.ORB_Classic(_log),
                new ConditionSets.ORB_Measure(_log)
				); 
        }
    }
}
