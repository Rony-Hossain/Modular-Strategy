#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    public enum FVGType { None = 0, Bullish = 1, Bearish = 2 }

    public struct FairValueGap
    {
        public FVGType Type;
        public double Upper, Lower;
        public int CreatedBar;
        public bool IsFilled;
        public bool IsValid => Type != FVGType.None && Upper > Lower;
        public double Size => Upper - Lower;
        public static readonly FairValueGap Empty = new FairValueGap { Type = FVGType.None };
    }

    public class SMC_FVG_Retest : IConditionSet
    {
        public string SetId => "SMC_FVG_v1";
        private double _tickSize, _tickValue;
        private int _lastFillBar = -1;
        private SignalDirection _lastFillDir = SignalDirection.None;
        private FairValueGap _bullFVG1 = FairValueGap.Empty, _bullFVG2 = FairValueGap.Empty;
        private FairValueGap _bearFVG1 = FairValueGap.Empty, _bearFVG2 = FairValueGap.Empty;
        private const double MIN_GAP_ATR = 0.15;
        private const int MAX_AGE_BARS = 200;

        public void Initialise(double tickSize, double tickValue) { _tickSize = tickSize; _tickValue = tickValue; }
        public void OnSessionOpen(MarketSnapshot snapshot) { }
        public void OnFill(SignalObject signal, double fillPrice) { if (signal.ConditionSetId == SetId) { _lastFillBar = signal.BarIndex; _lastFillDir = signal.Direction; } }
        public void OnClose(SignalObject signal, double exitPrice, double pnl) { _lastFillDir = SignalDirection.None; }

        public string LastDiagnostic => "";

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;
            if (p.Highs == null || p.Highs.Length < 3 || p.Lows == null || p.Lows.Length < 3) return RawDecision.None;
            if (_lastFillBar > 0 && p.CurrentBar - _lastFillBar < 5) return RawDecision.None;

            if (p.Lows[0] > p.Highs[2] + _tickSize && p.Lows[0] - p.Highs[2] > atr * MIN_GAP_ATR)
            { _bullFVG2 = _bullFVG1; _bullFVG1 = new FairValueGap { Type = FVGType.Bullish, Upper = p.Lows[0], Lower = p.Highs[2], CreatedBar = p.CurrentBar }; }
            if (p.Highs[0] < p.Lows[2] - _tickSize && p.Lows[2] - p.Highs[0] > atr * MIN_GAP_ATR)
            { _bearFVG2 = _bearFVG1; _bearFVG1 = new FairValueGap { Type = FVGType.Bearish, Upper = p.Lows[2], Lower = p.Highs[0], CreatedBar = p.CurrentBar }; }

            var r = TryBull(ref _bullFVG1, p, atr, snapshot); if (r.Direction != SignalDirection.None) return r;
            r = TryBull(ref _bullFVG2, p, atr, snapshot); if (r.Direction != SignalDirection.None) return r;
            r = TryBear(ref _bearFVG1, p, atr, snapshot); if (r.Direction != SignalDirection.None) return r;
            r = TryBear(ref _bearFVG2, p, atr, snapshot); if (r.Direction != SignalDirection.None) return r;
            return RawDecision.None;
        }

        private RawDecision TryBull(ref FairValueGap fvg, BarSnapshot p, double atr, MarketSnapshot snap)
        {
            if (!fvg.IsValid || fvg.IsFilled) return RawDecision.None;
            if (p.CurrentBar - fvg.CreatedBar > MAX_AGE_BARS) { fvg = FairValueGap.Empty; return RawDecision.None; }
            if (!(p.Low <= fvg.Upper && p.Close >= fvg.Lower)) return RawDecision.None;
            int score = Math.Min(100, 55 + (fvg.Size >= atr * 0.5 ? 5 : 0));
            if (p.Close < (p.High + p.Low) / 2.0) score -= 8; // Soft Penalty instead of hard block
            
            if (p.Low <= fvg.Lower) fvg.IsFilled = true;

            double stop = Math.Min(fvg.Lower - 2 * _tickSize, p.Close - 1.0 * atr);
            return new RawDecision
            {
                Direction = SignalDirection.Long, RawScore = score, IsValid = true,
                EntryPrice = p.Close,
                StopPrice = stop,
                TargetPrice = p.Close + fvg.Size,
                Target2Price = p.Close + atr * 2.0,
                SignalId = $"SMC_FVG_v1:{p.CurrentBar}",
                Label = $"FVG bull retest [{SetId}]",
                Source = SignalSource.SMC_OrderBlock
            };
        }

        private RawDecision TryBear(ref FairValueGap fvg, BarSnapshot p, double atr, MarketSnapshot snap)
        {
            if (!fvg.IsValid || fvg.IsFilled) return RawDecision.None;
            if (p.CurrentBar - fvg.CreatedBar > MAX_AGE_BARS) { fvg = FairValueGap.Empty; return RawDecision.None; }
            if (!(p.High >= fvg.Lower && p.Close <= fvg.Upper)) return RawDecision.None;
            int score = Math.Min(100, 55 + (fvg.Size >= atr * 0.5 ? 5 : 0));
            if (p.Close > (p.High + p.Low) / 2.0) score -= 8; // Soft Penalty
            
            if (p.High >= fvg.Upper) fvg.IsFilled = true;

            double stop = Math.Max(fvg.Upper + 2 * _tickSize, p.Close + 1.0 * atr);
            return new RawDecision
            {
                Direction = SignalDirection.Short, RawScore = score, IsValid = true,
                EntryPrice = p.Close,
                StopPrice = stop,
                TargetPrice = p.Close - fvg.Size,
                Target2Price = p.Close - atr * 2.0,
                SignalId = $"SMC_FVG_v1:{p.CurrentBar}",
                Label = $"FVG bear retest [{SetId}]",
                Source = SignalSource.SMC_OrderBlock
            };
        }
    }

    public class SMC_Liquidity_Sweep : IConditionSet
    {
        public string SetId => "SMC_LiqSweep_v1";
        public string LastDiagnostic => "";
        private double _tickSize, _tickValue;
        private int _lastFillBar = -1;
        private double _sLo1, _sLo2, _sHi1, _sHi2;
        private int _sLo1B, _sLo2B, _sHi1B, _sHi2B;

        public void Initialise(double tickSize, double tickValue) { _tickSize = tickSize; _tickValue = tickValue; }
        public void OnSessionOpen(MarketSnapshot snapshot) { _sLo1 = 0; _sLo2 = 0; _sHi1 = 0; _sHi2 = 0; _sLo1B = 0; _sLo2B = 0; _sHi1B = 0; _sHi2B = 0; }
        public void OnFill(SignalObject signal, double fillPrice) { if (signal.ConditionSetId == SetId) _lastFillBar = signal.BarIndex; }
        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;
            if (p.Lows == null || p.Lows.Length < 3) return RawDecision.None;
            if (_lastFillBar > 0 && p.CurrentBar - _lastFillBar < 5) return RawDecision.None;

            if (p.Lows.Length >= 3 && p.Lows[1] < p.Lows[0] && p.Lows[1] < p.Lows[2])
            { if (_sLo1 <= 0) { _sLo1 = p.Lows[1]; _sLo1B = p.CurrentBar - 1; } else if (p.CurrentBar - 1 - _sLo1B >= 5) { _sLo2 = _sLo1; _sLo2B = _sLo1B; _sLo1 = p.Lows[1]; _sLo1B = p.CurrentBar - 1; } }
            if (p.Highs.Length >= 3 && p.Highs[1] > p.Highs[0] && p.Highs[1] > p.Highs[2])
            { if (_sHi1 <= 0) { _sHi1 = p.Highs[1]; _sHi1B = p.CurrentBar - 1; } else if (p.CurrentBar - 1 - _sHi1B >= 5) { _sHi2 = _sHi1; _sHi2B = _sHi1B; _sHi1 = p.Highs[1]; _sHi1B = p.CurrentBar - 1; } }

            if (_sLo1 > 0 && _sLo2 > 0 && Math.Abs(_sLo1 - _sLo2) < atr * 0.3 && Math.Abs(_sLo1B - _sLo2B) <= 100)
            {
                double lv = Math.Min(_sLo1, _sLo2);
                if (p.Low < lv - _tickSize && p.Close > lv)
                {
                    int score = 60;

                    double stop = Math.Min(p.Low - 2 * _tickSize, p.Close - 1.0 * atr);
                    _sLo1 = 0; _sLo2 = 0;
                    return new RawDecision
                    {
                        Direction = SignalDirection.Long, RawScore = score, IsValid = true,
                        EntryPrice = p.Close, StopPrice = stop,
                        TargetPrice = p.Close + atr * 1.0, Target2Price = p.Close + atr * 2.0,
                        SignalId = $"SMC_LiqSweep_v1:{p.CurrentBar}",
                        Label = $"Liq sweep double bottom [{SetId}]", Source = SignalSource.SMC_BOS
                    };
                }
            }
            if (_sHi1 > 0 && _sHi2 > 0 && Math.Abs(_sHi1 - _sHi2) < atr * 0.3 && Math.Abs(_sHi1B - _sHi2B) <= 100)
            {
                double lv = Math.Max(_sHi1, _sHi2);
                if (p.High > lv + _tickSize && p.Close < lv)
                {
                    int score = 60;

                    double stop = Math.Max(p.High + 2 * _tickSize, p.Close + 1.0 * atr);
                    _sHi1 = 0; _sHi2 = 0;
                    return new RawDecision
                    {
                        Direction = SignalDirection.Short, RawScore = score, IsValid = true,
                        EntryPrice = p.Close, StopPrice = stop,
                        TargetPrice = p.Close - atr * 1.0, Target2Price = p.Close - atr * 2.0,
                        SignalId = $"SMC_LiqSweep_v1:{p.CurrentBar}",
                        Label = $"Liq sweep double top [{SetId}]", Source = SignalSource.SMC_BOS
                    };
                }
            }
            return RawDecision.None;
        }
    }

    public class SMC_Session_Sweep : IConditionSet
    {
        public string SetId => "SMC_SessionSweep_v1";
        public string LastDiagnostic => "";
        private double _tickSize, _tickValue;
        private bool _aHiSwept, _aLoSwept, _lHiSwept, _lLoSwept;

        public void Initialise(double tickSize, double tickValue) { _tickSize = tickSize; _tickValue = tickValue; }
        public void OnSessionOpen(MarketSnapshot snapshot) { _aHiSwept = false; _aLoSwept = false; _lHiSwept = false; _lLoSwept = false; }
        public void OnFill(SignalObject signal, double fillPrice) { }
        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;

            if (snapshot.Tokyo.IsValid && snapshot.Tokyo.High > 0)
            {
                if (!_aHiSwept && p.High > snapshot.Tokyo.High + _tickSize)
                { _aHiSwept = true; if (p.Close < snapshot.Tokyo.High ) return Bld(false, p, snapshot, atr, snapshot.Tokyo.High, "AsiaSweepHigh"); }
                if (!_aLoSwept && p.Low < snapshot.Tokyo.Low - _tickSize)
                { _aLoSwept = true; if (p.Close > snapshot.Tokyo.Low ) return Bld(true, p, snapshot, atr, snapshot.Tokyo.Low, "AsiaSweepLow"); }
            }
            if (snapshot.London.IsValid && snapshot.London.High > 0)
            {
                if (!_lHiSwept && p.High > snapshot.London.High + _tickSize)
                { _lHiSwept = true; if (p.Close < snapshot.London.High ) return Bld(false, p, snapshot, atr, snapshot.London.High, "LondonSweepHigh"); }
                if (!_lLoSwept && p.Low < snapshot.London.Low - _tickSize)
                { _lLoSwept = true; if (p.Close > snapshot.London.Low ) return Bld(true, p, snapshot, atr, snapshot.London.Low, "LondonSweepLow"); }
            }
            return RawDecision.None;
        }

        private RawDecision Bld(bool isLong, BarSnapshot p, MarketSnapshot snap, double atr, double level, string label)
        {
            int score = 58;
            double barMid = (p.High + p.Low) / 2.0;
            if (isLong && p.Close < barMid) score -= 8;
            if (!isLong && p.Close > barMid) score -= 8;

            double stop = isLong
                ? Math.Min(p.Low - 2 * _tickSize, p.Close - 1.0 * atr)
                : Math.Max(p.High + 2 * _tickSize, p.Close + 1.0 * atr);

            return new RawDecision
            {
                Direction = isLong ? SignalDirection.Long : SignalDirection.Short,
                RawScore = score, IsValid = true,
                EntryPrice = p.Close, StopPrice = stop,
                TargetPrice = isLong ? p.Close + atr * 1.0 : p.Close - atr * 1.0,
                Target2Price = isLong ? p.Close + atr * 2.0 : p.Close - atr * 2.0,
                SignalId = $"SMC_SessionSweep_v1:{label}:{p.CurrentBar}",
                Label = $"{label} [{SetId}]", Source = SignalSource.SMC_CHoCH
            };
        }
    }

    public class SMC_IB_Retest : IConditionSet
    {
        public string SetId => "SMC_IB_v1";
        public string LastDiagnostic => "";
        private double _tickSize, _tickValue;
        private double _ibHigh, _ibLow;
        private bool _ibComplete;
        private int _ibStartBar;
        private int _ibCompleteBar;
        private bool _tradedHi, _tradedLo;

        private const int IB_MINUTES = 60;

        public void Initialise(double tickSize, double tickValue) { _tickSize = tickSize; _tickValue = tickValue; }
        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _ibHigh = 0; _ibLow = double.MaxValue;
            _ibComplete = false; _ibStartBar = -1; _ibCompleteBar = 0;
            _tradedHi = false; _tradedLo = false;
        }
        public void OnFill(SignalObject signal, double fillPrice) { }
        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;

            // ── Build IB from first bar of session ────────────────────────
            if (!_ibComplete)
            {
                if (_ibStartBar < 0) _ibStartBar = p.CurrentBar;

                if (p.High > _ibHigh) _ibHigh = p.High;
                if (p.Low < _ibLow) _ibLow = p.Low;

                if (p.CurrentBar - _ibStartBar >= IB_MINUTES)
                {
                    _ibComplete = true;
                    _ibCompleteBar = p.CurrentBar;
                }
                return RawDecision.None;
            }
            if (p.CurrentBar - _ibCompleteBar < 3) return RawDecision.None;
            double buf = atr * 0.1;
            double ibRange = _ibHigh - _ibLow;

            if (!_tradedLo && p.Low <= _ibLow + buf && p.Close > _ibLow)
            {
                _tradedLo = true;
                int score = 52;

                double stop = Math.Min(_ibLow - 2 * _tickSize, p.Close - 1.0 * atr);
                return new RawDecision
                {
                    Direction = SignalDirection.Long, RawScore = score, IsValid = true,
                    EntryPrice = p.Close, StopPrice = stop,
                    TargetPrice = _ibHigh,
                    Target2Price = _ibHigh + ibRange * 0.5,
                    SignalId = $"SMC_IB_v1:IBLow:{p.CurrentBar}",
                    Label = $"IB low retest [{SetId}]",
                    Source = SignalSource.SMC_IB_Retest
                };
            }
            if (!_tradedHi && p.High >= _ibHigh - buf && p.Close < _ibHigh)
            {
                _tradedHi = true;
                int score = 52;

                double stop = Math.Max(_ibHigh + 2 * _tickSize, p.Close + 1.0 * atr);
                return new RawDecision
                {
                    Direction = SignalDirection.Short, RawScore = score, IsValid = true,
                    EntryPrice = p.Close, StopPrice = stop,
                    TargetPrice = _ibLow,
                    Target2Price = _ibLow - ibRange * 0.5,
                    SignalId = $"SMC_IB_v1:IBHigh:{p.CurrentBar}",
                    Label = $"IB high retest [{SetId}]",
                    Source = SignalSource.SMC_IB_Retest
                };
            }
            return RawDecision.None;
        }
    }
}
