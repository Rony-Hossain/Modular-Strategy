using System;
using System.Collections.Generic;
using System.Text;
using MathLogic;
using MathLogic.Strategy;
using NinjaTrader.NinjaScript.Strategies;

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    public sealed class ForwardReturnTracker
    {
        private readonly StrategyLogger _log;
        private readonly int            _windowBars;
        private readonly double         _pointValue;
        private readonly Dictionary<string, ActiveTouch> _active;

        // Pre/post bar capture window — must match StrategyLogger.CONTEXT_BARS
        private const int BAR_WINDOW = 5;

        // Single reusable builder for BuildEnrichedDetail (NT8 is single-threaded).
        private readonly StringBuilder _esb = new StringBuilder(1024);

        private sealed class ActiveTouch
        {
            public string          SignalId;
            public string          ConditionSetId;
            public SignalDirection Direction;
            public double          EntryPrice;
            public double          StopPrice;
            public double          TargetPrice;
            public int             StartBar;
            public DateTime        StartTime;
            public DateTime        SessionDate;
            public double          MfeDollars;
            public double          MaeDollars;
            public int             BarsElapsed;

            // ── Gate reason ("" for accepted signals) ────────────────────────
            public string GateReason;

            // ── Footprint snapshot frozen at registration time ───────────────
            public bool   SnapValid;
            public double SnapBd, SnapCd, SnapAbs, SnapSbull, SnapSbear;
            public double SnapH1, SnapH2, SnapH4;
            public double SnapRegime, SnapStrength, SnapDex;
            public int    SnapBullDiv, SnapBearDiv, SnapHasVol;
            public double SnapPoc, SnapVah, SnapVal;
            public double SnapSwings, SnapTrend, SnapAtr;

            // ── Pre-signal bars: [0]=5 bars ago (oldest), [4]=1 bar ago ──────
            public readonly double[] PreO = new double[BAR_WINDOW];
            public readonly double[] PreH = new double[BAR_WINDOW];
            public readonly double[] PreL = new double[BAR_WINDOW];
            public readonly double[] PreC = new double[BAR_WINDOW];
            public readonly double[] PreV = new double[BAR_WINDOW];
            public readonly double[] PreD = new double[BAR_WINDOW];

            // ── Post-signal bars: [0]=+1 bar, [4]=+5 bars ───────────────────
            public readonly double[] PostO = new double[BAR_WINDOW];
            public readonly double[] PostH = new double[BAR_WINDOW];
            public readonly double[] PostL = new double[BAR_WINDOW];
            public readonly double[] PostC = new double[BAR_WINDOW];
            public readonly double[] PostV = new double[BAR_WINDOW];
            public readonly double[] PostD = new double[BAR_WINDOW];
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

        /// <summary>
        /// Register a signal for forward-return tracking.
        /// Pass <paramref name="snap"/> to freeze the footprint context at signal time.
        /// Pass <paramref name="gateReason"/> for rejected signals (gate that blocked entry).
        /// </summary>
        public void Register(
            string signalId, string conditionSetId, SignalDirection dir,
            double entry, double stop, double target,
            int startBar, DateTime startTime,
            MarketSnapshot snap = default, string gateReason = "")
        {
            if (string.IsNullOrEmpty(signalId)) return;
            if (stop <= 0 || target <= 0)       return;
            if (_active.ContainsKey(signalId))  return;

            var t = new ActiveTouch {
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
                BarsElapsed    = 0,
                GateReason     = gateReason ?? ""
            };

            // Freeze snapshot + pre-bars if a valid snapshot was provided.
            if (snap.IsValid)
            {
                var p = snap.Primary;
                t.SnapValid    = true;
                t.SnapBd       = snap.Get(SnapKeys.BarDelta);
                t.SnapCd       = snap.Get(SnapKeys.CumDelta);
                t.SnapAbs      = snap.Get(SnapKeys.AbsorptionScore);
                t.SnapSbull    = snap.Get(SnapKeys.StackedImbalanceBull);
                t.SnapSbear    = snap.Get(SnapKeys.StackedImbalanceBear);
                t.SnapH1       = snap.Get(SnapKeys.H1EmaBias);
                t.SnapH2       = snap.Get(SnapKeys.H2HrEmaBias);
                t.SnapH4       = snap.Get(SnapKeys.H4HrEmaBias);
                t.SnapRegime   = snap.Get(SnapKeys.Regime);
                t.SnapStrength = snap.Get(SnapKeys.Strength);
                t.SnapDex      = snap.Get(SnapKeys.DeltaExhaustion);
                t.SnapBullDiv  = snap.GetFlag(SnapKeys.BullDivergence) ? 1 : 0;
                t.SnapBearDiv  = snap.GetFlag(SnapKeys.BearDivergence) ? 1 : 0;
                t.SnapHasVol   = snap.GetFlag(SnapKeys.HasVolumetric)  ? 1 : 0;
                t.SnapPoc      = snap.Get(SnapKeys.POC);
                t.SnapVah      = snap.Get(SnapKeys.VAHigh);
                t.SnapVal      = snap.Get(SnapKeys.VALow);
                t.SnapSwings   = snap.Get(SnapKeys.ConfirmedSwings);
                t.SnapTrend    = snap.Get(SnapKeys.SwingTrend);
                t.SnapAtr      = snap.ATR;

                // Pre-bars: oldest first (src 5→1 maps to index 0→4)
                for (int i = 0; i < BAR_WINDOW; i++)
                {
                    int src = BAR_WINDOW - i; // 5, 4, 3, 2, 1
                    t.PreO[i] = SafeGet(p.Opens,     src);
                    t.PreH[i] = SafeGet(p.Highs,     src);
                    t.PreL[i] = SafeGet(p.Lows,      src);
                    t.PreC[i] = SafeGet(p.Closes,    src);
                    t.PreV[i] = SafeGet(p.Volumes,   src);
                    t.PreD[i] = SafeGet(p.BarDeltas, src);
                }
            }

            _active[signalId] = t;
        }

        /// <summary>
        /// Call once per primary bar from HostStrategy.OnBarUpdate.
        /// Collects post-signal OHLCV bars, updates MFE/MAE, detects stop/target hits.
        /// </summary>
        public void OnBar(
            double open, double high, double low, double close,
            double volume, double barDelta,
            DateTime time, int currentBar, bool isNewSession)
        {
            if (_active.Count == 0) return;

            List<string> toFinalize = null;

            foreach (var kv in _active)
            {
                var t = kv.Value;

                // Skip the registration bar — tracking starts on the next bar.
                if (t.StartBar == currentBar) continue;

                t.BarsElapsed++;

                // Collect post-signal bars (slots 0..4 = bars +1..+5 after signal)
                int barsAfter = currentBar - t.StartBar;
                if (barsAfter >= 1 && barsAfter <= BAR_WINDOW)
                {
                    int pi = barsAfter - 1;
                    t.PostO[pi] = open;
                    t.PostH[pi] = high;
                    t.PostL[pi] = low;
                    t.PostC[pi] = close;
                    t.PostV[pi] = volume;
                    t.PostD[pi] = barDelta;
                }

                // MFE/MAE in dollars
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

                // Stop/target detection
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

                string firstHit  = null;
                double exitPrice = 0.0;
                if (stopHit && targetHit) {
                    firstHit  = "BOTH_SAMEBAR";
                    exitPrice = t.StopPrice;
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
                    int    hitStop   = firstHit == "STOP" || firstHit == "BOTH_SAMEBAR" ? 1 : 0;
                    int    hitTarget = firstHit == "TARGET"                             ? 1 : 0;
                    double simPnL;
                    int    barsToHit;
                    double closeEnd  = close;

                    if (firstHit != null) {
                        double ptsMove = t.Direction == SignalDirection.Long
                                       ? exitPrice - t.EntryPrice
                                       : t.EntryPrice - exitPrice;
                        simPnL    = ptsMove * _pointValue;
                        barsToHit = t.BarsElapsed;
                    } else {
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
                        closeEnd, t.BarsElapsed, time,
                        BuildEnrichedDetail(t));

                    if (toFinalize == null) toFinalize = new List<string>();
                    toFinalize.Add(t.SignalId);
                }
            }

            if (toFinalize != null)
                for (int i = 0; i < toFinalize.Count; i++)
                    _active.Remove(toFinalize[i]);
        }

        public void Reset() { _active.Clear(); }

        /// <summary>
        /// Expires all remaining active entries as TERMINATED.
        /// Call at strategy termination so no tracked signals are silently dropped.
        /// </summary>
        public void FlushRemaining(DateTime time, int currentBar)
        {
            if (_active.Count == 0) return;

            var keys = new List<string>(_active.Keys);
            foreach (var key in keys)
            {
                var t = _active[key];
                if (t.StartBar == currentBar) continue;

                _log.LogTouchOutcome(
                    t.SignalId, t.ConditionSetId, t.Direction,
                    t.MfeDollars, t.MaeDollars,
                    0, 0,
                    "TERMINATED", 0.0, 0,
                    t.EntryPrice, t.BarsElapsed, time,
                    BuildEnrichedDetail(t));
            }
            _active.Clear();
        }

        public int ActiveCount { get { return _active.Count; } }

        // ── Private helpers ────────────────────────────────────────────────────

        private static double SafeGet(double[] arr, int i) =>
            arr != null && i < arr.Length ? arr[i] : 0.0;

        /// <summary>
        /// Builds the enriched detail string that is appended to TOUCH_OUTCOME.
        /// Contains: gate reason, frozen footprint snapshot, 5 pre-bars, 5 post-bars.
        /// </summary>
        private string BuildEnrichedDetail(ActiveTouch t)
        {
            _esb.Clear();

            // Gate reason
            if (!string.IsNullOrEmpty(t.GateReason))
                _esb.AppendFormat("GATE={0} | ", t.GateReason);

            // Frozen footprint snapshot
            if (t.SnapValid)
            {
                string reg = t.SnapRegime > 0 ? "+1" : t.SnapRegime < 0 ? "-1" : "0";
                string dex = t.SnapDex   > 0 ? "+1" : t.SnapDex   < 0 ? "-1" : "0";
                string trd = t.SnapTrend > 0 ? "+1" : t.SnapTrend < 0 ? "-1" : "0";

                _esb.AppendFormat(
                    "SNAP_BD={0:F0} SNAP_CD={1:F0} SNAP_ABS={2:F1} SNAP_SBULL={3:F0} SNAP_SBEAR={4:F0} | " +
                    "SNAP_H1={5:F2} SNAP_H2={6:F2} SNAP_H4={7:F2} | " +
                    "SNAP_REG={8} SNAP_STR={9:F2} SNAP_DEX={10} | " +
                    "SNAP_BDIV={11} SNAP_BERDIV={12} | " +
                    "SNAP_POC={13:F2} SNAP_VAH={14:F2} SNAP_VAL={15:F2} | " +
                    "SNAP_SW={16:F0} SNAP_TRD={17} SNAP_ATR={18:F2} SNAP_HASVOL={19} | ",
                    t.SnapBd, t.SnapCd, t.SnapAbs, t.SnapSbull, t.SnapSbear,
                    t.SnapH1, t.SnapH2, t.SnapH4,
                    reg, t.SnapStrength, dex,
                    t.SnapBullDiv, t.SnapBearDiv,
                    t.SnapPoc, t.SnapVah, t.SnapVal,
                    t.SnapSwings, trd, t.SnapAtr, t.SnapHasVol);

                // Pre-signal bars (oldest → newest)
                _esb.Append("PRE:");
                for (int i = 0; i < BAR_WINDOW; i++)
                {
                    if (i > 0) _esb.Append('|');
                    _esb.AppendFormat("O:{0:F2} H:{1:F2} L:{2:F2} C:{3:F2} V:{4:F0}",
                        t.PreO[i], t.PreH[i], t.PreL[i], t.PreC[i], t.PreV[i]);
                    if (t.PreD[i] != 0) _esb.AppendFormat(" D:{0:F0}", t.PreD[i]);
                }
                _esb.Append(" | ");
            }

            // Post-signal bars (+1 → +5)
            _esb.Append("POST:");
            for (int i = 0; i < BAR_WINDOW; i++)
            {
                if (i > 0) _esb.Append('|');
                _esb.AppendFormat("O:{0:F2} H:{1:F2} L:{2:F2} C:{3:F2} V:{4:F0}",
                    t.PostO[i], t.PostH[i], t.PostL[i], t.PostC[i], t.PostV[i]);
                if (t.PostD[i] != 0) _esb.AppendFormat(" D:{0:F0}", t.PostD[i]);
            }

            return _esb.ToString();
        }
    }
}
