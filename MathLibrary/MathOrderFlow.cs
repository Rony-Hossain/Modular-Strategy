#region Using declarations
using System;
#endregion

namespace MathLogic
{
    /// <summary>
    /// ORDER FLOW MODULE - Delta, absorption, and imbalance calculations.
    /// Optimized for tick/volume bar processing.
    /// Caller maintains cumulative state.
    /// CRITICAL: Minimize allocations - runs on every tick in some cases.
    /// </summary>
    public static class MathOrderFlow
    {
        // ===================================================================
        // DELTA CALCULATIONS
        // ===================================================================

        /// <summary>
        /// Calculate raw delta
        /// </summary>
        public static double Delta_Raw(double askVolume, double bidVolume) => askVolume - bidVolume;

        /// <summary>
        /// Normalize delta (avoid division by zero)
        /// </summary>
        public static double Delta_Normalized(double delta, double totalVolume) => (totalVolume > 0.0) ? delta / totalVolume : 0.0;

        /// <summary>
        /// Classify normalized delta strength - returns enum index
        /// 0 = NOISE (<0.2)
        /// 1 = WEAK (0.2-0.4)
        /// 2 = MODERATE (0.4-0.6)
        /// 3 = STRONG (>0.6)
        /// </summary>
        public static int Delta_ClassifyStrength(double normalizedDelta)
        {
            double abs = Math.Abs(normalizedDelta);
            if (abs < 0.2) return 0; // NOISE
            if (abs < 0.4) return 1; // WEAK
            if (abs < 0.6) return 2; // MODERATE
            return 3; // STRONG
        }

        // ===================================================================
        // DIVERGENCE DETECTION
        // ===================================================================

        /// <summary>
        /// Check for bullish divergence (lower lows in price, higher lows in delta)
        /// Arrays: [0]=current, [1]=1-bar-ago, [2]=2-bars-ago
        /// </summary>
        public static bool Divergence_Bullish(
            double low0, double low1, double low2,
            double cd0, double cd1, double cd2,
            double minStrength,
            out double strength)
        {
            strength = 0.0;
            
            // Price structure: lower lows
            bool priceLL = (low0 < low1) && (low1 < low2);
            if (!priceLL) return false;
            
            // Delta structure: higher lows
            bool deltaHL = (cd0 > cd1) && (cd1 > cd2);
            if (!deltaHL) return false;
            
            // Calculate divergence strength
            strength = Math.Abs(cd0 - cd2) / 3.0;
            
            return strength > minStrength;
        }

        /// <summary>
        /// Check for bearish divergence (higher highs in price, lower highs in delta)
        /// </summary>
        public static bool Divergence_Bearish(
            double high0, double high1, double high2,
            double cd0, double cd1, double cd2,
            double minStrength,
            out double strength)
        {
            strength = 0.0;
            
            // Price structure: higher highs
            bool priceHH = (high0 > high1) && (high1 > high2);
            if (!priceHH) return false;
            
            // Delta structure: lower highs
            bool deltaLH = (cd0 < cd1) && (cd1 < cd2);
            if (!deltaLH) return false;
            
            // Calculate divergence strength
            strength = Math.Abs(cd0 - cd2) / 3.0;
            
            return strength > minStrength;
        }

        // ===================================================================
        // MOMENTUM CLASSIFICATION
        // ===================================================================

        /// <summary>
        /// Classify delta momentum state - returns enum index
        /// 0 = BALANCED
        /// 1 = ACCELERATING_BULLISH
        /// 2 = DECELERATING_BULLISH
        /// 3 = ACCELERATING_BEARISH
        /// 4 = DECELERATING_BEARISH
        /// </summary>
        public static int DeltaMomentum_Classify(double roc, double acceleration,
            double accelThreshold = 300.0, double decelThreshold = 100.0)
        {
            if (roc > accelThreshold  && acceleration > 0) return 1; // ACCELERATING_BULLISH
            if (roc > decelThreshold  && acceleration < 0) return 2; // DECELERATING_BULLISH
            if (roc < -accelThreshold && acceleration < 0) return 3; // ACCELERATING_BEARISH
            if (roc < -decelThreshold && acceleration > 0) return 4; // DECELERATING_BEARISH
            return 0; // BALANCED
        }

        // ===================================================================
        // DELTA CONSISTENCY
        // ===================================================================

        /// <summary>
        /// Calculate delta-price consistency ratio
        /// Returns ratio of bars where price and delta agreed
        /// OPTIMIZED: Uses array indices directly, no LINQ
        /// </summary>
        public static double DeltaConsistency_Calculate(double[] closes, double[] deltas, int length)
        {
            if (closes == null || deltas == null || length < 2 || 
                length > closes.Length || length > deltas.Length)
                return 0.5;
            
            int consistentBars = 0;
            for (int i = 0; i < length - 1; i++)
            {
                bool priceUp = closes[i] > closes[i + 1];
                bool deltaUp = deltas[i] > 0;
                if (priceUp == deltaUp)
                    consistentBars++;
            }
            
            return (double)consistentBars / (length - 1);
        }

        /// <summary>
        /// Is consistency ratio a fade signal (low consistency)
        /// </summary>
        public static bool DeltaConsistency_IsFadeSignal(double consistencyRatio, double threshold = 0.4) => consistencyRatio < threshold;

        // ===================================================================
        // ABSORPTION CALCULATIONS
        // ===================================================================

        /// <summary>
        /// Calculate absorption score from bid/ask volumes
        /// CRITICAL: This can be called on every tick - must be fast
        /// </summary>
        public static double Absorption_CalculateScore(
            double[] bidVols, 
            double[] askVols, 
            int length,
            double high,
            double low,
            double tickSize,
            double imbalanceThreshold = 2.0)
        {
            if (bidVols == null || askVols == null || length <= 0 || 
                length > bidVols.Length || length > askVols.Length)
                return 0.0;

            double totalImbalanced = 0.0;
            for (int i = 0; i < length; i++)
            {
                double minVol = Math.Min(bidVols[i], askVols[i]);
                if (minVol <= 0.0) continue;
                
                double ratio = Math.Max(bidVols[i], askVols[i]) / minVol;
                if (ratio > imbalanceThreshold)
                {
                    totalImbalanced += Math.Abs(askVols[i] - bidVols[i]);
                }
            }

            double rangeTicks = (tickSize > 0.0) ? (high - low) / tickSize : 0.0;
            return (rangeTicks > 0.0) ? totalImbalanced / (rangeTicks + 1.0) : 0.0;
        }

        /// <summary>
        /// Count stacked imbalances for long (bid absorption)
        /// Returns count of levels with bid imbalance above threshold
        /// </summary>
        public static int Absorption_CountStackedBid(
            double[] bidVols,
            double[] askVols,
            int length,
            double imbalanceThreshold = 3.0)
        {
            if (bidVols == null || askVols == null || length <= 0 ||
                length > bidVols.Length || length > askVols.Length)
                return 0;

            int stackedCount = 0;
            for (int i = 0; i < length; i++)
            {
                double minVol = Math.Min(bidVols[i], askVols[i]);
                if (minVol <= 0.0) continue;
                
                double ratio = Math.Max(bidVols[i], askVols[i]) / minVol;
                if (ratio > imbalanceThreshold && bidVols[i] > askVols[i])
                    stackedCount++;
            }
            return stackedCount;
        }

        /// <summary>
        /// Count stacked imbalances for short (ask absorption)
        /// </summary>
        public static int Absorption_CountStackedAsk(
            double[] bidVols,
            double[] askVols,
            int length,
            double imbalanceThreshold = 3.0)
        {
            if (bidVols == null || askVols == null || length <= 0 ||
                length > bidVols.Length || length > askVols.Length)
                return 0;

            int stackedCount = 0;
            for (int i = 0; i < length; i++)
            {
                double minVol = Math.Min(bidVols[i], askVols[i]);
                if (minVol <= 0.0) continue;
                
                double ratio = Math.Max(bidVols[i], askVols[i]) / minVol;
                if (ratio > imbalanceThreshold && askVols[i] > bidVols[i])
                    stackedCount++;
            }
            return stackedCount;
        }

        /// <summary>
        /// Check if stacked count meets absorption threshold
        /// </summary>
        public static bool Absorption_IsDetected(int stackedCount, int minStackedLevels = 3) => stackedCount >= minStackedLevels;

        // ===================================================================
        // DELTA EXHAUSTION
        // Detects when bar-by-bar delta momentum is fading in the direction
        // of the current price move — the core technique from footprint trading.
        //
        // The rule (bull exhaustion = sellers starting to win):
        //   Price was moving UP (recent close > prior close)
        //   AND bar delta is weakening: delta[0] < delta[1] AND delta[1] < delta[2]
        //   → buyers are losing conviction despite higher prices
        //   → signals potential short or at minimum caution on longs
        //
        // Bear exhaustion is the mirror: price moving DOWN, delta becoming less
        // negative — sellers fading, potential long or caution on shorts.
        //
        // Arrays: index [0] = current bar, [1] = 1 bar ago, [2] = 2 bars ago.
        // This matches the NT8 / BarSnapshot convention throughout this codebase.
        //
        // Returns:
        //   +1  = bull exhaustion  (buyers fading on up-move  → confirms short)
        //   -1  = bear exhaustion  (sellers fading on down-move → confirms long)
        //    0  = no exhaustion detected
        // ===================================================================

        /// <summary>
        /// Detect bar-delta exhaustion over a 3-bar window.
        ///
        /// Bull exhaustion (+1): price moving up AND delta weakening consecutively.
        ///   delta[0] &lt; delta[1] &lt; delta[2] while closes[0] &gt; closes[1]
        ///
        /// Bear exhaustion (-1): price moving down AND delta strengthening (less negative).
        ///   delta[0] &gt; delta[1] &gt; delta[2] (i.e. becoming less negative) while closes[0] &lt; closes[1]
        ///
        /// The minimum delta change per step prevents noise from triggering on
        /// essentially flat delta bars (e.g. +5 vs +3 on thin volume).
        /// </summary>
        /// <param name="barDeltas">Rolling bar delta array. [0]=current, [1]=prev, [2]=2 bars ago.</param>
        /// <param name="closes">Rolling close array. [0]=current, [1]=previous.</param>
        /// <param name="minDeltaStep">Minimum delta change per step to qualify (filters noise).</param>
        /// <returns>+1 bull exhaustion, -1 bear exhaustion, 0 none.</returns>
        public static int DeltaExhaustion_Detect(
            double[] barDeltas,
            double[] closes,
            double   minDeltaStep = 0.0)
        {
            if (barDeltas == null || barDeltas.Length < 3) return 0;
            if (closes    == null || closes.Length    < 3) return 0;

            double d0 = barDeltas[0];
            double d1 = barDeltas[1];
            double d2 = barDeltas[2];

            // Price direction uses 3 bars, not 1. The video trader looks at a
            // "straight move" — several consecutive bars in one direction — before
            // reading delta exhaustion. A single close comparison fires on noise.
            // Requiring closes[0] > closes[1] > closes[2] (or the reverse) ensures
            // we are in a genuine directional move before flagging exhaustion.
            bool priceUp   = closes[0] > closes[1] && closes[1] > closes[2];
            bool priceDown = closes[0] < closes[1] && closes[1] < closes[2];

            // Bull exhaustion: price moving up consecutively AND delta weakening.
            // d0 < d1 < d2 — each bar has less buying than the previous bar.
            // The video: "229 is greater than 393 no... 51 is greater than 229 no
            // → uh oh the trend may be getting ready to correct"
            bool bullFading = (d1 - d0) > minDeltaStep
                           && (d2 - d1) > minDeltaStep;

            // Bear exhaustion: price falling consecutively AND delta strengthening.
            // d0 > d1 > d2 — each bar has less selling pressure than the previous.
            bool bearFading = (d0 - d1) > minDeltaStep
                           && (d1 - d2) > minDeltaStep;

            if (priceUp   && bullFading) return  1;  // bull exhaustion — confirms short
            if (priceDown && bearFading) return -1;  // bear exhaustion — confirms long

            return 0;
        }

        /// <summary>
        /// Check whether a given price is inside or within proximity of an
        /// imbalance zone defined by [zoneLow, zoneHigh].
        ///
        /// Returns true when: price >= zoneLow - buffer AND price &lt;= zoneHigh + buffer
        /// Buffer is expressed in ticks. Zero buffer = strict inside-zone check.
        /// </summary>
        public static bool ImbalanceZone_IsNearPrice(
            double price,
            double zoneLow,
            double zoneHigh,
            double tickSize,
            double bufferTicks = 4.0)
        {
            if (zoneLow <= 0.0 || zoneHigh <= 0.0 || tickSize <= 0.0) return false;
            double buffer = bufferTicks * tickSize;
            return price >= (zoneLow - buffer) && price <= (zoneHigh + buffer);
        }

        // ===================================================================
        // BLOCK FLOW CLASSIFICATION
        // ===================================================================

        /// <summary>
        /// Classify block flow state - returns enum index
        /// 0 = BALANCED
        /// 1 = MODERATE_INSTITUTIONAL_BUYING
        /// 2 = STRONG_INSTITUTIONAL_BUYING
        /// 3 = MODERATE_INSTITUTIONAL_SELLING
        /// 4 = STRONG_INSTITUTIONAL_SELLING
        /// </summary>
        public static int BlockFlow_Classify(int buyingBlocks, int sellingBlocks)
        {
            int imbalance = buyingBlocks - sellingBlocks;
            
            if (imbalance >= 4) return 2; // STRONG_INSTITUTIONAL_BUYING
            if (imbalance >= 2) return 1; // MODERATE_INSTITUTIONAL_BUYING
            if (imbalance <= -4) return 4; // STRONG_INSTITUTIONAL_SELLING
            if (imbalance <= -2) return 3; // MODERATE_INSTITUTIONAL_SELLING
            return 0; // BALANCED
        }

        // ===================================================================
        // EXHAUSTION DETECTION
        // ===================================================================

        /// <summary>
        /// Check for order flow exhaustion - long exit signal
        /// </summary>
        public static bool Exhaustion_Long(int momentumState, double deltaROC, 
            double close, double target, double threshold)
        {
            // momentumState 3 = ACCELERATING_BEARISH
            bool momentumReversed = (momentumState == 3 && deltaROC < -200.0);
            bool nearTarget = Math.Abs(close - target) < threshold;
            
            return momentumReversed && nearTarget;
        }

        /// <summary>
        /// Check for order flow exhaustion - short exit signal
        /// </summary>
        public static bool Exhaustion_Short(int momentumState, double deltaROC,
            double close, double target, double threshold)
        {
            // momentumState 1 = ACCELERATING_BULLISH
            bool momentumReversed = (momentumState == 1 && deltaROC > 200.0);
            bool nearTarget = Math.Abs(close - target) < threshold;
            
            return momentumReversed && nearTarget;
        }
    }
}
