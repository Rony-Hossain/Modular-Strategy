#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    // =========================================================================
    // SMC_IFVG.cs — Inversion Fair Value Gap condition set
    //
    // CONCEPT:
    //   An Inversion Fair Value Gap (IFVG) occurs when a standard FVG is
    //   "disrespected" or broken through by price. Instead of acting as support
    //   or resistance, the gap is sliced through, indicating a shift in
    //   market control. After the breach, the gap flips polarity:
    //     - A disrespected Bullish FVG becomes Bearish Resistance.
    //     - A disrespected Bearish FVG becomes Bullish Support.
    //
    // DETECTION:
    //   1. Identify a standard FVG (3-bar pattern).
    //   2. Wait for a candle to CLOSE beyond the gap (above bear upper, below bull lower).
    //   3. The gap is now an "Inversion" zone.
    //   4. Entry fires on the first RETEST touch of the disrespected gap.
    // =========================================================================

    public class SMC_IFVG : IConditionSet
    {
        public string SetId => "SMC_IFVG_v1";
        public string LastDiagnostic => "";

        private double _tickSize;
        private double _tickValue;

        // ── State ─────────────────────────────────────────────────────────
        private FairValueGap _bullIFVG = FairValueGap.Empty;
        private FairValueGap _bearIFVG = FairValueGap.Empty;
        private int _lastFillBar = -1;

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _bullIFVG = FairValueGap.Empty;
            _bearIFVG = FairValueGap.Empty;
            _lastFillBar = -1;
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
                _lastFillBar = signal.BarIndex;
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        // ── Main evaluation ───────────────────────────────────────────────

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;

            // Re-entry suppression
            if (_lastFillBar > 0 && p.CurrentBar - _lastFillBar < 5)
                return RawDecision.None;

            // Update inversion state logic here...
            return RawDecision.None;
        }
    }
}
