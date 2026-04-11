#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ===================================================================
    // NULL SLIPPAGE MODEL
    // ===================================================================
    public sealed class NullSlippageModel : ISlippageModel
    {
        public SlippageResult Apply(RawDecision decision, MarketSnapshot snapshot)
        {
            var result = SlippageResult.Zero;
            result.AdjustedEntryPrice = decision.EntryPrice;
            return result;
        }

        public double EstimateExitSlippage(MarketSnapshot snapshot) => 0.0;
    }

    // ===================================================================
    // SCORE + SESSION SLIPPAGE MODEL
    // ===================================================================
    public sealed class ScoreSessionSlippageModel : ISlippageModel
    {
        private readonly InstrumentKind _instrument;

        public ScoreSessionSlippageModel(InstrumentKind instrument)
        {
            _instrument = instrument;
        }

        public SlippageResult Apply(RawDecision decision, MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return BuildZeroResult(decision.EntryPrice, "Snapshot Invalid");

            double entryTicks = MathSlippage.EntrySlippage_Ticks(
                score:      decision.RawScore, 
                session:    snapshot.Primary.Session, 
                instrument: _instrument);

            double exitTicks  = MathSlippage.ExitSlippage_Ticks(
                session:    snapshot.Primary.Session, 
                instrument: _instrument);

            double adjustedEntry = decision.Direction == SignalDirection.Long
                ? decision.EntryPrice + (entryTicks * snapshot.Primary.TickSize)
                : decision.EntryPrice - (entryTicks * snapshot.Primary.TickSize);

            return new SlippageResult
            {
                AdjustedEntryPrice = adjustedEntry,
                EntrySlippageTicks = entryTicks,
                ExitSlippageTicks  = exitTicks,
                Reason             = "SessionAware",
                IsValid            = true
            };
        }

        public double EstimateExitSlippage(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return 0.0;
            return MathSlippage.ExitSlippage_Ticks(snapshot.Primary.Session, _instrument);
        }

        private static SlippageResult BuildZeroResult(double entryPrice, string reason)
        {
            var r = SlippageResult.Zero;
            r.AdjustedEntryPrice = entryPrice;
            r.Reason             = reason;
            return r;
        }
    }
}
