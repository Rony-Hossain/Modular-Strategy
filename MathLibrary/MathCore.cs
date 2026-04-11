#region Using declarations
using System;
#endregion

namespace MathLogic
{
    /// <summary>
    /// CORE MATH MODULE - Pure deterministic calculations only.
    /// NO policy logic, NO allocation-heavy operations.
    /// Optimized for per-bar execution in NinjaTrader 8.
    /// Thread-safe, allocation-minimal, stateless.
    /// </summary>
    public static class MathCore
    {
        // ===================================================================
        // VWAP CALCULATIONS
        // ===================================================================

        /// <summary>
        /// Enhanced Typical Price - Minimal allocation version
        /// </summary>
        public static double TP_Adaptive(double high, double low, double close, double atr, 
            double rangeThresholdMultiplier = 1.5)
        {
            double range = high - low;
            double threshold = rangeThresholdMultiplier * atr;
            
            // Conditional without branching where possible
            return (range > threshold) 
                ? (high + low + close + close) * 0.25  // weighted
                : (high + low + close) / 3.0;          // standard
        }

        /// <summary>
        /// VWAP calculation - caller provides pre-allocated arrays
        /// FIXED: Added volume.Length validation
        /// </summary>
        public static double VWAP_Calculate(double[] tpAdaptive, double[] volume, int length, out bool valid)
        {
            valid = false;
            if (tpAdaptive == null || volume == null || length <= 0 || 
                length > tpAdaptive.Length || length > volume.Length)
                return 0.0;

            double pvSum = 0.0;
            double vSum = 0.0;
            
            for (int i = 0; i < length; i++)
            {
                pvSum += tpAdaptive[i] * volume[i];
                vSum += volume[i];
            }
            
            if (vSum == 0.0)
                return 0.0;

            valid = true;
            return pvSum / vSum;
        }

        /// <summary>
        /// VWAP Velocity - simple rate of change
        /// </summary>
        public static double VWAP_Velocity(double current, double past, int periods = 5) => (periods > 0) ? (current - past) / periods : 0.0;

        /// <summary>
        /// VWAP Drift Classification - returns enum index for performance
        /// 0 = BALANCED, 1 = TRANSITIONAL, 2 = TRENDING
        /// </summary>
        public static int VWAP_DriftClassify(double velocity, double trendingThreshold = 0.02, 
            double balancedThreshold = 0.005)
        {
            double absVel = Math.Abs(velocity);
            if (absVel < balancedThreshold) return 0; // BALANCED
            if (absVel > trendingThreshold) return 2; // TRENDING
            return 1; // TRANSITIONAL
        }

        // ===================================================================
        // STANDARD DEVIATION CALCULATIONS
        // ===================================================================

        /// <summary>
        /// Standard Deviation - pre-allocated array, no LINQ
        /// </summary>
        public static double SD_Calculate(double[] deviations, int length, out bool valid)
        {
            valid = false;
            if (deviations == null || length < 2 || length > deviations.Length)
                return 0.0;

            // Calculate mean
            double sum = 0.0;
            for (int i = 0; i < length; i++)
                sum += deviations[i];
            double mean = sum / length;

            // Calculate sum of squared differences
            double sumSq = 0.0;
            for (int i = 0; i < length; i++)
            {
                double diff = deviations[i] - mean;
                sumSq += diff * diff;
            }

            valid = true;
            return Math.Sqrt(sumSq / (length - 1));
        }

        /// <summary>
        /// Adaptive SD Window - returns window size based on session timing
        /// </summary>
        public static int SD_WindowSize(int barsSinceOpen, int barsToClose)
        {
            if (barsSinceOpen < 30) return 20;
            if (barsToClose < 12) return 10;
            return 15;
        }

        /// <summary>
        /// Hybrid SD - use TradingMath.SD_Hybrid_RMS() instead.
        /// Kept for backward compatibility only — delegates to TradingMath.
        /// NOTE: TradingMath.SD_Hybrid_RMS uses RMS blending (sqrt of weighted sum of squares)
        /// which is more accurate than the linear blend here. Use it.
        /// </summary>
        [Obsolete("Use TradingMath.SD_Hybrid_RMS() — RMS blending avoids under-sizing when one SD dominates.")]
        public static double SD_Hybrid(double sdSession, double sdRolling, double sessionPct)
            => TradingMath.SD_Hybrid_RMS(sdSession, sdRolling, sessionPct);

        /// <summary>
        /// SD Tradeable Check - use TradingMath.SD_IsTradeableTicks() instead.
        /// Kept for backward compatibility only — delegates to TradingMath.
        /// </summary>
        [Obsolete("Use TradingMath.SD_IsTradeableTicks() — it is identical and is the canonical version.")]
        public static bool SD_IsTradeable(double sdValue, double tickSize,
            double minTicks = 12.0, double maxTicks = 150.0)
            => TradingMath.SD_IsTradeableTicks(sdValue, tickSize, minTicks, maxTicks);

        // ===================================================================
        // Z-SCORE & NORMALIZATION
        // ===================================================================

        /// <summary>
        /// Z-Score calculation
        /// </summary>
        public static double ZScore(double value, double mean, double stdDev) => (stdDev > 0.0) ? (value - mean) / stdDev : 0.0;

        /// <summary>
        /// Zone Classification by Z-Score - returns enum index
        /// 0 = INSIDE_CORE, 1 = INSIDE_ACCEPTANCE, 2 = BELOW_ACCEPTANCE, 3 = ABOVE_ACCEPTANCE
        /// </summary>
        public static int Zone_Classify(double zScore)
        {
            if (zScore < -1.0) return 2; // BELOW_ACCEPTANCE
            if (zScore > 1.0) return 3;  // ABOVE_ACCEPTANCE
            if (Math.Abs(zScore) <= 0.5) return 0; // INSIDE_CORE
            return 1; // INSIDE_ACCEPTANCE
        }

        /// <summary>
        /// Normalize delta by volume
        /// </summary>
        public static double Delta_Normalize(double delta, double volume) => (volume > 0.0) ? delta / volume : 0.0;

        // ===================================================================
        // RATIO & IMBALANCE CALCULATIONS
        // ===================================================================

        /// <summary>
        /// Imbalance Ratio - high-frequency safe
        /// </summary>
        public static double Imbalance_Ratio(double bidVol, double askVol)
        {
            double minVol = Math.Min(bidVol, askVol);
            if (minVol <= 0.0) return double.MaxValue; // Avoid infinity for comparisons
            return Math.Max(bidVol, askVol) / minVol;
        }

        /// <summary>
        /// Dominant Side - returns 0 for BID, 1 for ASK
        /// </summary>
        public static int Dominant_Side(double bidVol, double askVol) => (askVol > bidVol) ? 1 : 0;

        // ===================================================================
        // VELOCITY & MOMENTUM
        // ===================================================================

        /// <summary>
        /// Rate of Change - generic
        /// </summary>
        public static double ROC(double current, double past, int periods) => (periods > 0) ? (current - past) / periods : 0.0;

        /// <summary>
        /// Acceleration - second derivative
        /// </summary>
        public static double Acceleration(double rocCurrent, double rocPrevious) => rocCurrent - rocPrevious;

        // ===================================================================
        // DISTANCE & RANGE CALCULATIONS
        // ===================================================================

        /// <summary>
        /// Distance in ticks
        /// </summary>
        public static double DistanceInTicks(double price1, double price2, double tickSize) => (tickSize > 0.0) ? Math.Abs(price1 - price2) / tickSize : 0.0;

        /// <summary>
        /// Is price near target - within threshold
        /// </summary>
        public static bool IsNear(double current, double target, double threshold) => Math.Abs(current - target) < threshold;

        // ===================================================================
        // SWING POINT CALCULATIONS
        // ===================================================================

        /// <summary>
        /// Find minimum in array segment
        /// </summary>
        public static double ArrayMin(double[] values, int start, int length)
        {
            if (values == null || start < 0 || start + length > values.Length)
                return 0.0;
            
            double min = double.MaxValue;
            for (int i = start; i < start + length; i++)
            {
                if (values[i] < min)
                    min = values[i];
            }
            return min;
        }

        /// <summary>
        /// Find maximum in array segment
        /// </summary>
        public static double ArrayMax(double[] values, int start, int length)
        {
            if (values == null || start < 0 || start + length > values.Length)
                return 0.0;
            
            double max = double.MinValue;
            for (int i = start; i < start + length; i++)
            {
                if (values[i] > max)
                    max = values[i];
            }
            return max;
        }

        // ===================================================================
        // PERCENTAGE CALCULATIONS
        // ===================================================================

        /// <summary>
        /// Percentage change
        /// </summary>
        public static double PercentChange(double newValue, double oldValue) => (oldValue != 0.0) ? (newValue - oldValue) / Math.Abs(oldValue) : 0.0;

        /// <summary>
        /// Is percentage change above threshold
        /// </summary>
        public static bool IsLargeMove(double newValue, double oldValue, double thresholdPct) => Math.Abs(PercentChange(newValue, oldValue)) > thresholdPct;
    }
}
