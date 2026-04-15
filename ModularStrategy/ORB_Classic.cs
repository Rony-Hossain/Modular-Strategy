#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using NinjaTrader.NinjaScript.Strategies;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    /// <summary>
    /// ORB_CLASSIC — Institutional Value-Based ORB (Production Rewrite)
    /// 
    /// Logic:
    /// 1. Wait for valid levels (ORBComplete, POC/VAH/VAL > 0).
    /// 2. First side to break defines session bias (_firstBreakout).
    /// 3. Touch detection: Range overlap with [POC-buffer, VAH+buffer] (Long) or [VAL-buffer, POC+buffer] (Short).
    /// 4. Delta Confirmation: Long (barDelta > 0), Short (barDelta < 0).
    /// 5. Invalidation: Reset bias if price closes beyond the opposite side of the OR.
    /// 6. Risk: Stops max(100 ticks, 1x ATR), Targets 1R and 2R.
    /// </summary>
    public class ORB_Classic : IConditionSet
    {
        public string SetId => "ORB_Value_v2";

        private readonly StrategyLogger _log;
        private double _tickSize;
        private double _tickValue;

        public ORB_Classic() { }

        public ORB_Classic(StrategyLogger log)
        {
            _log = log;
        }

        // Session State
        private SignalDirection _firstBreakout = SignalDirection.None;
        private int             _lastFillBar   = -1;
        private const int       REENTRY_COOLDOWN = 5;

        // ── Diagnostics ──
        private string _lastBailReason = "";
        public string LastDiagnostic => _lastBailReason;

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _firstBreakout = SignalDirection.None;
            _lastFillBar   = -1;
            _lastBailReason = "session_open";
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
                _lastFillBar = signal.BarIndex;
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid)                  { _lastBailReason = "snapshot_invalid"; return RawDecision.None; }
            if (!snapshot.ORBComplete)              { _lastBailReason = "orb_not_complete"; return RawDecision.None; }

            var p = snapshot.Primary;
            double orbPoc = snapshot.Get(SnapKeys.ORBPoc);
            double orbVah = snapshot.Get(SnapKeys.ORBVaHigh);
            double orbVal = snapshot.Get(SnapKeys.ORBVaLow);
            
            if (orbPoc <= 0 || orbVah <= 0 || orbVal <= 0)
            { 
                _lastBailReason = $"levels_zero (POC={orbPoc:F2})"; 
                return RawDecision.None; 
            }

            double atr = snapshot.ATR;
            if (atr <= 0) { _lastBailReason = "atr_zero"; return RawDecision.None; }
            double buffer = 0.25 * atr;

            // First Breakout Wins
            if (_firstBreakout == SignalDirection.None)
            {
                if      (p.Close > snapshot.ORBHigh) { _firstBreakout = SignalDirection.Long;  }
                else if (p.Close < snapshot.ORBLow)  { _firstBreakout = SignalDirection.Short; }
            }

            if (_firstBreakout == SignalDirection.None)
            { 
                _lastBailReason = $"no_breakout_yet ORBH={snapshot.ORBHigh:F2} ORBL={snapshot.ORBLow:F2}"; 
                return RawDecision.None; 
            }

            // Invalidation: breakout thesis is dead if price closes beyond the opposite side of the OR
            if (_firstBreakout == SignalDirection.Long && p.Close < snapshot.ORBLow)
            {
                _firstBreakout = SignalDirection.None;
                _lastBailReason = $"INVALIDATED_long C={p.Close:F2} < ORBLow={snapshot.ORBLow:F2}";
                return RawDecision.None;
            }
            if (_firstBreakout == SignalDirection.Short && p.Close > snapshot.ORBHigh)
            {
                _firstBreakout = SignalDirection.None;
                _lastBailReason = $"INVALIDATED_short C={p.Close:F2} > ORBHigh={snapshot.ORBHigh:F2}";
                return RawDecision.None;
            }

            if (_lastFillBar >= 0 && p.CurrentBar - _lastFillBar < REENTRY_COOLDOWN)
            { 
                _lastBailReason = "cooldown"; 
                return RawDecision.None; 
            }

            double barDelta = snapshot.Get(SnapKeys.BarDelta);

            if (_firstBreakout == SignalDirection.Long)
            {
                double bandLo = orbPoc - buffer;
                double bandHi = orbVah + buffer;
                bool inReloadZone = p.Low <= bandHi && p.High >= bandLo;

                if (!inReloadZone)
                { 
                    _lastBailReason = $"long_no_touch L={p.Low:F2} H={p.High:F2} band=[{bandLo:F2},{bandHi:F2}]"; 
                    return RawDecision.None; 
                }
                if (barDelta <= 0)
                { 
                    _lastBailReason = $"long_delta_neg bd={barDelta:F0} (soft_skip)"; 
                    return RawDecision.None; 
                }

                double stopDistance = Math.Max(100.0 * _tickSize, 1.0 * atr);
                double entryPrice   = p.Close;
                double stopPrice    = entryPrice - stopDistance;
                double targetPrice  = entryPrice + stopDistance;

                if (_log != null)
                {
                    string signalId = string.Format("{0}:{1:yyyyMMdd_HHmmss}:{2}", SetId, p.Time, p.CurrentBar);
                    _log.LogTouchEvent(
                        signalId, SetId, SignalDirection.Long,
                        entryPrice, bandLo, bandHi,
                        stopPrice, targetPrice,
                        "ORB_VALUE", p.Time, snapshot);
                }

                _lastBailReason = "FIRED_LONG";
                return new RawDecision
                {
                    Direction    = SignalDirection.Long,
                    Source       = SignalSource.ORB_Retest,
                    ConditionSetId = SetId,
                    EntryPrice   = entryPrice,
                    StopPrice    = stopPrice,
                    TargetPrice  = targetPrice,
                    Target2Price = entryPrice + (2.0 * stopDistance),
                    Label        = $"ORB Reload Long [{SetId}]",
                    RawScore     = 85,
                    IsValid      = true
                };
            }

            if (_firstBreakout == SignalDirection.Short)
            {
                double bandLo = orbVal - buffer;
                double bandHi = orbPoc + buffer;
                bool inReloadZone = p.Low <= bandHi && p.High >= bandLo;

                if (!inReloadZone)
                { 
                    _lastBailReason = $"short_no_touch L={p.Low:F2} H={p.High:F2} band=[{bandLo:F2},{bandHi:F2}]"; 
                    return RawDecision.None; 
                }
                if (barDelta >= 0)
                { 
                    _lastBailReason = $"short_delta_pos bd={barDelta:F0} (soft_skip)"; 
                    return RawDecision.None; 
                }

                double stopDistance = Math.Max(100.0 * _tickSize, 1.0 * atr);
                double entryPrice   = p.Close;
                double stopPrice    = entryPrice + stopDistance;
                double targetPrice  = entryPrice - stopDistance;

                if (_log != null)
                {
                    string signalId = string.Format("{0}:{1:yyyyMMdd_HHmmss}:{2}", SetId, p.Time, p.CurrentBar);
                    _log.LogTouchEvent(
                        signalId, SetId, SignalDirection.Short,
                        entryPrice, bandLo, bandHi,
                        stopPrice, targetPrice,
                        "ORB_VALUE", p.Time, snapshot);
                }

                _lastBailReason = "FIRED_SHORT";
                return new RawDecision
                {
                    Direction    = SignalDirection.Short,
                    Source       = SignalSource.ORB_Retest,
                    ConditionSetId = SetId,
                    EntryPrice   = entryPrice,
                    StopPrice    = stopPrice,
                    TargetPrice  = targetPrice,
                    Target2Price = entryPrice - (2.0 * stopDistance),
                    Label        = $"ORB Reload Short [{SetId}]",
                    RawScore     = 85,
                    IsValid      = true
                };
            }

            return RawDecision.None;
        }
    }
}
