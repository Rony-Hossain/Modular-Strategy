#region Using declarations
using System;
using NinjaTrader.NinjaScript.Strategies;
using MathLogic.Strategy;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ORB_Classic : SMCBase
    {
        private readonly StrategyLogger _log;
        private readonly double _tickSize;
        private double _firstOrbHigh;
        private double _firstOrbLow;
        private double _firstOrbPoc;
        private SignalDirection _firstBreakout = SignalDirection.None;

        public ORB_Classic(StrategyHost host, StrategyLogger log) : base(host)
        {
            SetId = "ORB_Value_v2";
            _log = log;
            _tickSize = host.TickSize;
        }

        public override void OnSessionOpen(MarketSnapshot snapshot)
        {
            base.OnSessionOpen(snapshot);
            _firstOrbHigh = 0;
            _firstOrbLow = 0;
            _firstOrbPoc = 0;
            _firstBreakout = SignalDirection.None;
        }

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.ORBComplete) return RawDecision.None;

            var p = snapshot.Primary;
            if (p.Session != SessionPhase.EarlySession && p.Session != SessionPhase.MidSession)
                return RawDecision.None;

            if (_firstOrbHigh == 0)
            {
                _firstOrbHigh = snapshot.ORBVaHigh;
                _firstOrbLow  = snapshot.ORBVaLow;
                _firstOrbPoc  = snapshot.ORBPoc;
            }

            if (_firstBreakout == SignalDirection.None)
            {
                if (p.High > _firstOrbHigh) _firstBreakout = SignalDirection.Long;
                if (p.Low < _firstOrbLow)   _firstBreakout = SignalDirection.Short;
                return RawDecision.None;
            }

            double orbVal = _firstOrbLow;
            double orbPoc = _firstOrbPoc;
            double orbVeh = _firstOrbHigh;
            double buffer = 5.0 * _tickSize;
            double atr    = snapshot.ATR;
            double barDelta = snapshot.Get(SnapKeys.BarDelta);

            if (_firstBreakout == SignalDirection.Long)
            {
                double bandLo = orbPoc - buffer;
                double bandHi = orbVal + buffer;
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

                // Generate SignalId at touch time — flows through SignalGenerator unchanged
                string signalId = string.Format("{0}:{1:yyyyMMdd}:{2}",
                    SetId, p.Time, p.CurrentBar);

                // Log the qualifying touch — fires for every touch ORB considered, 
                // including ones the downstream pipeline may later reject. The unmatched 
                // TOUCH rows in post-processing tell us what got dropped.
                _log?.LogTouchEvent(
                    signalId, SetId, SignalDirection.Long,
                    entryPrice, bandLo, bandHi,
                    entryPrice - stopDistance,            // stop
                    entryPrice + stopDistance,            // target (T1)
                    "ORB_RELOAD",
                    p.Time, snapshot);

                _lastBailReason = "FIRED_LONG";
                return new RawDecision
                {
                    Direction    = SignalDirection.Long,
                    Source       = SignalSource.ORB_Retest,
                    ConditionSetId = SetId,
                    SignalId     = signalId,
                    EntryPrice   = entryPrice,
                    StopPrice    = entryPrice - stopDistance,
                    TargetPrice  = entryPrice + stopDistance,
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

                // Generate SignalId at touch time — flows through SignalGenerator unchanged
                string signalId = string.Format("{0}:{1:yyyyMMdd}:{2}",
                    SetId, p.Time, p.CurrentBar);

                // Log the qualifying touch — fires for every touch ORB considered, 
                // including ones the downstream pipeline may later reject.
                _log?.LogTouchEvent(
                    signalId, SetId, SignalDirection.Short,
                    entryPrice, bandLo, bandHi,
                    entryPrice + stopDistance,            // stop (above for shorts)
                    entryPrice - stopDistance,            // target (below for shorts)
                    "ORB_RELOAD",
                    p.Time, snapshot);

                _lastBailReason = "FIRED_SHORT";
                return new RawDecision
                {
                    Direction    = SignalDirection.Short,
                    Source       = SignalSource.ORB_Retest,
                    ConditionSetId = SetId,
                    SignalId     = signalId,
                    EntryPrice   = entryPrice,
                    StopPrice    = entryPrice + stopDistance,
                    TargetPrice  = entryPrice - stopDistance,
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
