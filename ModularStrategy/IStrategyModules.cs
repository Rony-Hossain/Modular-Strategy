#region Using declarations
using System;
using System.Collections.Generic;
#endregion

namespace MathLogic.Strategy
{
    // ===================================================================
    // INSTRUMENT IDENTITY
    // ===================================================================
    // ===================================================================
    // TIMEFRAME CONTEXT
    // ===================================================================

    /// <summary>
    /// Timeframe identifier for multi-timeframe bar snapshots.
    /// </summary>
    public enum TimeframeId
    {
        Primary   = 0,   // 5-min  — execution timeframe
        Higher1   = 1,   // 15-min — intraday structure / zone TF
        Higher2   = 2,   // 60-min — filter / value area TF (1H)
        Daily     = 3,   // Daily  — daily bias / PrevDay levels
        Higher3   = 4,   // 2-hour — anchor / intermediate trend TF (120-min)
        Higher4   = 5    // 4-hour — macro bias / dominant trend TF (240-min)
    }

    // ===================================================================
    // SESSION CONTEXT
    // ===================================================================

    /// <summary>
    /// Intraday session context for session-aware logic.
    /// </summary>
    public enum SessionPhase
    {
        PreMarket    = 0,
        OpeningRange = 1,   // first 30 min — ORB window
        EarlySession = 2,   // 9:30–11:00 ET
        MidSession   = 3,   // 11:00–14:00 ET
        LateSession  = 4,   // 14:00–close
        AfterHours   = 5
    }

    // ===================================================================
    // SIGNAL TYPES
    // ===================================================================

    /// <summary>
    /// Directional bias of a signal or raw decision.
    /// </summary>
    public enum SignalDirection { None = 0, Long = 1, Short = -1 }

    /// <summary>
    /// The strategy archetype that generated the signal.
    /// Used for labeling on chart and for post-trade analysis.
    /// </summary>
    public enum SignalSource
    {
        None            = 0,
        SMC_BOS         = 1,   // Break of structure continuation
        SMC_CHoCH       = 2,   // Change of character reversal
        SMC_OrderBlock  = 3,   // Order block retest entry
        SMC_Breaker     = 4,   // Breaker block retest
        VWAP_Reversion  = 5,   // Price extended from VWAP, mean-revert
        VWAP_Reclaim    = 6,   // Price reclaims VWAP after rejection
        ORB_Breakout    = 7,   // Opening range breakout
        ORB_Retest      = 8,   // Retest of ORB level
        EMA_Cross       = 9,   // 9/21 EMA cross in trend direction
        ADX_Trend       = 10,  // ADX trending + DI signal
        OrderFlow_Abs   = 11,  // Absorption detected at key level
        OrderFlow_Delta = 12,  // Delta divergence signal
        Confluence      = 13,  // Multiple sources agreed
        // ── SMF (SmartMoneyFlowCloudBOSWaves) signals ──────────────────
        SMF_Impulse     = 20,  // Regime flip + strong flow (highest conviction)
        SMF_Switch      = 21,  // Regime flip without impulse threshold
        SMF_Retest      = 22,  // Wick probe to basis in existing regime

        // ── Wyckoff signals ─────────────────────────────────────────────
        Wyckoff_Spring   = 30,  // False break below support + snap back (long)
        Wyckoff_Upthrust = 31,  // False break above resistance + snap back (short)
		SMC_IB_Retest    = 32,  // Initial Balance boundary retest (long at IBLow, short at IBHigh)
        SMC_IFVG         = 33,  // Inverse FVG — filled gap revisited from opposite side
        OrderFlow_StackedImbalance = 34,  // 3+ stacked bid/ask imbalance levels detected
        EMA_CrossSignal  = 35,  // 9/21 EMA cross with trend + VWAP confirmation
        ADX_TrendSignal  = 36,  // ADX > 25 + DI crossover in trend direction
        FailedAuction     = 37,  // Failed Auction — return trade to a previously rejected extreme
        SMF_BandReclaim  = 38   // SMF basis reclaim — price re-enters cloud then closes back in regime direction
    }

    /// <summary>
    /// Order execution method.
    /// </summary>
    public enum OrderMethod
    {
        Limit              = 0,
        Market             = 1,
        LimitWithFallback  = 2   // Try limit, fall back to market after timeout
    }

    // ===================================================================
    // CORE DATA STRUCTS
    // ===================================================================

    /// <summary>
    /// Normalized snapshot of one bar on one timeframe.
    /// Populated by DataFeed every OnBarUpdate. Immutable once created.
    /// bars[0] = current, bars[1] = 1 bar ago (NT8 convention).
    /// </summary>
    public struct BarSnapshot
    {
        // Identity
        public TimeframeId  Timeframe      { get; set; }
        public InstrumentKind Instrument   { get; set; }
        public int          CurrentBar     { get; set; }
        public DateTime     Time           { get; set; }

        // OHLCV (index 0 = current bar)
        public double       Open           { get; set; }
        public double       High           { get; set; }
        public double       Low            { get; set; }
        public double       Close          { get; set; }
        public double       Volume         { get; set; }

        // Rolling arrays — caller manages depth
        public double[]     Closes         { get; set; }
        public double[]     Highs          { get; set; }
        public double[]     Lows           { get; set; }
        public double[]     Opens          { get; set; }
        public double[]     Volumes        { get; set; }

        // Order flow rolling arrays (same depth as price arrays)
        // Index 0 = current bar. All zeros without Order Flow+ tick replay.
        public double[]     AskVolumes     { get; set; }   // per-bar aggressive buy volume
        public double[]     BidVolumes     { get; set; }   // per-bar aggressive sell volume
        public double[]     BarDeltas      { get; set; }   // per-bar AskVol − BidVol

        // Order flow (populated only when Order Flow+ tick replay is active)
        // AskVolume = aggressive buys (hit ask), BidVolume = aggressive sells (hit bid)
        // BarDelta  = AskVolume − BidVolume for this single bar — the most useful
        //             single number for confirming whether the signal bar has real
        //             directional pressure behind it.
        // All three are 0.0 without tick replay — condition sets degrade gracefully.
        public double       AskVolume      { get; set; }
        public double       BidVolume      { get; set; }
        public double       BarDelta       { get; set; }

        // WARNING: CumDelta on BarSnapshot is NEVER populated by DataFeed.
        // DataFeed has no access to the OFC indicator.
        // To read cumulative delta, use: snapshot.Get(SnapKeys.CumDelta)
        // which is set by HostStrategy.OnPopulateIndicatorBag() each bar.
        // This field exists only for future use if DataFeed is extended with a
        // local cumulative delta accumulator.
        public double       CumDelta       { get; set; }

        // Instrument parameters
        public double       TickSize       { get; set; }
        public double       TickValue      { get; set; }
        public double       PointValue     { get; set; }

        // Session context
        public SessionPhase Session        { get; set; }
        public int          BarsSinceOpen  { get; set; }
        public bool         IsFirstBarOfSession { get; set; }
        public bool         IsLastBarOfSession  { get; set; }

        // Execution gating
        public bool         IsFirstTickOfBar { get; set; }
        public bool         IsHistorical     { get; set; }
    }

    /// <summary>
    /// OHLCV + order-flow summary for one time period or trading session.
    ///
    /// Used for: Today / PrevDay / ThisWeek / PrevWeek / ThisMonth / PrevMonth /
    ///           Sydney / Tokyo / London / NewYork level tracking.
    ///
    /// Fields that require Order Flow+ tick replay (BuyVolume, SellVolume, OrderCount)
    /// are zero until tick replay is enabled. All other fields are always populated.
    ///
    /// Mid and Delta are computed — they derive from the other fields and cost nothing
    /// to access. IsValid is false until at least one bar has been accumulated.
    /// </summary>
    public struct PeriodLevel
    {
        /// <summary>Highest price reached during this period.</summary>
        public double High       { get; set; }

        /// <summary>Lowest price reached during this period.</summary>
        public double Low        { get; set; }

        /// <summary>First price of this period (open of the first bar).</summary>
        public double Open       { get; set; }

        /// <summary>Last price of this period (close of the most recent bar).</summary>
        public double Close      { get; set; }

        /// <summary>Midpoint of High–Low range. Zero if IsValid is false.</summary>
        public double Mid        => IsValid ? (High + Low) / 2.0 : 0.0;

        /// <summary>Total contracts traded during this period.</summary>
        public double Volume     { get; set; }

        /// <summary>
        /// Aggressive buy volume — contracts executed at the ask (buyers hitting offers).
        /// Requires Order Flow+ with tick replay. Zero without tick replay.
        /// </summary>
        public double BuyVolume  { get; set; }

        /// <summary>
        /// Aggressive sell volume — contracts executed at the bid (sellers hitting bids).
        /// Requires Order Flow+ with tick replay. Zero without tick replay.
        /// </summary>
        public double SellVolume { get; set; }

        /// <summary>
        /// Net buyer/seller pressure for this period. Positive = buyers dominant.
        /// Delta = BuyVolume − SellVolume. Zero if Order Flow+ is not active.
        /// </summary>
        public double Delta      => BuyVolume - SellVolume;

        /// <summary>
        /// Number of individual trade executions during this period.
        /// Requires tick replay + OnMarketData() counting. Zero without tick replay.
        /// </summary>
        public int    OrderCount { get; set; }

        /// <summary>Number of primary-timeframe bars that formed this level.</summary>
        public int    Bars       { get; set; }

        /// <summary>True once at least one bar has been accumulated into this level.</summary>
        public bool   IsValid    { get; set; }
    }

    /// <summary>
    /// Multi-timeframe market context snapshot.
    /// DataFeed assembles this from all registered timeframes.
    /// </summary>
    public struct MarketSnapshot
    {
        public bool         IsValid  => Primary.CurrentBar > 0;
        public BarSnapshot  Primary  { get; set; }
        public BarSnapshot  Higher1  { get; set; }   // 15-min
        public BarSnapshot  Higher2  { get; set; }   // 60-min (1H)
        public BarSnapshot  Daily    { get; set; }   // Daily
        public BarSnapshot  Higher3  { get; set; }   // 2-hour (120-min)
        public BarSnapshot  Higher4  { get; set; }   // 4-hour (240-min)

        // Pre-computed indicators (populated by DataFeed)
        public double       VWAP         { get; set; }
        public double       VWAPUpperSD1 { get; set; }
        public double       VWAPLowerSD1 { get; set; }
        public double       VWAPUpperSD2 { get; set; }
        public double       VWAPLowerSD2 { get; set; }
        public double       ATR          { get; set; }   // primary TF ATR
        public double       ATRTicks     { get; set; }

        // ORB levels (set after opening range completes)
        public double       ORBHigh      { get; set; }
        public double       ORBLow       { get; set; }
        public bool         ORBComplete  { get; set; }

        // Daily levels
        public double       PrevDayHigh  { get; set; }
        public double       PrevDayLow   { get; set; }
        public double       PrevDayClose { get; set; }

        // ── Period levels (trading day, week, month) ─────────────────────────
        // Each PeriodLevel holds High, Low, Open, Close, Mid, Volume,
        // BuyVolume, SellVolume, Delta, OrderCount, Bars, IsValid.
        // Mid and Delta are computed properties — free to access.
        // BuyVolume/SellVolume/OrderCount are zero without Order Flow+ tick replay.

        /// <summary>Current trading day (resets at 18:00 ET / Globex open).</summary>
        public PeriodLevel  Today        { get; set; }

        /// <summary>Previous completed trading day.</summary>
        public PeriodLevel  PrevDay      { get; set; }

        /// <summary>Current calendar week (starts Sunday 18:00 ET).</summary>
        public PeriodLevel  ThisWeek     { get; set; }

        /// <summary>Previous calendar week.</summary>
        public PeriodLevel  PrevWeek     { get; set; }

        /// <summary>Current calendar month.</summary>
        public PeriodLevel  ThisMonth    { get; set; }

        /// <summary>Previous calendar month.</summary>
        public PeriodLevel  PrevMonth    { get; set; }

        // ── Session levels (global trading sessions) ─────────────────────────
        // Each session resets when the session next opens.
        // While a session is active: shows in-progress data.
        // After a session closes: shows completed data until the next instance opens.
        // IsValid is false until the first bar of the first session has been processed.

        /// <summary>Most recent Sydney session (18:00–03:00 ET). Thin, sets overnight context.</summary>
        public PeriodLevel  Sydney       { get; set; }

        /// <summary>Most recent Tokyo session (19:00–04:00 ET). Asian tech + JPY sentiment.</summary>
        public PeriodLevel  Tokyo        { get; set; }

        /// <summary>
        /// Most recent London session (03:00–12:00 ET).
        /// Highest overnight volume — London High/Low are the primary level gates
        /// in SMF_Impulse and SMF_Retest. London.Delta tells you who was dominant.
        /// </summary>
        public PeriodLevel  London       { get; set; }

        /// <summary>Current or most recent New York session (09:30–16:00 ET).</summary>
        public PeriodLevel  NewYork      { get; set; }

        // ── Generic indicator bag ────────────────────────────────────────
        // Condition sets can store any indicator value here by string key.
        // DataFeed populates core values; condition sets can add their own.
        // Use snap.Get("key") and snap.GetFlag("key") — never access _bag directly.
        private System.Collections.Generic.Dictionary<string, double> _bag;

        /// <summary>
        /// Store a named indicator value. Called by DataFeed and condition sets.
        /// </summary>
        public void Set(string key, double value)
        {
            if (_bag == null) _bag = new System.Collections.Generic.Dictionary<string, double>(16);
            _bag[key] = value;
        }

        /// <summary>
        /// Read a named indicator value. Returns fallback (default 0) if not set.
        /// </summary>
        public double Get(string key, double fallback = 0.0)
        {
            if (_bag == null) return fallback;
            return _bag.TryGetValue(key, out double v) ? v : fallback;
        }

        /// <summary>
        /// Read a named indicator as a boolean flag (true when value > 0.5).
        /// </summary>
        public bool GetFlag(string key)
        {
            return Get(key) > 0.5;
        }
    }

    /// <summary>
    /// Opening Range Box — tracked separately for ORB logic.
    /// </summary>
    public struct ORBContext
    {
        public double   High            { get; set; }
        public double   Low             { get; set; }
        public double   Midpoint        { get; set; }
        public bool     IsComplete      { get; set; }
        public int      CompletedBar    { get; set; }   // bar index when range locked
        public DateTime CompletedTime   { get; set; }
        public bool     BullishBreakout { get; set; }   // price above ORB high
        public bool     BearishBreakout { get; set; }   // price below ORB low
    }

    // ===================================================================
    // DECISION AND SIGNAL OBJECTS
    // ===================================================================

    /// <summary>
    /// Raw output from strategy logic layer. Not yet scored or risk-filtered.
    /// Strategy logic produces this; SignalGenerator consumes it.
    /// </summary>
    public struct RawDecision
    {
        public SignalDirection Direction   { get; set; }
        public SignalSource    Source      { get; set; }
        public double          EntryPrice  { get; set; }   // suggested entry (0 = use market)
        public double          StopPrice   { get; set; }   // structural stop
        public double          TargetPrice { get; set; }   // first target
        public double          Target2Price { get; set; }  // second target
        public string          Label           { get; set; }   // human-readable description
        public int             RawScore        { get; set; }   // pre-policy score 0–100
        public bool            IsValid         { get; set; }

        // ── Origin tracking ─────────────────────────────────────────────
        /// <summary>
        /// The SetId of the IConditionSet that produced this decision.
        /// e.g. "ORB_Classic_v1", "SMF_Impulse_v1"
        /// </summary>
        public string          ConditionSetId  { get; set; }

        /// <summary>
        /// Globally unique signal identifier stamped by StrategyEngine.
        /// Format: "{SetId}:{yyyyMMdd}:{barIndex}"
        /// e.g. "ORB_Classic_v1:20260319:1023"
        /// Appears in CSV log, NT8 entry name, and trade audit trail.
        /// </summary>
        public string          SignalId        { get; set; }

        /// <summary>
        /// Bar index when this decision was generated. Used for dedup and logging.
        /// </summary>
        public int             BarIndex        { get; set; }

        public RawDecision Clone()
        {
            return (RawDecision)this.MemberwiseClone();
        }

        public static readonly RawDecision None = new RawDecision
        {
            Direction      = SignalDirection.None,
            Source         = SignalSource.None,
            IsValid        = false,
            ConditionSetId = "",
            SignalId       = ""
        };
    }

    /// <summary>
    /// Fully scored, risk-filtered, execution-ready signal.
    /// Emitted by SignalGenerator. Consumed by UIRenderer and OrderManager.
    /// </summary>
    public sealed class SignalObject
    {
        public SignalDirection Direction    { get; set; }
        public SignalSource    Source       { get; set; }
        public double          EntryPrice   { get; set; }
        public double          StopPrice    { get; set; }
        public double          Target1Price { get; set; }
        public double          Target2Price { get; set; }
        public double          StopTicks    { get; set; }
        public double          RRRatio      { get; set; }   // risk:reward T1
        public int             Score        { get; set; }   // MathPolicy grade 0–100
        public string          Grade        { get; set; }   // A+, A, B, C
        public string          Label          { get; set; }
        /// <summary>SetId of the IConditionSet that produced this signal.</summary>
        public string          ConditionSetId { get; set; }
        /// <summary>Unique ID: "{SetId}:{yyyyMMdd}:{barIndex}" — used as NT8 order name.</summary>
        public string          SignalId       { get; set; }
        public int             BarIndex     { get; set; }
        public DateTime        SignalTime   { get; set; }
        public int             Contracts    { get; set; }

        /// <summary>
        /// Candle high and low at signal bar — used by UIRenderer to anchor
        /// the bubble tip to the bar edge (long = below CandleLow,
        /// short = above CandleHigh). Populated by SignalGenerator.
        /// </summary>
        public double          CandleHigh   { get; set; }
        public double          CandleLow    { get; set; }
        public OrderMethod     Method       { get; set; }
        public bool            IsActive     { get; set; }
        public bool            IsFilled     { get; set; }
        public bool            IsRejected   { get; set; }

        /// <summary>
        /// Confluence reason string carried from the winning ConfluenceResult.
        /// Contains layer tags (h4+/h2+/… trap+/ice+/… bp+/vel+/swp+/tice+).
        /// Written by SignalGenerator, logged by StrategyLogger.SignalAccepted.
        /// Empty when no confluence detail is available.
        /// </summary>
        public string          Detail       { get; set; }

        public SignalObject Clone()
        {
            return (SignalObject)this.MemberwiseClone();
        }

        public override string ToString() =>
            $"{Direction} {Grade} [{Score}] {Label} Entry:{EntryPrice:F2} Stop:{StopPrice:F2} T1:{Target1Price:F2}";
    }

    // ===================================================================
    // INTERFACES
    // ===================================================================

    /// <summary>
    /// Contract for all strategy logic plug-ins.
    /// Implement this interface to create a new strategy.
    /// Zero NT8 dependencies — fully unit-testable.
    /// </summary>
    public interface IStrategyLogic
    {
        /// <summary>
        /// Called once at initialization. Pass instrument-specific params.
        /// </summary>
        void Initialize(InstrumentKind instrument, double tickSize, double tickValue);

        /// <summary>
        /// Called every bar update. Returns a RawDecision or RawDecision.None.
        /// Must be fast — runs on the instrument thread.
        /// </summary>
        RawDecision Evaluate(MarketSnapshot snapshot);

        /// <summary>
        /// Called on session open to reset intraday state.
        /// </summary>
        void OnSessionOpen(MarketSnapshot snapshot);

        /// <summary>
        /// Called on position fill — allows logic to update internal state.
        /// </summary>
        void OnFill(SignalObject signal, double fillPrice);

        /// <summary>
        /// Called on position close — allows logic to record outcome.
        /// </summary>
        void OnClose(SignalObject signal, double exitPrice, double pnl);
    }

    /// <summary>
    /// Contract for the data feed layer.
    /// </summary>
    public interface IDataFeed
    {
        MarketSnapshot GetSnapshot();
        ORBContext GetORB();
        void OnBarUpdate(int barSeriesIndex);
        void OnSessionOpen();
        bool IsReady { get; }
    }

    /// <summary>
    /// Contract for the signal generator.
    /// </summary>
    public interface ISignalGenerator
    {
        SignalObject Process(RawDecision decision, MarketSnapshot snapshot, string confluenceDetail);
        bool IsBlocked { get; }   // true when circuit breaker or daily limit hit
        string LastRejectReason { get; }
        SignalObject LastRejectedSignal { get; }
        void OnSessionOpen();
        void OnFill(SignalObject signal, double fillPrice);
        void OnClose(SignalObject signal, double pnl, DateTime exitTime);
    }

    /// <summary>
    /// Contract for the order manager.
    /// </summary>
    public interface IOrderManager
    {
        void SubmitEntry(SignalObject signal);
        void ManagePosition(MarketSnapshot snapshot, SignalObject activeSignal);
        void CancelPending();
        bool HasOpenPosition { get; }
        bool HasPendingEntry { get; }
        void OnOrderUpdate(string orderName, NinjaTrader.Cbi.OrderState orderState, double fillPrice, int qty);

        /// <summary>
        /// Called on session open. Cancel stale orders and reset position state.
        /// </summary>
        void OnSessionOpen();
    }

    // ===================================================================
    // RENDER DTOs
    // Strategy logic builds these; UIRenderer reads them.
    // Decouples UIRenderer from MathLogic types (OrderBlock, SRLevel).
    // ===================================================================

    /// <summary>
    /// Direction-neutral zone box for rendering.
    /// Populated by strategy logic from OrderBlock or BreakerBlock.
    /// </summary>
    public struct ZoneDTO
    {
        public double  High      { get; set; }
        public double  Low       { get; set; }
        public int     BarIndex  { get; set; }
        public bool    IsBullish { get; set; }   // true = green, false = red
        public bool    IsBreaker { get; set; }   // breaker block = different opacity
        public string  Label     { get; set; }
        public double  Strength  { get; set; }
        public bool    IsValid   { get; set; }
    }

    /// <summary>
    /// Support or resistance level for rendering.
    /// Populated by strategy logic from SRLevel.
    /// </summary>
    public struct LevelDTO
    {
        public double  Price      { get; set; }
        public int     Touches    { get; set; }
        public bool    IsSupport  { get; set; }
        public bool    IsValid    { get; set; }
    }

    /// <summary>
    /// Contract for the UI renderer.
    /// Swap the implementation to change all chart visuals without
    /// touching strategy logic or order management.
    /// </summary>
    public interface IUIRenderer
    {
        // Configuration
        bool ShowVWAP           { get; set; }
        bool ShowSDbands        { get; set; }
        bool ShowSignalBubbles  { get; set; }
        bool ShowOBZones        { get; set; }
        bool ShowSRLevels       { get; set; }
        bool ShowORBLines       { get; set; }
        bool ShowLabels         { get; set; }

        // Signal queue
        void AddSignal(SignalObject signal);

        // Zone / level feeds (strategy logic populates these per bar)
        void SetZones(ZoneDTO[] zones, int count);
        void SetLevels(LevelDTO[] levels, int count);

        // Resource lifecycle — called by host from OnRenderTargetChanged / Terminated
        void CreateResources(object renderTarget, object writeFactory);
        void DisposeResources();

        void OnRender(
            object renderTarget,
            object chartControl,
            object chartScale,
            object chartBars,
            MarketSnapshot snapshot,
            ORBContext orb);
    }
}
