#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    // =========================================================================
    // Wyckoff_Signals.cs — Rejection and absorption logic (Wyckoff-style)
    //
    // Components:
    //   Wyckoff_Spring     Rejection of a structural LOW.
    //   Wyckoff_Upthrust   Rejection of a structural HIGH.
    //
    // Integration:
    //   Registered in HostStrategy.CreateLogic().
    // =========================================================================

    // ── BASE CLASS ────────────────────────────────────────────────────────

    /// <summary>
    /// Base class for all Wyckoff-driven condition sets.
    /// provides shared state for rejection logic.
    /// </summary>
    public abstract class WyckoffBase : IConditionSet
    {
        public abstract string SetId { get; }
        public string LastDiagnostic => "";

        protected double _tickSize;
        protected double _tickValue;

        // Re-entry suppression
        protected int _lastSignalBar = -1;
        protected const int SIGNAL_COOLDOWN = 10;

        // Episode tracking (prevents firing multiple times on same rejection)
        protected double _activeLevel = 0.0;
        protected bool   _isEpisodeActive = false;

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _lastSignalBar = -1;
            ResetEpisode();
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
                _lastSignalBar = signal.BarIndex;
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        public abstract RawDecision Evaluate(MarketSnapshot snapshot);

        // ── Shared helpers ────────────────────────────────────────────────

        protected void ResetEpisode()
        {
            _activeLevel = 0.0;
            _isEpisodeActive = false;
        }

        /// <summary>
        /// Logic for identifying a structural rejection.
        ///   1. Price wicks below/above a key level.
        ///   2. Price returns and closes back inside.
        ///   3. Delta confirms institutional absorption.
        /// </summary>
        protected RawDecision BuildSpring(BarSnapshot p, MarketSnapshot snap, double level, double atr)
        {
            if (p.Close < level) return RawDecision.None; // failed spring

            int score = 65;
            // footprint confirms absorption at low
            if (snap.Get(SnapKeys.VolDeltaSl) > 0) score += 10;
            
            return new RawDecision
            {
                Direction = SignalDirection.Long,
                Source    = SignalSource.Wyckoff_Spring,
                EntryPrice = p.Close,
                StopPrice  = p.Low - (2 * _tickSize),
                TargetPrice = p.Close + atr * 1.5,
                Target2Price = p.Close + atr * 3.0,
                Label     = $"Spring @ {level:F2}",
                RawScore  = score,
                IsValid   = true,
                SignalId  = $"{SetId}:S:{p.CurrentBar}"
            };
        }

        protected RawDecision BuildUpthrust(BarSnapshot p, MarketSnapshot snap, double level, double atr)
        {
            if (p.Close > level) return RawDecision.None; // failed upthrust

            int score = 65;
            if (snap.Get(SnapKeys.VolDeltaSh) < 0) score += 10;

            return new RawDecision
            {
                Direction = SignalDirection.Short,
                Source    = SignalSource.Wyckoff_Upthrust,
                EntryPrice = p.Close,
                StopPrice  = p.High + (2 * _tickSize),
                TargetPrice = p.Close - atr * 1.5,
                Target2Price = p.Close - atr * 3.0,
                Label     = $"Upthrust @ {level:F2}",
                RawScore  = score,
                IsValid   = true,
                SignalId  = $"{SetId}:U:{p.CurrentBar}"
            };
        }
    }

    // ── SPRING ────────────────────────────────────────────────────────────

    public class Wyckoff_Spring : WyckoffBase
    {
        public override string SetId => "Wyckoff_Spring_v1";

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;

            // Logic logic: rejection of PDL, VAL, or structural low
            return RawDecision.None;
        }
    }

    // ── UPTHRUST ──────────────────────────────────────────────────────────

    public class Wyckoff_Upthrust : WyckoffBase
    {
        public override string SetId => "Wyckoff_Upthrust_v1";

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            return RawDecision.None;
        }
    }
}
