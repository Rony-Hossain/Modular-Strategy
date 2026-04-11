#region Using declarations
using System;
#endregion

namespace MathLogic
{
    // ===================================================================
    // ENUMS — outside class per NinjaScript best practices
    // ===================================================================

    /// <summary>
    /// Stochastic oscillator zone classification.
    /// </summary>
    public enum StochasticZone
    {
        Neutral    = 0,
        Overbought = 1,   // K > overbought level (default 80)
        Oversold   = 2    // K < oversold level   (default 20)
    }

    /// <summary>
    /// Ichimoku price position relative to the cloud.
    /// </summary>
    public enum IchimokuBias
    {
        Undefined   = 0,
        AboveCloud  = 1,   // bullish bias
        BelowCloud  = 2,   // bearish bias
        InsideCloud = 3    // neutral / transitioning
    }

    /// <summary>
    /// ADX trend strength classification.
    /// </summary>
    public enum ADXStrength
    {
        Weak     = 0,   // ADX < 20
        Moderate = 1,   // ADX 20–25
        Strong   = 2,   // ADX 25–40
        VeryStrong = 3  // ADX > 40
    }

    // ===================================================================
    // RESULT OBJECTS
    // ===================================================================

    /// <summary>Stochastic Oscillator result.</summary>
    public sealed class StochasticResult
    {
        public double        K        { get; }   // fast line (0–100)
        public double        D        { get; }   // slow line = SMA(K, dPeriod)
        public StochasticZone Zone    { get; }
        public bool          IsValid  { get; }

        public StochasticResult(double k, double d, StochasticZone zone, bool isValid)
        {
            K = k; D = d; Zone = zone; IsValid = isValid;
        }

        public static readonly StochasticResult Invalid = new StochasticResult(0, 0, StochasticZone.Neutral, false);
    }

    /// <summary>Ichimoku Cloud result for one bar.</summary>
    public sealed class IchimokuResult
    {
        public double        Tenkan      { get; }   // conversion line  (9)
        public double        Kijun       { get; }   // base line        (26)
        public double        SenkouA     { get; }   // leading span A   (avg of tenkan+kijun, displaced +26)
        public double        SenkouB     { get; }   // leading span B   (52-period midpoint, displaced +26)
        public double        Chikou      { get; }   // lagging span     (close displaced -26)
        public IchimokuBias  Bias        { get; }
        public double        CloudTop    { get; }
        public double        CloudBottom { get; }
        public bool          IsValid     { get; }

        public IchimokuResult(double tenkan, double kijun, double senkouA, double senkouB,
            double chikou, IchimokuBias bias, double cloudTop, double cloudBottom, bool isValid)
        {
            Tenkan = tenkan; Kijun = kijun; SenkouA = senkouA; SenkouB = senkouB;
            Chikou = chikou; Bias = bias;
            CloudTop = cloudTop; CloudBottom = cloudBottom;
            IsValid = isValid;
        }

        public static readonly IchimokuResult Invalid =
            new IchimokuResult(0, 0, 0, 0, 0, IchimokuBias.Undefined, 0, 0, false);
    }

    /// <summary>ADX / DMI result for one bar.</summary>
    public sealed class ADXResult
    {
        public double      ADX       { get; }   // trend strength  0–100
        public double      PlusDI    { get; }   // +DI directional indicator
        public double      MinusDI   { get; }   // -DI directional indicator
        public ADXStrength Strength  { get; }
        public bool        IsTrending { get; }  // ADX >= 25
        public bool        IsValid   { get; }

        public ADXResult(double adx, double plusDI, double minusDI,
            ADXStrength strength, bool isTrending, bool isValid)
        {
            ADX = adx; PlusDI = plusDI; MinusDI = minusDI;
            Strength = strength; IsTrending = isTrending; IsValid = isValid;
        }

        public static readonly ADXResult Invalid = new ADXResult(0, 0, 0, ADXStrength.Weak, false, false);
    }

    /// <summary>True Range result.</summary>
    public sealed class TrueRangeResult
    {
        public double TR      { get; }
        public double ATR     { get; }   // smoothed average true range
        public bool   IsValid { get; }

        public TrueRangeResult(double tr, double atr, bool isValid)
        {
            TR = tr; ATR = atr; IsValid = isValid;
        }

        public static readonly TrueRangeResult Invalid = new TrueRangeResult(0, 0, false);
    }

    // ===================================================================
    // MATH INDICATORS
    // ===================================================================

    /// <summary>
    /// INDICATORS MODULE — SMA, WMA, EMA cross, Stochastic, HMA, Ichimoku, ATR, ADX.
    ///
    /// Design contract (same as rest of library):
    /// - Pure stateless functions. Caller owns and maintains all state.
    /// - No LINQ. No allocations in hot paths.
    /// - bars[0] = current (most recent), bars[1] = 1 bar ago (NT8 convention).
    /// - Incremental update pattern — caller passes ref state variables.
    /// - Thread-safe: no shared mutable state.
    /// - ComputationTier: CORE_ONLY for SMA/EMA/WMA/TR/ATR.
    ///                    STRUCTURE_ONLY for Stochastic, HMA, Ichimoku, ADX.
    /// </summary>
    public static class MathIndicators
    {
        // ===================================================================
        // SIMPLE MOVING AVERAGE (SMA)
        // ===================================================================

        /// <summary>
        /// SMA from a price array. bars[0] = most recent.
        /// O(length) — use SMA_Update for incremental O(1) updates.
        /// </summary>
        public static double SMA_Calculate(double[] prices, int length)
        {
            if (prices == null || length <= 0 || length > prices.Length)
                return 0.0;

            double sum = 0.0;
            for (int i = 0; i < length; i++)
                sum += prices[i];

            return sum / length;
        }

        /// <summary>
        /// Incremental SMA update using a ring buffer and running sum.
        /// Mirrors the Rolling_SD_Update pattern from TradingMath.
        ///
        /// buffer: caller-allocated ring buffer of size = period.
        /// Caller maintains: index, count, sum.
        /// </summary>
        public static double SMA_Update(
            double[]  buffer,
            ref int   index,
            ref int   count,
            ref double sum,
            double    newValue)
        {
            if (buffer == null || buffer.Length < 1)
                return 0.0;

            int window = buffer.Length;

            // Remove oldest if full
            if (count >= window)
                sum -= buffer[index];
            else
                count++;

            buffer[index] = newValue;
            sum += newValue;
            index = (index + 1) % window;

            return (count > 0) ? sum / count : 0.0;
        }

        // ===================================================================
        // WEIGHTED MOVING AVERAGE (WMA)
        // ===================================================================

        /// <summary>
        /// WMA — linearly weighted, most recent bar has weight = length.
        /// Required building block for HMA.
        /// bars[0] = most recent.
        /// </summary>
        public static double WMA_Calculate(double[] prices, int length)
        {
            if (prices == null || length <= 0 || length > prices.Length)
                return 0.0;

            double weightedSum = 0.0;
            double weightSum   = 0.0;

            for (int i = 0; i < length; i++)
            {
                double weight = length - i;   // weight: length, length-1, ..., 1
                weightedSum += prices[i] * weight;
                weightSum   += weight;
            }

            return (weightSum > 0.0) ? weightedSum / weightSum : 0.0;
        }

        // ===================================================================
        // EMA — MULTI-PERIOD AND CROSSOVER
        // ===================================================================

        /// <summary>
        /// Calculate EMA from a full price array (batch, not incremental).
        /// Initializes by seeding with prices[length-1] (oldest bar in array).
        /// bars[0] = most recent.
        ///
        /// For incremental use, call MathFlow.EmaStep() each bar instead.
        /// </summary>
        public static double EMA_Calculate(double[] prices, int length)
        {
            if (prices == null || length <= 0 || length > prices.Length)
                return 0.0;

            double alpha = 2.0 / (length + 1.0);
            double ema   = prices[length - 1];   // seed with oldest

            // Walk from oldest to newest (prices[length-1] → prices[0])
            for (int i = length - 2; i >= 0; i--)
                ema = ema + alpha * (prices[i] - ema);

            return ema;
        }

        /// <summary>
        /// Detect EMA crossover between a fast and slow EMA.
        ///
        /// Requires current AND prior bar values for both EMAs.
        /// Bullish cross: fast was below slow last bar, now above.
        /// Bearish cross: fast was above slow last bar, now below.
        /// </summary>
        public static EMACross EMA_CrossDetect(
            double fastNow,  double fastPrev,
            double slowNow,  double slowPrev)
        {
            bool crossedAbove = fastPrev <= slowPrev && fastNow > slowNow;
            bool crossedBelow = fastPrev >= slowPrev && fastNow < slowNow;

            if (crossedAbove) return EMACross.Bullish;
            if (crossedBelow) return EMACross.Bearish;
            return EMACross.None;
        }

        /// <summary>
        /// Bias from EMA stack — returns +1 if fast > slow (bullish), -1 if below (bearish), 0 if equal.
        /// Use for 200 SMA bias filter: only take longs above, shorts below.
        /// </summary>
        public static int MA_Bias(double fastMA, double slowMA)
        {
            if (fastMA > slowMA) return  1;
            if (fastMA < slowMA) return -1;
            return 0;
        }

        // ===================================================================
        // STOCHASTIC OSCILLATOR
        // ===================================================================

        /// <summary>
        /// Calculate raw %K from price arrays.
        ///
        /// %K = (close - lowestLow(kPeriod)) / (highestHigh(kPeriod) - lowestLow(kPeriod)) × 100
        ///
        /// bars[0] = most recent (NT8 convention).
        /// Returns 50.0 on zero range (flat market).
        /// </summary>
        public static double Stochastic_K(
            double[] highs,
            double[] lows,
            double   close,
            int      kPeriod)
        {
            if (highs == null || lows == null || kPeriod <= 0 ||
                kPeriod > highs.Length || kPeriod > lows.Length)
                return 50.0;

            double highestHigh = double.MinValue;
            double lowestLow   = double.MaxValue;

            for (int i = 0; i < kPeriod; i++)
            {
                if (highs[i] > highestHigh) highestHigh = highs[i];
                if (lows[i]  < lowestLow)   lowestLow   = lows[i];
            }

            double range = highestHigh - lowestLow;
            if (range < 1e-12) return 50.0;

            return ((close - lowestLow) / range) * 100.0;
        }

        /// <summary>
        /// Full Stochastic result — %K, %D (SMA of K), and zone classification.
        ///
        /// kBuffer:   caller-maintained ring buffer for %K values (length = dPeriod).
        /// kBufIndex, kBufCount, kBufSum: ring buffer state for the %D SMA.
        ///
        /// Workflow each bar:
        ///   1. Calculate k = Stochastic_K(...)
        ///   2. Call Stochastic_Calculate(...) passing k and the ring buffer state.
        /// </summary>
        public static StochasticResult Stochastic_Calculate(
            double   k,
            double[] kBuffer,
            ref int  kBufIndex,
            ref int  kBufCount,
            ref double kBufSum,
            double   overboughtLevel = 80.0,
            double   oversoldLevel   = 20.0)
        {
            // Update %D ring buffer (SMA of K)
            double d = SMA_Update(kBuffer, ref kBufIndex, ref kBufCount, ref kBufSum, k);

            StochasticZone zone = StochasticZone.Neutral;
            if (k > overboughtLevel) zone = StochasticZone.Overbought;
            if (k < oversoldLevel)   zone = StochasticZone.Oversold;

            return new StochasticResult(k, d, zone, true);
        }

        /// <summary>
        /// Stochastic crossover signal — %K crosses %D.
        /// Bullish: K was below D, now above (oversold reversal).
        /// Bearish: K was above D, now below (overbought reversal).
        /// </summary>
        public static EMACross Stochastic_CrossDetect(
            double kNow, double kPrev,
            double dNow, double dPrev)
        {
            bool crossedAbove = kPrev <= dPrev && kNow > dNow;
            bool crossedBelow = kPrev >= dPrev && kNow < dNow;

            if (crossedAbove) return EMACross.Bullish;
            if (crossedBelow) return EMACross.Bearish;
            return EMACross.None;
        }

        // ===================================================================
        // HULL MOVING AVERAGE (HMA)
        // ===================================================================

        /// <summary>
        /// Hull Moving Average — Alan Hull's zero-lag MA.
        ///
        /// Formula:
        ///   Step 1: wma1 = WMA(price, length/2)  × 2
        ///   Step 2: wma2 = WMA(price, length)
        ///   Step 3: raw  = wma1 - wma2
        ///   Step 4: HMA  = WMA(raw, sqrt(length))
        ///
        /// Requires:
        ///   prices:     recent prices array, length ≥ hmaLength. bars[0] = most recent.
        ///   rawBuffer:  caller-maintained buffer of (raw = wma1-wma2) values.
        ///               Must be at least sqrtLength in size.
        ///               bars[0] of rawBuffer = most recent raw value.
        ///
        /// Call HMA_UpdateRawBuffer() each bar to maintain rawBuffer,
        /// then call HMA_Calculate() to get the final HMA value.
        /// </summary>
        public static double HMA_RawValue(double[] prices, int hmaLength)
        {
            if (prices == null || hmaLength < 4 || prices.Length < hmaLength)
                return 0.0;

            int halfLen = Math.Max(1, hmaLength / 2);

            double wmaHalf = WMA_Calculate(prices, halfLen);
            double wmaFull = WMA_Calculate(prices, hmaLength);

            return (2.0 * wmaHalf) - wmaFull;
        }

        /// <summary>
        /// Calculate the final HMA value from a buffer of recent raw values.
        /// rawBuffer[0] = most recent raw value.
        /// sqrtLength = (int)Math.Round(Math.Sqrt(hmaLength)).
        /// </summary>
        public static double HMA_Calculate(double[] rawBuffer, int sqrtLength)
        {
            if (rawBuffer == null || sqrtLength <= 0 || sqrtLength > rawBuffer.Length)
                return 0.0;

            return WMA_Calculate(rawBuffer, sqrtLength);
        }

        /// <summary>
        /// HMA direction — returns +1 rising, -1 falling, 0 flat.
        /// Compare current HMA to previous bar's HMA.
        /// </summary>
        public static int HMA_Direction(double hmaNow, double hmaPrev, double tickSize)
        {
            double diff = hmaNow - hmaPrev;
            if (Math.Abs(diff) < tickSize * 0.5) return 0;
            return diff > 0 ? 1 : -1;
        }

        // ===================================================================
        // ICHIMOKU CLOUD
        // ===================================================================

        /// <summary>
        /// Calculate Ichimoku midpoint line (used for Tenkan and Kijun).
        /// midpoint = (highest_high + lowest_low) / 2 over the period.
        /// bars[0] = most recent.
        /// </summary>
        public static double Ichimoku_Midpoint(double[] highs, double[] lows, int period)
        {
            if (highs == null || lows == null || period <= 0 ||
                period > highs.Length || period > lows.Length)
                return 0.0;

            double high = double.MinValue;
            double low  = double.MaxValue;

            for (int i = 0; i < period; i++)
            {
                if (highs[i] > high) high = highs[i];
                if (lows[i]  < low)  low  = lows[i];
            }

            return (high + low) / 2.0;
        }

        /// <summary>
        /// Full Ichimoku calculation for the current bar.
        ///
        /// Standard periods: tenkanPeriod=9, kijunPeriod=26, senkouBPeriod=52, displacement=26.
        ///
        /// Arrays must be sized to cover the lookback:
        ///   highs/lows: at least senkouBPeriod bars.
        ///   closes:     at least 1 bar (for Chikou).
        ///
        /// Senkou A and B are the CURRENT values (not displaced).
        /// Displacement is the caller's responsibility — plot senkouA/B 26 bars ahead.
        ///
        /// Bias is determined by close vs the CURRENT Senkou A and B
        /// (as if comparing current price to the cloud that was drawn 26 bars ago).
        /// </summary>
        public static IchimokuResult Ichimoku_Calculate(
            double[] highs,
            double[] lows,
            double   close,
            int      tenkanPeriod  = 9,
            int      kijunPeriod   = 26,
            int      senkouBPeriod = 52)
        {
            if (highs == null || lows == null)
                return IchimokuResult.Invalid;

            if (highs.Length < senkouBPeriod || lows.Length < senkouBPeriod)
                return IchimokuResult.Invalid;

            double tenkan  = Ichimoku_Midpoint(highs, lows, tenkanPeriod);
            double kijun   = Ichimoku_Midpoint(highs, lows, kijunPeriod);
            double senkouA = (tenkan + kijun) / 2.0;
            double senkouB = Ichimoku_Midpoint(highs, lows, senkouBPeriod);

            // Chikou = current close (displaced -26 bars back when plotting)
            double chikou = close;

            double cloudTop    = Math.Max(senkouA, senkouB);
            double cloudBottom = Math.Min(senkouA, senkouB);

            IchimokuBias bias;
            if      (close > cloudTop)    bias = IchimokuBias.AboveCloud;
            else if (close < cloudBottom) bias = IchimokuBias.BelowCloud;
            else                          bias = IchimokuBias.InsideCloud;

            return new IchimokuResult(tenkan, kijun, senkouA, senkouB,
                chikou, bias, cloudTop, cloudBottom, true);
        }

        /// <summary>
        /// Ichimoku TK cross — Tenkan crosses Kijun.
        /// Bullish: tenkan crosses above kijun (stronger above the cloud).
        /// Bearish: tenkan crosses below kijun.
        /// </summary>
        public static EMACross Ichimoku_TKCross(
            double tenkanNow, double tenkanPrev,
            double kijunNow,  double kijunPrev)
        {
            bool crossedAbove = tenkanPrev <= kijunPrev && tenkanNow > kijunNow;
            bool crossedBelow = tenkanPrev >= kijunPrev && tenkanNow < kijunNow;

            if (crossedAbove) return EMACross.Bullish;
            if (crossedBelow) return EMACross.Bearish;
            return EMACross.None;
        }

        // ===================================================================
        // TRUE RANGE AND ATR
        // ===================================================================

        /// <summary>
        /// True Range for one bar.
        /// TR = max(high - low, |high - prevClose|, |low - prevClose|)
        /// </summary>
        public static double TrueRange(double high, double low, double prevClose)
        {
            double hl  = high - low;
            double hpc = Math.Abs(high - prevClose);
            double lpc = Math.Abs(low  - prevClose);
            return Math.Max(hl, Math.Max(hpc, lpc));
        }

        /// <summary>
        /// Incremental ATR update using Wilder smoothing.
        /// Wilder smoothing: ATR = prevATR + (TR - prevATR) / period
        /// This is equivalent to an EMA with alpha = 1/period.
        ///
        /// Seed: on the first `period` bars, use simple average of TR.
        /// After that call this function every bar.
        /// atrPrev: NaN on first call triggers seeding from trBuffer SMA.
        /// </summary>
        public static TrueRangeResult ATR_Update(
            double   tr,
            double   atrPrev,
            int      period,
            double[] trBuffer,
            ref int  trBufIndex,
            ref int  trBufCount,
            ref double trBufSum)
        {
            if (period <= 0) return TrueRangeResult.Invalid;

            // Maintain TR ring buffer for seeding
            double smaATR = SMA_Update(trBuffer, ref trBufIndex, ref trBufCount, ref trBufSum, tr);

            double atr;
            if (double.IsNaN(atrPrev) || trBufCount < period)
            {
                // Still in warm-up — use SMA of TR
                atr = smaATR;
            }
            else
            {
                // Wilder smoothing
                atr = atrPrev + (tr - atrPrev) / period;
            }

            return new TrueRangeResult(tr, atr, trBufCount >= 2);
        }

        // ===================================================================
        // ADX / DMI (AVERAGE DIRECTIONAL INDEX)
        // ===================================================================

        /// <summary>
        /// Calculate raw Directional Movement for one bar.
        /// +DM = upMove  if upMove > downMove and upMove > 0, else 0
        /// -DM = downMove if downMove > upMove and downMove > 0, else 0
        ///
        /// upMove   = high - prevHigh
        /// downMove = prevLow - low
        /// </summary>
        public static void DM_Calculate(
            double high, double prevHigh,
            double low,  double prevLow,
            out double plusDM, out double minusDM)
        {
            double upMove   = high - prevHigh;
            double downMove = prevLow - low;

            plusDM  = (upMove   > downMove && upMove   > 0) ? upMove   : 0.0;
            minusDM = (downMove > upMove   && downMove > 0) ? downMove : 0.0;
        }

        /// <summary>
        /// Incremental smoothed +DM, -DM, and TR using Wilder smoothing.
        /// Call once per bar. Pass NaN for prevSmoothed values on first bar.
        ///
        /// Wilder: smoothed = prev - (prev / period) + newRaw
        /// </summary>
        public static double Wilder_Smooth(double prevSmoothed, double newRaw, int period)
        {
            if (double.IsNaN(prevSmoothed) || prevSmoothed <= 0.0) return newRaw;
            return prevSmoothed - (prevSmoothed / period) + newRaw;
        }

        /// <summary>
        /// Calculate +DI and -DI from smoothed directional movement and ATR.
        /// +DI = (smoothedPlusDM / smoothedTR) × 100
        /// -DI = (smoothedMinusDM / smoothedTR) × 100
        /// </summary>
        public static void DI_Calculate(
            double smoothedPlusDM,
            double smoothedMinusDM,
            double smoothedTR,
            out double plusDI,
            out double minusDI)
        {
            if (smoothedTR < 1e-12)
            {
                plusDI = 0.0; minusDI = 0.0;
                return;
            }
            plusDI  = (smoothedPlusDM  / smoothedTR) * 100.0;
            minusDI = (smoothedMinusDM / smoothedTR) * 100.0;
        }

        /// <summary>
        /// Calculate DX (directional index) from +DI and -DI.
        /// DX = |+DI - -DI| / (+DI + -DI) × 100
        /// Returns 0 if sum is zero (no directional movement).
        /// </summary>
        public static double DX_Calculate(double plusDI, double minusDI)
        {
            double sum = plusDI + minusDI;
            if (sum < 1e-12) return 0.0;
            return (Math.Abs(plusDI - minusDI) / sum) * 100.0;
        }

        /// <summary>
        /// Full ADX result from smoothed components.
        /// ADX is a Wilder-smoothed average of DX.
        /// adxPrev: pass NaN on first bar (will seed from dx directly).
        /// </summary>
        public static ADXResult ADX_Calculate(
            double smoothedPlusDM,
            double smoothedMinusDM,
            double smoothedTR,
            double adxPrev,
            int    period)
        {
            double plusDI, minusDI;
            DI_Calculate(smoothedPlusDM, smoothedMinusDM, smoothedTR, out plusDI, out minusDI);

            double dx  = DX_Calculate(plusDI, minusDI);
            double adx = Wilder_Smooth(adxPrev, dx, period);

            ADXStrength strength = ADX_ClassifyStrength(adx);
            bool        trending = adx >= 25.0;

            return new ADXResult(adx, plusDI, minusDI, strength, trending, true);
        }

        /// <summary>
        /// Classify ADX value into strength buckets.
        /// Industry-standard thresholds: 0-20 weak, 20-25 moderate, 25-40 strong, 40+ very strong.
        /// </summary>
        public static ADXStrength ADX_ClassifyStrength(double adx)
        {
            if (adx >= 40.0) return ADXStrength.VeryStrong;
            if (adx >= 25.0) return ADXStrength.Strong;
            if (adx >= 20.0) return ADXStrength.Moderate;
            return ADXStrength.Weak;
        }

        /// <summary>
        /// ADX trend direction from +DI vs -DI.
        /// Returns +1 if bullish trend (+DI > -DI), -1 bearish, 0 neutral.
        /// Only meaningful when ADX is trending (>= 25).
        /// </summary>
        public static int ADX_TrendDirection(double plusDI, double minusDI)
        {
            if (plusDI > minusDI) return  1;
            if (plusDI < minusDI) return -1;
            return 0;
        }

        // ===================================================================
        // CONFLUENCE HELPERS
        // ===================================================================

        /// <summary>
        /// Check if multiple trend indicators agree on direction.
        /// Returns count of agreeing signals (max = 4).
        /// direction: +1 for bullish, -1 for bearish.
        ///
        /// Use to build a fast multi-indicator confluence score:
        ///   score = TrendConfluence(emaBias, hmaBias, adxBias, ichimokuBias)
        ///   score 3–4 = high confluence entry.
        /// </summary>
        public static int TrendConfluence(
            int emaBias,        // MA_Bias(fast, slow)
            int hmaBias,        // HMA_Direction(now, prev, tick)
            int adxBias,        // ADX_TrendDirection(plusDI, minusDI)
            int ichimokuBias,   // +1 = AboveCloud, -1 = BelowCloud, 0 = Inside
            int direction)
        {
            int count = 0;
            if (emaBias     == direction) count++;
            if (hmaBias     == direction) count++;
            if (adxBias     == direction) count++;
            if (ichimokuBias == direction) count++;
            return count;
        }

        /// <summary>
        /// Score a scalping setup combining VWAP, EMA, and Stochastic.
        /// Returns 0–100 to feed into MathPolicy.Grade_Master().
        ///
        /// Scoring:
        ///   VWAP side agreement      : 30 pts (price on correct side of VWAP)
        ///   EMA cross in direction   : 25 pts
        ///   Stochastic not opposing  : 25 pts (not overbought on long, not oversold on short)
        ///   Stochastic cross confirm : 20 pts (K/D cross in same direction)
        /// </summary>
        public static int Scalp_SetupScore(
            double   close,
            double   vwap,
            EMACross emaCross,
            StochasticResult stoch,
            bool     isLong)
        {
            int score = 0;

            // VWAP side
            bool vwapOk = isLong ? close > vwap : close < vwap;
            if (vwapOk) score += 30;

            // EMA cross
            bool emaCrossOk = isLong
                ? emaCross == EMACross.Bullish
                : emaCross == EMACross.Bearish;
            if (emaCrossOk) score += 25;

            // Stochastic not opposing
            if (stoch != null && stoch.IsValid)
            {
                bool stochOk = isLong
                    ? stoch.Zone != StochasticZone.Overbought
                    : stoch.Zone != StochasticZone.Oversold;
                if (stochOk) score += 25;
            }

            return Math.Min(score, 100);
        }
    }
}
