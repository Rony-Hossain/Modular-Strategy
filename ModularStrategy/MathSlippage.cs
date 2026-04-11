#region Using declarations
using System;
using MathLogic.Strategy;   // SessionPhase lives in IStrategyModules.cs (MathLogic.Strategy)
                             // InstrumentKind, GradeThresholds live in CommonTypes.cs (MathLogic) — no using needed
#endregion

namespace MathLogic
{
    /// <summary>
    /// SLIPPAGE MATH MODULE — pure stateless slippage calculations.
    ///
    /// Design contract (same as rest of library):
    ///   - Pure stateless functions. Caller owns all state.
    ///   - No LINQ, no allocations in hot paths.
    ///   - No NT8 dependencies — fully unit-testable.
    ///   - ComputationTier: CORE_ONLY (lightweight, per-signal).
    ///
    /// What this module knows:
    ///   - How signal score maps to expected fill quality.
    ///   - How session phase affects spread and queue depth.
    ///   - How instrument liquidity affects slippage magnitude.
    ///
    /// What this module does NOT know:
    ///   - Whether we are in backtest or live (caller decides).
    ///   - Order type (limit vs market — caller decides).
    ///   - NT8 execution details.
    ///
    /// All slippage values are in TICKS (not price). Caller converts
    /// using tickSize. This keeps the math instrument-agnostic.
    /// </summary>
    public static class MathSlippage
    {
        // ===================================================================
        // SESSION PHASE MULTIPLIERS
        // Reflects real-world spread behaviour across the RTH session.
        //
        //   OpeningRange  — first 30 min. Wide spreads, fast moves,
        //                   queue position unpredictable. +50% slippage.
        //   EarlySession  — 9:30–11:00 ET. Still active but settling. +20%.
        //   MidSession    — 11:00–14:00 ET. Tightest spreads, most depth. 1.0x.
        //   LateSession   — 14:00–close. MOC flows introduce volatility. +10%.
        //   Pre/AfterHours— thin markets. +80% slippage assumption.
        // ===================================================================

        private const double SESSION_MULTIPLIER_PREMARKET    = 1.80;
        private const double SESSION_MULTIPLIER_OPENING      = 1.50;
        private const double SESSION_MULTIPLIER_EARLY        = 1.20;
        private const double SESSION_MULTIPLIER_MID          = 1.00;  // baseline
        private const double SESSION_MULTIPLIER_LATE         = 1.10;
        private const double SESSION_MULTIPLIER_AFTERHOURS   = 1.80;

        // ===================================================================
        // INSTRUMENT LIQUIDITY MULTIPLIERS
        // ES is the most liquid futures contract in the world.
        // NQ is deep but slightly less so.
        // Micros (MES, MNQ) have proportionally wider relative spreads.
        // ===================================================================

        private const double INSTRUMENT_MULTIPLIER_ES  = 1.00;  // baseline — deepest book
        private const double INSTRUMENT_MULTIPLIER_NQ  = 1.10;
        private const double INSTRUMENT_MULTIPLIER_MES = 1.20;
        private const double INSTRUMENT_MULTIPLIER_MNQ = 1.20;

        // ===================================================================
        // BASE ENTRY SLIPPAGE BY SCORE TIER (ticks)
        //
        // Higher score = better setup = entered in more favourable conditions
        // = tighter spread + better queue position.
        //
        // A+ (80+) — aggressive limit, filled quickly at or near touch.
        // A  (75+) — strong limit, minor adverse movement expected.
        // B  (65+) — standard limit, moderate queue wait.
        // C  (60+) — marginal setup, wider spread + longer wait.
        //
        // These are conservative assumptions. Real slippage on ES during
        // MidSession on an A+ setup can be as low as 0.5 ticks. We do not
        // use optimistic values here — this is a risk model, not a wish.
        // ===================================================================

        private const double BASE_ENTRY_SLIPPAGE_A_PLUS = 1.5;
        private const double BASE_ENTRY_SLIPPAGE_A      = 2.0;
        private const double BASE_ENTRY_SLIPPAGE_B      = 2.5;
        private const double BASE_ENTRY_SLIPPAGE_C      = 3.0;

        // Exit slippage is fixed at 2.0 ticks base.
        // Exits are reactive — you are always chasing price when stopped out
        // or taking profit. Score does not reduce exit slippage.
        private const double BASE_EXIT_SLIPPAGE         = 2.0;

        // ===================================================================
        // PUBLIC API
        // ===================================================================

        /// <summary>
        /// Calculate entry slippage in ticks for the given context.
        ///
        /// Returns a positive number of ticks. The caller is responsible for
        /// applying direction:
        ///   Long:  actualEntry = theoreticalEntry + (ticks * tickSize)
        ///   Short: actualEntry = theoreticalEntry - (ticks * tickSize)
        /// </summary>
        /// <param name="score">Raw signal score 0–100.</param>
        /// <param name="session">Current session phase from MarketSnapshot.</param>
        /// <param name="instrument">Instrument kind for liquidity adjustment.</param>
        /// <returns>Expected entry slippage in ticks (always >= 0).</returns>
        public static double EntrySlippage_Ticks(
            int            score,
            SessionPhase   session,
            InstrumentKind instrument)
        {
            double baseTicks        = BaseEntrySlippage(score);
            double sessionMult      = SessionMultiplier(session);
            double instrumentMult   = InstrumentMultiplier(instrument);

            // Round to nearest 0.5 tick — avoids false precision in a
            // model that is fundamentally an approximation.
            return RoundHalf(baseTicks * sessionMult * instrumentMult);
        }

        /// <summary>
        /// Calculate exit slippage in ticks for the given context.
        ///
        /// Exit slippage is score-independent — you cannot control when a
        /// stop is hit or when you chase a market order to take profit.
        /// Session and instrument adjustments still apply.
        /// </summary>
        /// <param name="session">Current session phase.</param>
        /// <param name="instrument">Instrument kind.</param>
        /// <returns>Expected exit slippage in ticks (always >= 0).</returns>
        public static double ExitSlippage_Ticks(
            SessionPhase   session,
            InstrumentKind instrument)
        {
            double sessionMult    = SessionMultiplier(session);
            double instrumentMult = InstrumentMultiplier(instrument);
            return RoundHalf(BASE_EXIT_SLIPPAGE * sessionMult * instrumentMult);
        }

        /// <summary>
        /// Apply entry slippage to a theoretical entry price.
        ///
        /// Long  entries: price moves UP against you (you pay more to buy).
        /// Short entries: price moves DOWN against you (you receive less to sell).
        /// </summary>
        public static double ApplyEntrySlippage(
            double         theoreticalEntry,
            bool           isLong,
            double         slippageTicks,
            double         tickSize)
        {
            double slippagePrice = slippageTicks * tickSize;
            return isLong
                ? theoreticalEntry + slippagePrice
                : theoreticalEntry - slippagePrice;
        }

        /// <summary>
        /// Calculate the full round-trip slippage cost in dollars.
        /// Useful for performance logging and R:R adjustment.
        /// </summary>
        /// <param name="entrySlippageTicks">From EntrySlippage_Ticks().</param>
        /// <param name="exitSlippageTicks">From ExitSlippage_Ticks().</param>
        /// <param name="contracts">Number of contracts.</param>
        /// <param name="tickValue">Dollar value per tick per contract.</param>
        /// <returns>Total round-trip slippage cost in dollars (always >= 0).</returns>
        public static double RoundTripCost_Dollars(
            double entrySlippageTicks,
            double exitSlippageTicks,
            int    contracts,
            double tickValue)
        {
            return (entrySlippageTicks + exitSlippageTicks) * contracts * tickValue;
        }

        // ===================================================================
        // PRIVATE HELPERS
        // ===================================================================

        private static double BaseEntrySlippage(int score)
        {
            if (score >= GradeThresholds.AGGRESSIVE_ENTRY) return BASE_ENTRY_SLIPPAGE_A_PLUS;
            if (score >= GradeThresholds.A_SETUP)          return BASE_ENTRY_SLIPPAGE_A;
            if (score >= GradeThresholds.B_SETUP)          return BASE_ENTRY_SLIPPAGE_B;
            return BASE_ENTRY_SLIPPAGE_C;
        }

        private static double SessionMultiplier(SessionPhase session)
        {
            switch (session)
            {
                case SessionPhase.PreMarket:    return SESSION_MULTIPLIER_PREMARKET;
                case SessionPhase.OpeningRange: return SESSION_MULTIPLIER_OPENING;
                case SessionPhase.EarlySession: return SESSION_MULTIPLIER_EARLY;
                case SessionPhase.MidSession:   return SESSION_MULTIPLIER_MID;
                case SessionPhase.LateSession:  return SESSION_MULTIPLIER_LATE;
                case SessionPhase.AfterHours:   return SESSION_MULTIPLIER_AFTERHOURS;
                default:                        return SESSION_MULTIPLIER_MID;
            }
        }

        private static double InstrumentMultiplier(InstrumentKind instrument)
        {
            switch (instrument)
            {
                case InstrumentKind.ES:  return INSTRUMENT_MULTIPLIER_ES;
                case InstrumentKind.NQ:  return INSTRUMENT_MULTIPLIER_NQ;
                case InstrumentKind.MES: return INSTRUMENT_MULTIPLIER_MES;
                case InstrumentKind.MNQ: return INSTRUMENT_MULTIPLIER_MNQ;
                default:                 return INSTRUMENT_MULTIPLIER_ES;
            }
        }

        // Round to nearest 0.5 — avoids spurious precision (2.3750 ticks).
        private static double RoundHalf(double ticks)
            => Math.Round(ticks * 2.0, MidpointRounding.AwayFromZero) / 2.0;
    }
}
