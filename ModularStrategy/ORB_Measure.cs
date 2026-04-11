#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    /// <summary>
    /// ORB_MEASURE — Baseline Measurement Module (Instrumentation Only)
    /// 
    /// Tracks the occurrence and quality of ORB Reload setups per the Task 2 spec.
    /// Returns RawDecision.None always. 
    /// </summary>
    public class ORB_Measure : IConditionSet
    {
        public string SetId => "ORB_Measure_v1";
        public string LastDiagnostic => "";

        private readonly StrategyLogger _logger;
        private double _tickSize;
        private double _tickValue;

        // ── Session State ──
        private bool     _levelsCaptured;
        private DateTime _sessionDate;
        private DateTime _lastBarTime;
        private double   _orbH, _orbL, _poc, _vah, _val, _atrAtLock;
        
        private int             _breakoutBar = -1;
        private SignalDirection _breakoutDir = SignalDirection.None;
        private double          _breakoutClose;

        private int    _barsToTouch = -1;
        private bool   _touchDetected;
        private double _maxAdversePrice;
        private double _maxFavorablePrice;
        private int    _barsInWindow;
        private bool   _loggedThisSession;
        private bool   _wasOutsideVA;

        // ── Task 2.5: Post-Touch Tracking ──
        private double _touchClose;
        private double _touchMaePrice;
        private double _touchMfePrice;
        private int    _touchBarsToProfit_1R = -1;
        private int    _postTouchBars;

        // ── Task 2.6: Touch Context ──
        private double _touchBarDelta = -1;
        private double _touchBarVol = -1;
        private double _touchDeltaPct = -1;
        private double _touchAtr = -1;
        private double _atrRegime = -1;
        private int    _touchHourEt = -1;
        private double _distFromPocTicks = -1;
        private int    _bullDivAtTouch = 0;
        private int    _bearDivAtTouch = 0;

        // ── Task 2.7: Additional Touch Analytics ──
        private int    _preTouchWicks;
        private int    _postTouchWicks;
        private double _htfBias = -999;
        private double _touchBarBodyPct = -1;

        // ── Phase 1B-2: Footprint Context at Touch ──
        private double _touchAbsScore = -1;
        private int    _touchStackBull = -1;
        private int    _touchStackBear = -1;
        private int    _touchHasBullStack = 0;
        private int    _touchHasBearStack = 0;
        private int    _touchBullDiv = 0;
        private int    _touchBearDiv = 0;

        public ORB_Measure(StrategyLogger logger)
        {
            _logger = logger;
        }

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            if (_levelsCaptured && !_loggedThisSession)
            {
                EmitMeasurement(_lastBarTime);
            }

            _levelsCaptured = false;
            _sessionDate = snapshot.Primary.Time.Date;
            _orbH = _orbL = _poc = _vah = _val = _atrAtLock = 0;
            _breakoutBar = -1;
            _breakoutDir = SignalDirection.None;
            _breakoutClose = 0;
            _barsToTouch = -1;
            _touchDetected = false;
            _maxAdversePrice = 0;
            _maxFavorablePrice = 0;
            _barsInWindow = 0;
            _loggedThisSession = false;
            _wasOutsideVA = false;

            _touchClose = 0;
            _touchMaePrice = 0;
            _touchMfePrice = 0;
            _touchBarsToProfit_1R = -1;
            _postTouchBars = 0;

            _touchBarDelta = -1;
            _touchBarVol = -1;
            _touchDeltaPct = -1;
            _touchAtr = -1;
            _atrRegime = -1;
            _touchHourEt = -1;
            _distFromPocTicks = -1;
            _bullDivAtTouch = 0;
            _bearDivAtTouch = 0;

            _preTouchWicks = 0;
            _postTouchWicks = 0;
            _htfBias = -999;
            _touchBarBodyPct = -1;

            _touchAbsScore = -1;
            _touchStackBull = -1;
            _touchStackBear = -1;
            _touchHasBullStack = 0;
            _touchHasBearStack = 0;
            _touchBullDiv = 0;
            _touchBearDiv = 0;
        }

        public void OnFill(SignalObject signal, double fillPrice) { }
        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;
            _lastBarTime = p.Time;

            // 1. Capture Levels
            if (!_levelsCaptured && snapshot.ORBComplete)
            {
                double poc = snapshot.Get(SnapKeys.ORBPoc);
                if (poc > 0)
                {
                    _orbH = snapshot.ORBHigh;
                    _orbL = snapshot.ORBLow;
                    _poc  = poc;
                    _vah  = snapshot.Get(SnapKeys.ORBVaHigh);
                    _val  = snapshot.Get(SnapKeys.ORBVaLow);
                    _atrAtLock = snapshot.ATR;
                    _levelsCaptured = true;
                }
            }

            if (!_levelsCaptured) return RawDecision.None;

            // 2. Detect First Breakout
            if (_breakoutDir == SignalDirection.None)
            {
                if (p.Close > _orbH) 
                { 
                    _breakoutDir = SignalDirection.Long; 
                    _breakoutBar = p.CurrentBar; 
                    _breakoutClose = p.Close;
                    _maxAdversePrice = p.Low;
                    _maxFavorablePrice = p.High;
                }
                else if (p.Close < _orbL) 
                { 
                    _breakoutDir = SignalDirection.Short; 
                    _breakoutBar = p.CurrentBar; 
                    _breakoutClose = p.Close;
                    _maxAdversePrice = p.High;
                    _maxFavorablePrice = p.Low;
                }
            }
            // 3. Track Windows
            else if (_barsInWindow < 60)
            {
                _barsInWindow++;
                
                // Excursion tracking from breakout
                if (_breakoutDir == SignalDirection.Long)
                {
                    _maxAdversePrice = Math.Min(_maxAdversePrice, p.Low);
                    _maxFavorablePrice = Math.Max(_maxFavorablePrice, p.High);
                }
                else
                {
                    _maxAdversePrice = Math.Max(_maxAdversePrice, p.High);
                    _maxFavorablePrice = Math.Min(_maxFavorablePrice, p.Low);
                }

                // Touch Detection
                if (!_touchDetected)
                {
                    double buffer = 0.25 * _atrAtLock;
                    double bandLo = (_breakoutDir == SignalDirection.Long) ? (_poc - buffer) : (_val - buffer);
                    double bandHi = (_breakoutDir == SignalDirection.Long) ? (_vah + buffer) : (_poc + buffer);

                    if (p.Low <= bandHi && p.High >= bandLo)
                    {
                        _touchDetected = true;
                        _barsToTouch = _barsInWindow;
                        _touchClose = p.Close;
                        _touchMaePrice = (_breakoutDir == SignalDirection.Long) ? p.Low : p.High;
                        _touchMfePrice = (_breakoutDir == SignalDirection.Long) ? p.High : p.Low;
                        _postTouchBars = 0;

                        // Capture Touch Context
                        _touchBarDelta = snapshot.Get(SnapKeys.BarDelta);
                        _touchBarVol = snapshot.Get(SnapKeys.VolBuyVol) + snapshot.Get(SnapKeys.VolSellVol);
                        _touchDeltaPct = _touchBarVol > 0 ? Math.Abs(_touchBarDelta) / _touchBarVol : 0;
                        _touchAtr = snapshot.ATR;
                        _atrRegime = _atrAtLock > 0 ? _touchAtr / _atrAtLock : 0;
                        _touchHourEt = p.Time.Hour;
                        _distFromPocTicks = (_breakoutDir == SignalDirection.Long) ? (p.Close - _poc) / _tickSize : (_poc - p.Close) / _tickSize;
                        _bullDivAtTouch = snapshot.GetFlag(SnapKeys.BullDivergence) ? 1 : 0;
                        _bearDivAtTouch = snapshot.GetFlag(SnapKeys.BearDivergence) ? 1 : 0;

                        // Capture Task 2.7 Context
                        _htfBias = snapshot.Get(SnapKeys.H4HrEmaBias);
                        double range = p.High - p.Low;
                        if (range > 0)
                        {
                            _touchBarBodyPct = (_breakoutDir == SignalDirection.Long) 
                                ? (p.Close - p.Low) / range 
                                : (p.High - p.Close) / range;
                        }
                        else _touchBarBodyPct = 0;

                        // Phase 1B-2: Footprint context
                        _touchAbsScore = snapshot.Get(SnapKeys.AbsorptionScore);
                        _touchStackBull = (int)snapshot.Get(SnapKeys.StackedImbalanceBull);
                        _touchStackBear = (int)snapshot.Get(SnapKeys.StackedImbalanceBear);
                        _touchHasBullStack = snapshot.GetFlag(SnapKeys.HasBullStack) ? 1 : 0;
                        _touchHasBearStack = snapshot.GetFlag(SnapKeys.HasBearStack) ? 1 : 0;
                        _touchBullDiv = snapshot.GetFlag(SnapKeys.BullDivergence) ? 1 : 0;
                        _touchBearDiv = snapshot.GetFlag(SnapKeys.BearDivergence) ? 1 : 0;
                    }
                }
                else if (_postTouchBars < 12)
                {
                    // 12-bar window after touch
                    _postTouchBars++;

                    if (_breakoutDir == SignalDirection.Long)
                    {
                        _touchMaePrice = Math.Min(_touchMaePrice, p.Low);
                        _touchMfePrice = Math.Max(_touchMfePrice, p.High);
                        
                        double curMae = (_touchClose - _touchMaePrice) / _tickSize;
                        double curMfe = (_touchMfePrice - _touchClose) / _tickSize;
                        if (_touchBarsToProfit_1R == -1 && curMae > 0 && curMfe >= curMae)
                            _touchBarsToProfit_1R = _postTouchBars;
                    }
                    else
                    {
                        _touchMaePrice = Math.Max(_touchMaePrice, p.High);
                        _touchMfePrice = Math.Min(_touchMfePrice, p.Low);

                        double curMae = (_touchMaePrice - _touchClose) / _tickSize;
                        double curMfe = (_touchClose - _touchMfePrice) / _tickSize;
                        if (_touchBarsToProfit_1R == -1 && curMae > 0 && curMfe >= curMae)
                            _touchBarsToProfit_1R = _postTouchBars;
                    }
                }

                // Wick Invalidation Tracking (Noise vs Reversal)
                if (_breakoutDir == SignalDirection.Long)
                {
                    if (_wasOutsideVA && p.Close >= _val && p.Close <= _vah)
                    {
                        if (!_touchDetected) _preTouchWicks++;
                        else _postTouchWicks++;
                    }
                    _wasOutsideVA = (p.Close < _val);
                }
                else if (_breakoutDir == SignalDirection.Short)
                {
                    if (_wasOutsideVA && p.Close <= _vah && p.Close >= _val)
                    {
                        if (!_touchDetected) _preTouchWicks++;
                        else _postTouchWicks++;
                    }
                    _wasOutsideVA = (p.Close > _vah);
                }
            }

            // 4. Emit Log
            if (_breakoutDir != SignalDirection.None && _barsInWindow >= 60 && !_loggedThisSession)
            {
                EmitMeasurement(p.Time);
            }

            return RawDecision.None;
        }

        private void EmitMeasurement(DateTime time)
        {
            double maxAdvTicks = 0;
            double maxFavTicks = 0;

            if (_breakoutDir == SignalDirection.Long)
            {
                maxAdvTicks = (_breakoutClose - _maxAdversePrice) / _tickSize;
                maxFavTicks = (_maxFavorablePrice - _breakoutClose) / _tickSize;
            }
            else if (_breakoutDir == SignalDirection.Short)
            {
                maxAdvTicks = (_maxAdversePrice - _breakoutClose) / _tickSize;
                maxFavTicks = (_breakoutClose - _maxFavorablePrice) / _tickSize;
            }

            double tMae = -1, tMfe = -1;
            string tOutcome = "NONE";

            if (_touchDetected)
            {
                if (_breakoutDir == SignalDirection.Long)
                {
                    tMae = (_touchClose - _touchMaePrice) / _tickSize;
                    tMfe = (_touchMfePrice - _touchClose) / _tickSize;
                }
                else
                {
                    tMae = (_touchMaePrice - _touchClose) / _tickSize;
                    tMfe = (_touchClose - _touchMfePrice) / _tickSize;
                }

                if (tMfe >= 2.0 * tMae && tMae > 0) tOutcome = "WIN";
                else if (tMae >= 1.5 * tMfe && tMfe > 0) tOutcome = "LOSS";
                else tOutcome = "FLAT";
            }

            string line = string.Format("ORB_MEASURE: date={0:yyyy-MM-dd},dir={1},breakoutBar={2},barsToTouch={3},maxAdvTicks={4:F0},maxFavTicks={5:F0},orbH={6:F2},orbL={7:F2},poc={8:F2},vah={9:F2},val={10:F2},atr={11:F2},preTouchWicks={12},postTouchWicks={13},touchMaeTicks={14:F0},touchMfeTicks={15:F0},touchBarsToProfit_1R={16},touchOutcome={17},touchBarDelta={18:F0},touchBarVol={19:F0},touchDeltaPct={20:F4},touchAtr={21:F2},atrRegime={22:F2},touchHourEt={23},distFromPocTicks={24:F0},bullDivAtTouch={25},bearDivAtTouch={26},htfBias={27:F0},touchBarBodyPct={28:F4},touchAbsScore={29:F2},touchStackBull={30},touchStackBear={31},touchHasBullStack={32},touchHasBearStack={33},touchBullDiv={34},touchBearDiv={35}", _sessionDate, _breakoutDir, _breakoutBar, _barsToTouch, maxAdvTicks, maxFavTicks, _orbH, _orbL, _poc, _vah, _val, _atrAtLock, _preTouchWicks, _postTouchWicks, tMae, tMfe, _touchBarsToProfit_1R, tOutcome, _touchBarDelta, _touchBarVol, _touchDeltaPct, _touchAtr, _atrRegime, _touchHourEt, _distFromPocTicks, _bullDivAtTouch, _bearDivAtTouch, _htfBias, _touchBarBodyPct, _touchAbsScore, _touchStackBull, _touchStackBear, _touchHasBullStack, _touchHasBearStack, _touchBullDiv, _touchBearDiv);

            _logger?.Warn(time, line);
            _loggedThisSession = true;
        }
    }
}
