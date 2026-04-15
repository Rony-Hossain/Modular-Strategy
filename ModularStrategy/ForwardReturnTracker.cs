using System;
using System.Collections.Generic;
using MathLogic.Strategy;
using NinjaTrader.NinjaScript.Strategies;

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    public sealed class ForwardReturnTracker
    {
        private readonly StrategyLogger _log;
        private readonly int _windowBars;
        private readonly double _pointValue;
        private readonly Dictionary<string, ActiveTouch> _active;

        private sealed class ActiveTouch
        {
            public string SignalId;
            public string ConditionSetId;
            public SignalDirection Direction;
            public double EntryPrice;
            public double StopPrice;
            public double TargetPrice;
            public int StartBar;
            public DateTime StartTime;
            public DateTime SessionDate;      // StartTime.Date — used to detect session rollover
            public double MfeDollars;          // running max favorable, in DOLLARS (point-adjusted)
            public double MaeDollars;          // running max adverse, in DOLLARS (positive number)
            public int BarsElapsed;            // increments per OnBar, excluding the start bar
        }

        public ForwardReturnTracker(StrategyLogger log, int windowBars, double pointValue)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            if (windowBars <= 0) throw new ArgumentOutOfRangeException(nameof(windowBars));
            if (pointValue <= 0) throw new ArgumentOutOfRangeException(nameof(pointValue));

            _windowBars = windowBars;
            _pointValue = pointValue;
            _active = new Dictionary<string, ActiveTouch>();
        }

        public void Register(string signalId, string conditionSetId, SignalDirection dir, double entry, double stop, double target, int startBar, DateTime startTime)
        {
            // No-op guards — do NOT throw, just return:
            if (string.IsNullOrEmpty(signalId)) return;
            if (stop <= 0 || target <= 0) return;       // cannot simulate
            if (_active.ContainsKey(signalId)) return;  // already tracking

            _active[signalId] = new ActiveTouch {
                SignalId       = signalId,
                ConditionSetId = conditionSetId,
                Direction      = dir,
                EntryPrice     = entry,
                StopPrice      = stop,
                TargetPrice    = target,
                StartBar       = startBar,
                StartTime      = startTime,
                SessionDate    = startTime.Date,
                MfeDollars     = 0.0,
                MaeDollars     = 0.0,
                BarsElapsed    = 0
            };
        }

        public void OnBar(double high, double low, double close, DateTime time, int currentBar, bool isNewSession)
        {
            if (_active.Count == 0) return;

            // Collect keys to remove AFTER iteration — cannot mutate dictionary during enumeration.
            List<string> toFinalize = null;

            foreach (var kv in _active)
            {
                var t = kv.Value;

                // Skip the bar on which the touch was registered — entry
                // is assumed at that bar's close; forward tracking begins
                // on the NEXT bar.
                if (t.StartBar == currentBar) continue;

                t.BarsElapsed++;

                // Update MFE/MAE in DOLLARS. For longs, favorable = high
                // above entry; adverse = low below entry. Inverted for
                // shorts.
                double favorablePts, adversePts;
                if (t.Direction == SignalDirection.Long)
                {
                    favorablePts = Math.Max(0.0, high - t.EntryPrice);
                    adversePts   = Math.Max(0.0, t.EntryPrice - low);
                }
                else
                {
                    favorablePts = Math.Max(0.0, t.EntryPrice - low);
                    adversePts   = Math.Max(0.0, high - t.EntryPrice);
                }

                double favDollars = favorablePts * _pointValue;
                double advDollars = adversePts   * _pointValue;
                if (favDollars > t.MfeDollars) t.MfeDollars = favDollars;
                if (advDollars > t.MaeDollars) t.MaeDollars = advDollars;

                // Detect stop/target hits on THIS bar.
                bool stopHit, targetHit;
                if (t.Direction == SignalDirection.Long)
                {
                    stopHit   = low  <= t.StopPrice;
                    targetHit = high >= t.TargetPrice;
                }
                else
                {
                    stopHit   = high >= t.StopPrice;
                    targetHit = low  <= t.TargetPrice;
                }

                string firstHit = null;
                double exitPrice = 0.0;
                if (stopHit && targetHit) {
                    firstHit  = "BOTH_SAMEBAR";
                    exitPrice = t.StopPrice;   // conservative — assume stop
                } else if (stopHit) {
                    firstHit  = "STOP";
                    exitPrice = t.StopPrice;
                } else if (targetHit) {
                    firstHit  = "TARGET";
                    exitPrice = t.TargetPrice;
                }

                bool expiredByBars    = t.BarsElapsed >= _windowBars;
                bool expiredBySession = isNewSession && time.Date != t.SessionDate;

                if (firstHit != null || expiredByBars || expiredBySession)
                {
                    // Compute SimPnL, bars-to-hit, closeAtWindowEnd.
                    int hitStop   = firstHit == "STOP"   || firstHit == "BOTH_SAMEBAR" ? 1 : 0;
                    int hitTarget = firstHit == "TARGET"                               ? 1 : 0;
                    double simPnL;
                    int barsToHit;
                    double closeEnd = close;

                    if (firstHit != null) {
                        double ptsMove = t.Direction == SignalDirection.Long
                                       ? exitPrice - t.EntryPrice
                                       : t.EntryPrice - exitPrice;
                        simPnL    = ptsMove * _pointValue;
                        barsToHit = t.BarsElapsed;
                    } else {
                        // NEITHER — close at window-end (or session end).
                        double ptsMove = t.Direction == SignalDirection.Long
                                       ? close - t.EntryPrice
                                       : t.EntryPrice - close;
                        simPnL    = ptsMove * _pointValue;
                        barsToHit = 0;
                        firstHit  = "NEITHER";
                    }

                    _log.LogTouchOutcome(
                        t.SignalId, t.ConditionSetId, t.Direction,
                        t.MfeDollars, t.MaeDollars,
                        hitStop, hitTarget,
                        firstHit, simPnL, barsToHit,
                        closeEnd, t.BarsElapsed, time);

                    if (toFinalize == null) toFinalize = new List<string>();
                    toFinalize.Add(t.SignalId);
                }
            }

            if (toFinalize != null)
                for (int i = 0; i < toFinalize.Count; i++)
                    _active.Remove(toFinalize[i]);
        }

        public void Reset() { _active.Clear(); }

        public int ActiveCount { get { return _active.Count; } }
    }
}
