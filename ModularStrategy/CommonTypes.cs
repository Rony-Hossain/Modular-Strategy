#region Using declarations
using System;
using System.Collections.Generic;
#endregion

// ===================================================================
// COMMON TYPES
// ===================================================================
// Single source of truth for all types shared across the math library
// AND the strategy layer.
//
// WHAT BELONGS HERE:
//   - Primitive enums that cross the MathLogic ↔ Strategy boundary
//   - Instrument specification lookup (tick size, values, point value)
//   - Strategy-wide named constants (no more scattered magic numbers)
//   - Score / grade thresholds (one definition, used everywhere)
//   - Session time constants
//
// WHAT DOES NOT BELONG HERE:
//   - Result objects (stay in their math module: MathSMC, MathFlow, etc.)
//   - NT8-specific types (stay in NinjaTrader namespaces)
//   - Interfaces (stay in IStrategyModules.cs)
//   - Calculation logic (stay in their math modules)
//
// NAMESPACE: MathLogic — both the math library and strategy layer
//            already import MathLogic, so no extra using required.
// ===================================================================

namespace MathLogic
{
    // ===================================================================
    // CROSS-BOUNDARY ENUMS
    // These enums are defined in the math library but consumed by the
    // strategy layer. Moving them here breaks the direct dependency
    // between MathSMC/MathIndicators and StrategyLogic.
    // ===================================================================

    /// <summary>
    /// Structural label assigned to a confirmed swing point.
    /// HH = Higher High, HL = Higher Low, LH = Lower High, LL = Lower Low.
    /// Defined here (not in MathSMC) so StrategyLogic can reference it
    /// without importing MathSMC internals.
    /// </summary>
    public enum SwingLabel
    {
        None = 0,
        HH = 1, // Higher High — bullish structure
        HL = 2, // Higher Low  — bullish structure
        LH = 3, // Lower High  — bearish structure
        LL = 4, // Lower Low   — bearish structure
    }

    /// <summary>
    /// Overall market structure bias derived from swing sequence.
    /// </summary>
    public enum StructureTrend
    {
        Undefined = 0,
        Bullish = 1, // HH + HL sequence
        Bearish = 2, // LH + LL sequence
    }

    /// <summary>
    /// Break of Structure direction — continuation break in trend direction.
    /// </summary>
    public enum BOSType
    {
        None = 0,
        Bullish = 1, // close above last swing high (bullish trend)
        Bearish = 2, // close below last swing low  (bearish trend)
    }

    /// <summary>
    /// Change of Character — first break AGAINST the prevailing trend.
    /// Signals potential reversal. After CHoCH, next same-direction break = BOS.
    /// </summary>
    public enum CHoCHType
    {
        None = 0,
        Bullish = 1, // first close above swing high while trend is bearish
        Bearish = 2, // first close below swing low  while trend is bullish
    }

    /// <summary>
    /// EMA crossover signal. Shared by MathIndicators and StrategyLogic.
    /// Also used for Stochastic K/D crossover and Ichimoku TK cross.
    /// </summary>
    public enum EMACross
    {
        None = 0,
        Bullish = 1, // fast MA crosses above slow MA
        Bearish = 2, // fast MA crosses below slow MA
    }

    /// <summary>
    /// Regime state — the ±1 signal from MathFlow's band-cross engine.
    /// </summary>
    public enum RegimeState
    {
        Undefined = 0,
        Bullish = 1,
        Bearish = -1,
    }

    /// <summary>
    /// Computation tier — controls which calculations run in live vs backtest.
    /// CORE_ONLY in live, FULL_ANALYSIS in replay only.
    /// </summary>
    public enum ComputationTier
    {
        /// <summary>Minimal: VWAP, SD, basic ratios. Use LIVE.</summary>
        CORE_ONLY = 0,

        /// <summary>Add structure: POC, Value Area, zone classification.</summary>
        STRUCTURE_ONLY = 1,

        /// <summary>Add order flow: Delta, Absorption, Divergence.</summary>
        ORDERFLOW_ENABLED = 2,

        /// <summary>Full analysis. REPLAY / BACKTEST ONLY — never use live.</summary>
        FULL_ANALYSIS = 3,
    }

    // ===================================================================
    // INSTRUMENT SPECIFICATIONS
    // Static lookup table for the four supported instruments.
    // Prevents magic numbers scattered across DataFeed, StrategyLogic,
    // and SignalGenerator.
    // ===================================================================

    /// <summary>
    /// Immutable specification for one instrument.
    /// Values match NinjaTrader 8 Instrument Master settings.
    /// </summary>
    // ============================================================
    // PATCH — CommonTypes.cs
    // Scope: InstrumentSpec class + InstrumentSpecs._specs entries only.
    // All other content in CommonTypes.cs is unchanged.
    //
    // Change: Add RoundNumberInterval to InstrumentSpec.
    //
    // Rationale: SupportResistanceCoreConfig.ForInstrument() was maintaining
    // a private switch statement that duplicated instrument-specific knowledge
    // already owned by InstrumentSpecs. RoundNumberInterval belongs with the
    // other instrument facts (TickSize, TickValue, PointValue), not scattered
    // across consuming types.
    //
    // Migration: InstrumentSpec constructor gains a 5th parameter.
    // All four _specs entries must be updated — see below.
    // No other call sites create InstrumentSpec instances outside this file.
    // ============================================================

    /// <summary>
    /// Immutable specification for one instrument.
    /// Values match NinjaTrader 8 Instrument Master settings.
    /// </summary>
    public sealed class InstrumentSpec
    {
        public string Name { get; }
        public double TickSize { get; } // minimum price increment
        public double TickValue { get; } // dollar value of one tick
        public double PointValue { get; } // dollar value of one full point

        /// <summary>
        /// Standard round-number price interval for this instrument.
        /// Used by SupportResistanceEngine.RoundNumberLevelSource to generate
        /// contextual reference levels bracketing current price.
        ///
        /// Values:
        ///   ES  / MES  = 25.0 points  (standard floor-trader reference)
        ///   NQ  / MNQ  = 100.0 points
        /// </summary>
        public double RoundNumberInterval { get; }

        public InstrumentSpec(
            string name,
            double tickSize,
            double tickValue,
            double pointValue,
            double roundNumberInterval
        )
        {
            if (roundNumberInterval <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(roundNumberInterval),
                    "roundNumberInterval must be > 0."
                );

            Name = name;
            TickSize = tickSize;
            TickValue = tickValue;
            PointValue = pointValue;
            RoundNumberInterval = roundNumberInterval;
        }

        /// <summary>Convert a price distance to ticks.</summary>
        public double ToTicks(double priceDistance) => TickSize > 0 ? priceDistance / TickSize : 0;

        /// <summary>Convert ticks to a dollar value.</summary>
        public double TicksToDollars(double ticks) => ticks * TickValue;

        /// <summary>Convert ticks back to price distance.</summary>
        public double TicksToPrice(double ticks) => ticks * TickSize;
    }

    /// <summary>
    /// Static registry of all supported instrument specifications.
    /// Reference via: InstrumentSpecs.Get(InstrumentKind.MNQ)
    /// </summary>
    public static class InstrumentSpecs
    {
        // NT8 verified values — tick sizes and values as of 2024.
        // RoundNumberInterval: standard floor-trader price reference intervals.
        //   ES/MES  = 25.0 — institutional levels at every 25 points
        //   NQ/MNQ  = 100.0 — institutional levels at every 100 points
        private static readonly Dictionary<InstrumentKind, InstrumentSpec> _specs = new Dictionary<
            InstrumentKind,
            InstrumentSpec
        >
        {
            { InstrumentKind.MNQ, new InstrumentSpec("MNQ", 0.25, 0.50, 2.0, 100.0) },
            { InstrumentKind.NQ, new InstrumentSpec("NQ", 0.25, 5.00, 20.0, 100.0) },
            { InstrumentKind.ES, new InstrumentSpec("ES", 0.25, 12.50, 50.0, 25.0) },
            { InstrumentKind.MES, new InstrumentSpec("MES", 0.25, 1.25, 5.0, 25.0) },
        };

        public static InstrumentSpec Get(InstrumentKind kind)
        {
            InstrumentSpec spec;
            if (_specs.TryGetValue(kind, out spec))
                return spec;
            
            throw new ArgumentException($"Unsupported InstrumentKind requested: {kind}. Cannot resolve InstrumentSpec.");
        }

        /// <summary>
        /// Resolve from the NT8 instrument name string.
        /// e.g. "MNQ 06-25" → InstrumentKind.MNQ
        /// </summary>
        public static InstrumentKind Resolve(string instrumentName)
        {
            if (string.IsNullOrEmpty(instrumentName))
                throw new ArgumentException("Instrument name cannot be null or empty.");
            string upper = instrumentName.ToUpperInvariant();
            if (upper.StartsWith("MNQ"))
                return InstrumentKind.MNQ;
            if (upper.StartsWith("NQ"))
                return InstrumentKind.NQ;
            if (upper.StartsWith("MES"))
                return InstrumentKind.MES;
            if (upper.StartsWith("ES"))
                return InstrumentKind.ES;
            throw new ArgumentException($"Unsupported instrument name: {instrumentName}. Must start with MNQ, NQ, MES, or ES.");
        }
    }

    // ============================================================
    // END PATCH
    // ============================================================

    /// <summary>
    /// Instrument identifier — used as key in InstrumentSpecs and
    /// as a typed field in BarSnapshot / MarketSnapshot.
    /// Kept separate from NT8's InstrumentType enum.
    /// </summary>
    public enum InstrumentKind
    {
        Unknown = 0,
        MNQ = 1,
        NQ = 2,
        ES = 3,
        MES = 4,
    }

    // ===================================================================
    // STRATEGY-WIDE CONSTANTS
    // Named constants previously scattered across StrategyLogic,
    // DataFeed, and SignalGenerator. Change once, affects everywhere.
    // ===================================================================

    /// <summary>
    /// Data feed constants — bar history depth and session timing.
    /// </summary>
    public static class FeedConstants
    {
        /// <summary>Bars of history maintained per timeframe in rolling arrays.</summary>
        public const int SNAPSHOT_DEPTH = 100;

        /// <summary>Minutes of the opening range window (locks ORB high/low).</summary>
        public const int ORB_MINUTES = 30;

        /// <summary>Minimum bars required before strategy logic starts (all TFs warm).</summary>
        public const int BARS_REQUIRED_TO_TRADE = 50;
    }

    /// <summary>
    /// SMC (Smart Money Concepts) tuning constants.
    /// </summary>
    public static class SMCConstants
    {
        /// <summary>Bars on each side required to confirm a swing high or low.</summary>
        public const int SWING_STRENGTH = 3;

        /// <summary>Minimum ticks a close must exceed a swing level to count as BOS.</summary>
        public const double MIN_BOS_TICKS = 2.0;

        /// <summary>
        /// Minimum CHoCH strength ratio (breakTicks / ATRTicks) to qualify.
        /// Below this the break is considered noise.
        /// </summary>
        public const double MIN_CHOCH_STRENGTH = 0.30;

        /// <summary>Bars back from the BOS bar to search for the order block candle.</summary>
        public const int OB_LOOKBACK = 5;

        /// <summary>Minimum ticks beyond OB edge to confirm invalidation.</summary>
        public const double OB_INVALIDATION_BUFFER_TICKS = 2.0;
    }

    /// <summary>
    /// VWAP mean-reversion tuning constants.
    /// </summary>
    public static class VWAPConstants
    {
        /// <summary>
        /// Price must be beyond this many standard deviations from VWAP
        /// to qualify as an overextension worth fading.
        /// </summary>
        public const double REVERSION_SD_THRESHOLD = 1.8;

        /// <summary>
        /// Fraction of one SD used as confirmation buffer when price reclaims VWAP.
        /// 0.15 = price must be within 15% of the SD band to count as a reclaim.
        /// </summary>
        public const double RECLAIM_BUFFER_PCT = 0.15;
    }

    /// <summary>
    /// EMA periods — shared between StrategyLogic and any indicator using EMAs.
    /// </summary>
    public static class EMAConstants
    {
        /// <summary>Fast EMA period for crossover signals.</summary>
        public const int FAST = 9;

        /// <summary>Slow EMA period for crossover signals.</summary>
        public const int SLOW = 21;

        /// <summary>Long-term EMA for daily trend bias filter.</summary>
        public const int TREND = 200;
    }

    /// <summary>
    /// ADX / trend-following constants.
    /// </summary>
    public static class ADXConstants
    {
        /// <summary>ADX must be at or above this value to confirm a trending market.</summary>
        public const double MIN_TREND = 25.0;

        /// <summary>ATR period used for Wilder smoothing.</summary>
        public const int ATR_PERIOD = 14;

        /// <summary>ADX smoothing period (same as ATR by convention).</summary>
        public const int ADX_PERIOD = 14;
    }

    /// <summary>
    /// ORB (Opening Range Breakout) constants.
    /// </summary>
    public static class ORBConstants
    {
        /// <summary>
        /// Ticks above/below ORB boundary required to confirm a breakout close.
        /// Prevents false triggers on wicks that barely touch the boundary.
        /// </summary>
        public const double BREAKOUT_BUFFER_TICKS = 1.5;

        /// <summary>
        /// Fraction of ATR within which price is considered to be retesting the ORB level.
        /// </summary>
        public const double RETEST_ATR_FRACTION = 0.30;
    }

    // ===================================================================
    // SNAPSHOT BAG KEYS
    // Compile-time constants for MarketSnapshot.Get() / .Set() / .GetFlag().
    // A typo in a const reference is a build error; a typo in a raw string
    // silently returns 0 with no log entry. Use these everywhere.
    // ===================================================================

    /// <summary>
    /// Keys for the MarketSnapshot indicator bag.
    /// All Set()/Get()/GetFlag() calls MUST use these constants.
    /// Adding a new indicator? Add its key here first.
    /// </summary>
    public static class SnapKeys
    {
        // ── SMF regime and bands ────────────────────────────────────────
        /// <summary>SMF regime: +1 bull, -1 bear, 0 undefined.</summary>
        public const string Regime = "SMFRegime";

        /// <summary>SMF basis (EMA center line).</summary>
        public const string Basis = "SMFBasis";

        /// <summary>SMF upper band.</summary>
        public const string Upper = "SMFUpper";

        /// <summary>SMF lower band.</summary>
        public const string Lower = "SMFLower";

        /// <summary>SMF flow strength [0–1].</summary>
        public const string Strength = "SMFStrength";

        /// <summary>Smoothed money flow (CLV × volume EMA).</summary>
        public const string MfSm = "SMFMfSm";

        // ── SMF impulse / switch / retest flags (1.0 = fired) ───────────
        /// <summary>Impulse breakout long fired this bar.</summary>
        public const string ImpulseLong = "SMFImpulseLong";

        /// <summary>Impulse breakdown short fired this bar.</summary>
        public const string ImpulseShort = "SMFImpulseShort";

        /// <summary>Regime switch up (bull BOS) fired this bar.</summary>
        public const string SwitchUp = "SMFSwitchUp";

        /// <summary>Regime switch down (bear BOS) fired this bar.</summary>
        public const string SwitchDown = "SMFSwitchDown";

        /// <summary>Bull retest (wick probe to basis in bull regime).</summary>
        public const string RetestBull = "SMFRetestBull";

        /// <summary>Bear retest (wick probe to basis in bear regime).</summary>
        public const string RetestBear = "SMFRetestBear";

        // ── SMF non-confirmation flags ──────────────────────────────────
        /// <summary>Flow does NOT confirm long — divergence warning.</summary>
        public const string NonConfLong = "SMFNonConfLong";

        /// <summary>Flow does NOT confirm short — divergence warning.</summary>
        public const string NonConfShort = "SMFNonConfShort";

        // ── Order flow — populated by HostStrategy.OnPopulateIndicatorBag() ───
        // All four keys are zero when Order Flow+ tick replay is not active.
        // Condition sets must degrade gracefully when these return 0.

        /// <summary>
        /// Cumulative delta from NT8's OrderFlowCumulativeDelta indicator.
        /// Positive = net buyer pressure since session open. Zero without tick replay.
        /// </summary>
        public const string CumDelta = "CumDelta";

        /// <summary>
        /// Bar delta for the current primary bar: AskVolume − BidVolume.
        /// Positive = buyers were aggressive this bar. Zero without tick replay.
        /// Used to confirm the signal bar has genuine directional pressure.
        /// </summary>
        public const string BarDelta = "BarDelta";

        // ── Opening Range Volume Profile (ORB-VP) ────────────────────────
        /// <summary>Point of Control of the Opening Range (e.g. first 15/30m).</summary>
        public const string ORBPoc = "ORBPoc";
        /// <summary>Value Area High of the Opening Range.</summary>
        public const string ORBVaHigh = "ORBVaHigh";
        /// <summary>Value Area Low of the Opening Range.</summary>
        public const string ORBVaLow = "ORBVaLow";

        /// <summary>
        /// Delta divergence flag (1.0 = divergence detected, 0.0 = none).
        /// Set when price makes a new high/low but cumulative delta disagrees.
        /// Long signals with DeltaDivergence=1 → price going up on hollow buying.
        /// Read via snapshot.GetFlag(SnapKeys.DeltaDivergence).
        /// </summary>
        public const string DeltaDivergence = "DeltaDivergence";

        /// <summary>
        /// Bullish CVD divergence: over the 10-bar ring, price trended DOWN but
        /// CumDelta trended UP. Sellers failing to move price lower = absorption.
        /// Vetoes short signals. Adds conviction to long signals.
        /// 1.0 only when ring is full (10 bars) AND divergence confirmed.
        /// Zero without Volumetric or during ring warm-up.
        /// Read via snapshot.GetFlag(SnapKeys.BullDivergence).
        /// </summary>
        public const string BullDivergence = "BullDivergence";

        /// <summary>
        /// Bearish CVD divergence: over the 10-bar ring, price trended UP but
        /// CumDelta trended DOWN. Buyers failing to push price higher = hollow rally.
        /// Vetoes long signals. Adds conviction to short signals.
        /// 1.0 only when ring is full (10 bars) AND divergence confirmed.
        /// Zero without Volumetric or during ring warm-up.
        /// Read via snapshot.GetFlag(SnapKeys.BearDivergence).
        /// </summary>
        public const string BearDivergence = "BearDivergence";

        /// <summary>
        /// SMF band-in-cloud flag. 1.0 when price is currently inside the adaptive
        /// bands (between LowerBand and UpperBand) after having been outside them.
        /// Used by SMF_BandReclaim to detect the pullback phase before re-entry.
        /// Written by SMF_BandReclaim.Evaluate() each bar — not by HostStrategy.
        /// </summary>
        public const string SMFBandInCloud = "SMFBandInCloud";

        /// <summary>
        /// Absorption score for the current bar.
        /// Published by HostStrategy from FootprintResult.AbsorptionScore.
        /// Higher values indicate larger same-level ask/bid imbalance accumulation
        /// across the assembled primary-bar ladder.
        /// </summary>
        public const string AbsorptionScore = "AbsorptionScore";

        // ── Volumetric Bars keys ─────────────────────────────────────────
        // Populated from AddVolumetric() BarsType. Works in BOTH backtest
        // and live (unlike the GetCurrentAskVolume path which is broken in
        // backtest). All degrade to 0.0 if Volumetric is not attached.

        /// <summary>Delta since price last touched bar high — usually negative (selling pushed off high).</summary>
        public const string VolDeltaSh = "VolDeltaSh";

        /// <summary>Delta since price last touched bar low — usually positive (buying bounced off low).</summary>
        public const string VolDeltaSl = "VolDeltaSl";

        /// <summary>Highest positive delta seen intra-bar — peak buying conviction.</summary>
        public const string VolMaxSeenDelta = "VolMaxDelta";

        /// <summary>Lowest negative delta seen intra-bar — peak selling conviction.</summary>
        public const string VolMinSeenDelta = "VolMinDelta";

        /// <summary>Total buying volume (ask aggressor) for the bar.</summary>
        public const string VolBuyVol = "VolBuyVol";

        /// <summary>Total selling volume (bid aggressor) for the bar.</summary>
        public const string VolSellVol = "VolSellVol";

        /// <summary>Total number of trades in the bar.</summary>
        public const string VolTrades = "VolTrades";

        // ── Delta exhaustion ─────────────────────────────────────────────
        // Published by HostStrategy.OnPopulateIndicatorBag() each bar.
        // Requires Volumetric Bars for accurate BarDelta values.

        /// <summary>
        /// Bar-delta exhaustion signal over a 3-bar window.
        /// Detects when delta momentum is fading in the direction of the price move.
        ///
        ///  +1.0 = Bull exhaustion: price moving up AND delta weakening bar-by-bar.
        ///         Buyers are losing conviction despite higher prices.
        ///         Confirms SHORT signals. Adds conviction to bear BOSWave entries.
        ///
        ///  -1.0 = Bear exhaustion: price moving down AND delta strengthening (less negative).
        ///         Sellers are losing conviction despite lower prices.
        ///         Confirms LONG signals. Adds conviction to bull BOSWave entries.
        ///
        ///   0.0 = No exhaustion detected this bar.
        ///
        /// Read as: if (snap.Get(SnapKeys.DeltaExhaustion) > 0) → bull exhaustion present.
        /// Zero when Volumetric absent or insufficient bar history.
        /// </summary>
        public const string DeltaExhaustion = "DeltaExhaustion";

        // ── Historical imbalance zones ───────────────────────────────────
        // Published by HostStrategy.OnPopulateIndicatorBag() each bar.
        // Requires Volumetric Bars. Zones persist across bars until expired.

        /// <summary>
        /// 1.0 when current price is inside or within 4-tick proximity of an
        /// active BULL imbalance zone (3+ consecutive bid-dominated levels from a
        /// prior bar). These zones act as support — price returning to them often
        /// bounces. Confirms LONG signals.
        /// 0.0 when no active bull zone is nearby, or Volumetric absent.
        /// </summary>
        public const string ImbalZoneAtBull = "ImbalZoneAtBull";

        /// <summary>
        /// 1.0 when current price is inside or within 4-tick proximity of an
        /// active BEAR imbalance zone (3+ consecutive ask-dominated levels from a
        /// prior bar). These zones act as resistance — price returning to them often
        /// rejects. Confirms SHORT signals.
        /// 0.0 when no active bear zone is nearby, or Volumetric absent.
        /// </summary>
        public const string ImbalZoneAtBear = "ImbalZoneAtBear";

        // ── Multi-timeframe EMA trend bias ──────────────────────────────
        // Published by HostStrategy.OnPopulateIndicatorBag() each bar.
        // Incremental 50-EMA computed on Higher2 (1H) and Higher4 (4H) closes.
        // Gracefully zero on startup until warm-up period (50 bars) is satisfied.

        /// <summary>
        /// 50-EMA bias on the 1H (Higher2) timeframe.
        /// +1.0 = Higher2.Close > 50-EMA (bullish 1H bias).
        /// -1.0 = Higher2.Close &lt; 50-EMA (bearish 1H bias).
        ///  0.0 = not yet warm or exactly at EMA (neutral).
        /// </summary>
        public const string H1EmaBias = "H1EmaBias";

        /// <summary>
        /// 50-EMA bias on the 2H (Higher3) timeframe.
        /// +1.0 = Higher3.Close > 50-EMA (bullish 2H bias).
        /// -1.0 = Higher3.Close &lt; 50-EMA (bearish 2H bias).
        ///  0.0 = not yet warm or neutral.
        /// </summary>
        public const string H2HrEmaBias = "H2HrEmaBias";

        /// <summary>
        /// 50-EMA bias on the 4H (Higher4) timeframe.
        /// +1.0 = Higher4.Close > 50-EMA (bullish 4H macro bias).
        /// -1.0 = Higher4.Close &lt; 50-EMA (bearish 4H macro bias).
        ///  0.0 = not yet warm or neutral.
        /// This is the highest-timeframe structural filter — the dominant trend.
        /// </summary>
        public const string H4HrEmaBias = "H4HrEmaBias";

        /// <summary>
        /// Count of timeframes (0–3: H1, H2, H4 + primary higher1) whose
        /// 50-EMA bias agrees with the signal direction.
        /// 3 = all three higher TFs aligned — highest conviction macro context.
        /// 0 = all three oppose — macro headwind present.
        /// Read as double; compare >= 2.0 for meaningful alignment.
        /// </summary>
        public const string MTFAlignScore = "MTFAlignScore";

        // Populated by HostStrategy.OnPopulateIndicatorBag() each bar.
        // Requires Volumetric Bars — degrades to 0 when absent.

        /// <summary>
        /// Maximum consecutive diagonal bull imbalance run in the current bar.
        /// Published by HostStrategy from FootprintResult.StackedBullRun.
        /// Diagonal rule: bid[i] > ask[i-1] * ratio (FootprintCore canonical model).
        /// </summary>
        public const string StackedImbalanceBull = "StackedImbBull";

        /// <summary>
        /// Maximum consecutive diagonal bear imbalance run in the current bar.
        /// Published by HostStrategy from FootprintResult.StackedBearRun.
        /// Diagonal rule: ask[i] > bid[i-1] * ratio (FootprintCore canonical model).
        /// </summary>
        public const string StackedImbalanceBear = "StackedImbBear";

        /// <summary>
        /// 1.0 when FootprintCore detects >= MinStackedLevels consecutive
        /// diagonal bull imbalance levels in the current bar.
        /// Published by HostStrategy from FootprintResult.HasBullStack.
        /// </summary>
        public const string HasBullStack = "HasBullStack";

        /// <summary>
        /// 1.0 when FootprintCore detects >= MinStackedLevels consecutive
        /// diagonal bear imbalance levels in the current bar.
        /// Published by HostStrategy from FootprintResult.HasBearStack.
        /// </summary>
        public const string HasBearStack = "HasBearStack";

        /// <summary>
        /// Rolling 20-bar simple average of VolTrades.
        /// Populated by HostStrategy.OnPopulateIndicatorBag() each bar.
        /// Used by ScoreConfluence to judge whether the current bar has
        /// above-average activity — meaningful context for the candle layer.
        /// Zero until 20 bars have elapsed; ScoreConfluence degrades gracefully.
        /// </summary>
        public const string AvgTrades = "AvgTrades";

        /// <summary>
        /// 1.0 when Volumetric Bars are attached AND producing real bid/ask data this bar.
        /// 0.0 when Volumetric is absent or not yet producing data (e.g. warm-up bars).
        ///
        /// Used by PassesConfluence to select thresholds dynamically:
        ///   HasVolumetric = 0 → minTotalScore=25, minLayersActive=2
        ///                        (regime + VWAP carry the gate; order flow not available)
        ///   HasVolumetric = 1 → minTotalScore=55, minLayersActive=3
        ///                        (all three original layers must contribute; real data present)
        ///
        /// Prevents order-flow-absent bars from passing the same gate as fully-confirmed bars.
        /// Set in HostStrategy.OnPopulateIndicatorBag() each bar.
        /// </summary>
        public const string HasVolumetric = "HasVolumetric";

        // ── Fallback / local indicators ─────────────────────────────────
        /// <summary>Locally computed smoothed money flow (when SMF indicator not loaded).</summary>
        public const string LocalMfSm = "LocalMfSm";

        // ── Swing structure ─────────────────────────────────────────────
        // Published by SMCBase.UpdateSwings() on every bar all four SMC sets run.
        // Final written values come from SMC_BOS (last in engine list).
        // All non-SMC condition sets and ScoreConfluence read these from the bag
        // without owning any SMCBase state.

        /// <summary>
        /// Structural trend derived from confirmed HH/HL vs LH/LL sequence.
        /// +1.0 = Bullish (HH+HL), -1.0 = Bearish (LH+LL), 0.0 = Undefined.
        /// Mirrors the StructureTrend enum: Bullish=1, Bearish=2 → stored as +1/-1.
        /// </summary>
        public const string SwingTrend = "SwingTrend";

        /// <summary>Price of the last confirmed swing high. 0 before first swing.</summary>
        public const string LastSwingHigh = "LastSwingHigh";

        /// <summary>Price of the last confirmed swing low. 0 before first swing.</summary>
        public const string LastSwingLow = "LastSwingLow";

        /// <summary>
        /// SwingLabel int for the last confirmed swing high.
        /// 1 = HH (higher high — bullish), 3 = LH (lower high — bearish), 0 = None.
        /// </summary>
        public const string LastHighLabel = "LastHighLabel";

        /// <summary>
        /// SwingLabel int for the last confirmed swing low.
        /// 2 = HL (higher low — bullish), 4 = LL (lower low — bearish), 0 = None.
        /// </summary>
        public const string LastLowLabel = "LastLowLabel";

        /// <summary>
        /// Count of confirmed swings since strategy start. Structure is
        /// trustworthy once this reaches 4 (at least 2 highs + 2 lows confirmed).
        /// </summary>
        public const string ConfirmedSwings = "ConfirmedSwings";

        /// <summary>
        /// 1.0 when SMC_CHoCH fired a BULLISH Change of Character on the current bar.
        /// Published by SMC_CHoCH.Evaluate() immediately after the flip is confirmed.
        /// Resets to 0.0 on the next bar. Used by ConfluenceEngine.LayerD to credit
        /// reversal entries that would otherwise show stale bearish structure labels.
        /// </summary>
        public const string CHoCHFiredLong = "CHoCHFiredLong";

        /// <summary>
        /// 1.0 when SMC_CHoCH fired a BEARISH Change of Character on the current bar.
        /// Mirror of CHoCHFiredLong for short reversals.
        /// </summary>
        public const string CHoCHFiredShort = "CHoCHFiredShort";

        // ── Volume profile ───────────────────────────────────────────────
        // Published by SupportResistanceEngine.Update() each bar.
        // Computed from SupportResistanceEngine (price→volume profile).
        // Reset at session open. Degrade gracefully to 0.0 early in the session.

        /// <summary>
        /// Point of Control — price level with the highest cumulative volume
        /// since session open. Most liquid price this session.
        /// </summary>
        public const string POC = "POC";

        /// <summary>Value Area High — upper boundary of the 70% volume zone.</summary>
        public const string VAHigh = "VAHigh";

        /// <summary>Value Area Low — lower boundary of the 70% volume zone.</summary>
        public const string VALow = "VALow";

        /// <summary>
        /// POC skew: volume above POC / volume below POC.
        /// &gt;1.0 = bullish distribution (more buying above control price),
        /// &lt;1.0 = bearish distribution. 1.0 = balanced / symmetric.
        /// </summary>
        public const string POCSkew = "POCSkew";

        // ── HTF swing levels ─────────────────────────────────────────────
        // Published by SupportResistanceEngine.Update() each primary bar.
        // Derived from IsSwingHigh/IsSwingLow on Higher2 (1H), Higher3 (2H),
        // Higher4 (4H) bar arrays. Persist until price invalidates them.
        // 0.0 when not yet detected or invalidated by a close-through.

        /// <summary>Last confirmed 1H swing high. Resistance. 0 if invalid.</summary>
        public const string H1SwingHigh = "H1SwingHigh";

        /// <summary>Last confirmed 1H swing low. Support. 0 if invalid.</summary>
        public const string H1SwingLow = "H1SwingLow";

        /// <summary>Last confirmed 2H swing high. Resistance. 0 if invalid.</summary>
        public const string H2SwingHigh = "H2SwingHigh";

        /// <summary>Last confirmed 2H swing low. Support. 0 if invalid.</summary>
        public const string H2SwingLow = "H2SwingLow";

        /// <summary>Last confirmed 4H swing high. Resistance — highest weight. 0 if invalid.</summary>
        public const string H4SwingHigh = "H4SwingHigh";

        /// <summary>Last confirmed 4H swing low. Support — highest weight. 0 if invalid.</summary>
        public const string H4SwingLow = "H4SwingLow";

        // ── Floor Trader Pivots ──────────────────────────────────────────
        // Computed daily from PrevDayHigh, PrevDayLow, PrevDayClose.
        // Standard formula: PP = (H+L+C)/3, R1/R2/S1/S2 derived from PP.
        // Published by SupportResistanceEngine.Update(). Reset each trading day.
        // 0.0 until PrevDay data is available (first trading day).

        /// <summary>Floor Trader Pivot Point (central pivot). Acts as both S and R.</summary>
        public const string PivotPP = "PivotPP";

        /// <summary>Pivot Resistance 1 — first resistance above PP.</summary>
        public const string PivotR1 = "PivotR1";

        /// <summary>Pivot Resistance 2 — extended resistance.</summary>
        public const string PivotR2 = "PivotR2";

        /// <summary>Pivot Support 1 — first support below PP.</summary>
        public const string PivotS1 = "PivotS1";

        /// <summary>Pivot Support 2 — extended support.</summary>
        public const string PivotS2 = "PivotS2";

        // ── Footprint engine keys ───────────────────────────────────────
        // Published by HostStrategy.OnPopulateIndicatorBag() each bar.
        // Used by OrderManager for trail veto and CVD slope BE trigger.

        /// <summary>
        /// CVD slope over last 5 bars: (newest CumDelta - 5-bar-ago CumDelta) / 5.
        /// Negative = sellers accelerating. Positive = buyers accelerating.
        /// 0.0 when ring buffer not yet filled or Volumetric absent.
        /// Used by OrderManager Trigger D (CVD acceleration BE) and trail veto.
        /// </summary>
        public const string CvdSlope = "CvdSlope";
    }

    /// <summary>
    /// Scoring weights — must sum to 100.
    /// Used in SMC_SetupScore and Scalp_SetupScore to weight each edge component.
    /// </summary>
    public static class ScoreWeights
    {
        public const int STRUCTURE = 25; // SMC structural context
        public const int FLOW = 25; // CLV money flow / order flow
        public const int VWAP = 20; // VWAP position and SD zone
        public const int TREND = 15; // EMA + ADX trend alignment
        public const int MOMENTUM = 15; // Stochastic / CHoCH strength
    }

    // ===================================================================
    // GRADE THRESHOLDS AND LABELS
    // Single definition — previously duplicated in MathPolicy and
    // SignalGenerator. Both now reference these constants.
    // ===================================================================

    /// <summary>
    /// Score thresholds that determine trade grade.
    /// Match MathPolicy.Grade_Master() breakpoints exactly.
    /// </summary>
    public static class GradeThresholds
    {
        /// <summary>Score below this value → trade rejected, no order submitted.</summary>
        public const int REJECT = 60;

        /// <summary>Score 60–64 → C setup (take only in ideal conditions).</summary>
        public const int C_SETUP = 65;

        /// <summary>Score 65–74 → B setup (standard valid trade).</summary>
        public const int B_SETUP = 75;

        /// <summary>Score 75–84 → A setup (high-confidence trade).</summary>
        public const int A_SETUP = 85;

        // Score 85+ → A+ setup (maximum position size, aggressive entry)

        /// <summary>Minimum score to enable aggressive entry (limit + 3 ticks rather than + 1).</summary>
        public const int AGGRESSIVE_ENTRY = 80;
    }

    /// <summary>
    /// Human-readable grade labels. Indexed by MathPolicy.Grade_Master() return value.
    /// Index 0 = reject, 1 = C, 2 = B, 3 = A, 4 = A+
    /// </summary>
    public static class GradeLabels
    {
        private static readonly string[] Labels = { "X", "C", "B", "A", "A+" };

        public static string Get(int gradeIndex)
        {
            if (gradeIndex < 0 || gradeIndex >= Labels.Length)
                return "X";
            return Labels[gradeIndex];
        }

        public const string REJECT = "X";
        public const string C = "C";
        public const string B = "B";
        public const string A = "A";
        public const string A_PLUS = "A+";
    }

    // ===================================================================
    // SESSION TIME CONSTANTS
    // ET session boundaries. Used by DataFeed and StrategyLogic
    // for session phase detection without magic numbers.
    // ===================================================================

    /// <summary>
    /// Eastern Time session boundaries for US equity futures.
    /// All times are TimeOfDay values for comparison with DateTime.TimeOfDay.
    ///
    /// DAYLIGHT SAVING NOTE: These times are in ET (Eastern Time) which automatically
    /// shifts between EST (UTC-5) and EDT (UTC-4). CME futures follow ET, so these
    /// constants remain correct year-round without adjustment.
    ///
    /// Sessions that cross midnight (Sydney, Tokyo) must use InSession() — a simple
    /// >= open && < close comparison will fail for those windows.
    /// </summary>
    public static class SessionTimes
    {
        // ── US equity futures intraday phases ───────────────────────────────

        /// <summary>Regular session open: 9:30 ET</summary>
        public static readonly TimeSpan REGULAR_OPEN = TimeSpan.FromHours(9.5);

        /// <summary>Opening range end / early session start: 10:00 ET</summary>
        public static readonly TimeSpan ORB_END = TimeSpan.FromHours(10.0);

        /// <summary>Early session end / mid session start: 11:00 ET</summary>
        public static readonly TimeSpan EARLY_END = TimeSpan.FromHours(11.0);

        /// <summary>Mid session end / late session start: 14:00 ET</summary>
        public static readonly TimeSpan MID_END = TimeSpan.FromHours(14.0);

        /// <summary>Regular session close: 16:00 ET</summary>
        public static readonly TimeSpan REGULAR_CLOSE = TimeSpan.FromHours(16.0);

        /// <summary>Globex open (CME futures overnight): 18:00 ET</summary>
        public static readonly TimeSpan GLOBEX_OPEN = TimeSpan.FromHours(18.0);

        /// <summary>No-trade buffer before regular open: 9:25 ET</summary>
        public static readonly TimeSpan PRE_OPEN_BUFFER = TimeSpan.FromMinutes(9 * 60 + 25);

        // ── Global session boundaries (Eastern Time) ────────────────────────
        // These four sessions cover the 24-hour futures clock.
        // Sydney and Tokyo cross midnight ET — always use InSession() to test them.

        /// <summary>
        /// Sydney (ASX / SFE) opens at 18:00 ET — same moment as Globex open.
        /// Thin session; useful as a "who moved price overnight" gate.
        /// CROSSES MIDNIGHT: use InSession(t, SYDNEY_OPEN, SYDNEY_CLOSE).
        /// </summary>
        public static readonly TimeSpan SYDNEY_OPEN = TimeSpan.FromHours(18.0);

        /// <summary>Sydney closes at 03:00 ET (crosses midnight).</summary>
        public static readonly TimeSpan SYDNEY_CLOSE = TimeSpan.FromHours(3.0);

        /// <summary>
        /// Tokyo (TSE) opens at 19:00 ET. Overlaps with Sydney tail.
        /// Key for NQ: JPY correlations and Asian tech sentiment.
        /// CROSSES MIDNIGHT: use InSession(t, TOKYO_OPEN, TOKYO_CLOSE).
        /// </summary>
        public static readonly TimeSpan TOKYO_OPEN = TimeSpan.FromHours(19.0);

        /// <summary>Tokyo closes at 04:00 ET (crosses midnight).</summary>
        public static readonly TimeSpan TOKYO_CLOSE = TimeSpan.FromHours(4.0);

        /// <summary>
        /// London (LIFFE / LSE) opens at 03:00 ET — highest FX + futures volume.
        /// London High / Low are the most important overnight levels for NY traders.
        /// Does NOT cross midnight — standard InSession() comparison works.
        /// </summary>
        public static readonly TimeSpan LONDON_OPEN = TimeSpan.FromHours(3.0);

        /// <summary>London closes at 12:00 ET (noon). Overlaps NY open by 2.5 hours.</summary>
        public static readonly TimeSpan LONDON_CLOSE = TimeSpan.FromHours(12.0);

        /// <summary>
        /// New York session open — explicit alias for REGULAR_OPEN for clarity in level gates.
        /// 09:30 ET.
        /// </summary>
        public static readonly TimeSpan NEWYORK_OPEN = TimeSpan.FromHours(9.5);

        /// <summary>New York session close — explicit alias for REGULAR_CLOSE. 16:00 ET.</summary>
        public static readonly TimeSpan NEWYORK_CLOSE = TimeSpan.FromHours(16.0);

        // ── Calendar boundary ────────────────────────────────────────────────

        /// <summary>
        /// Futures trading day boundary: 18:00 ET.
        /// A new trading "day" starts at Globex open, not midnight.
        /// Sunday 18:00 ET = start of the new trading week.
        /// </summary>
        public static readonly TimeSpan TRADING_DAY_START = TimeSpan.FromHours(18.0);

        // ── Session test helper ──────────────────────────────────────────────

        /// <summary>
        /// Returns true when <paramref name="time"/> falls inside the session window.
        /// Correctly handles sessions that cross midnight (Sydney 18:00→03:00,
        /// Tokyo 19:00→04:00) — a plain >= open &amp;&amp; &lt; close check fails for those.
        /// </summary>
        /// <param name="time">The TimeOfDay value to test.</param>
        /// <param name="open">Session open time-of-day.</param>
        /// <param name="close">Session close time-of-day.</param>
        /// <returns>True when <paramref name="time"/> is inside the session.</returns>
        public static bool InSession(TimeSpan time, TimeSpan open, TimeSpan close)
        {
            // Normal window (open < close): e.g. London 03:00–12:00
            if (open < close)
                return time >= open && time < close;

            // Crosses midnight (open > close): e.g. Sydney 18:00–03:00
            // Time is inside if it is at or after open (evening) OR before close (early morning)
            return time >= open || time < close;
        }
    }

    // ===================================================================
    // RISK CONSTANTS
    // Default risk parameters. Overridable from HostStrategy UI params.
    // ===================================================================

    /// <summary>
    /// Default risk management parameters.
    /// These are the fallback values used when not overridden by UI parameters.
    /// </summary>
    public static class RiskDefaults
    {
        /// <summary>Default risk per trade as fraction of account (1%).</summary>
        public const double RISK_PCT = 0.01;

        /// <summary>Default maximum contracts regardless of position sizing formula.</summary>
        public const int MAX_CONTRACTS = 5;

        /// <summary>Default maximum daily loss in dollars before circuit breaker fires.</summary>
        public const double MAX_DAILY_LOSS = 500.0;

        /// <summary>Minimum risk:reward ratio — signals below this RR are rejected.</summary>
        public const double MIN_RR_RATIO = 1.5;

        /// <summary>Consecutive losses before directional halt fires.
        /// FIX: Was 3. At 56% win rate with 15 trades/day, 3-in-a-row has
        /// an 8.5% probability per streak and triggered on 58% of trading days.
        /// On 42% of those days (11/26), the strategy recovered profitably
        /// after the CB point — $3,800+ in missed recovery profits.
        /// Raised to 5: probability per streak drops to 1.6%, preserves
        /// protection against genuine adverse runs while eliminating
        /// false kills on normal variance days.</summary>
        public const int MAX_CONSECUTIVE_LOSSES = 5;

        /// <summary>Stop too tight — minimum ticks for a valid stop distance.</summary>
        public const double MIN_STOP_TICKS = 4.0;

        /// <summary>Bars to wait for limit fill before converting to market order.</summary>
        public const int LIMIT_FALLBACK_BARS = 3;

        /// <summary>Fraction of position to exit at Target 1 (50% partial).</summary>
        public const double T1_PARTIAL_PCT = 0.5;
    }
}
