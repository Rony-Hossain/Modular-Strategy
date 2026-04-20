#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    public class DeltaDivergenceSignal : IConditionSet
    {
        public string SetId => "DeltaDivergence_v1";

        private const double MIN_ABSORPTION_SCORE = StrategyConfig.Modules.DD_MIN_ABSORPTION_SCORE;
        private const double ATR_STOP_BUFFER = StrategyConfig.Modules.DD_ATR_STOP_BUFFER;
        private const double MIN_STOP_ATR_MULT = StrategyConfig.Modules.DD_MIN_STOP_ATR_MULT;
        private const int STOP_LOOKBACK_BARS = StrategyConfig.Modules.DD_STOP_LOOKBACK_BARS;
        private const int REENTRY_COOLDOWN = StrategyConfig.Modules.DD_COOLDOWN_BARS;

        private double _tickSize;
        private double _tickValue;
        private int    _lastFillBar = -1;
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

            var    p       = snapshot.Primary;
            double atr     = snapshot.ATR;
            double tickSz  = p.TickSize > 0 ? p.TickSize : _tickSize;

            if (atr <= 0)
            { _lastBailReason = "atr_zero"; return RawDecision.None; }

            if (_lastFillBar >= 0 && p.CurrentBar - _lastFillBar < REENTRY_COOLDOWN)
            { _lastBailReason = "cooldown"; return RawDecision.None; }

            bool hasVol = snapshot.GetFlag(SnapKeys.HasVolumetric);
            if (!hasVol)
            { _lastBailReason = "no_volumetric"; return RawDecision.None; }

            bool bullDiv = snapshot.GetFlag(SnapKeys.BullDivergence);
            bool bearDiv = snapshot.GetFlag(SnapKeys.BearDivergence);

            if (!bullDiv && !bearDiv)
            { _lastBailReason = "no_divergence"; return RawDecision.None; }

            if (p.BarDeltas == null || p.BarDeltas.Length < 2)
            { _lastBailReason = "delta_array_short"; return RawDecision.None; }

            double deltaNow  = p.BarDeltas[0];
            double deltaPrev = p.BarDeltas[1];

            SignalDirection direction = SignalDirection.None;

            if (bullDiv && deltaNow > 0 && deltaPrev <= 0)
                direction = SignalDirection.Long;
            else if (bearDiv && deltaNow < 0 && deltaPrev >= 0)
                direction = SignalDirection.Short;

            if (direction == SignalDirection.None)
            { _lastBailReason = $"no_delta_flip (dNow={deltaNow:F0} dPrev={deltaPrev:F0} bull={bullDiv} bear={bearDiv})"; return RawDecision.None; }

            bool isLong = direction == SignalDirection.Long;

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
                    for (int i = 1; i < StrategyConfig.Modules.DD_STOP_LOOKBACK_BARS && i < p.Lows.Length; i++)
                        if (p.Lows[i] < recentLow) recentLow = p.Lows[i];

                stopPrice = recentLow - ATR_STOP_BUFFER * atr;

                double minStop = p.Close - MIN_STOP_ATR_MULT * atr;
                if (stopPrice > minStop) stopPrice = minStop;
            }
            else
            {
                double recentHigh = p.High;
                if (p.Highs != null)
                    for (int i = 1; i < StrategyConfig.Modules.DD_STOP_LOOKBACK_BARS && i < p.Highs.Length; i++)
                        if (p.Highs[i] > recentHigh) recentHigh = p.Highs[i];

                stopPrice = recentHigh + ATR_STOP_BUFFER * atr;

                double minStop = p.Close + MIN_STOP_ATR_MULT * atr;
                if (stopPrice < minStop) stopPrice = minStop;
            }

            if (isLong  && stopPrice >= p.Close)
            { _lastBailReason = "stop_above_entry"; return RawDecision.None; }
            if (!isLong && stopPrice <= p.Close)
            { _lastBailReason = "stop_below_entry"; return RawDecision.None; }

            double t1Price;
            double t2Price;

            double vwap         = snapshot.VWAP;
            double lastSwingHigh = snapshot.Get(SnapKeys.LastSwingHigh);
            double lastSwingLow  = snapshot.Get(SnapKeys.LastSwingLow);

            if (isLong)
            {
                t2Price = p.Close + 2.5 * atr;

                if (vwap > p.Close + 0.5 * atr)
                    t1Price = vwap;
                else if (lastSwingHigh > p.Close + 0.5 * atr && lastSwingHigh < p.Close + 3.0 * atr)
                    t1Price = lastSwingHigh;
                else
                    t1Price = p.Close + 1.5 * atr;
            }
            else
            {
                t2Price = p.Close - 2.5 * atr;

                if (vwap > 0 && vwap < p.Close - 0.5 * atr)
                    t1Price = vwap;
                else if (lastSwingLow > 0 && lastSwingLow < p.Close - 0.5 * atr && lastSwingLow > p.Close - 3.0 * atr)
                    t1Price = lastSwingLow;
                else
                    t1Price = p.Close - 1.5 * atr;
            }

            double riskTicks   = Math.Abs(p.Close - stopPrice) / tickSz;
            double rewardTicks = Math.Abs(t1Price  - p.Close)  / tickSz;
            if (riskTicks > 0 && rewardTicks / riskTicks < 1.2)
            { _lastBailReason = $"rr_insufficient ({rewardTicks:F1}/{riskTicks:F1}={rewardTicks/riskTicks:F2})"; return RawDecision.None; }

            int score = StrategyConfig.Modules.DD_BASE_SCORE;

            if (isLong  && snapshot.GetFlag(SnapKeys.ImbalZoneAtBull)) score += 6;
            if (!isLong && snapshot.GetFlag(SnapKeys.ImbalZoneAtBear))  score += 6;

            if (isLong  && snapshot.GetFlag(SnapKeys.TrappedShorts)) score += 5;
            if (!isLong && snapshot.GetFlag(SnapKeys.TrappedLongs))  score += 5;

            if (isLong  && snapshot.GetFlag(SnapKeys.BearExhaustion)) score += 4;
            if (!isLong && snapshot.GetFlag(SnapKeys.BullExhaustion)) score += 4;

            if (isLong  && snapshot.GetFlag(SnapKeys.UnfinishedTop))    score += 4;
            if (!isLong && snapshot.GetFlag(SnapKeys.UnfinishedBottom)) score += 4;

            if (isLong  && snapshot.GetFlag(SnapKeys.TapeBullIceberg)) score += 3;
            if (!isLong && snapshot.GetFlag(SnapKeys.TapeBearIceberg)) score += 3;

            score = Math.Min(score, 95);

            _lastBailReason = "FIRED_" + (isLong ? "LONG" : "SHORT");

            return new RawDecision
            {
                Direction      = direction,
                Source         = SignalSource.OrderFlow_Delta,
                ConditionSetId = SetId,
                EntryPrice     = p.Close,
                StopPrice      = stopPrice,
                TargetPrice    = t1Price,
                Target2Price   = t2Price,
                Label          = BuildLabel(isLong, absScore, deltaNow, deltaPrev),
                RawScore       = score,
                IsValid        = true,
                BarIndex       = p.CurrentBar,
                SignalId       = string.Format("{0}:{1:yyyyMMdd}:{2}", SetId, p.Time, p.CurrentBar)
            };
        }

        private string BuildLabel(bool isLong, double absScore, double dNow, double dPrev)
        {
            return string.Format(
                "DeltaDiv {0} abs={1:F0} flip={2:F0}->{3:F0} [{4}]",
                isLong ? "long" : "short",
                absScore,
                dPrev, dNow,
                SetId);
        }
    }
}
