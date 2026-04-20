#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    public class ImbalanceReAggressionSignal : IConditionSet
    {
        public string SetId => "ImbalanceReAggression_v1";

        private const double MIN_ABSORPTION_SCORE  = 5.0;
        private const double HIGH_ABSORPTION_SCORE = 10.0;
        private const double ATR_STOP_BUFFER = 0.06;
        private const double MIN_STOP_ATR_MULT = 0.18;
        private const int STOP_LOOKBACK_BARS = 3;
        private const int REENTRY_COOLDOWN = 8;
        private const double MAX_T1_ATR_DIST = 3.0;
        private const double MIN_T1_ATR_DIST = 0.5;

        private double _tickSize;
        private double _tickValue;
        private int    _lastFillBar    = -1;
        private string _lastBailReason = "";

        public string LastDiagnostic => _lastBailReason;

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _lastFillBar    = -1;
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
            if (!snapshot.IsValid)
            { _lastBailReason = "snapshot_invalid"; return RawDecision.None; }

            var    p      = snapshot.Primary;
            double atr    = snapshot.ATR;
            double tickSz = p.TickSize > 0 ? p.TickSize : _tickSize;

            if (atr <= 0)
            { _lastBailReason = "atr_zero"; return RawDecision.None; }

            if (_lastFillBar >= 0 && p.CurrentBar - _lastFillBar < REENTRY_COOLDOWN)
            { _lastBailReason = "cooldown"; return RawDecision.None; }

            if (!snapshot.GetFlag(SnapKeys.HasVolumetric))
            { _lastBailReason = "no_volumetric"; return RawDecision.None; }

            bool atBullZone = snapshot.GetFlag(SnapKeys.ImbalZoneAtBull);
            bool atBearZone = snapshot.GetFlag(SnapKeys.ImbalZoneAtBear);

            if (!atBullZone && !atBearZone)
            { _lastBailReason = "no_zone_proximity"; return RawDecision.None; }

            double barDelta = snapshot.Get(SnapKeys.BarDelta);

            SignalDirection direction = SignalDirection.None;

            if (atBullZone && barDelta > 0)
                direction = SignalDirection.Long;
            else if (atBearZone && barDelta < 0)
                direction = SignalDirection.Short;

            if (direction == SignalDirection.None)
            { _lastBailReason = $"delta_wrong_side (bd={barDelta:F0} bull={atBullZone} bear={atBearZone})"; return RawDecision.None; }

            bool isLong = direction == SignalDirection.Long;

            double barMid = (p.High + p.Low) / 2.0;
            if (isLong  && p.Close <= barMid)
            { _lastBailReason = $"close_weak_for_long (close={p.Close:F2} mid={barMid:F2})"; return RawDecision.None; }
            if (!isLong && p.Close >= barMid)
            { _lastBailReason = $"close_weak_for_short (close={p.Close:F2} mid={barMid:F2})"; return RawDecision.None; }

            if (isLong  && snapshot.GetFlag(SnapKeys.BearDivergence))
            { _lastBailReason = "veto_bear_divergence"; return RawDecision.None; }
            if (!isLong && snapshot.GetFlag(SnapKeys.BullDivergence))
            { _lastBailReason = "veto_bull_divergence"; return RawDecision.None; }

            if (isLong  && snapshot.GetFlag(SnapKeys.SellSweep))
            { _lastBailReason = "veto_sell_sweep"; return RawDecision.None; }
            if (!isLong && snapshot.GetFlag(SnapKeys.BuySweep))
            { _lastBailReason = "veto_buy_sweep"; return RawDecision.None; }

            double absScore = snapshot.Get(SnapKeys.AbsorptionScore);
            if (absScore < MIN_ABSORPTION_SCORE)
            { _lastBailReason = $"absorption_low ({absScore:F1}<{MIN_ABSORPTION_SCORE})"; return RawDecision.None; }

            double stopPrice;

            if (isLong)
            {
                double recentLow = p.Low;
                if (p.Lows != null)
                    for (int i = 1; i < STOP_LOOKBACK_BARS && i < p.Lows.Length; i++)
                        if (p.Lows[i] < recentLow) recentLow = p.Lows[i];

                stopPrice = recentLow - ATR_STOP_BUFFER * atr;

                double minStop = p.Close - MIN_STOP_ATR_MULT * atr;
                if (stopPrice > minStop) stopPrice = minStop;
            }
            else
            {
                double recentHigh = p.High;
                if (p.Highs != null)
                    for (int i = 1; i < STOP_LOOKBACK_BARS && i < p.Highs.Length; i++)
                        if (p.Highs[i] > recentHigh) recentHigh = p.Highs[i];

                stopPrice = recentHigh + ATR_STOP_BUFFER * atr;

                double minStop = p.Close + MIN_STOP_ATR_MULT * atr;
                if (stopPrice < minStop) stopPrice = minStop;
            }

            if (isLong  && stopPrice >= p.Close)
            { _lastBailReason = "stop_above_entry"; return RawDecision.None; }
            if (!isLong && stopPrice <= p.Close)
            { _lastBailReason = "stop_below_entry"; return RawDecision.None; }

            double lastSwingHigh = snapshot.Get(SnapKeys.LastSwingHigh);
            double lastSwingLow  = snapshot.Get(SnapKeys.LastSwingLow);
            double t1Price;
            double t2Price;

            if (isLong)
            {
                t2Price = p.Close + 2.5 * atr;

                double distToSwing = lastSwingHigh > 0 ? (lastSwingHigh - p.Close) / atr : 0.0;
                if (lastSwingHigh > 0
                    && distToSwing >= MIN_T1_ATR_DIST
                    && distToSwing <= MAX_T1_ATR_DIST)
                    t1Price = lastSwingHigh;
                else
                    t1Price = p.Close + 1.5 * atr;
            }
            else
            {
                t2Price = p.Close - 2.5 * atr;

                double distToSwing = lastSwingLow > 0 ? (p.Close - lastSwingLow) / atr : 0.0;
                if (lastSwingLow > 0
                    && distToSwing >= MIN_T1_ATR_DIST
                    && distToSwing <= MAX_T1_ATR_DIST)
                    t1Price = lastSwingLow;
                else
                    t1Price = p.Close - 1.5 * atr;
            }

            double riskTicks   = Math.Abs(p.Close - stopPrice) / tickSz;
            double rewardTicks = Math.Abs(t1Price  - p.Close)  / tickSz;
            if (riskTicks > 0 && rewardTicks / riskTicks < 1.2)
            { _lastBailReason = $"rr_low ({rewardTicks:F1}/{riskTicks:F1}={rewardTicks/riskTicks:F2})"; return RawDecision.None; }

            int score = 75;

            if (isLong  && snapshot.GetFlag(SnapKeys.BullDivergence)) score += 6;
            if (!isLong && snapshot.GetFlag(SnapKeys.BearDivergence)) score += 6;

            if (isLong  && snapshot.GetFlag(SnapKeys.TrappedShorts)) score += 5;
            if (!isLong && snapshot.GetFlag(SnapKeys.TrappedLongs))  score += 5;

            if (absScore >= HIGH_ABSORPTION_SCORE) score += 4;

            if (isLong  && snapshot.GetFlag(SnapKeys.BearExhaustion)) score += 4;
            if (!isLong && snapshot.GetFlag(SnapKeys.BullExhaustion)) score += 4;

            if (isLong  && snapshot.GetFlag(SnapKeys.TapeBullIceberg)) score += 3;
            if (!isLong && snapshot.GetFlag(SnapKeys.TapeBearIceberg)) score += 3;

            if (isLong  && snapshot.GetFlag(SnapKeys.UnfinishedBottom)) score += 3;
            if (!isLong && snapshot.GetFlag(SnapKeys.UnfinishedTop))    score += 3;

            score = Math.Min(score, 92);

            _lastBailReason = "FIRED_" + (isLong ? "LONG" : "SHORT");

            return new RawDecision
            {
                Direction      = direction,
                Source         = SignalSource.OrderFlow_StackedImbalance,
                ConditionSetId = SetId,
                EntryPrice     = p.Close,
                StopPrice      = stopPrice,
                TargetPrice    = t1Price,
                Target2Price   = t2Price,
                Label          = BuildLabel(isLong, absScore, barDelta),
                RawScore       = score,
                IsValid        = true,
                BarIndex       = p.CurrentBar,
                SignalId       = string.Format("{0}:{1:yyyyMMdd}:{2}", SetId, p.Time, p.CurrentBar)
            };
        }

        private string BuildLabel(bool isLong, double absScore, double barDelta)
        {
            return string.Format(
                "ImbalReAggr {0} abs={1:F0} bd={2:F0} [{3}]",
                isLong ? "long" : "short",
                absScore,
                barDelta,
                SetId);
        }
    }
}
