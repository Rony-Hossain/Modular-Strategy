#region Using declarations
using System;
using System.Collections.Generic;

#endregion

namespace MathLogic
{
    /// <summary>
    /// STRUCTURE MODULE - Market structure and volume profile analysis.
    /// Uses object pooling patterns where appropriate.
    /// Caller manages state and allocation.
    /// </summary>
    public static class MathStructure
    {
        // ===================================================================
        // POC (POINT OF CONTROL) CALCULATIONS
        // ===================================================================

        /// <summary>
        /// Find POC from volume profile - caller provides dictionary
        /// Returns POC price and max volume via out parameter
        /// </summary>
        public static double POC_Find(Dictionary<double, double> volumeAtPrice, out double maxVolume)
        {
            maxVolume = 0.0;
            if (volumeAtPrice == null || volumeAtPrice.Count == 0)
                return 0.0;

            double pocPrice = 0.0;
            foreach (var kvp in volumeAtPrice)
            {
                if (kvp.Value > maxVolume)
                {
                    maxVolume = kvp.Value;
                    pocPrice = kvp.Key;
                }
            }
            return pocPrice;
        }

        /// <summary>
        /// POC Stability - is POC stable over time
        /// </summary>
        public static bool POC_IsStable(double pocCurrent, double pocPast, double tickSize, 
            double thresholdTicks = 10.0)
        {
            if (tickSize <= 0.0) return false;
            double driftTicks = Math.Abs(pocCurrent - pocPast) / tickSize;
            return driftTicks < thresholdTicks;
        }

        /// <summary>
        /// POC Drift Direction - returns enum index
        /// 0 = STABLE, 1 = DRIFTING_UP, 2 = DRIFTING_DOWN
        /// </summary>
        public static int POC_DriftDirection(double pocCurrent, double poc5Ago, double poc30Ago, 
            double tickSize, double thresholdTicks = 3.0)
        {
            if (tickSize <= 0.0) return 0;
            
            double drift5 = (pocCurrent - poc5Ago) / tickSize;
            double drift30 = (pocCurrent - poc30Ago) / tickSize;
            
            if (drift30 > thresholdTicks && drift5 > 0) return 1; // DRIFTING_UP
            if (drift30 < -thresholdTicks && drift5 < 0) return 2; // DRIFTING_DOWN
            return 0; // STABLE
        }

        // ===================================================================
        // VALUE AREA CALCULATIONS
        // ===================================================================

        /// <summary>
        /// Calculate Value Area boundaries - DETERMINISTIC VERSION
        /// FIXED: Sorts prices to ensure consistent results across replays
        /// Expands bidirectionally from POC, choosing side with higher volume
        /// </summary>
        public static void ValueArea_Calculate(
            Dictionary<double, double> volumeAtPrice,
            double poc,
            double targetCoverage,
            out double vaHigh,
            out double vaLow)
        {
            vaHigh = poc;
            vaLow = poc;

            if (volumeAtPrice == null || volumeAtPrice.Count == 0)
                return;

            // CRITICAL FIX: Sort prices for deterministic behavior
            // Dictionary enumeration order is undefined in .NET Framework
            // NOTE: No LINQ — use Array.Sort to avoid allocator overhead
            var sortedPrices = new double[volumeAtPrice.Count];
            volumeAtPrice.Keys.CopyTo(sortedPrices, 0);
            Array.Sort(sortedPrices);
            
            // Calculate total volume
            double totalVol = 0.0;
            foreach (var vol in volumeAtPrice.Values)
                totalVol += vol;

            if (totalVol <= 0)
                return;

            double targetVol = targetCoverage * totalVol;

            // Find POC index in sorted array
            // Optimization: Use BinarySearch since array is sorted
            int pocIndex = Array.BinarySearch(sortedPrices, poc);
            if (pocIndex < 0)
            {
                // If exact match not found, find closest of the adjacent elements
                int insertionPoint = ~pocIndex;
                if (insertionPoint >= sortedPrices.Length) pocIndex = sortedPrices.Length - 1;
                else if (insertionPoint == 0) pocIndex = 0;
                else
                {
                    double d1 = Math.Abs(sortedPrices[insertionPoint] - poc);
                    double d2 = Math.Abs(sortedPrices[insertionPoint - 1] - poc);
                    pocIndex = (d2 < d1) ? insertionPoint - 1 : insertionPoint;
                }
            }

            if (pocIndex < 0)
                return;

            // Start expansion from POC
            int loIdx = pocIndex;
            int hiIdx = pocIndex;
            double coveredVol = volumeAtPrice[sortedPrices[pocIndex]];

            // Expand outward bidirectionally, choosing side with higher volume
            while (coveredVol < targetVol && (loIdx > 0 || hiIdx < sortedPrices.Length - 1))
            {
                double volBelow = (loIdx > 0) ? volumeAtPrice[sortedPrices[loIdx - 1]] : -1;
                double volAbove = (hiIdx < sortedPrices.Length - 1) ? volumeAtPrice[sortedPrices[hiIdx + 1]] : -1;

                if (volAbove >= volBelow && hiIdx < sortedPrices.Length - 1)
                {
                    hiIdx++;
                    coveredVol += volumeAtPrice[sortedPrices[hiIdx]];
                }
                else if (loIdx > 0)
                {
                    loIdx--;
                    coveredVol += volumeAtPrice[sortedPrices[loIdx]];
                }
                else if (hiIdx < sortedPrices.Length - 1)
                {
                    hiIdx++;
                    coveredVol += volumeAtPrice[sortedPrices[hiIdx]];
                }
                else
                {
                    break;
                }
            }

            vaLow = sortedPrices[loIdx];
            vaHigh = sortedPrices[hiIdx];
        }

        /// <summary>
        /// Value Area Width in ticks
        /// </summary>
        public static double ValueArea_Width(double vaHigh, double vaLow, double tickSize) => (tickSize > 0.0) ? (vaHigh - vaLow) / tickSize : 0.0;

        // ===================================================================
        // PRICE POSITION CLASSIFICATION
        // ===================================================================

        /// <summary>
        /// Price Position relative to Value Area - returns enum index
        /// 0 = INSIDE_VALUE
        /// 1 = BELOW_VALUE
        /// 2 = ABOVE_VALUE
        /// 3 = EXTENDED_BELOW
        /// 4 = EXTENDED_ABOVE
        /// </summary>
        public static int PricePosition_Classify(double close, double vaHigh, double vaLow, double sdHybrid)
        {
            double extendedThreshold = 0.5 * sdHybrid;
            
            if (close > vaHigh + extendedThreshold) return 4; // EXTENDED_ABOVE
            if (close > vaHigh) return 2; // ABOVE_VALUE
            if (close < vaLow - extendedThreshold) return 3; // EXTENDED_BELOW
            if (close < vaLow) return 1; // BELOW_VALUE
            return 0; // INSIDE_VALUE
        }

        /// <summary>
        /// Check if price position supports fade long
        /// </summary>
        public static bool PricePosition_SupportsFadeLong(int positionIndex, int driftIndex) =>
            // EXTENDED_BELOW or BELOW_VALUE, and NOT DRIFTING_DOWN
            (positionIndex == 3 || positionIndex == 1) && driftIndex != 2;

        /// <summary>
        /// Check if price position supports fade short
        /// </summary>
        public static bool PricePosition_SupportsFadeShort(int positionIndex, int driftIndex) =>
            // EXTENDED_ABOVE or ABOVE_VALUE, and NOT DRIFTING_UP
            (positionIndex == 4 || positionIndex == 2) && driftIndex != 1;

        // ===================================================================
        // CHOP DETECTION
        // ===================================================================

        /// <summary>
        /// Detect choppy market conditions
        /// </summary>
        public static bool IsChoppy(int regimeFlips, double vwapSlope, double tickSize, 
            int flipThreshold = 4, double slopeThresholdTicks = 5.0)
        {
            if (tickSize <= 0.0) return false;
            double slopeTicks = Math.Abs(vwapSlope) / tickSize;
            return (regimeFlips > flipThreshold) && (slopeTicks < slopeThresholdTicks);
        }

        // ===================================================================
        // SESSION TIMING
        // ===================================================================

        /// <summary>
        /// Calculate session progress percentage (0.0 to 1.0)
        /// </summary>
        public static double SessionProgress(int barsSinceOpen, int totalSessionBars)
        {
            if (totalSessionBars <= 0) return 0.0;
            return Math.Min(1.0, (double)barsSinceOpen / totalSessionBars);
        }

        /// <summary>
        /// Is session in opening period (first X%)
        /// </summary>
        public static bool IsOpeningPeriod(double sessionPct, double threshold = 0.15) => sessionPct < threshold;

        /// <summary>
        /// Is session in closing period (last X%)
        /// </summary>
        public static bool IsClosingPeriod(double sessionPct, double threshold = 0.85) => sessionPct > threshold;

        // ===================================================================
        // ACCEPTANCE METRICS
        // ===================================================================

        /// <summary>
        /// Count bars outside boundary
        /// </summary>
        public static int Acceptance_BarsOutside(double[] closes, double boundary, bool isAbove, int length)
        {
            if (closes == null || length <= 0 || length > closes.Length)
                return 0;

            int count = 0;
            for (int i = 0; i < length; i++)
            {
                if (isAbove && closes[i] > boundary)
                    count++;
                else if (!isAbove && closes[i] < boundary)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Calculate volume percentage outside boundary
        /// </summary>
        public static double Acceptance_VolumeOutsidePct(
            double[] closes, 
            double[] volumes, 
            double boundary, 
            bool isAbove, 
            int length)
        {
            if (closes == null || volumes == null || length <= 0 || 
                length > closes.Length || length > volumes.Length)
                return 0.0;

            double totalVol = 0.0;
            double outsideVol = 0.0;

            for (int i = 0; i < length; i++)
            {
                totalVol += volumes[i];
                if (isAbove && closes[i] > boundary)
                    outsideVol += volumes[i];
                else if (!isAbove && closes[i] < boundary)
                    outsideVol += volumes[i];
            }

            return (totalVol > 0.0) ? outsideVol / totalVol : 0.0;
        }

        /// <summary>
        /// Calculate reentry speed (how quickly price returned inside)
        /// Returns number of bars to reenter from first excursion
        /// </summary>
        public static int Acceptance_ReentrySpeed(double[] closes, double boundary, bool isAbove, int length)
        {
            if (closes == null || length <= 0 || length > closes.Length)
                return 0;

            // Find first bar outside
            int firstOutside = -1;
            for (int i = 0; i < length; i++)
            {
                bool isOutside = isAbove ? (closes[i] > boundary) : (closes[i] < boundary);
                if (isOutside)
                {
                    firstOutside = i;
                    break;
                }
            }

            if (firstOutside < 0)
                return 0; // Never went outside

            // Find first bar back inside after going outside
            for (int i = firstOutside + 1; i < length; i++)
            {
                bool isInside = isAbove ? (closes[i] <= boundary) : (closes[i] >= boundary);
                if (isInside)
                    return i - firstOutside;
            }

            return length - firstOutside; // Never returned, return max
        }

        /// <summary>
        /// Classify acceptance state - returns enum index
        /// 0 = INSIDE (never went outside)
        /// 1 = REJECTED (went outside, came back quickly)
        /// 2 = TESTING (went outside, slow return)
        /// 3 = ACCEPTED (went outside, stayed outside)
        /// </summary>
        public static int Acceptance_Classify(
            int barsOutside, 
            double volumeOutsidePct, 
            int reentrySpeed,
            int maxBarsOutside = 3,
            double maxVolOutside = 0.30)
        {
            if (barsOutside == 0)
                return 0; // INSIDE

            if (barsOutside <= maxBarsOutside && volumeOutsidePct < maxVolOutside && reentrySpeed <= 3)
                return 1; // REJECTED

            if (reentrySpeed > 3 && reentrySpeed < 10)
                return 2; // TESTING

            return 3; // ACCEPTED
        }

        // ===================================================================
        // BALANCE QUALITY METRICS
        // ===================================================================

        /// <summary>
        /// Calculate overlap ratio between two value areas
        /// </summary>
        public static double Balance_OverlapRatio(
            double va1High, 
            double va1Low, 
            double va2High, 
            double va2Low)
        {
            double overlapHigh = Math.Min(va1High, va2High);
            double overlapLow = Math.Max(va1Low, va2Low);
            double overlap = Math.Max(0, overlapHigh - overlapLow);

            double va1Width = va1High - va1Low;
            double va2Width = va2High - va2Low;
            double avgWidth = (va1Width + va2Width) / 2.0;

            return (avgWidth > 0.0) ? overlap / avgWidth : 0.0;
        }

        /// <summary>
        /// Calculate range compression ratio
        /// </summary>
        public static double Balance_CompressionRatio(double[] ranges, int length)
        {
            if (ranges == null || length < 2 || length > ranges.Length)
                return 1.0;

            // Calculate current range vs average range
            double currentRange = ranges[0];
            double sum = 0.0;
            for (int i = 1; i < length; i++)
                sum += ranges[i];

            double avgRange = sum / (length - 1);
            return (avgRange > 0.0) ? currentRange / avgRange : 1.0;
        }

        /// <summary>
        /// Calculate VWAP touch rate
        /// </summary>
        public static double Balance_VWAPTouchRate(
            double[] highs, 
            double[] lows, 
            double[] vwaps, 
            int length, 
            double threshold)
        {
            if (highs == null || lows == null || vwaps == null || length <= 0 ||
                length > highs.Length || length > lows.Length || length > vwaps.Length)
                return 0.0;

            int touches = 0;
            for (int i = 0; i < length; i++)
            {
                // Price within threshold of VWAP
                if ((vwaps[i] >= lows[i] - threshold) && (vwaps[i] <= highs[i] + threshold))
                    touches++;
            }

            return (double)touches / length;
        }

        /// <summary>
        /// Calculate Value Area width stability (standard deviation of VA widths)
        /// Lower value = more stable = more balanced conditions
        /// </summary>
        public static double Balance_VAStability(double[] vaWidths, int length)
        {
            if (vaWidths == null || length < 2 || length > vaWidths.Length)
                return 0.0;

            // Calculate mean
            double sum = 0.0;
            for (int i = 0; i < length; i++)
                sum += vaWidths[i];
            double mean = sum / length;

            // Calculate standard deviation
            double sumSq = 0.0;
            for (int i = 0; i < length; i++)
            {
                double diff = vaWidths[i] - mean;
                sumSq += diff * diff;
            }

            return Math.Sqrt(sumSq / (length - 1));
        }

        /// <summary>
        /// Calculate overall balance quality score (0.0 to 1.0)
        /// Higher score = more balanced/ranging
        /// </summary>
        /// <param name="overlapRatio">Overlap ratio</param>
        /// <param name="compression">Compression ratio</param>
        /// <param name="vwapTouchRate">VWAP touch rate</param>
        /// <param name="vaStability">VA width stability (lower = more balanced)</param>
        /// <returns>Balance quality score 0.0 to 1.0</returns>
        public static double Balance_QualityScore(
            double overlapRatio,
            double compression,
            double vwapTouchRate,
            double vaStability)
        {
            // Overlap component (0-1, higher = more balanced)
            double overlapScore = overlapRatio;

            // Compression component (normalize: < 0.8 = trending, > 1.2 = compressed)
            double compressionScore = compression < 0.8 ? 0.0 :
                                     compression > 1.2 ? 1.0 :
                                     (compression - 0.8) / 0.4;

            // Touch rate component (0-1, higher = more balanced)
            double touchScore = Math.Min(vwapTouchRate / 0.5, 1.0); // Normalize to 50% touch rate

            // Stability component (normalize inverse: lower stdev = more stable)
            // Assume typical VA stdev range of 0-20 ticks
            double stabilityScore = Math.Max(0.0, 1.0 - (vaStability / 20.0));

            // Weighted average
            return (0.30 * overlapScore) + (0.25 * compressionScore) + 
                   (0.25 * touchScore) + (0.20 * stabilityScore);
        }

        // ===================================================================
        // PROFILE SHAPE METRICS
        // ===================================================================

        /// <summary>
        /// Calculate POC skew - volume distribution above vs below POC
        /// </summary>
        /// <param name="volumeAtPrice">Volume profile dictionary</param>
        /// <param name="poc">POC price</param>
        /// <returns>Skew ratio (> 1.0 = more volume above, < 1.0 = more below)</returns>
        public static double Profile_POCSkew(Dictionary<double, double> volumeAtPrice, double poc)
        {
            if (volumeAtPrice == null || volumeAtPrice.Count == 0)
                return 1.0;

            double volumeAbove = 0.0;
            double volumeBelow = 0.0;

            foreach (var kvp in volumeAtPrice)
            {
                if (kvp.Key > poc)
                    volumeAbove += kvp.Value;
                else if (kvp.Key < poc)
                    volumeBelow += kvp.Value;
            }

            return (volumeBelow > 0.0) ? volumeAbove / volumeBelow : 1.0;
        }

        /// <summary>
        /// Detect excess tail at extremes (low volume tail indicating rejection)
        /// </summary>
        /// <param name="volumeAtPrice">Volume profile dictionary</param>
        /// <param name="extremePrice">Price at extreme (high or low)</param>
        /// <param name="tailThreshold">Volume threshold for "excess" (% of max volume)</param>
        /// <returns>Tail strength in price levels</returns>
        public static int Profile_ExcessTail(
            Dictionary<double, double> volumeAtPrice,
            double extremePrice,
            double tailThreshold = 0.20)
        {
            if (volumeAtPrice == null || volumeAtPrice.Count == 0)
                return 0;

            // Find max volume
            double maxVol = 0.0;
            foreach (var vol in volumeAtPrice.Values)
                if (vol > maxVol) maxVol = vol;

            if (maxVol <= 0.0) return 0;

            double thresholdVol = maxVol * tailThreshold;

            // Build sorted price array — no LINQ
            var sortedPrices = new double[volumeAtPrice.Count];
            volumeAtPrice.Keys.CopyTo(sortedPrices, 0);
            Array.Sort(sortedPrices);

            // Determine direction: if extremePrice is near top, walk downward from top
            // If near bottom, walk upward from bottom
            double mid = (sortedPrices[0] + sortedPrices[sortedPrices.Length - 1]) / 2.0;
            int tailLevels = 0;

            if (extremePrice >= mid)
            {
                // Walk down from the top extreme
                for (int i = sortedPrices.Length - 1; i >= 0; i--)
                {
                    if (volumeAtPrice[sortedPrices[i]] <= thresholdVol)
                        tailLevels++;
                    else
                        break;
                }
            }
            else
            {
                // Walk up from the bottom extreme
                for (int i = 0; i < sortedPrices.Length; i++)
                {
                    if (volumeAtPrice[sortedPrices[i]] <= thresholdVol)
                        tailLevels++;
                    else
                        break;
                }
            }

            return tailLevels;
        }

        /// <summary>
        /// Simple multi-distribution detection - count local maxima in profile
        /// Multiple peaks suggest overlapping distributions (bracket, not trend)
        /// </summary>
        /// <param name="volumeAtPrice">Volume profile dictionary</param>
        /// <param name="peakThreshold">Min volume to be considered a peak (% of max)</param>
        /// <returns>Number of peaks detected</returns>
        public static int Profile_PeakCount(
            Dictionary<double, double> volumeAtPrice,
            double peakThreshold = 0.50)
        {
            if (volumeAtPrice == null || volumeAtPrice.Count < 3)
                return 0;

            // Build sorted price array — no LINQ
            var sortedPrices = new double[volumeAtPrice.Count];
            volumeAtPrice.Keys.CopyTo(sortedPrices, 0);
            Array.Sort(sortedPrices);

            // Find max volume for relative threshold
            double maxVol = 0.0;
            foreach (var vol in volumeAtPrice.Values)
                if (vol > maxVol) maxVol = vol;

            if (maxVol <= 0.0) return 0;

            double minPeakVol = maxVol * peakThreshold;
            int peaks = 0;

            // Count local maxima: a price level is a peak if its volume is greater
            // than both neighbours and above the minimum peak threshold
            for (int i = 1; i < sortedPrices.Length - 1; i++)
            {
                double vol = volumeAtPrice[sortedPrices[i]];
                if (vol < minPeakVol) continue;

                double volPrev = volumeAtPrice[sortedPrices[i - 1]];
                double volNext = volumeAtPrice[sortedPrices[i + 1]];

                if (vol > volPrev && vol > volNext)
                    peaks++;
            }

            return Math.Max(1, peaks); // At minimum 1 peak (the POC)
        }
    }

    // ===================================================================
    // ADDITIONAL IMMUTABLE RESULT OBJECTS
    // ===================================================================

    /// <summary>
    /// Acceptance Metrics Result - Immutable
    /// FIXED: Added IAcceptanceResult interface implementation
    /// </summary>
    public sealed class AcceptanceResult : IAcceptanceResult
    {
        public int BarsOutside { get; }
        public double VolumeOutsidePct { get; }
        public int ReentrySpeed { get; }
        public int Classification { get; }  // 0=INSIDE, 1=REJECTED, 2=TESTING, 3=ACCEPTED

        public AcceptanceResult(int barsOutside, double volumeOutsidePct, int reentrySpeed, int classification)
        {
            BarsOutside = barsOutside;
            VolumeOutsidePct = volumeOutsidePct;
            ReentrySpeed = reentrySpeed;
            Classification = classification;
        }

        public static readonly AcceptanceResult Inside = new AcceptanceResult(0, 0, 0, 0);
    }

    /// <summary>
    /// Balance Quality Result - Immutable
    /// FIXED: Added IBalanceQualityResult interface implementation
    /// </summary>
    public sealed class BalanceQualityResult : IBalanceQualityResult
    {
        public double OverlapRatio { get; }
        public double Compression { get; }
        public double VWAPTouchRate { get; }
        public double VAStability { get; }
        public double QualityScore { get; }  // 0.0 to 1.0

        public BalanceQualityResult(double overlapRatio, double compression, double vwapTouchRate, 
            double vaStability, double qualityScore)
        {
            OverlapRatio = overlapRatio;
            Compression = compression;
            VWAPTouchRate = vwapTouchRate;
            VAStability = vaStability;
            QualityScore = qualityScore;
        }

        public static readonly BalanceQualityResult Default = 
            new BalanceQualityResult(0.5, 1.0, 0.5, 10.0, 0.5);
    }

    /// <summary>
    /// Profile Shape Result - Immutable
    /// FIXED: Added IProfileShapeResult interface implementation
    /// </summary>
    public sealed class ProfileShapeResult : IProfileShapeResult
    {
        public double POCSkew { get; }          // > 1.0 = bullish, < 1.0 = bearish
        public int ExcessTailSize { get; }      // Tail levels at extremes
        public int PeakCount { get; }           // Number of distribution peaks

        public ProfileShapeResult(double pocSkew, int excessTailSize, int peakCount)
        {
            POCSkew = pocSkew;
            ExcessTailSize = excessTailSize;
            PeakCount = peakCount;
        }

        public static readonly ProfileShapeResult Balanced = new ProfileShapeResult(1.0, 0, 1);
    }
}
