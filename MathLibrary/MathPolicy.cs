#region Using declarations
using System;
#endregion

namespace MathLogic
{
    /// <summary>
    /// POLICY MODULE - Scoring, risk limits, and configuration logic.
    /// These are NOT pure math - they're strategic decisions.
    /// Separate from core math to enable:
    /// - Multiple strategy variants
    /// - A/B testing
    /// - Different risk profiles
    /// - Easy modification without touching core calculations
    /// 
    /// IMPORTANT: This module contains business logic, not mathematical truth.
    /// </summary>
    public static class MathPolicy
    {
        // ===================================================================
        // SCORING SYSTEM
        // ===================================================================

        /// <summary>
        /// Map absorption score to points (0-20 scale)
        /// </summary>
        public static int Score_Absorption(double absScore)
        {
            if (absScore > 25) return 20;
            if (absScore > 20) return 17;
            if (absScore > 15) return 14;
            if (absScore > 10) return 10;
            if (absScore > 7) return 6;
            return 0;
        }

        /// <summary>
        /// Assign master grade - returns enum index
        /// 0 = REJECT (<60)
        /// 1 = C_SETUP (60-64)
        /// 2 = B_SETUP (65-74)
        /// 3 = A_SETUP (75-84)
        /// 4 = A_PLUS_SETUP (85+)
        /// </summary>
        public static int Grade_Master(int totalScore)
        {
            if (totalScore < GradeThresholds.REJECT) return 0; // REJECT
            if (totalScore < GradeThresholds.C_SETUP) return 1; // C_SETUP
            if (totalScore < GradeThresholds.B_SETUP) return 2; // B_SETUP
            if (totalScore < GradeThresholds.A_SETUP) return 3; // A_SETUP
            return 4; // A_PLUS_SETUP
        }

        /// <summary>
        /// Is score a reject
        /// </summary>
        public static bool Grade_IsReject(int totalScore) => totalScore < GradeThresholds.REJECT;

        // ===================================================================
        // RISK LIMITS
        // ===================================================================

        /// <summary>
        /// Calculate position size based on account risk
        /// </summary>
        public static int PositionSize_Calculate(
            double accountSize,
            double riskPct,
            double stopDistanceTicks,
            double tickValue,
            int maxLimit)
        {
            if (stopDistanceTicks <= 0.0 || tickValue <= 0.0)
                return 0;
            
            double riskDollars = accountSize * riskPct;
            int size = (int)Math.Floor(riskDollars / (stopDistanceTicks * tickValue));
            
            return Math.Min(size, maxLimit);
        }

        /// <summary>
        /// Check if daily circuit breaker triggered
        /// </summary>
        public static bool CircuitBreaker_Daily(double dailyPnL, double maxDailyLoss) => dailyPnL <= -Math.Abs(maxDailyLoss);

        /// <summary>
        /// Check if directional loss limit hit
        /// </summary>
        public static bool LossLimit_Directional(int consecutiveLosses, int maxAllowed = 2) => consecutiveLosses >= maxAllowed;

        // ===================================================================
        // STOP PLACEMENT POLICY
        // ===================================================================

        /// <summary>
        /// Calculate structural stop for long.
        ///
        /// minStopFloor parameter allows callers to override the default 40-tick floor.
        /// 40 ticks is correct for 1-min and 5-min NQ intraday swings.
        /// The tighter 20-tick floor was tested and caused T2 hits to drop from
        /// 31 to 21, losing ~$5,300 in winner revenue — trades that would have
        /// survived to target got stopped prematurely.
        /// </summary>
        public static double Stop_StructuralLong(
            double entryPrice,
            double swingLow5,
            double swingLow10,
            double bufferPrice,
            double atrTicks,
            double tickSize,
            double minStopFloor = 40.0)
        {
            double structuralLow = Math.Min(swingLow5, swingLow10);
            double maxStopTicks = Math.Max(minStopFloor, 0.45 * atrTicks);
            double maxStopPrice = maxStopTicks * tickSize;
            
            // Use structural level, but cap at max stop distance
            return Math.Max(structuralLow - bufferPrice, entryPrice - maxStopPrice);
        }

        /// <summary>
        /// Calculate structural stop for short. Mirror of long.
        /// </summary>
        public static double Stop_StructuralShort(
            double entryPrice,
            double swingHigh5,
            double swingHigh10,
            double bufferPrice,
            double atrTicks,
            double tickSize,
            double minStopFloor = 40.0)
        {
            double structuralHigh = Math.Max(swingHigh5, swingHigh10);
            double maxStopTicks = Math.Max(minStopFloor, 0.45 * atrTicks);
            double maxStopPrice = maxStopTicks * tickSize;
            
            return Math.Min(structuralHigh + bufferPrice, entryPrice + maxStopPrice);
        }

        // ===================================================================
        // TARGET PLACEMENT POLICY
        // ===================================================================

        /// <summary>
        /// Calculate dynamic target for long
        /// </summary>
        public static double Target_DynamicLong(double entry, double vwap, double poc)
        {
            double targetDistance = 0.5 * Math.Abs(entry - vwap);
            double targetVWAP = entry + targetDistance;
            
            // Use POC as ceiling if it's above entry
            return (poc > entry) ? Math.Min(targetVWAP, poc) : targetVWAP;
        }

        /// <summary>
        /// Calculate dynamic target for short
        /// </summary>
        public static double Target_DynamicShort(double entry, double vwap, double poc)
        {
            double targetDistance = 0.5 * Math.Abs(entry - vwap);
            double targetVWAP = entry - targetDistance;
            
            // Use POC as floor if it's below entry
            return (poc < entry) ? Math.Max(targetVWAP, poc) : targetVWAP;
        }

        /// <summary>
        /// Calculate partial exit targets (T1 and T2)
        /// </summary>
        public static void Targets_Partial(
            double entry,
            double vwap,
            double sdHybrid,
            bool isLong,
            out double t1,
            out double t2)
        {
            if (isLong)
            {
                t1 = entry + (0.5 * Math.Abs(entry - vwap));
                t2 = vwap + (0.25 * sdHybrid);
            }
            else
            {
                t1 = entry - (0.5 * Math.Abs(entry - vwap));
                t2 = vwap - (0.25 * sdHybrid);
            }
        }

        // ===================================================================
        // TRAILING STOP POLICY
        // ===================================================================

        /// <summary>
        /// Calculate trailing stop for long — ATR-proportional two-mode trail.
        ///
        /// Two modes driven by t1Hit:
        ///   Pre-T1  (scalp mode):  trail = ATR × TRAIL_PRE_T1_ATR_FACTOR (tighter)
        ///   Post-T1 (runner mode): trail = ATR × TRAIL_POST_T1_ATR_FACTOR (wider)
        ///
        /// Why ATR-proportional instead of fixed ticks:
        ///   NQ ATR ranged 6–37 ticks over the Jan–Mar backtest period.
        ///   The old TRAIL_DISTANCE_TICKS = 5 was 83% of ATR in January (fine)
        ///   but only 13% of ATR in March — clipped on every single bar.
        ///   ATR-proportional trail scales with the actual volatility regime.
        ///
        /// Breakeven logic (unchanged in structure, threshold lowered by caller):
        ///   BE arm threshold is passed as beThresholdTicks.
        ///   Caller (OrderManager) sets this to ATR × BE_ARM_ATR_FACTOR (0.10).
        ///   If atrTicks is zero (no volumetric data), falls back to MFE_BE_TICKS.
        /// </summary>
        public static double TrailingStop_Long(
            double entry,
            double currentPrice,
            double currentStop,
            bool   t1Hit,
            double tickSize,
            double maxMFETicks      = 0.0,
            double beThresholdTicks = 0.0,
            double atrTicks         = 0.0)
        {
            double newStop = currentStop;

            // ── Breakeven: MFE-based (independent of T1) ─────────────────
            // Threshold comes from caller (ATR × 0.10) or falls back to constant.
            double effectiveBeTicks = beThresholdTicks > 0 ? beThresholdTicks : MFE_BE_TICKS;
            if (maxMFETicks >= effectiveBeTicks)
            {
                double beStop = entry + tickSize;
                newStop = Math.Max(newStop, beStop);
            }

            // ── Breakeven: tighter lock after T1 partial ──────────────────
            if (t1Hit)
            {
                double beStop = entry + 2.0 * tickSize;
                newStop = Math.Max(newStop, beStop);
            }

            // ── ATR-proportional trail ─────────────────────────────────────
            // Pre-T1: tighter trail — protects position before partial exit.
            // Post-T1: wider trail — gives runner room to develop to T2.
            // Falls back to fixed constants when atrTicks not available.
            double trailDistTicks;
            if (atrTicks > 0.0)
            {
                // High-ATR regime gate (pre-T1 only):
                // When session ATR exceeds HIGH_ATR_THRESHOLD, use the tighter
                // TRAIL_HIGH_ATR_FACTOR (0.20) instead of TRAIL_PRE_T1_ATR_FACTOR (0.30).
                // Post-T1 runner trail is intentionally unchanged — once a partial
                // has locked profit, the runner gets the full 0.40 factor regardless.
                double factor;
                if (!t1Hit && atrTicks > HIGH_ATR_THRESHOLD)
                    factor = TRAIL_HIGH_ATR_FACTOR;
                else
                    factor = t1Hit ? TRAIL_POST_T1_ATR_FACTOR : TRAIL_PRE_T1_ATR_FACTOR;

                trailDistTicks = Math.Max(atrTicks * factor, TRAIL_MIN_TICKS);
            }
            else
            {
                // Legacy fallback — only used when ATR data absent
                trailDistTicks = t1Hit ? TRAIL_DISTANCE_TICKS * 2.0 : TRAIL_DISTANCE_TICKS;
            }

            // Trail starts after TRAIL_START_TICKS of profit
            double profitTicks = (currentPrice - entry) / tickSize;
            if (profitTicks > TRAIL_START_TICKS)
            {
                double trailStop = currentPrice - trailDistTicks * tickSize;
                newStop = Math.Max(newStop, trailStop);
            }

            return newStop;
        }

        /// <summary>
        /// Calculate trailing stop for short. Exact mirror of long.
        /// See TrailingStop_Long for full documentation.
        /// </summary>
        public static double TrailingStop_Short(
            double entry,
            double currentPrice,
            double currentStop,
            bool   t1Hit,
            double tickSize,
            double maxMFETicks      = 0.0,
            double beThresholdTicks = 0.0,
            double atrTicks         = 0.0)
        {
            double newStop = currentStop;

            double effectiveBeTicks = beThresholdTicks > 0 ? beThresholdTicks : MFE_BE_TICKS;
            if (maxMFETicks >= effectiveBeTicks)
            {
                double beStop = entry - tickSize;
                newStop = Math.Min(newStop, beStop);
            }

            if (t1Hit)
            {
                double beStop = entry - 2.0 * tickSize;
                newStop = Math.Min(newStop, beStop);
            }

            double trailDistTicks;
            if (atrTicks > 0.0)
            {
                // Mirror of TrailingStop_Long high-ATR gate.
                double factor;
                if (!t1Hit && atrTicks > HIGH_ATR_THRESHOLD)
                    factor = TRAIL_HIGH_ATR_FACTOR;
                else
                    factor = t1Hit ? TRAIL_POST_T1_ATR_FACTOR : TRAIL_PRE_T1_ATR_FACTOR;

                trailDistTicks = Math.Max(atrTicks * factor, TRAIL_MIN_TICKS);
            }
            else
            {
                trailDistTicks = t1Hit ? TRAIL_DISTANCE_TICKS * 2.0 : TRAIL_DISTANCE_TICKS;
            }

            double profitTicks = (entry - currentPrice) / tickSize;
            if (profitTicks > TRAIL_START_TICKS)
            {
                double trailStop = currentPrice + trailDistTicks * tickSize;
                newStop = Math.Min(newStop, trailStop);
            }

            return newStop;
        }

        // ── Trailing stop constants ───────────────────────────────────────
        //
        // ATR-proportional factors (primary path — used when atrTicks > 0):
        //
        //   BE_ARM_ATR_FACTOR:    arm breakeven after this fraction of ATR in profit.
        //                         Per-set values override this via GetBeArmTicks() in
        //                         OrderManager (0.15 Retest, 0.20 BOS, 0.30 Impulse).
        //                         This constant is the MathPolicy fallback only.
        //
        //   TRAIL_PRE_T1_ATR_FACTOR:  trail distance before T1 partial exit.
        //                         0.30 × ATR (raised from 0.25 — backtest v2 result).
        //                         At ATR=40t: trail distance = 12t. Was 10t.
        //                         Tighter trail catches wicked-out trades 2t earlier.
        //                         Geometric check: trail stop at MFE=0.50×ATR is now
        //                         entry+0.20×ATR (was entry+0.25×ATR). More protective.
        //
        //   TRAIL_POST_T1_ATR_FACTOR: trail distance after T1 partial exit (runner mode).
        //                         0.40 × ATR — unchanged. Runner needs room to T2.
        //
        //   TRAIL_MIN_TICKS:      absolute floor on trail distance.
        //                         Prevents sub-4-tick trails on dead Globex sessions.
        //
        //   T1_PROX_BE_FACTOR:    trigger BE when MFE reaches 70% of T1 distance.
        //
        // Fixed fallback constants (secondary path — used when atrTicks = 0):
        //
        //   TRAIL_START_TICKS:    begin trailing after this much open profit.
        //                         4.0 ticks (lowered from 6.0 — backtest v2 result).
        //                         Stop hunts cluster in the 4–8 tick MFE range.
        //                         At 6t start, those trades reversed before trail
        //                         activated. At 4t, trail engages 2 ticks sooner
        //                         and catches the fastest reversals.
        //                         On NQ: 4 ticks = $20. Still above normal spread noise.
        //
        //   MFE_BE_TICKS, TRAIL_DISTANCE_TICKS — legacy values preserved.

        public const double BE_ARM_ATR_FACTOR        = 0.10;
        public const double TRAIL_PRE_T1_ATR_FACTOR  = 0.30;   // was 0.25
        public const double TRAIL_POST_T1_ATR_FACTOR = 0.40;
        public const double TRAIL_MIN_TICKS          = 4.0;
        public const double T1_PROX_BE_FACTOR        = 0.70;

        // ── High-ATR volatility regime ────────────────────────────────────
        //
        //   HIGH_ATR_THRESHOLD:   session ATR above this = high-volatility regime.
        //                         35 ticks chosen from backtest data:
        //                         Jan median ATR = 16.6t — only 7/72 trades affected.
        //                         Feb median ATR = 20.2t — 12/60 trades affected (worst sessions).
        //                         Mar median ATR = 26.6t — 15/80 trades affected.
        //                         Clips the volatile tail without touching normal conditions.
        //
        //   TRAIL_HIGH_ATR_FACTOR: replaces TRAIL_PRE_T1_ATR_FACTOR in high-ATR regime.
        //                         0.20 instead of 0.30 — trail follows 33% closer to price.
        //                         At ATR=40t: trail shrinks 12t→8t, saving $20/bar of profit.
        //                         At ATR=50t: trail shrinks 15t→10t, saving $25/bar.
        //                         At ATR=60t: trail shrinks 18t→12t, saving $30/bar.
        //                         POST-T1 trail (0.40) is intentionally unchanged —
        //                         once a partial exit has secured profit, let the runner
        //                         breathe regardless of volatility regime.
        //
        //   Only fires pre-T1. Does NOT affect entry logic or stop placement.

        public const double HIGH_ATR_THRESHOLD    = 35.0;
        public const double TRAIL_HIGH_ATR_FACTOR = 0.20;

        // Legacy fixed constants — fallback path when atrTicks = 0.
        public const double MFE_BE_TICKS         = 5.0;
        public const double TRAIL_START_TICKS     = 4.0;   // was 6.0
        public const double TRAIL_DISTANCE_TICKS  = 5.0;

        // ===================================================================
        // ENTRY METHOD POLICY
        // ===================================================================

        /// <summary>
        /// Calculate entry price and retry count based on score
        /// Returns enum index for method: 0 = PATIENT, 1 = AGGRESSIVE
        /// </summary>
        public static int EntryMethod_Select(int score, double barExtreme, double tickSize,
            bool isLong, out double entryPrice, out int retries)
        {
            if (score >= GradeThresholds.AGGRESSIVE_ENTRY) // AGGRESSIVE
            {
                entryPrice = isLong 
                    ? barExtreme + (3.0 * tickSize)
                    : barExtreme - (3.0 * tickSize);
                retries = 3;
                return 1;
            }
            
            // PATIENT
            entryPrice = isLong
                ? barExtreme + (1.0 * tickSize)
                : barExtreme - (1.0 * tickSize);
            retries = 2;
            return 0;
        }

        // ===================================================================
        // INSTRUMENT-SPECIFIC WEIGHTS
        // ===================================================================

        /// <summary>
        /// Get instrument-specific scoring weights using InstrumentKind from CommonTypes.
        /// Micro contracts (MNQ, MES) weight absorption/delta higher — they move faster.
        /// Returns: 0 = wPOC, 1 = wAbs, 2 = wDelta
        /// </summary>
        public static void InstrumentWeights_Get(string instrument,
            out double wPOC, out double wAbs, out double wDelta)
        {
            InstrumentKind kind = InstrumentSpecs.Resolve(instrument);

            if (kind == InstrumentKind.MNQ || kind == InstrumentKind.MES)
            {
                // Micros: lighter POC, heavier absorption + delta
                wPOC   = 0.20;
                wAbs   = 0.28;
                wDelta = 0.22;
            }
            else // NQ, ES — fuller contracts follow profile more
            {
                wPOC   = 0.30;
                wAbs   = 0.25;
                wDelta = 0.20;
            }
        }

        // ===================================================================
        // VIX FILTER POLICY
        // ===================================================================

        /// <summary>
        /// Apply VIX-based adjustments to parameters
        /// Returns regime index: 0 = NORMAL, 1 = ELEVATED, 2 = HIGH
        /// </summary>
        public static int VIXFilter_Apply(double vix, ref int minScore, ref double sizeMultiplier)
        {
            if (vix > 30.0)
            {
                minScore = 85;
                sizeMultiplier = 0.3;
                return 2; // HIGH
            }
            
            if (vix > 20.0)
            {
                minScore = 70;
                sizeMultiplier = 0.6;
                return 1; // ELEVATED
            }
            
            return 0; // NORMAL
        }

        // ===================================================================
        // GAP FILTER POLICY
        // ===================================================================

        /// <summary>
        /// Check if overnight gap restricts trading
        /// </summary>
        public static bool GapFilter_IsRestricted(double open, double prevClose, 
            bool isFirstHourComplete, double largeGapThreshold = 0.01)
        {
            if (prevClose <= 0.0) return false;
            
            double gapPct = Math.Abs((open - prevClose) / prevClose);
            
            // Large gap (>1.0%) blocks trading during first hour
            if (gapPct > largeGapThreshold)
            {
                return !isFirstHourComplete;
            }
            
            return false;
        }

        // ===================================================================
        // FAST MARKET DETECTION
        // ===================================================================

        /// <summary>
        /// Detect fast market conditions requiring adjusted risk
        /// </summary>
        public static bool FastMarket_Detect(double atrTicks, double currentVol, 
            double avgVol20, double barRange, double avgATR)
        {
            if (avgVol20 <= 0.0 || avgATR <= 0.0) return false;
            
            bool highATR = atrTicks > 120.0;
            bool highVolume = currentVol > (3.0 * avgVol20);
            bool wideRange = barRange > (2.0 * avgATR);
            
            return highATR && highVolume && wideRange;
        }

        // ===================================================================
        // RECLAIM CONFIRMATION POLICY
        // ===================================================================

        /// <summary>
        /// Check if price reclaims level with confirmation
        /// </summary>
        public static bool Reclaim_ConfirmLong(double close, double open, 
            double vwap, double zFade, double sdHybrid)
        {
            double reclaimLevel = vwap + (zFade * sdHybrid * 0.85);
            return (close > reclaimLevel) && (close > open);
        }

        /// <summary>
        /// Check if price reclaims level with confirmation
        /// </summary>
        public static bool Reclaim_ConfirmShort(double close, double open,
            double vwap, double zFade, double sdHybrid)
        {
            double reclaimLevel = vwap + (zFade * sdHybrid * 0.85);
            return (close < reclaimLevel) && (close < open);
        }
    }

    // ===================================================================
    // REPLAY-ONLY MODULE (NEVER USE LIVE)
    // ===================================================================

    /// <summary>
    /// REPLAY-ONLY calculations for backtesting and analysis.
    /// ⚠️ WARNING: Do NOT call these in live trading.
    /// Use ComputationTier enforcement to prevent accidental usage.
    /// </summary>
    public static class MathReplayOnly
    {
        /// <summary>
        /// REPLAY ONLY: Adjusted P&L with slippage modeling
        /// </summary>
        public static double AdjustedPnL_Calculate(
            double theoreticalEntry,
            double theoreticalExit,
            double commission,
            int posSize,
            int score,
            double tickSize,
            bool isLong = true)
        {
            // Slippage based on score
            double entrySlippage = (score >= GradeThresholds.AGGRESSIVE_ENTRY) ? 1.5 : (score >= GradeThresholds.C_SETUP) ? 2.5 : 4.0;
            double exitSlippage = 2.0;

            // Long: slip into entry (pay up), slip out of exit (give up)
            // Short: slip into entry (sell lower), slip out of exit (buy higher)
            double actualEntry = isLong
                ? theoreticalEntry + (entrySlippage * tickSize)
                : theoreticalEntry - (entrySlippage * tickSize);

            double actualExit = isLong
                ? theoreticalExit - (exitSlippage * tickSize)
                : theoreticalExit + (exitSlippage * tickSize);

            double pnlPerContract = isLong
                ? (actualExit - actualEntry - commission)
                : (actualEntry - actualExit - commission);

            return pnlPerContract * posSize;
        }

        /// <summary>
        /// REPLAY ONLY: Calculate trade slippage in ticks
        /// </summary>
        public static double TradeSlippage_Calculate(
            double expectedPrice,
            double actualPrice,
            double tickSize,
            bool isLongEntry)
        {
            if (tickSize <= 0.0) return 0.0;
            
            return isLongEntry
                ? (actualPrice - expectedPrice) / tickSize
                : (expectedPrice - actualPrice) / tickSize;
        }

        /// <summary>
        /// REPLAY ONLY: Realistic expectancy calculation
        /// </summary>
        public static double Expectancy_Calculate(
            double winRate,
            double avgWin,
            double avgLoss,
            double commission,
            double slippageImpact = 50.0) => (winRate * (avgWin - slippageImpact))
                - ((1.0 - winRate) * (avgLoss + slippageImpact))
                - commission;

        /// <summary>
        /// REPLAY ONLY: Performance variance alert
        /// </summary>
        public static bool PerformanceVariance_Alert(
            double actualPnL,
            double expectedPnL,
            out double variancePct,
            double alertThreshold = 0.25)
        {
            variancePct = 0.0;
            if (expectedPnL == 0.0) return false;

            variancePct = (actualPnL - expectedPnL) / Math.Abs(expectedPnL);
            return Math.Abs(variancePct) > alertThreshold;
        }

        /// <summary>
        /// REPLAY ONLY: Check if edge is lost (below threshold)
        /// </summary>
        public static bool EdgeDecay_Check(double rollingWinRate, double threshold = 0.55) => rollingWinRate < threshold;
    }
}
