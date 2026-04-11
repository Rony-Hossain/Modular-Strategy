#region Using declarations
using System;
#endregion

namespace MathLogic
{
    // ===================================================================
    // MATHFLOW ENUMS — outside class per NinjaScript best practices
    // ===================================================================

    // ===================================================================
    // RESULT OBJECTS — Immutable sealed classes, matching library convention
    // ===================================================================

    /// <summary>
    /// CLV-based money flow result for a single bar update.
    /// </summary>
    public sealed class CLVFlowResult
    {
        /// <summary>Normalized money flow ratio: rollingNum / rollingDen. Range [-1, 1].</summary>
        public double MF        { get; }
        /// <summary>EMA-smoothed MF value.</summary>
        public double MFSmooth  { get; }
        /// <summary>Math.Pow(|MFSmooth|, FlowBoost) — clamped to [0, 1].</summary>
        public double Strength  { get; }
        public bool   IsValid   { get; }

        public CLVFlowResult(double mf, double mfSmooth, double strength, bool isValid)
        {
            MF       = mf;
            MFSmooth = mfSmooth;
            Strength = strength;
            IsValid  = isValid;
        }

        public static readonly CLVFlowResult Invalid = new CLVFlowResult(0, 0, 0, false);
    }

    /// <summary>
    /// Adaptive band result for a single bar.
    /// upper = basisMain + atr * mult
    /// lower = basisMain - atr * mult
    /// </summary>
    public sealed class AdaptiveBandResult
    {
        public double BasisMain { get; }
        public double Upper     { get; }
        public double Lower     { get; }
        public double Mult      { get; }   // actual multiplier used this bar
        public bool   IsValid   { get; }

        public AdaptiveBandResult(double basisMain, double upper, double lower, double mult, bool isValid)
        {
            BasisMain = basisMain;
            Upper     = upper;
            Lower     = lower;
            Mult      = mult;
            IsValid   = isValid;
        }

        public static readonly AdaptiveBandResult Invalid = new AdaptiveBandResult(0, 0, 0, 0, false);
    }

    /// <summary>
    /// Regime state machine result for a single bar.
    /// </summary>
    public sealed class RegimeResult
    {
        public RegimeState State   { get; }
        public bool        DidFlip { get; }   // true if state changed this bar
        public bool        IsValid { get; }

        public RegimeResult(RegimeState state, bool didFlip, bool isValid)
        {
            State   = state;
            DidFlip = didFlip;
            IsValid = isValid;
        }

        public static readonly RegimeResult None = new RegimeResult(RegimeState.Undefined, false, false);
    }

    /// <summary>
    /// Gauge result — momentum display value derived from smoothed flow.
    /// </summary>
    public sealed class GaugeResult
    {
        /// <summary>EMA of tanh-normalized flow. Range approximately [-1, 1].</summary>
        public double VEma     { get; }
        /// <summary>Math.Abs(VEma) * 100. Range [0, 100].</summary>
        public double PctValue { get; }
        public bool   IsValid  { get; }

        public GaugeResult(double vEma, double pctValue, bool isValid)
        {
            VEma     = vEma;
            PctValue = pctValue;
            IsValid  = isValid;
        }

        public static readonly GaugeResult Invalid = new GaugeResult(0, 0, false);
    }

    // ===================================================================
    // MATH FLOW
    // ===================================================================

    /// <summary>
    /// FLOW MODULE — CLV Money Flow, ALMA, Regime State Machine, Gauge.
    ///
    /// Formulas extracted from SmartMoneyFlowCloudBOSWavesV3 and promoted
    /// to the shared library so any indicator or strategy can reuse them.
    ///
    /// Design contract (same as rest of library):
    /// - Pure stateless functions. Caller owns and maintains all state.
    /// - No LINQ, no allocations in hot paths.
    /// - Incremental update pattern — caller passes ref state variables.
    /// - Thread-safe: no shared mutable state.
    /// - ComputationTier: CORE_ONLY (all methods are lightweight).
    /// </summary>
    public static class MathFlow
    {
        // ===================================================================
        // CLV (CLOSE LOCATION VALUE) — core of the money flow calculation
        // ===================================================================

        /// <summary>
        /// Close Location Value — measures where the close landed within the bar's range.
        /// Returns +1.0 if close == high, -1.0 if close == low, 0.0 on zero range.
        ///
        /// Formula (exact match to SmartMoneyFlowCloudBOSWavesV3):
        ///   clv = ((close - low) - (high - close)) / range
        /// </summary>
        public static double CLV_Calculate(double high, double low, double close)
        {
            double range = high - low;
            if (Math.Abs(range) < 1e-12) return 0.0;
            return ((close - low) - (high - close)) / range;
        }

        /// <summary>
        /// Raw CLV-weighted flow for one bar: clv × volume.
        /// Positive = buying pressure, negative = selling pressure.
        /// </summary>
        public static double CLV_RawFlow(double high, double low, double close, double volume)
            => CLV_Calculate(high, low, close) * volume;

        // ===================================================================
        // ROLLING WINDOW MONEY FLOW
        // ===================================================================

        /// <summary>
        /// Incremental rolling window update for CLV money flow.
        ///
        /// Caller maintains:
        ///   rollingNum  — running sum of raw flow values
        ///   rollingDen  — running sum of absolute raw flow values
        ///   rawFlowBuf  — circular buffer of raw flow (length = window)
        ///   absBuf      — circular buffer of |raw flow| (length = window)
        ///   bufIndex    — current write position in the buffer
        ///   barCount    — number of bars processed so far
        ///
        /// Call ONCE per bar, BEFORE using the result.
        /// On bar 0, pre-seed rollingNum, rollingDen, and both buffers manually.
        /// </summary>
        public static void CLV_RollingUpdate(
            ref double rollingNum,
            ref double rollingDen,
            double[]   rawFlowBuf,
            double[]   absBuf,
            ref int    bufIndex,
            int        window,
            double     newRawFlow)
        {
            if (rawFlowBuf == null || absBuf == null || window <= 0) return;

            int capacity = rawFlowBuf.Length;
            double newAbs = Math.Abs(newRawFlow);

            // Remove the oldest value if window is full
            if (bufIndex >= window)
            {
                int oldIdx = bufIndex % capacity;
                rollingNum -= rawFlowBuf[oldIdx];
                rollingDen -= absBuf[oldIdx];
            }

            // Write new value
            int writeIdx = bufIndex % capacity;
            rawFlowBuf[writeIdx] = newRawFlow;
            absBuf[writeIdx]     = newAbs;

            rollingNum += newRawFlow;
            rollingDen += newAbs;

            bufIndex++;
        }

        /// <summary>
        /// Compute normalized money flow from rolling sums.
        /// mf = rollingNum / rollingDen. Returns 0 if denominator is zero.
        /// Result range: [-1, 1].
        /// </summary>
        public static double CLV_NormalizedFlow(double rollingNum, double rollingDen)
            => (rollingDen <= 0.0) ? 0.0 : rollingNum / rollingDen;

        // ===================================================================
        // FLOW STRENGTH
        // ===================================================================

        /// <summary>
        /// Compute flow strength from smoothed money flow.
        /// Applies power boost (FlowBoost exponent) then clamps to [0, 1].
        ///
        /// Formula (exact match to SmartMoneyFlowCloudBOSWavesV3):
        ///   strength = Clamp(|mfSmooth|^flowBoost, 0, 1)
        /// </summary>
        public static double Flow_Strength(double mfSmooth, double flowBoost)
        {
            double raw = Math.Pow(Math.Abs(mfSmooth), flowBoost);
            return Clamp(raw, 0.0, 1.0);
        }

        /// <summary>
        /// Compute the adaptive band multiplier from flow strength.
        /// Blends linearly between calm (minimum) and strong (maximum) multiplier.
        ///
        /// Formula:
        ///   mult = tightnessCalm + (expansionStrong - tightnessCalm) × strength
        /// </summary>
        public static double Flow_BandMultiplier(double strength, double tightnessCalm, double expansionStrong)
            => tightnessCalm + (expansionStrong - tightnessCalm) * strength;

        // ===================================================================
        // EMA STEP (INCREMENTAL)
        // ===================================================================

        /// <summary>
        /// Single-step EMA update. Exact implementation from SmartMoneyFlowCloudBOSWavesV3.
        ///
        /// Returns x unchanged if:
        ///   - period &lt;= 1
        ///   - prev is NaN (warm-up seed)
        ///
        /// α = 2 / (period + 1)
        /// result = prev + α × (x - prev)
        ///
        /// Usage pattern: store result back into your prev variable each bar.
        /// </summary>
        public static double EmaStep(double x, double prev, int period)
        {
            if (period <= 1)          return x;
            if (double.IsNaN(prev))   return x;
            double alpha = 2.0 / (period + 1.0);
            return prev + alpha * (x - prev);
        }

        /// <summary>
        /// Zero-Lag EMA step — single-bar incremental update.
        ///
        /// Standard EMA introduces phase lag of approximately (period-1)/2 bars.
        /// ZLEMA corrects for this by substituting an error-corrected price:
        ///
        ///   lag          = (period - 1) / 2               (integer division)
        ///   errorCorr    = 2 × price[0] − price[lag]      (de-lagged input)
        ///   ZLEMA[0]     = ZLEMA[prev] + α × (errorCorr − ZLEMA[prev])
        ///
        /// The errorCorr term projects price forward by the expected lag amount,
        /// so the EMA tracks the current bar much more tightly than standard EMA
        /// while still smoothing high-frequency noise.
        ///
        /// CALLER RESPONSIBILITY:
        ///   The caller must maintain a price history array of depth ≥ lag+1
        ///   and pass prices[lag] (the bar lag bars ago). This is necessary because
        ///   MathFlow is stateless — it cannot own price history.
        ///
        /// Parameters:
        ///   priceNow  — current bar's close (prices[0])
        ///   priceLag  — close from lag bars ago (prices[lag])
        ///               where lag = (period - 1) / 2
        ///   prevZL    — previous bar's ZLEMA value (NaN on first bar → seeds with priceNow)
        ///   period    — EMA period (same as the underlying EMA)
        ///
        /// Example for period=9: lag = 4. Pass prices[4] as priceLag.
        /// Example for period=21: lag = 10. Pass prices[10] as priceLag.
        ///
        /// Warm-up: until the price history array has lag+1 valid bars, pass
        /// priceNow as priceLag — this degrades to standard EMA during warm-up,
        /// which is correct and safe.
        /// </summary>
        public static double ZLEmaStep(double priceNow, double priceLag, double prevZL, int period)
        {
            if (period <= 1)           return priceNow;
            if (double.IsNaN(prevZL))  return priceNow;   // seed on first bar
            double alpha     = 2.0 / (period + 1.0);
            double errorCorr = 2.0 * priceNow - priceLag; // de-lagged price input
            return prevZL + alpha * (errorCorr - prevZL);
        }

        // ===================================================================
        // ALMA (ARNAUD LEGOUX MOVING AVERAGE)
        // ===================================================================

        /// <summary>
        /// Build ALMA weight array. Call whenever length, offset, or sigma changes.
        /// Caller should cache and reuse this array rather than rebuilding every bar.
        ///
        /// Parameters:
        ///   length  — lookback period (≥ 2)
        ///   offset  — controls weight placement: 0 = lag-free (right-biased), 1 = fully smoothed (left-biased). Default 0.85.
        ///   sigma   — Gaussian width: higher = smoother, lower = more responsive. Default 6.
        ///
        /// Weights are Gaussian-shaped and normalized so they sum to 1.0.
        /// </summary>
        public static double[] ALMA_BuildWeights(int length, double offset, double sigma)
        {
            int L = Math.Max(2, length);
            double[] w = new double[L];

            double m = offset * (L - 1);
            double s = L / Math.Max(sigma, 1e-9);

            double sum = 0.0;
            for (int i = 0; i < L; i++)
            {
                double d = i - m;
                w[i] = Math.Exp(-(d * d) / (2.0 * s * s));
                sum += w[i];
            }

            // Normalize
            if (sum <= 0.0) sum = 1.0;
            for (int i = 0; i < L; i++)
                w[i] /= sum;

            return w;
        }

        /// <summary>
        /// Check whether ALMA weights need to be rebuilt (parameters changed).
        /// Avoids reallocating every bar — call before ALMA_Calculate.
        ///
        /// Usage:
        ///   if (ALMA_NeedsRebuild(cachedLen, cachedOff, cachedSig, length, offset, sigma))
        ///       weights = ALMA_BuildWeights(length, offset, sigma);
        /// </summary>
        public static bool ALMA_NeedsRebuild(
            int    cachedLength, double cachedOffset, double cachedSigma,
            int    newLength,    double newOffset,    double newSigma)
        {
            return cachedLength != newLength
                || Math.Abs(cachedOffset - newOffset) > 1e-12
                || Math.Abs(cachedSigma  - newSigma)  > 1e-12;
        }

        /// <summary>
        /// Calculate ALMA from a pre-built weight array and a price buffer.
        ///
        /// prices[0] = most recent bar, prices[length-1] = oldest bar (NT8 convention).
        /// offsetBarsAgo: additional offset into the prices array (use 0 for current, 1 for confirmed).
        ///
        /// Returns a simple average if weights are null or too short.
        /// </summary>
        public static double ALMA_Calculate(double[] prices, double[] weights, int length, int offsetBarsAgo = 0)
        {
            if (prices == null || weights == null) return 0.0;

            int L = Math.Max(2, length);
            double acc = 0.0;

            for (int i = 0; i < L; i++)
            {
                int barsAgo = (L - 1) - i + offsetBarsAgo;
                if (barsAgo < 0)             barsAgo = 0;
                if (barsAgo >= prices.Length) barsAgo = prices.Length - 1;

                double wt = (i < weights.Length) ? weights[i] : 0.0;
                acc += wt * prices[barsAgo];
            }

            return acc;
        }

        // ===================================================================
        // ADAPTIVE BANDS
        // ===================================================================

        /// <summary>
        /// Calculate adaptive upper and lower bands around a basis price.
        ///
        /// Exact formula from SmartMoneyFlowCloudBOSWavesV3:
        ///   upper = basisMain + atr × mult
        ///   lower = basisMain - atr × mult
        ///
        /// basisMain is typically (basisOpen + basisClose) / 2 or just basisClose,
        /// depending on strategy preference.
        /// </summary>
        public static AdaptiveBandResult Band_Calculate(
            double basisMain,
            double atr,
            double mult)
        {
            if (atr <= 0.0 || mult <= 0.0)
                return AdaptiveBandResult.Invalid;

            double upper = basisMain + atr * mult;
            double lower = basisMain - atr * mult;

            return new AdaptiveBandResult(basisMain, upper, lower, mult, true);
        }

        // ===================================================================
        // REGIME STATE MACHINE
        // ===================================================================

        /// <summary>
        /// Update the regime state based on band cross conditions.
        ///
        /// Exact logic from SmartMoneyFlowCloudBOSWavesV3:
        ///   - If longCond (close crosses above upper band): regime = Bullish
        ///   - If shortCond (close crosses below lower band): regime = Bearish
        ///   - Otherwise: regime stays unchanged (no flip)
        ///   - If regime is Undefined on first call: seed from price vs basisMain
        ///
        /// prevState: the regime value from the previous bar.
        /// Returns a RegimeResult with the new state and whether a flip occurred.
        /// </summary>
        public static RegimeResult Regime_Update(
            RegimeState prevState,
            bool        longCond,
            bool        shortCond,
            double      close,
            double      basisMain)
        {
            RegimeState current = prevState;

            // Seed if undefined
            if (current == RegimeState.Undefined)
                current = (close >= basisMain) ? RegimeState.Bullish : RegimeState.Bearish;

            RegimeState next;
            if      (longCond)  next = RegimeState.Bullish;
            else if (shortCond) next = RegimeState.Bearish;
            else                next = current;

            bool flipped = (next != current);
            return new RegimeResult(next, flipped, true);
        }

        /// <summary>
        /// Band-cross detection for confirmed-bar mode (NT8 historical/live parity).
        ///
        /// Confirmed long:  close[0] > upper[0]  AND  close[1] ≤ upperBand[1]
        /// Confirmed short: close[0] < lower[0]  AND  close[1] ≥ lowerBand[1]
        ///
        /// This is the exact condition used in SmartMoneyFlowCloudBOSWavesV3
        /// for OnBarCloseConfirmed timing.
        /// </summary>
        public static void Regime_BandCrossConfirmed(
            double close0,  double close1,
            double upper0,  double upperBand1,
            double lower0,  double lowerBand1,
            out bool longCond,
            out bool shortCond)
        {
            longCond  = close0 > upper0  && close1 <= upperBand1;
            shortCond = close0 < lower0  && close1 >= lowerBand1;
        }

        /// <summary>
        /// Retest detection — price crosses back through the basis line against the regime.
        ///
        /// Bearish retest: regime == Bullish  AND  low &lt; basisMain  (price dipped below mid)
        /// Bullish retest: regime == Bearish  AND  high > basisMain  (price spiked above mid)
        ///
        /// Returns 1 for bullish retest, -1 for bearish retest, 0 for none.
        /// </summary>
        public static int Regime_RetestType(RegimeState regime, double high, double low, double basisMain)
        {
            if (regime == RegimeState.Bearish && high > basisMain) return  1; // bullish retest
            if (regime == RegimeState.Bullish && low  < basisMain) return -1; // bearish retest
            return 0;
        }

        // ===================================================================
        // GAUGE (MOMENTUM DISPLAY)
        // ===================================================================

        /// <summary>
        /// Calculate the raw gauge input using tanh normalization.
        ///
        /// Exact formula from SmartMoneyFlowCloudBOSWavesV3:
        ///   rawSigned = regime == Bullish
        ///               ? (close - basisMain) / upSpan
        ///               : -(basisMain - close) / dnSpan
        ///   v = tanh(rawSigned × 1.5)
        ///
        /// upSpan = upper - basisMain
        /// dnSpan = basisMain - lower
        ///
        /// Output range: (-1, +1), capped by tanh asymptote.
        /// </summary>
        public static double Gauge_TanhInput(
            RegimeState regime,
            double      close,
            double      basisMain,
            double      upper,
            double      lower)
        {
            double upSpan = upper - basisMain;
            double dnSpan = basisMain - lower;

            double rawSigned;
            if (regime == RegimeState.Bullish)
            {
                rawSigned = (upSpan > 1e-12)
                    ? (close - basisMain) / upSpan
                    : 0.0;
            }
            else
            {
                rawSigned = (dnSpan > 1e-12)
                    ? -(basisMain - close) / dnSpan
                    : 0.0;
            }

            return Math.Tanh(rawSigned * 1.5);
        }

        /// <summary>
        /// Update the gauge EMA and compute the final gauge output.
        ///
        /// Exact formula from SmartMoneyFlowCloudBOSWavesV3:
        ///   vEma    = EmaStep(v, gaugeEmaPrev, 3)
        ///   gaugePct = |vEma| × 100
        ///
        /// gaugePeriod: the EMA period for gauge smoothing (default 3 in the indicator).
        /// gaugeEmaPrev: caller maintains this between bars.
        /// </summary>
        public static GaugeResult Gauge_Update(
            double     v,
            ref double gaugeEmaPrev,
            int        gaugePeriod = 3)
        {
            double vEma = EmaStep(v, gaugeEmaPrev, gaugePeriod);
            gaugeEmaPrev = vEma;

            double pct = Math.Abs(vEma) * 100.0;
            return new GaugeResult(vEma, pct, true);
        }

        // ===================================================================
        // FULL PIPELINE HELPERS
        // ===================================================================

        /// <summary>
        /// Compute the complete CLV flow result for one bar in a single call.
        /// Convenience wrapper — handles CLV, normalization, smoothing, and strength.
        ///
        /// Caller is still responsible for maintaining the rolling window state
        /// (rollingNum, rollingDen, rawFlowBuf, absBuf, bufIndex) and
        /// the EMA state (mfSmPrev).
        ///
        /// Call CLV_RollingUpdate() first, then call this.
        /// </summary>
        public static CLVFlowResult Flow_Calculate(
            double     rollingNum,
            double     rollingDen,
            ref double mfSmPrev,
            int        flowSmoothing,
            double     flowBoost)
        {
            double mf      = CLV_NormalizedFlow(rollingNum, rollingDen);
            double mfSm    = (flowSmoothing > 1) ? EmaStep(mf, mfSmPrev, flowSmoothing) : mf;
            mfSmPrev       = mfSm;

            double strength = Flow_Strength(mfSm, flowBoost);

            return new CLVFlowResult(mf, mfSm, strength, true);
        }

        // ===================================================================
        // UTILITIES
        // ===================================================================

        /// <summary>
        /// Clamp a value to [lo, hi]. Exact implementation from SmartMoneyFlowCloudBOSWavesV3.
        /// Exposed here so callers don't need to duplicate the guard.
        /// </summary>
        public static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>
        /// Check if a regime flip qualifies as an Impulse warning.
        /// Matches SmartMoneyFlowCloudBOSWavesV3 impulse logic:
        ///   impulse = flip occurred AND strength ≥ impulseThreshold
        /// </summary>
        public static bool IsImpulse(bool regimeFlipped, double strength, double impulseThreshold)
            => regimeFlipped && strength >= impulseThreshold;

        /// <summary>
        /// Check for a Non-Confirmation warning.
        /// Fires when price flipped regime direction BUT normalized flow (mfSm)
        /// has NOT confirmed the new direction (flow disagrees with price).
        ///
        /// Bullish non-conf: regime just flipped to Bullish but mfSm &lt; 0
        /// Bearish non-conf: regime just flipped to Bearish but mfSm > 0
        /// </summary>
        public static bool IsNonConfirmation(RegimeState newRegime, bool regimeFlipped, double mfSmooth)
        {
            if (!regimeFlipped) return false;

            if (newRegime == RegimeState.Bullish && mfSmooth < 0.0) return true;
            if (newRegime == RegimeState.Bearish && mfSmooth > 0.0) return true;

            return false;
        }
    }
}
