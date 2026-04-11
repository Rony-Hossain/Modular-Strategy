#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    /// <summary>
    /// VWAP_RTH — VWAP reclaim and rejection logic.
    /// 
    /// Fires when price crosses back over the session VWAP from above (short)
    /// or below (long), indicating a failure of the trend and a return to value.
    /// 
    /// Mandatory context:
    ///   - Must be during Early/Mid/Late session (no pre-market).
    ///   - VWAP must be initialized (> 0).
    /// </summary>
    public class VWAP_RTH : IConditionSet
    {
        public string SetId => "VWAP_RTH_v1";
        public string LastDiagnostic => "";

        private double _tickSize;
        private double _tickValue;

        // Re-entry suppression
        private int _lastFillBar = -1;
        private const int REENTRY_COOLDOWN = 10;

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _lastFillBar = -1;
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
                _lastFillBar = signal.BarIndex;
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        // ===================================================================
        // EVALUATE
        // ===================================================================

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;

            // Session gate: only trade during regular session
            if (p.Session != SessionPhase.EarlySession &&
                p.Session != SessionPhase.MidSession   &&
                p.Session != SessionPhase.LateSession)
                return RawDecision.None;

            // VWAP must be ready
            double vwap = snapshot.VWAP;
            if (vwap <= 0) return RawDecision.None;

            // Cooldown
            if (_lastFillBar >= 0 && p.CurrentBar - _lastFillBar < REENTRY_COOLDOWN)
                return RawDecision.None;

            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;

            // ── BULLISH RECLAIM ──────────────────────────────────────────
            // Price was below VWAP, crossed above, and closed above.
            bool bullReclaim = p.Opens[0] < vwap && p.Close > vwap;
            
            if (bullReclaim)
            {
                return new RawDecision
                {
                    Direction    = SignalDirection.Long,
                    Source       = SignalSource.VWAP_Reclaim,
                    EntryPrice   = p.Close,
                    StopPrice    = p.Low - (2 * _tickSize),
                    TargetPrice  = p.Close + (atr * 1.5),
                    Target2Price = p.Close + (atr * 3.0),
                    Label        = $"VWAP bull reclaim [{SetId}]",
                    RawScore     = 65,
                    IsValid      = true,
                    SignalId     = $"{SetId}:Bull:{p.CurrentBar}"
                };
            }

            // ── BEARISH RECLAIM ──────────────────────────────────────────
            // Price was above VWAP, crossed below, and closed below.
            bool bearReclaim = p.Opens[0] > vwap && p.Close < vwap;

            if (bearReclaim)
            {
                return new RawDecision
                {
                    Direction    = SignalDirection.Short,
                    Source       = SignalSource.VWAP_Reclaim,
                    EntryPrice   = p.Close,
                    StopPrice    = p.High + (2 * _tickSize),
                    TargetPrice  = p.Close - (atr * 1.5),
                    Target2Price = p.Close - (atr * 3.0),
                    Label        = $"VWAP bear reclaim [{SetId}]",
                    RawScore     = 65,
                    IsValid      = true,
                    SignalId     = $"{SetId}:Bear:{p.CurrentBar}"
                };
            }

            return RawDecision.None;
        }
    }
}
