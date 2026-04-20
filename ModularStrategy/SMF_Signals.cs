#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    // =========================================================================
    // SMF_Signals.cs — Pattern-B Smart Money Flow condition sets
    //
    // Components:
    //   SMF_Impulse      Signals based on momentum shifts in the SMF cloud.
    //   SMF_Retest       Pullback entries aligned with the dominant SMF trend.
    //   SMF_Reversal     Counter-trend exhaustion entries at SMF extremes.
    //
    // Integration:
    //   Registered in HostStrategy.CreateLogic().
    // =========================================================================

    // ── BASE CLASS ────────────────────────────────────────────────────────

    /// <summary>
    /// Base class for all SMF-driven condition sets.
    /// </summary>
    public abstract class SMFBase : IConditionSet
    {
        public abstract string SetId { get; }
        public string LastDiagnostic => "";

        protected double _tickSize;
        protected double _tickValue;

        // Re-entry suppression
        protected int _lastSignalBar = -1;
        protected const int SIGNAL_COOLDOWN = 5;

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _lastSignalBar = -1;
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
                _lastSignalBar = signal.BarIndex;
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        public abstract RawDecision Evaluate(MarketSnapshot snapshot);

        // ── Shared helpers ────────────────────────────────────────────────

        protected static bool IsRTH(BarSnapshot p)
        {
            return true;
        }
    }

    // ── IMPULSE ───────────────────────────────────────────────────────────

    public class SMF_Impulse : SMFBase
    {
        public override string SetId => "SMF_Impulse_v1";

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;

            // Cooldown
            if (_lastSignalBar >= 0 && p.CurrentBar - _lastSignalBar < SIGNAL_COOLDOWN)
                return RawDecision.None;

            // Logic placeholder: Impulse is a fresh shift in cloud direction
            // Requires SnapKeys.Regime shift from 0 or opposite side.
            return RawDecision.None;
        }
    }

    // ── RETEST ────────────────────────────────────────────────────────────

    public class SMF_Retest : SMFBase
    {
        public override string SetId => "SMF_Retest_v1";

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;

            // Cooldown
            if (_lastSignalBar >= 0 && p.CurrentBar - _lastSignalBar < SIGNAL_COOLDOWN)
                return RawDecision.None;

            // Logic placeholder: Pullback to cloud basis or bands
            return RawDecision.None;
        }
    }
}
