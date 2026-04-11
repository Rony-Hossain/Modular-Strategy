#region Using declarations
using System;
#endregion

namespace MathLogic
{
    /// <summary>
    /// WYCKOFF MODULE - Effort vs Result, Spring/Upthrust detection.
    /// Tier: STRUCTURE_ONLY (not heavy, but needs multiple bars)
    /// Core Wyckoff principle: analyze volume (effort) vs price movement (result).
    /// </summary>
    public static class MathWyckoff
    {
        // ===================================================================
        // EFFORT VS RESULT
        // ===================================================================

        /// <summary>
        /// Calculate Effort (volume or delta accumulation over N bars)
        /// </summary>
        /// <param name="volumes">Volume array [0]=current</param>
        /// <param name="length">Lookback period</param>
        /// <returns>Total effort</returns>
        public static double Effort_Volume(double[] volumes, int length)
        {
            if (volumes == null || length <= 0 || length > volumes.Length)
                return 0.0;

            double effort = 0.0;
            for (int i = 0; i < length; i++)
                effort += volumes[i];
            
            return effort;
        }

        /// <summary>
        /// Calculate Effort using cumulative delta (more sophisticated)
        /// </summary>
        /// <param name="deltas">Delta array [0]=current</param>
        /// <param name="length">Lookback period</param>
        /// <returns>Absolute delta accumulation</returns>
        public static double Effort_Delta(double[] deltas, int length)
        {
            if (deltas == null || length <= 0 || length > deltas.Length)
                return 0.0;

            double effort = 0.0;
            for (int i = 0; i < length; i++)
                effort += Math.Abs(deltas[i]);
            
            return effort;
        }

        /// <summary>
        /// Calculate Result (price movement in ticks)
        /// </summary>
        /// <param name="closeCurrent">Current close</param>
        /// <param name="closePast">Close N bars ago</param>
        /// <param name="tickSize">Instrument tick size</param>
        /// <returns>Absolute price movement in ticks</returns>
        public static double Result_Ticks(double closeCurrent, double closePast, double tickSize)
        {
            if (tickSize <= 0.0) return 0.0;
            return Math.Abs(closeCurrent - closePast) / tickSize;
        }

        /// <summary>
        /// Calculate Result in ATR units (normalized)
        /// </summary>
        /// <param name="closeCurrent">Current close</param>
        /// <param name="closePast">Close N bars ago</param>
        /// <param name="atr">Current ATR value</param>
        /// <returns>Price movement in ATR units</returns>
        public static double Result_ATR(double closeCurrent, double closePast, double atr)
        {
            if (atr <= 0.0) return 0.0;
            return Math.Abs(closeCurrent - closePast) / atr;
        }

        /// <summary>
        /// Calculate Effort/Result Ratio
        /// High ratio = "Big volume, no progress" (stopping action/absorption)
        /// Low ratio = Efficient movement
        /// </summary>
        /// <param name="effort">Effort value (volume or delta sum)</param>
        /// <param name="result">Result value (price movement)</param>
        /// <param name="epsilon">Minimum result to avoid division by zero</param>
        /// <returns>Effort/Result ratio</returns>
        public static double EffortResultRatio(double effort, double result, double epsilon = 0.1) => effort / Math.Max(result, epsilon);

        /// <summary>
        /// Classify Effort/Result condition - returns enum index
        /// 0 = EFFICIENT (low ER, normal trend)
        /// 1 = NORMAL (moderate ER)
        /// 2 = STOPPING_ACTION (high ER, absorption)
        /// 3 = CLIMACTIC (very high ER, potential reversal)
        /// </summary>
        /// <param name="effortResultRatio">ER ratio</param>
        /// <param name="normalThreshold">Normal threshold (default 10)</param>
        /// <param name="stoppingThreshold">Stopping threshold (default 20)</param>
        /// <param name="climacticThreshold">Climactic threshold (default 40)</param>
        /// <returns>Condition index</returns>
        public static int EffortResult_Classify(double effortResultRatio,
            double normalThreshold = 10.0,
            double stoppingThreshold = 20.0,
            double climacticThreshold = 40.0)
        {
            if (effortResultRatio >= climacticThreshold) return 3; // CLIMACTIC
            if (effortResultRatio >= stoppingThreshold) return 2;  // STOPPING_ACTION
            if (effortResultRatio >= normalThreshold) return 1;    // NORMAL
            return 0; // EFFICIENT
        }

        // ===================================================================
        // SPRING DETECTION (LONG SETUP)
        // ===================================================================

        /// <summary>
        /// Detect Spring (Wyckoff reversal pattern for long)
        /// Spring = Break below support, no acceptance, snap back inside
        /// </summary>
        /// <param name="low">Current bar low</param>
        /// <param name="close">Current bar close</param>
        /// <param name="open">Current bar open</param>
        /// <param name="rangeLow">Range low or VAL</param>
        /// <param name="buffer">Buffer below range (in price units)</param>
        /// <param name="outsideBars">Bars spent outside range</param>
        /// <param name="outsideVolPct">Volume percentage outside range</param>
        /// <param name="maxOutsideBars">Max bars allowed outside (default 3)</param>
        /// <param name="maxOutsideVolPct">Max volume % allowed outside (default 0.30)</param>
        /// <param name="requireBullishClose">Require close > open for confirmation</param>
        /// <returns>True if spring detected</returns>
        public static bool Spring_Detect(
            double low,
            double close,
            double open,
            double rangeLow,
            double buffer,
            int outsideBars,
            double outsideVolPct,
            int maxOutsideBars = 3,
            double maxOutsideVolPct = 0.30,
            bool requireBullishClose = true)
        {
            // 1. Break: Low penetrates below range
            bool hasBreak = low < (rangeLow - buffer);
            if (!hasBreak) return false;

            // 2. No acceptance: Limited time/volume outside
            bool noAcceptance = (outsideBars <= maxOutsideBars) && (outsideVolPct <= maxOutsideVolPct);
            if (!noAcceptance) return false;

            // 3. Re-entry: Close back inside range
            bool reentry = close > rangeLow;
            if (!reentry) return false;

            // 4. Optional: Bullish close confirmation
            if (requireBullishClose)
            {
                bool bullishClose = close > open;
                if (!bullishClose) return false;
            }

            return true;
        }

        /// <summary>
        /// Calculate Spring strength score (0.0 to 1.0)
        /// Higher score = stronger spring
        /// </summary>
        /// <param name="penetrationTicks">How far below range (in ticks)</param>
        /// <param name="reentrySpeed">Bars to re-enter (fewer = faster = better)</param>
        /// <param name="volumeRatio">Volume on spring bar vs average</param>
        /// <returns>Strength score 0.0 to 1.0</returns>
        public static double Spring_Strength(
            double penetrationTicks,
            int reentrySpeed,
            double volumeRatio)
        {
            // Penetration component (deeper = stronger, cap at 10 ticks)
            double penetrationScore = Math.Min(penetrationTicks / 10.0, 1.0);

            // Speed component (faster = stronger, cap at 5 bars)
            double speedScore = Math.Max(0.0, 1.0 - (reentrySpeed / 5.0));

            // Volume component (higher = stronger, cap at 2x average)
            double volumeScore = Math.Min(volumeRatio / 2.0, 1.0);

            // Weighted average
            return (0.3 * penetrationScore) + (0.4 * speedScore) + (0.3 * volumeScore);
        }

        // ===================================================================
        // UPTHRUST DETECTION (SHORT SETUP)
        // ===================================================================

        /// <summary>
        /// Detect Upthrust (Wyckoff reversal pattern for short)
        /// Upthrust = Break above resistance, no acceptance, snap back inside
        /// </summary>
        /// <param name="high">Current bar high</param>
        /// <param name="close">Current bar close</param>
        /// <param name="open">Current bar open</param>
        /// <param name="rangeHigh">Range high or VAH</param>
        /// <param name="buffer">Buffer above range (in price units)</param>
        /// <param name="outsideBars">Bars spent outside range</param>
        /// <param name="outsideVolPct">Volume percentage outside range</param>
        /// <param name="maxOutsideBars">Max bars allowed outside (default 3)</param>
        /// <param name="maxOutsideVolPct">Max volume % allowed outside (default 0.30)</param>
        /// <param name="requireBearishClose">Require close < open for confirmation</param>
        /// <returns>True if upthrust detected</returns>
        public static bool Upthrust_Detect(
            double high,
            double close,
            double open,
            double rangeHigh,
            double buffer,
            int outsideBars,
            double outsideVolPct,
            int maxOutsideBars = 3,
            double maxOutsideVolPct = 0.30,
            bool requireBearishClose = true)
        {
            // 1. Break: High penetrates above range
            bool hasBreak = high > (rangeHigh + buffer);
            if (!hasBreak) return false;

            // 2. No acceptance: Limited time/volume outside
            bool noAcceptance = (outsideBars <= maxOutsideBars) && (outsideVolPct <= maxOutsideVolPct);
            if (!noAcceptance) return false;

            // 3. Re-entry: Close back inside range
            bool reentry = close < rangeHigh;
            if (!reentry) return false;

            // 4. Optional: Bearish close confirmation
            if (requireBearishClose)
            {
                bool bearishClose = close < open;
                if (!bearishClose) return false;
            }

            return true;
        }

        /// <summary>
        /// Calculate Upthrust strength score (0.0 to 1.0)
        /// </summary>
        public static double Upthrust_Strength(
            double penetrationTicks,
            int reentrySpeed,
            double volumeRatio)
        {
            // Same logic as Spring_Strength (mirror)
            double penetrationScore = Math.Min(penetrationTicks / 10.0, 1.0);
            double speedScore = Math.Max(0.0, 1.0 - (reentrySpeed / 5.0));
            double volumeScore = Math.Min(volumeRatio / 2.0, 1.0);
            
            return (0.3 * penetrationScore) + (0.4 * speedScore) + (0.3 * volumeScore);
        }

        // ===================================================================
        // STOPPING VOLUME DETECTION
        // ===================================================================

        /// <summary>
        /// Detect stopping volume at extremes
        /// High volume with little price progress near range extremes
        /// </summary>
        /// <param name="effortResultRatio">Current ER ratio</param>
        /// <param name="close">Current close</param>
        /// <param name="rangeLow">Range low</param>
        /// <param name="rangeHigh">Range high</param>
        /// <param name="rangeExtremePct">% of range considered "extreme" (default 0.20)</param>
        /// <param name="minERThreshold">Minimum ER for stopping (default 20)</param>
        /// <returns>0 = NONE, 1 = STOPPING_AT_LOW, 2 = STOPPING_AT_HIGH</returns>
        public static int StoppingVolume_Detect(
            double effortResultRatio,
            double close,
            double rangeLow,
            double rangeHigh,
            double rangeExtremePct = 0.20,
            double minERThreshold = 20.0)
        {
            // Must have high ER
            if (effortResultRatio < minERThreshold)
                return 0; // NONE

            double rangeSize = rangeHigh - rangeLow;
            if (rangeSize <= 0.0)
                return 0;

            double extremeZone = rangeSize * rangeExtremePct;

            // Check if at low extreme
            if (close < rangeLow + extremeZone)
                return 1; // STOPPING_AT_LOW

            // Check if at high extreme
            if (close > rangeHigh - extremeZone)
                return 2; // STOPPING_AT_HIGH

            return 0; // NONE
        }

        // ===================================================================
        // WEAK TREND CONTINUATION
        // ===================================================================

        /// <summary>
        /// Detect weak trend continuation (poor effort/result in trend direction)
        /// Used to anticipate exhaustion or reversal
        /// </summary>
        /// <param name="effortResultRatio">Current ER ratio</param>
        /// <param name="trendDirection">1 = uptrend, -1 = downtrend, 0 = no trend</param>
        /// <param name="weakThreshold">ER threshold for weakness (default 15)</param>
        /// <returns>True if trend showing weakness</returns>
        public static bool WeakTrend_Detect(
            double effortResultRatio,
            int trendDirection,
            double weakThreshold = 15.0)
        {
            if (trendDirection == 0) return false;
            return effortResultRatio >= weakThreshold;
        }
    }

    // ===================================================================
    // IMMUTABLE RESULT OBJECTS
    // ===================================================================

    /// <summary>
    /// Effort/Result Analysis Result - Immutable
    /// </summary>
    public sealed class EffortResultResult : IEffortResultResult
    {
        public double Effort { get; }
        public double Result { get; }
        public double Ratio { get; }
        public int Condition { get; }  // 0=EFFICIENT, 1=NORMAL, 2=STOPPING, 3=CLIMACTIC

        public EffortResultResult(double effort, double result, double ratio, int condition)
        {
            Effort = effort;
            Result = result;
            Ratio = ratio;
            Condition = condition;
        }

        public static readonly EffortResultResult Invalid = new EffortResultResult(0, 0, 0, 0);
    }

    /// <summary>
    /// Spring Detection Result - Immutable
    /// </summary>
    public sealed class SpringResult : ISpringResult
    {
        public bool IsDetected { get; }
        public double Strength { get; }         // 0.0 to 1.0
        public double PenetrationTicks { get; }
        public int ReentrySpeed { get; }        // Bars to re-enter

        public SpringResult(bool isDetected, double strength, double penetrationTicks, int reentrySpeed)
        {
            IsDetected = isDetected;
            Strength = strength;
            PenetrationTicks = penetrationTicks;
            ReentrySpeed = reentrySpeed;
        }

        public static readonly SpringResult None = new SpringResult(false, 0, 0, 0);
    }

    /// <summary>
    /// Upthrust Detection Result - Immutable
    /// </summary>
    public sealed class UpthrustResult : IUpthrustResult
    {
        public bool IsDetected { get; }
        public double Strength { get; }
        public double PenetrationTicks { get; }
        public int ReentrySpeed { get; }

        public UpthrustResult(bool isDetected, double strength, double penetrationTicks, int reentrySpeed)
        {
            IsDetected = isDetected;
            Strength = strength;
            PenetrationTicks = penetrationTicks;
            ReentrySpeed = reentrySpeed;
        }

        public static readonly UpthrustResult None = new UpthrustResult(false, 0, 0, 0);
    }

    /// <summary>
    /// Stopping Volume Result - Immutable
    /// </summary>
    public sealed class StoppingVolumeResult : IStoppingVolumeResult
    {
        public int Location { get; }  // 0=NONE, 1=AT_LOW, 2=AT_HIGH
        public double EffortResultRatio { get; }
        public bool IsSignificant { get; }

        public StoppingVolumeResult(int location, double effortResultRatio, bool isSignificant)
        {
            Location = location;
            EffortResultRatio = effortResultRatio;
            IsSignificant = isSignificant;
        }

        public static readonly StoppingVolumeResult None = new StoppingVolumeResult(0, 0, false);
    }
}
