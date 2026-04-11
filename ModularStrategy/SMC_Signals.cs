#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    // =========================================================================
    // SMC_Signals.cs — Smart Money Concepts condition sets
    //
    // Components:
    //   SMC_BOS          Break of Structure signals (Trend following).
    //   SMC_CHoCH        Change of Character signals (Trend reversal).
    //   SMC_OB           Order Block mitigation entries.
    //
    // Integration:
    //   Registered in HostStrategy.CreateLogic().
    // =========================================================================

    // ── BASE CLASS ────────────────────────────────────────────────────────

    /// <summary>
    /// Base class for all SMC-driven condition sets.
    /// Provides shared structural tracking and re-entry logic.
    /// </summary>
    public abstract class SMCBase : IConditionSet
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

        /// <summary>
        /// Checks if the bar indicates institutional participation via volume or delta.
        /// </summary>
        protected bool HasInstitutionalWeight(MarketSnapshot snap)
        {
            if (!snap.GetFlag(SnapKeys.HasVolumetric)) return true; // degrade gracefully

            double delta = Math.Abs(snap.Get(SnapKeys.BarDelta));
            double total = snap.Get(SnapKeys.VolBuyVol) + snap.Get(SnapKeys.VolSellVol);
            
            if (total <= 0) return false;
            return (delta / total) > 0.15; // 15% relative delta minimum
        }
    }

    // ── BOS ───────────────────────────────────────────────────────────────

    public class SMC_BOS : SMCBase
    {
        public override string SetId => "SMC_BOS_v1";

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;

            // Cooldown
            if (_lastSignalBar >= 0 && p.CurrentBar - _lastSignalBar < SIGNAL_COOLDOWN)
                return RawDecision.None;

            // Logic implemented by individual classes now.
            return RawDecision.None;
        }
    }

    // ── CHoCH ─────────────────────────────────────────────────────────────

    public class SMC_CHoCH : SMCBase
    {
        public override string SetId => "SMC_CHoCH_v1";

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            return RawDecision.None;
        }
    }

    // ── OB ────────────────────────────────────────────────────────────────

    public class SMC_OB : SMCBase
    {
        public override string SetId => "SMC_OB_v1";

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            return RawDecision.None;
        }
    }
}
