#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    // =========================================================================
    // SMF_NATIVE — Core logic for Smart Money Flow Cloud (Pattern B).
    //
    // This file implements the "Engine" and "Signals" for the native SMF logic.
    //
    // Components:
    //   1. SMFNativeEngine: Stateful per-bar cloud calculator.
    //   2. SMF_Native_Impulse: Signal fired on cloud color flip.
    //   3. SMF_Native_BandReclaim: Signal fired on mean-reversion touch.
    //   4. SMF_Native_Retest: Signal fired on pullback to cloud basis.
    // =========================================================================

    public sealed class SMFNativeEngine
    {
        private double _prevBasis = double.NaN;
        private double _prevSignal = 0.0;
        private int    _warmupBars = 0;
        private const int WARMUP_REQUIRED = 34;

        private int    _lastBarIndex = -1;
        private double _cachedBasis;
        private double _cachedSignal;

        public void Reset()
        {
            _prevBasis = double.NaN;
            _prevSignal = 0.0;
            _warmupBars = 0;
            _lastBarIndex = -1;
        }

        public void Update(MarketSnapshot snap, out double basis, out double signal)
        {
            basis = 0.0;
            signal = 0.0;
            if (!snap.IsValid) return;

            int barIndex = snap.Primary.CurrentBar;
            if (barIndex == _lastBarIndex)
            {
                basis  = _cachedBasis;
                signal = _cachedSignal;
                return;
            }
            _lastBarIndex = barIndex;

            var p = snap.Primary;
            double close = p.Close;

            // Simple ZLEMA basis (34 period)
            if (double.IsNaN(_prevBasis))
            {
                _prevBasis = close;
                _warmupBars = 1;
            }
            else
            {
                // Proxy for ZLEMA lag correction: (close + (close - prev))
                double val = close + (close - _prevBasis);
                double alpha = 2.0 / (34.0 + 1.0);
                basis = alpha * val + (1.0 - alpha) * _prevBasis;
                _prevBasis = basis;
                _warmupBars++;
            }

            if (_warmupBars < WARMUP_REQUIRED) { _cachedBasis = basis; _cachedSignal = signal; return; }

            // Signal logic (Simplified SMF Cloud bias)
            if (close > basis)      signal = 1.0;  // Bullish
            else if (close < basis) signal = -1.0; // Bearish
            else                    signal = _prevSignal;

            _prevSignal = signal;
            _cachedBasis  = basis;
            _cachedSignal = signal;
        }
    }

    // ── BASE CLASS ────────────────────────────────────────────────────────

    public abstract class SMFNativeBase : IConditionSet
    {
        public abstract string SetId { get; }
        public string LastDiagnostic => "";

        protected readonly SMFNativeEngine _engine;
        protected double _tickSize;
        protected double _tickValue;
        protected int    _lastSignalBar = -1;

        public SMFNativeBase(SMFNativeEngine engine)
        {
            _engine = engine;
        }

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _lastSignalBar = -1;
            _engine.Reset();
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
                _lastSignalBar = signal.BarIndex;
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        public abstract RawDecision Evaluate(MarketSnapshot snapshot);

        protected static bool IsRTH(BarSnapshot p)
        {
            return p.Session != SessionPhase.PreMarket && p.Session != SessionPhase.AfterHours;
        }
    }

    // ── IMPULSE ───────────────────────────────────────────────────────────

    public class SMF_Native_Impulse : SMFNativeBase
    {
        public override string SetId => "SMF_Native_Impulse_v1";
        private double _prevSignal = 0.0;

        public SMF_Native_Impulse(SMFNativeEngine engine) : base(engine) { }

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            double basis, signal;
            _engine.Update(snapshot, out basis, out signal);

            if (signal != 0 && signal != _prevSignal && IsRTH(snapshot.Primary))
            {
                _prevSignal = signal;
                double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;
                
                return new RawDecision
                {
                    Direction = signal > 0 ? SignalDirection.Long : SignalDirection.Short,
                    Source    = SignalSource.SMF_Impulse,
                    EntryPrice = snapshot.Primary.Close,
                    StopPrice  = signal > 0 ? basis - atr * 0.5 : basis + atr * 0.5,
                    TargetPrice = signal > 0 ? snapshot.Primary.Close + atr * 1.5 : snapshot.Primary.Close - atr * 1.5,
                    Target2Price = signal > 0 ? snapshot.Primary.Close + atr * 3.0 : snapshot.Primary.Close - atr * 3.0,
                    Label     = $"SMF[N] Impulse {(signal > 0 ? "bull" : "bear")}",
                    RawScore  = 72,
                    IsValid   = true,
                    SignalId  = $"{SetId}:{snapshot.Primary.CurrentBar}"
                };
            }
            _prevSignal = signal;
            return RawDecision.None;
        }
    }

    // ── BAND RECLAIM ──────────────────────────────────────────────────────

    public class SMF_Native_BandReclaim : SMFNativeBase
    {
        public override string SetId => "SMF_Native_BandReclaim_v1";
        public SMF_Native_BandReclaim(SMFNativeEngine engine) : base(engine) { }

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            double basis, signal;
            _engine.Update(snapshot, out basis, out signal);

            var p = snapshot.Primary;
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;
            double upper = basis + atr * 2.0;
            double lower = basis - atr * 2.0;

            // Bullish reclaim: Price was below lower band, now closes above it
            if (p.Low < lower && p.Close > lower && signal > 0 && IsRTH(p))
            {
                return new RawDecision
                {
                    Direction = SignalDirection.Long,
                    Source    = SignalSource.SMF_BandReclaim,
                    EntryPrice = p.Close,
                    StopPrice  = p.Low - _tickSize * 2,
                    TargetPrice = basis,
                    Target2Price = upper,
                    Label     = "SMF[N] Band Reclaim bull",
                    RawScore  = 68,
                    IsValid   = true,
                    SignalId  = $"{SetId}:Bull:{p.CurrentBar}"
                };
            }

            // Bearish reclaim: Price was above upper band, now closes below it
            if (p.High > upper && p.Close < upper && signal < 0 && IsRTH(p))
            {
                return new RawDecision
                {
                    Direction = SignalDirection.Short,
                    Source    = SignalSource.SMF_BandReclaim,
                    EntryPrice = p.Close,
                    StopPrice  = p.High + _tickSize * 2,
                    TargetPrice = basis,
                    Target2Price = lower,
                    Label     = "SMF[N] Band Reclaim bear",
                    RawScore  = 68,
                    IsValid   = true,
                    SignalId  = $"{SetId}:Bear:{p.CurrentBar}"
                };
            }

            return RawDecision.None;
        }
    }

    // ── RETEST ────────────────────────────────────────────────────────────

    public class SMF_Native_Retest : SMFNativeBase
    {
        public override string SetId => "SMF_Native_Retest_v1";
        public SMF_Native_Retest(SMFNativeEngine engine) : base(engine) { }

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            double basis, signal;
            _engine.Update(snapshot, out basis, out signal);

            var p = snapshot.Primary;
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;

            // Bullish Retest: Price pulls back to basis while signal is bullish
            if (signal > 0 && p.Low <= basis + _tickSize * 2 && p.Close > basis && IsRTH(p))
            {
                return new RawDecision
                {
                    Direction = SignalDirection.Long,
                    Source    = SignalSource.SMF_Retest,
                    EntryPrice = p.Close,
                    StopPrice  = basis - atr * 0.5,
                    TargetPrice = p.Close + atr * 1.5,
                    Target2Price = p.Close + atr * 3.0,
                    Label     = "SMF[N] Retest bull",
                    RawScore  = 70,
                    IsValid   = true,
                    SignalId  = $"{SetId}:Bull:{p.CurrentBar}"
                };
            }

            // Bearish Retest: Price pulls back to basis while signal is bearish
            if (signal < 0 && p.High >= basis - _tickSize * 2 && p.Close < basis && IsRTH(p))
            {
                return new RawDecision
                {
                    Direction = SignalDirection.Short,
                    Source    = SignalSource.SMF_Retest,
                    EntryPrice = p.Close,
                    StopPrice  = basis + atr * 0.5,
                    TargetPrice = p.Close - atr * 1.5,
                    Target2Price = p.Close - atr * 3.0,
                    Label     = "SMF[N] Retest bear",
                    RawScore  = 70,
                    IsValid   = true,
                    SignalId  = $"{SetId}:Bear:{p.CurrentBar}"
                };
            }

            return RawDecision.None;
        }
    }
}
