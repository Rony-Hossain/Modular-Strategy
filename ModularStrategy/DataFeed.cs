#region Using declarations
using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// DATA FEED — NT8 normalization layer.
    ///
    /// Responsibility: translate NT8's bar series, indicators, and market data
    /// into instrument-agnostic BarSnapshot / MarketSnapshot structs.
    ///
    /// The strategy logic layer NEVER touches NT8 APIs directly.
    /// Everything flows through this class.
    ///
    /// Multi-timeframe setup (caller configures in OnStateChange):
    ///   BarsArray[0] = primary timeframe  (e.g. 5-min)
    ///   BarsArray[1] = higher TF 1        (e.g. 15-min)
    ///   BarsArray[2] = higher TF 2        (e.g. 1-hour)
    ///   BarsArray[3] = daily
    ///
    /// Array depth: FeedConstants.SNAPSHOT_DEPTH bars of history maintained per timeframe.
    /// </summary>
    public class DataFeed : IDataFeed
    {
        // ===================================================================
        // CONSTANTS
        // ===================================================================
        // ===================================================================
        // PRIVATE STATE
        // ===================================================================

        private readonly NinjaScriptBase  _host;
        private readonly InstrumentKind   _instrument;
        private readonly int              _primaryBarSeriesIndex;
        private readonly int              _higher1BarSeriesIndex;
        private readonly int              _higher2BarSeriesIndex;
        private readonly int              _dailyBarSeriesIndex;
		private readonly int              _higher3BarSeriesIndex;
        private readonly int              _higher4BarSeriesIndex;

        // Rolling price arrays (index 0 = current)
        private readonly double[] _closes0,  _highs0,  _lows0,  _opens0,  _vols0;
        private readonly double[] _closes1,  _highs1,  _lows1,  _opens1,  _vols1;
        private readonly double[] _closes2,  _highs2,  _lows2,  _opens2,  _vols2;
		private readonly double[] _closes3, _highs3, _lows3, _opens3, _vols3;
        private readonly double[] _closes4, _highs4, _lows4, _opens4, _vols4;
        private readonly double[] _closesD,  _highsD,  _lowsD,  _opensD,  _volsD;

        // Order flow rolling arrays — primary TF only (index 0 = current bar)
        // Populated from GetCurrentAskVolume() / GetCurrentBidVolume() when tick replay is on.
        // All values are 0.0 without Order Flow+ tick replay — safe for condition sets to read.
        private readonly double[] _askVols0;    // aggressive buy volume per bar
        private readonly double[] _bidVols0;    // aggressive sell volume per bar
        private readonly double[] _barDeltas0;  // AskVol − BidVol per bar (for divergence detection)

        // VWAP incremental state
        private double _pvSum,  _volSum;
        private int    _welfCount;
        private double _welfMean, _welfM2;
        private double _vwap, _sdSession;

        // ATR state (primary TF, Wilder)
        private double   _atrPrev  = double.NaN;
        private double[] _trBuffer;
        private int      _trBufIdx, _trBufCount;
        private double   _trBufSum;

        // ORB state
        private double   _orbHigh, _orbLow;
        private bool     _orbComplete;
        private int      _orbCompleteBar;
        private DateTime _orbCompleteTime;
        private DateTime _sessionOpenTime;

        // Previous day levels
        private double _prevDayHigh, _prevDayLow, _prevDayClose;
        private bool   _prevDaySet;

        // Session state
        private bool   _sessionOpenFired;
        private int    _barsSinceOpen;

        // Cached snapshots
        private MarketSnapshot _lastSnapshot;
        private ORBContext     _orbContext;
        private bool           _isReady;

        // ===================================================================
        // LEVEL ACCUMULATOR
        // Private helper struct — accumulates OHLCV per period/session.
        // Converts to a PeriodLevel snapshot via ToLevel().
        // BuyVolume / SellVolume / OrderCount are populated in Layer 2
        // (MathOrderFlow connection) — always zero here in Layer 1.
        // ===================================================================

        private struct LevelAccum
        {
            public double High, Low, Open, Close, Volume, BuyVolume, SellVolume;
            public int    Bars, OrderCount;
            public bool   HasData;

            /// <summary>Reset all accumulators. openPrice seeds Open for the period.</summary>
            public void Reset(double openPrice = 0.0)
            {
                High       = double.MinValue;
                Low        = double.MaxValue;
                Open       = openPrice;
                Close      = openPrice;
                Volume     = 0.0;
                BuyVolume  = 0.0;
                SellVolume = 0.0;
                Bars       = 0;
                OrderCount = 0;
                HasData    = false;
            }

            /// <summary>
            /// Incorporate one bar's data. Call every bar while inside the period.
            /// <paramref name="open"/> is used to seed Open on the first bar — it must be
            /// _host.Open[0], not Close. Reset() also seeds Open, but only after a roll;
            /// the very first bar of each period reaches here with HasData=false, so the
            /// open parameter is the only correct initialisation path.
            /// buyVol / sellVol default to zero — populated in Layer 2.
            /// </summary>
            public void Update(double open, double high, double low, double close, double vol,
                               double buyVol = 0.0, double sellVol = 0.0)
            {
                if (!HasData || high > High) High = high;
                if (!HasData || low  < Low)  Low  = low;
                if (!HasData) Open = open;   // use the bar's actual open, not its close
                Close       = close;
                Volume     += vol;
                BuyVolume  += buyVol;
                SellVolume += sellVol;
                Bars++;
                HasData = true;
            }

            /// <summary>Convert the accumulator to a read-only PeriodLevel snapshot.</summary>
            public PeriodLevel ToLevel() => new PeriodLevel
            {
                High       = HasData ? High       : 0.0,
                Low        = HasData ? Low        : 0.0,
                Open       = Open,
                Close      = Close,
                Volume     = Volume,
                BuyVolume  = BuyVolume,
                SellVolume = SellVolume,
                Bars       = Bars,
                OrderCount = OrderCount,
                IsValid    = HasData
            };
        }

        // ── Period accumulators ──────────────────────────────────────────────

        private LevelAccum _today     = new LevelAccum();
        private LevelAccum _prevDay   = new LevelAccum();
        private LevelAccum _thisWeek  = new LevelAccum();
        private LevelAccum _prevWeek  = new LevelAccum();
        private LevelAccum _thisMonth = new LevelAccum();
        private LevelAccum _prevMonth = new LevelAccum();

        // ── Session accumulators ─────────────────────────────────────────────

        private LevelAccum _sydney  = new LevelAccum();
        private LevelAccum _tokyo   = new LevelAccum();
        private LevelAccum _london  = new LevelAccum();
        private LevelAccum _newYork = new LevelAccum();

        // ── Session / calendar roll tracking ────────────────────────────────

        private int  _lastRollMonth   = -1;    // month number at last month-roll event
        // _lastRollWasSunday removed — never used (week roll is detected directly from DayOfWeek)
        private bool _sydneyWasActive  = false;
        private bool _tokyoWasActive   = false;
        private bool _londonWasActive  = false;
        private bool _nyWasActive      = false;

        // NOTE — two PrevDay sources intentionally coexist in MarketSnapshot:
        //   snapshot.PrevDayHigh / PrevDayLow / PrevDayClose
        //     → from UpdatePrevDayLevels() / daily BarsArray[3]
        //     → RTH only (9:30–16:00 ET), matches the official daily candle
        //   snapshot.PrevDay  (PeriodLevel)
        //     → from _prevDay accumulator here
        //     → full 24-hr Globex session (18:00→18:00 ET)
        // Use RTH fields when comparing to charted daily levels.
        // Use snapshot.PrevDay when you need overnight range or full-session volume/delta.

        // ===================================================================
        // CONSTRUCTION
        // ===================================================================

        public DataFeed(
            NinjaScriptBase host,
            InstrumentKind  instrument,
            int primaryIdx  = 0,
            int higher1Idx  = 1,
            int higher2Idx  = 2,
            int dailyIdx    = 3,
            int higher3Idx  = 4,   // 2-hour — BarsArray[4]
            int higher4Idx  = 5)   // 4-hour — BarsArray[5]
        {
            _host                    = host;
            _instrument              = instrument;
            _primaryBarSeriesIndex   = primaryIdx;
            _higher1BarSeriesIndex   = higher1Idx;
            _higher2BarSeriesIndex   = higher2Idx;
            _dailyBarSeriesIndex     = dailyIdx;
			_higher3BarSeriesIndex   = higher3Idx;
            _higher4BarSeriesIndex   = higher4Idx;

            // Allocate rolling arrays
            _closes0 = new double[FeedConstants.SNAPSHOT_DEPTH]; _highs0 = new double[FeedConstants.SNAPSHOT_DEPTH];
            _lows0   = new double[FeedConstants.SNAPSHOT_DEPTH]; _opens0 = new double[FeedConstants.SNAPSHOT_DEPTH];
            _vols0   = new double[FeedConstants.SNAPSHOT_DEPTH];

            _closes1 = new double[FeedConstants.SNAPSHOT_DEPTH]; _highs1 = new double[FeedConstants.SNAPSHOT_DEPTH];
            _lows1   = new double[FeedConstants.SNAPSHOT_DEPTH]; _opens1 = new double[FeedConstants.SNAPSHOT_DEPTH];
            _vols1   = new double[FeedConstants.SNAPSHOT_DEPTH];

            _closes2 = new double[FeedConstants.SNAPSHOT_DEPTH]; _highs2 = new double[FeedConstants.SNAPSHOT_DEPTH];
            _lows2   = new double[FeedConstants.SNAPSHOT_DEPTH]; _opens2 = new double[FeedConstants.SNAPSHOT_DEPTH];
            _vols2   = new double[FeedConstants.SNAPSHOT_DEPTH];
			
			_closes3 = new double[FeedConstants.SNAPSHOT_DEPTH]; _highs3 = new double[FeedConstants.SNAPSHOT_DEPTH];
            _lows3   = new double[FeedConstants.SNAPSHOT_DEPTH]; _opens3 = new double[FeedConstants.SNAPSHOT_DEPTH];
            _vols3   = new double[FeedConstants.SNAPSHOT_DEPTH];

            _closes4 = new double[FeedConstants.SNAPSHOT_DEPTH]; _highs4 = new double[FeedConstants.SNAPSHOT_DEPTH];
            _lows4   = new double[FeedConstants.SNAPSHOT_DEPTH]; _opens4 = new double[FeedConstants.SNAPSHOT_DEPTH];
            _vols4   = new double[FeedConstants.SNAPSHOT_DEPTH];

            _closesD = new double[FeedConstants.SNAPSHOT_DEPTH]; _highsD = new double[FeedConstants.SNAPSHOT_DEPTH];
            _lowsD   = new double[FeedConstants.SNAPSHOT_DEPTH]; _opensD = new double[FeedConstants.SNAPSHOT_DEPTH];
            _volsD   = new double[FeedConstants.SNAPSHOT_DEPTH];

            _trBuffer = new double[14];   // ATR period

            // Order flow arrays — same depth as price arrays
            _askVols0   = new double[FeedConstants.SNAPSHOT_DEPTH];
            _bidVols0   = new double[FeedConstants.SNAPSHOT_DEPTH];
            _barDeltas0 = new double[FeedConstants.SNAPSHOT_DEPTH];
        }

        // ===================================================================
        // IDataFeed
        // ===================================================================

        public bool            IsReady      => _isReady;
        public MarketSnapshot  GetSnapshot() => _lastSnapshot;
        public ORBContext      GetORB()      => _orbContext;

        /// <summary>
        /// Inject Volumetric Bars data into the order flow rolling arrays.
        /// Called by HostStrategy AFTER OnBarUpdate(0) and BEFORE GetSnapshot().
        ///
        /// This overwrites the broken GetCurrentAskVolume()/GetCurrentBidVolume()
        /// values (which return total bar volume in backtest, making BarDelta = 0)
        /// with accurate bid/ask decomposition from the Volumetric BarsType.
        ///
        /// Volumetric processes its own internal tick series so these values are
        /// correct in both backtest and live — no Tick Replay needed.
        /// </summary>
        public void SetVolumetricData(double buyVolume, double sellVolume, double barDelta)
        {
            // Overwrite the most recent entry (index 0) in the rolling arrays.
            // These arrays were just populated by UpdatePrimaryArrays() with broken
            // GetCurrentAskVolume() values — replace them with real Volumetric data.
            if (_askVols0 != null && _askVols0.Length > 0)
                _askVols0[0] = buyVolume;
            if (_bidVols0 != null && _bidVols0.Length > 0)
                _bidVols0[0] = sellVolume;
            if (_barDeltas0 != null && _barDeltas0.Length > 0)
                _barDeltas0[0] = barDelta;

            // CRITICAL: The snapshot was already built by BuildSnapshot() during
            // OnBarUpdate(0), BEFORE this method was called. The cached _lastSnapshot
            // still has the old broken values. Patch it in-place so GetSnapshot()
            // returns the corrected data.
            // MarketSnapshot and BarSnapshot are structs — must overwrite fields
            // through a local copy and reassign.
            var snap = _lastSnapshot;
            var prim = snap.Primary;
            prim.AskVolume = buyVolume;
            prim.BidVolume = sellVolume;
            prim.BarDelta  = barDelta;
            snap.Primary   = prim;
            _lastSnapshot  = snap;
        }

        public void OnSessionOpen()
        {
            _pvSum   = 0.0;
            _volSum  = 0.0;
            _welfCount = 0;
            _welfMean  = 0.0;
            _welfM2    = 0.0;
            _vwap      = 0.0;
            _sdSession = 0.0;

            _orbHigh         = double.MinValue;
            _orbLow          = double.MaxValue;
            _orbComplete     = false;
            _orbCompleteBar  = 0;
            _barsSinceOpen   = 0;
            _sessionOpenFired = true;
            _sessionOpenTime = _host.Time[0];
        }

        /// <summary>
        /// Called from the host strategy's OnBarUpdate for the given bar series.
        /// </summary>
        public void OnBarUpdate(int barSeriesIndex)
        {
            // Only update arrays on the primary bar series
            if (barSeriesIndex == _primaryBarSeriesIndex)
            {
                UpdatePrimaryArrays();
            }
            else if (barSeriesIndex == _higher1BarSeriesIndex)
                UpdateHigher1Arrays();
            else if (barSeriesIndex == _higher2BarSeriesIndex)
                UpdateHigher2Arrays();
            else if (barSeriesIndex == _dailyBarSeriesIndex)
                UpdateDailyArrays();
			else if (barSeriesIndex == _higher3BarSeriesIndex) 
				UpdateHigher3Arrays();
            else if (barSeriesIndex == _higher4BarSeriesIndex) 
				UpdateHigher4Arrays();

            // Rebuild snapshot only on primary bar
            if (barSeriesIndex != _primaryBarSeriesIndex) return;

            UpdateVWAP();
            UpdateATR();
            UpdateORB();
            UpdatePrevDayLevels();
            UpdateSessionLevels();
            BuildSnapshot();
        }

        // ===================================================================
        // ARRAY UPDATES
        // ===================================================================

        private void UpdatePrimaryArrays()
        {
            ShiftAndInsert(_closes0, _host.Close[0]);
            ShiftAndInsert(_highs0,  _host.High[0]);
            ShiftAndInsert(_lows0,   _host.Low[0]);
            ShiftAndInsert(_opens0,  _host.Open[0]);
            ShiftAndInsert(_vols0,   _host.Volume[0]);

            // Order flow — GetCurrentAskVolume/BidVolume return 0.0 without tick replay.
            // Docs: These methods are valid in OnBarUpdate with Calculate.OnBarClose only
            // when tick replay is enabled. Without it they return 0 silently.
            double askVol = _host.GetCurrentAskVolume();
            double bidVol = _host.GetCurrentBidVolume();
            ShiftAndInsert(_askVols0,   askVol);
            ShiftAndInsert(_bidVols0,   bidVol);
            ShiftAndInsert(_barDeltas0, askVol - bidVol);

            if (_sessionOpenFired)
            {
                _barsSinceOpen = 0;
                _sessionOpenFired = false;
            }
            else
            {
                _barsSinceOpen++;
            }
        }

        private void UpdateHigher1Arrays()
        {
            if (_host.BarsArray.Length <= _higher1BarSeriesIndex) return;
            var b = _host.BarsArray[_higher1BarSeriesIndex];
            ShiftAndInsert(_closes1, b.GetClose(0));
            ShiftAndInsert(_highs1,  b.GetHigh(0));
            ShiftAndInsert(_lows1,   b.GetLow(0));
            ShiftAndInsert(_opens1,  b.GetOpen(0));
            ShiftAndInsert(_vols1,   b.GetVolume(0));
        }

        private void UpdateHigher2Arrays()
        {
            if (_host.BarsArray.Length <= _higher2BarSeriesIndex) return;
            var b = _host.BarsArray[_higher2BarSeriesIndex];
            ShiftAndInsert(_closes2, b.GetClose(0));
            ShiftAndInsert(_highs2,  b.GetHigh(0));
            ShiftAndInsert(_lows2,   b.GetLow(0));
            ShiftAndInsert(_opens2,  b.GetOpen(0));
            ShiftAndInsert(_vols2,   b.GetVolume(0));
        }

        private void UpdateDailyArrays()
        {
            if (_host.BarsArray.Length <= _dailyBarSeriesIndex) return;
            var b = _host.BarsArray[_dailyBarSeriesIndex];
            ShiftAndInsert(_closesD, b.GetClose(0));
            ShiftAndInsert(_highsD,  b.GetHigh(0));
            ShiftAndInsert(_lowsD,   b.GetLow(0));
            ShiftAndInsert(_opensD,  b.GetOpen(0));
            ShiftAndInsert(_volsD,   b.GetVolume(0));
        }
		
		private void UpdateHigher3Arrays()
        {
            if (_host.BarsArray.Length <= _higher3BarSeriesIndex) return;
            var b = _host.BarsArray[_higher3BarSeriesIndex];
            ShiftAndInsert(_closes3, b.GetClose(0));
            ShiftAndInsert(_highs3,  b.GetHigh(0));
            ShiftAndInsert(_lows3,   b.GetLow(0));
            ShiftAndInsert(_opens3,  b.GetOpen(0));
            ShiftAndInsert(_vols3,   b.GetVolume(0));
        }

        private void UpdateHigher4Arrays()
        {
            if (_host.BarsArray.Length <= _higher4BarSeriesIndex) return;
            var b = _host.BarsArray[_higher4BarSeriesIndex];
            ShiftAndInsert(_closes4, b.GetClose(0));
            ShiftAndInsert(_highs4,  b.GetHigh(0));
            ShiftAndInsert(_lows4,   b.GetLow(0));
            ShiftAndInsert(_opens4,  b.GetOpen(0));
            ShiftAndInsert(_vols4,   b.GetVolume(0));
        }

        // ===================================================================
        // VWAP — incremental Welford session reset
        // ===================================================================

        private void UpdateVWAP()
        {
            double tp = (_host.High[0] + _host.Low[0] + _host.Close[0]) / 3.0;
            _vwap    = TradingMath.VWAP_Update(ref _pvSum, ref _volSum,
                           tp, _host.Volume[0], _vwap, out _);

            double dev = _host.Close[0] - _vwap;
            _sdSession = TradingMath.Welford_SD_Update(ref _welfCount, ref _welfMean, ref _welfM2, dev);
        }

        // ===================================================================
        // ATR — Wilder smoothing
        // ===================================================================

        private void UpdateATR()
        {
            if (_host.CurrentBar < 1) return;

            double tr  = MathIndicators.TrueRange(_host.High[0], _host.Low[0], _host.Close[1]);
            var    res = MathIndicators.ATR_Update(tr, _atrPrev, 14,
                             _trBuffer, ref _trBufIdx, ref _trBufCount, ref _trBufSum);
            _atrPrev = res.ATR;
        }

        // ===================================================================
        // OPENING RANGE BOX
        // ===================================================================

        private const int ORB_BARS = 30;

        private void UpdateORB()
        {
            if (_orbComplete) return;

            // Build the range for the first ORB_BARS bars of the session, then lock.
            if (_barsSinceOpen < ORB_BARS)
            {
                if (_host.High[0] > _orbHigh) _orbHigh = _host.High[0];
                if (_host.Low[0]  < _orbLow)  _orbLow  = _host.Low[0];
            }
            else
            {
                _orbComplete     = true;
                _orbCompleteBar  = _host.CurrentBar;
                _orbCompleteTime = _host.Time[0];
            }

            _orbContext = new ORBContext
            {
                High            = _orbHigh,
                Low             = _orbLow,
                Midpoint        = (_orbHigh + _orbLow) / 2.0,
                IsComplete      = _orbComplete,
                CompletedBar    = _orbCompleteBar,
                CompletedTime   = _orbCompleteTime,
                BullishBreakout = _orbComplete && _host.Close[0] > _orbHigh,
                BearishBreakout = _orbComplete && _host.Close[0] < _orbLow
            };
        }

        // ===================================================================
        // PREVIOUS DAY LEVELS
        // ===================================================================

        private void UpdatePrevDayLevels()
        {
            if (_host.BarsArray.Length <= _dailyBarSeriesIndex) return;
            var daily = _host.BarsArray[_dailyBarSeriesIndex];
            if (daily.Count < 2) return;

            _prevDayHigh  = daily.GetHigh(1);
            _prevDayLow   = daily.GetLow(1);
            _prevDayClose = daily.GetClose(1);
            _prevDaySet   = true;
        }

        // ===================================================================
        // SESSION PHASE
        // ===================================================================

        private SessionPhase GetSessionPhase()
        {
            return SessionPhase.EarlySession;
        }

        // ===================================================================
        // SESSION + PERIOD LEVEL TRACKING
        // ===================================================================

        /// <summary>
        /// Called every primary bar. Handles three types of events:
        ///   1. Trading-day roll  — NT8 IsFirstBarOfSession fires at 18:00 ET (Globex open).
        ///      Rolls Today → PrevDay. On Sunday: also rolls ThisWeek → PrevWeek.
        ///      On month change: rolls ThisMonth → PrevMonth.
        ///   2. Period accumulation — Today, ThisWeek, ThisMonth updated every bar.
        ///   3. Session accumulation — Sydney/Tokyo/London/NY each reset when their
        ///      session boundary is crossed (detected by comparing prev vs curr TimeOfDay).
        ///      While inside a session, that session's accumulator is updated.
        ///      After a session closes, the completed data persists until the next open.
        /// </summary>
        private void UpdateSessionLevels()
        {
            if (_host.CurrentBar < 1) return;

            TimeSpan t = _host.Time[0].TimeOfDay;

            // ── 1. Trading-day / week / month rolls ─────────────────────────
            if (_host.Bars.IsFirstBarOfSession)
            {
                // Day roll: save today as prevDay, start fresh
                _prevDay = _today;
                _today.Reset(_host.Open[0]);

                // Week roll: Sunday 18:00 ET = first Globex session of the new week
                if (_host.Time[0].DayOfWeek == DayOfWeek.Sunday)
                {
                    _prevWeek = _thisWeek;
                    _thisWeek.Reset(_host.Open[0]);
                }

                // Month roll: first session-open where the calendar month has changed
                int currMonth = _host.Time[0].Month;
                if (_lastRollMonth >= 0 && currMonth != _lastRollMonth)
                {
                    _prevMonth = _thisMonth;
                    _thisMonth.Reset(_host.Open[0]);
                }
                _lastRollMonth = currMonth;
            }

            // ── 2. Period accumulation (every primary bar) ──────────────────
            double askV = _askVols0[0];
            double bidV = _bidVols0[0];
            _today.Update    (_host.Open[0], _host.High[0], _host.Low[0], _host.Close[0], _host.Volume[0], askV, bidV);
            _thisWeek.Update (_host.Open[0], _host.High[0], _host.Low[0], _host.Close[0], _host.Volume[0], askV, bidV);
            _thisMonth.Update(_host.Open[0], _host.High[0], _host.Low[0], _host.Close[0], _host.Volume[0], askV, bidV);

            // ── 3. Intraday session boundary detection & accumulation ────────
            // InSession() handles midnight-crossing windows (Sydney, Tokyo) correctly.
            bool sydneyNow = SessionTimes.InSession(t, SessionTimes.SYDNEY_OPEN,   SessionTimes.SYDNEY_CLOSE);
            bool tokyoNow  = SessionTimes.InSession(t, SessionTimes.TOKYO_OPEN,    SessionTimes.TOKYO_CLOSE);
            bool londonNow = SessionTimes.InSession(t, SessionTimes.LONDON_OPEN,   SessionTimes.LONDON_CLOSE);
            bool nyNow     = SessionTimes.InSession(t, SessionTimes.NEWYORK_OPEN,  SessionTimes.NEWYORK_CLOSE);

            // Entering a session: reset the accumulator so this session starts fresh.
            // Data from the previous (completed) session survives in snapshot.Sydney etc.
            // until the very first Update() of the new session overwrites it.
            if (sydneyNow && !_sydneyWasActive) _sydney.Reset(_host.Open[0]);
            if (tokyoNow  && !_tokyoWasActive)  _tokyo.Reset(_host.Open[0]);
            if (londonNow && !_londonWasActive)  _london.Reset(_host.Open[0]);
            if (nyNow     && !_nyWasActive)      _newYork.Reset(_host.Open[0]);

            // Accumulate while inside each session — real ask/bid vol when available
            if (sydneyNow) _sydney.Update (_host.Open[0], _host.High[0], _host.Low[0], _host.Close[0], _host.Volume[0], askV, bidV);
            if (tokyoNow)  _tokyo.Update  (_host.Open[0], _host.High[0], _host.Low[0], _host.Close[0], _host.Volume[0], askV, bidV);
            if (londonNow) _london.Update (_host.Open[0], _host.High[0], _host.Low[0], _host.Close[0], _host.Volume[0], askV, bidV);
            if (nyNow)     _newYork.Update(_host.Open[0], _host.High[0], _host.Low[0], _host.Close[0], _host.Volume[0], askV, bidV);

            // Persist "was active" state for next bar's boundary detection
            _sydneyWasActive = sydneyNow;
            _tokyoWasActive  = tokyoNow;
            _londonWasActive = londonNow;
            _nyWasActive     = nyNow;
        }

        // ===================================================================
        // BUILD SNAPSHOT
        // ===================================================================

        private void BuildSnapshot()
        {
            double tickSize   = _host.Instrument.MasterInstrument.TickSize;
            double tickValue  = _host.Instrument.MasterInstrument.TickSize
                                * _host.Instrument.MasterInstrument.PointValue;

            // Primary bar snapshot
            var primary = new BarSnapshot
            {
                Timeframe          = TimeframeId.Primary,
                Instrument         = _instrument,
                CurrentBar         = _host.CurrentBar,
                Time               = _host.Time[0],
                Open               = _host.Open[0],
                High               = _host.High[0],
                Low                = _host.Low[0],
                Close              = _host.Close[0],
                Volume             = _host.Volume[0],
                Closes             = _closes0,
                Highs              = _highs0,
                Lows               = _lows0,
                Opens              = _opens0,
                Volumes            = _vols0,
                // Order flow — real values when tick replay active, 0.0 otherwise
                AskVolumes         = _askVols0,
                BidVolumes         = _bidVols0,
                BarDeltas          = _barDeltas0,
                AskVolume          = _askVols0[0],
                BidVolume          = _bidVols0[0],
                BarDelta           = _barDeltas0[0],
                TickSize           = tickSize,
                TickValue          = tickValue,
                PointValue         = _host.Instrument.MasterInstrument.PointValue,
                Session            = GetSessionPhase(),
                BarsSinceOpen      = _barsSinceOpen,
                IsFirstBarOfSession= _host.Bars.IsFirstBarOfSession,
                IsLastBarOfSession = _host.Bars.IsLastBarOfSession,
                // Docs: IsFirstTickOfBar is only meaningful when Calculate = OnEachTick or OnPriceChange.
                // With Calculate.OnBarClose (our default), this is always false during OnBarUpdate.
                // Stored here so if the host switches to tick mode, it propagates correctly.
                IsFirstTickOfBar   = _host.IsFirstTickOfBar,
                IsHistorical       = _host.State == State.Historical
            };

            // Higher TF1
            var higher1 = new BarSnapshot
            {
                Timeframe  = TimeframeId.Higher1,
                Instrument = _instrument,
                Closes = _closes1, Highs = _highs1, Lows = _lows1,
                Opens  = _opens1,  Volumes = _vols1,
                Close  = _closes1[0], High = _highs1[0], Low = _lows1[0],
                TickSize = tickSize, TickValue = tickValue
            };

            // Higher TF2
            var higher2 = new BarSnapshot
            {
                Timeframe  = TimeframeId.Higher2,
                Instrument = _instrument,
                Closes = _closes2, Highs = _highs2, Lows = _lows2,
                Opens  = _opens2,  Volumes = _vols2,
                Close  = _closes2[0], High = _highs2[0], Low = _lows2[0],
                TickSize = tickSize, TickValue = tickValue
            };
			// Higher TF3 — 2-hour (120-min)
            var higher3 = new BarSnapshot
            {
                Timeframe  = TimeframeId.Higher3,
                Instrument = _instrument,
                Closes = _closes3, Highs = _highs3, Lows = _lows3,
                Opens  = _opens3,  Volumes = _vols3,
                Close  = _closes3[0], High = _highs3[0], Low = _lows3[0],
                TickSize = tickSize, TickValue = tickValue
            };

            // Higher TF4 — 4-hour (240-min)
            var higher4 = new BarSnapshot
            {
                Timeframe  = TimeframeId.Higher4,
                Instrument = _instrument,
                Closes = _closes4, Highs = _highs4, Lows = _lows4,
                Opens  = _opens4,  Volumes = _vols4,
                Close  = _closes4[0], High = _highs4[0], Low = _lows4[0],
                TickSize = tickSize, TickValue = tickValue
            };

            // Daily
            var daily = new BarSnapshot
            {
                Timeframe  = TimeframeId.Daily,
                Instrument = _instrument,
                Closes = _closesD, Highs = _highsD, Lows = _lowsD,
                Opens  = _opensD,  Volumes = _volsD,
                Close  = _closesD[0], High = _highsD[0], Low = _lowsD[0],
                TickSize = tickSize, TickValue = tickValue
            };

            double atr = double.IsNaN(_atrPrev) ? 0.0 : _atrPrev;

            _lastSnapshot = new MarketSnapshot
            {
                Primary      = primary,
                Higher1      = higher1,
                Higher2      = higher2,
				Higher3      = higher3,  
                Higher4      = higher4,
                Daily        = daily,
                VWAP         = _vwap,
                VWAPUpperSD1 = _vwap + _sdSession,
                VWAPLowerSD1 = _vwap - _sdSession,
                VWAPUpperSD2 = _vwap + (2.0 * _sdSession),
                VWAPLowerSD2 = _vwap - (2.0 * _sdSession),
                ATR          = atr,
                ATRTicks     = (tickSize > 0) ? atr / tickSize : 0.0,
                ORBHigh      = _orbHigh > double.MinValue ? _orbHigh : 0.0,
                ORBLow       = _orbLow  < double.MaxValue ? _orbLow  : 0.0,
                ORBComplete  = _orbComplete,
                PrevDayHigh  = _prevDaySet ? _prevDayHigh  : 0.0,
                PrevDayLow   = _prevDaySet ? _prevDayLow   : 0.0,
                PrevDayClose = _prevDaySet ? _prevDayClose : 0.0,
                // ── Period levels ────────────────────────────────────────────
                Today        = _today.ToLevel(),
                PrevDay      = _prevDay.ToLevel(),
                ThisWeek     = _thisWeek.ToLevel(),
                PrevWeek     = _prevWeek.ToLevel(),
                ThisMonth    = _thisMonth.ToLevel(),
                PrevMonth    = _prevMonth.ToLevel(),
                // ── Session levels ───────────────────────────────────────────
                Sydney       = _sydney.ToLevel(),
                Tokyo        = _tokyo.ToLevel(),
                London       = _london.ToLevel(),
                NewYork      = _newYork.ToLevel()
            };

            _isReady = _lastSnapshot.IsValid;
        }

        // ===================================================================
        // UTILITIES
        // ===================================================================

        /// <summary>
        /// Shift array right and insert new value at index 0 (most recent).
        /// O(n) — acceptable for FeedConstants.SNAPSHOT_DEPTH = 100.
        /// </summary>
        private static void ShiftAndInsert(double[] arr, double value)
        {
            if (arr == null || arr.Length == 0) return;
            for (int i = arr.Length - 1; i > 0; i--)
                arr[i] = arr[i - 1];
            arr[0] = value;
        }
    }
}