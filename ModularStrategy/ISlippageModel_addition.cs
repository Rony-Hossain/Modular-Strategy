// ===================================================================
// SLIPPAGE MODEL — Interface and result DTO.
// ===================================================================

namespace MathLogic.Strategy
{
    public struct SlippageResult
    {
        public double AdjustedEntryPrice;
        public double EntrySlippageTicks;
        public double ExitSlippageTicks;
        public string Reason;
        public bool   IsValid;

        public static readonly SlippageResult Zero = new SlippageResult 
        { 
            IsValid = true, 
            Reason = "Zero" 
        };
    }

    public interface ISlippageModel
    {
        /// <summary>
        /// Apply slippage to a raw entry decision.
        /// </summary>
        SlippageResult Apply(RawDecision decision, MarketSnapshot snapshot);

        /// <summary>
        /// Estimate the expected exit slippage in ticks.
        /// </summary>
        double EstimateExitSlippage(MarketSnapshot snapshot);
    }
}
