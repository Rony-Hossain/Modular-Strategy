#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    // =========================================================================
    // SMF_Full — Exact port of SmartMoneyFlowCloudBOSWavesV3 signal logic
    //
    // SOURCE INDICATOR: SmartMoneyFlowCloudBOSWavesV3.cs
    // PARITY: All signal math is line-for-line identical. UI/rendering removed.
    //
    // WHAT THIS REPLICATES:
    //
    // STEP 1 — Smart Money Flow (rolling O(1)):
    //   CLV      = ((Close − Low) − (High − Close)) / Range
    //   rf       = CLV × Volume
    //   mf       = rollingSum(rf, FlowWindow=24) / rollingSum(|rf|, FlowWindow)
    //   mfSm     = EMA(mf, FlowSmoothing=5)     [−1..+1 smoothed money flow ratio]
    //   strength = clamp(|mfSm|^FlowBoost, 0, 1) [0=calm, 1=strong institutional flow]
    //
    // STEP 2 — Adaptive band multiplier:
    //   mult = BandTightnessCalm + (BandExpansionStrong − BandTightnessCalm) × strength
    //        = 0.9 + (2.2 − 0.9) × strength
    //
    // STEP 3 — Basis (double-smoothed EMA of Open/Close):
    //   bO_raw = EMA(Open,  TrendLength=34)
    //   bC_raw = EMA(Close, TrendLength=34)
    //   bO     = EMA(bO_raw, BasisSmooth=3)
    //   bC     = EMA(bC_raw, BasisSmooth=3)   ← basisMain
    //
    // STEP 4 — Bands:
    //   upper = basisMain + ATR × mult
    //   lower = basisMain − ATR × mult
    //
    // STEP 5 — Breakout conditions (bar-close semantics):
    //   longCond  = Close[0] > upper  AND Close[1] ≤ upper[1]
    //   shortCond = Close[0] < lower  AND Close[1] ≥ lower[1]
    //
    // STEP 6 — Regime state machine:
    //   lastSignal ∈ {+1, −1}. If longCond→+1. If shortCond→−1. Else carry.
    //
    // STEP 7 — One-shot flip events per bar:
    //   switchUp: lastSignal==+1 AND prevLastSignal==−1
    //   switchDn: lastSignal==−1 AND prevLastSignal==+1
    //
    // STEP 8 — Retest wick probes (cooldown=12 bars, no flip on same bar):
    //   bearRetestOk: regime==−1 AND High[0] > basisMain
    //   bullRetestOk: regime==+1 AND Low[0]  < basisMain
    //
    // STEP 9 — Impulse warnings:
    //   impulseUp: switchUp AND strength ≥ 0.70
    //   impulseDn: switchDn AND strength ≥ 0.70
    //
    // STEP 10 — Non-confirmation warnings:
    //   nonConfLong:  switchUp AND mfSm < 0  (price flipped bull, flow still bearish)
    //   nonConfShort: switchDn AND mfSm > 0  (price flipped bear, flow still bullish)
    //
    // THREE CONDITION SETS (each wraps the shared SMFFullEngine):
    //   SMF_Full_Impulse — switchUp/Dn with strength ≥ 0.70 (highest conviction)
    //   SMF_Full_Switch  — switchUp/Dn without threshold, skips if NonConf present
    //   SMF_Full_Retest  — bullRetestOk / bearRetestOk (pullback-to-basis entries)
    //
    // REGISTRATION in HostStrategy.CreateLogic():
    //   var smfFull = new SMFFullEngine();
    //   new ConditionSets.SMF_Full_Impulse(smfFull),
    //   new ConditionSets.SMF_Full_Switch(smfFull),
    //   new ConditionSets.SMF_Full_Retest(smfFull),
    // =========================================================================

    // =========================================================================
    // SMFFullEngine — the complete stateful math engine
    // One shared instance per strategy. All three condition sets call Update()
    // which runs once per bar (bar-index guard).
    // =========================================================================

    public sealed class SMFFullEngine
    {
        // ── Parameters (exact indicator defaults) ─────────────────────────
        public int    TrendLength              = 34;
        public int    BasisSmooth              = 3;
        public int    FlowWindow               = 24;
        public int    FlowSmoothing            = 5;
        public double FlowBoost                = 1.2;
        public double BandTightnessCalm        = 0.9;
        public double BandExpansionStrong      = 2.2;
        public int    DotCooldown              = 12;
        public double ImpulseStrengthThreshold = 0.70;

        // ── Rolling flow window (circular buffer, size > FlowWindow) ───────
        private const int RING_CAP = 32;
        private readonly double[] _rfRing  = new double[RING_CAP];
        private readonly double[] _absRing = new double[RING_CAP];
        private int    _rHead  = 0;
        private int    _rCount = 0;
        private double _rNum   = 0.0;
        private double _rDen   = 0.0;

        // ── EMA states ─────────────────────────────────────────────────────
        private double _emaOpen  = double.NaN;
        private double _emaClose = double.NaN;
        private double _bOPrev   = double.NaN;
        private double _bCPrev   = double.NaN;
        private double _mfPrev   = double.NaN;

        // ── Regime state ───────────────────────────────────────────────────
        private int _sig  = 0;
        private int _prev = 0;

        // ── Retest cooldown ────────────────────────────────────────────────
        private int _lastBearDot = int.MinValue;
        private int _lastBullDot = int.MinValue;

        // ── Prior-bar values for crossover comparison ──────────────────────
        private double _prevClose = double.NaN;
        private double _prevUpper = double.NaN;
        private double _prevLower = double.NaN;

        // ── Bar-index guard ────────────────────────────────────────────────
        private int _lastBar = -1;

        // ── Published outputs ──────────────────────────────────────────────
        public double BasisMain    { get; private set; }
        public double BasisOpen_   { get; private set; }
        public double UpperBand    { get; private set; }
        public double LowerBand    { get; private set; }
        public double MfSm         { get; private set; }
        public double Strength     { get; private set; }
        public double Mult         { get; private set; }
        public int    LastSignal   { get; private set; }

        // One-shot flags (true only on the bar they fire)
        public bool SwitchUp       { get; private set; }
        public bool SwitchDn       { get; private set; }
        public bool ImpulseUp      { get; private set; }
        public bool ImpulseDn      { get; private set; }
        public bool NonConfLong    { get; private set; }
        public bool NonConfShort   { get; private set; }
        public bool BullRetestOk   { get; private set; }
        public bool BearRetestOk   { get; private set; }

        public bool IsReady        { get; private set; }

        // ── Reset ──────────────────────────────────────────────────────────
        public void Reset()
        {
            _rHead = 0; _rCount = 0; _rNum = 0.0; _rDen = 0.0;
            _emaOpen = _emaClose = _bOPrev = _bCPrev = _mfPrev = double.NaN;
            _sig = 0; _prev = 0;
            _lastBearDot = _lastBullDot = int.MinValue;
            _prevClose = _prevUpper = _prevLower = double.NaN;
            _lastBar = -1;
            SwitchUp = SwitchDn = ImpulseUp = ImpulseDn = false;
            NonConfLong = NonConfShort = BullRetestOk = BearRetestOk = false;
            BasisMain = BasisOpen_ = UpperBand = LowerBand = 0.0;
            MfSm = Strength = Mult = 0.0;
            LastSignal = 0;
            IsReady = false;
        }

        // ── Main update ────────────────────────────────────────────────────
        public void Update(BarSnapshot p, double atr, int barIndex)
        {
            if (barIndex == _lastBar) return;
            _lastBar = barIndex;

            // Clear one-shot flags
            SwitchUp = SwitchDn = ImpulseUp = ImpulseDn = false;
            NonConfLong = NonConfShort = BullRetestOk = BearRetestOk = false;

            int fw = Math.Max(2, FlowWindow);
            int L  = Math.Max(2, TrendLength);

            // ── STEP 1: Smart Money Flow (CLV × Volume rolling ratio) ─────
            double range = p.High - p.Low;
            double clv   = (Math.Abs(range) < 1e-12)
                ? 0.0
                : ((p.Close - p.Low) - (p.High - p.Close)) / range;

            double rf    = clv * p.Volume;
            double absRf = Math.Abs(rf);

            // Evict oldest when window full
            if (_rCount >= fw)
            {
                int ev = (_rHead - _rCount + RING_CAP) % RING_CAP;
                _rNum -= _rfRing[ev];
                _rDen -= _absRing[ev];
                _rCount--;
            }

            _rfRing[_rHead]  = rf;
            _absRing[_rHead] = absRf;
            _rHead = (_rHead + 1) % RING_CAP;
            _rCount++;
            _rNum += rf;
            _rDen += absRf;

            double mf   = (_rDen <= 0.0) ? 0.0 : (_rNum / _rDen);
            double mfSm = EmaStep(mf, _mfPrev, FlowSmoothing);
            _mfPrev = mfSm;
            MfSm = mfSm;

            double str = Math.Pow(Math.Abs(mfSm), FlowBoost);
            str = Clamp(str, 0.0, 1.0);
            Strength = str;

            double mult = BandTightnessCalm + (BandExpansionStrong - BandTightnessCalm) * str;
            Mult = mult;

            // ── STEP 2: Basis — EMA(Open,L) and EMA(Close,L), then smooth ─
            double bO_raw, bC_raw;
            if (double.IsNaN(_emaOpen))
            {
                _emaOpen  = p.Open;
                _emaClose = p.Close;
                bO_raw = p.Open;
                bC_raw = p.Close;
            }
            else
            {
                bO_raw = EmaStep(p.Open,  _emaOpen,  L);
                bC_raw = EmaStep(p.Close, _emaClose, L);
                _emaOpen  = bO_raw;
                _emaClose = bC_raw;
            }

            double bO = EmaStep(bO_raw, _bOPrev, BasisSmooth);
            double bC = EmaStep(bC_raw, _bCPrev, BasisSmooth);
            _bOPrev = bO; _bCPrev = bC;
            BasisOpen_ = bO;
            BasisMain  = bC;

            // ── STEP 3: Bands ─────────────────────────────────────────────
            double upper = bC + atr * mult;
            double lower = bC - atr * mult;
            UpperBand = upper;
            LowerBand = lower;

            // ── STEP 4: Breakout conditions ───────────────────────────────
            // Requires prior-bar close and bands to be valid
            bool longCond  = false;
            bool shortCond = false;

            if (_rCount >= 2 && !double.IsNaN(_prevClose) && !double.IsNaN(_prevUpper))
            {
                longCond  = p.Close > upper && _prevClose <= _prevUpper;
                shortCond = p.Close < lower && _prevClose >= _prevLower;
            }

            // ── STEP 5: Regime state machine ──────────────────────────────
            int prevSig = _sig;
            if (prevSig == 0)
                prevSig = (p.Close >= bC) ? 1 : -1;

            int newSig;
            if      (longCond)  newSig = 1;
            else if (shortCond) newSig = -1;
            else                newSig = prevSig;

            _sig       = newSig;
            LastSignal = newSig;

            // ── STEP 6: Flip events ───────────────────────────────────────
            bool swUp = (newSig == 1  && _prev == -1 && _prev != 0);
            bool swDn = (newSig == -1 && _prev == 1  && _prev != 0);
            SwitchUp = swUp;
            SwitchDn = swDn;

            // ── STEP 7: Impulse warnings ──────────────────────────────────
            if (swUp && str >= ImpulseStrengthThreshold) ImpulseUp = true;
            if (swDn && str >= ImpulseStrengthThreshold) ImpulseDn = true;

            // ── STEP 8: Non-confirmation warnings ─────────────────────────
            if (swUp && mfSm < 0) NonConfLong  = true;
            if (swDn && mfSm > 0) NonConfShort = true;

            // ── STEP 9: Retest wick probes + cooldown ─────────────────────
            // Bear regime, wick above basis (short retest)
            if (newSig == -1 && p.High > bC && !swUp && !swDn)
            {
                bool cool = (DotCooldown == 0)
                    || (_lastBearDot == int.MinValue)
                    || (barIndex - _lastBearDot >= DotCooldown);
                if (cool) { BearRetestOk = true; _lastBearDot = barIndex; }
            }

            // Bull regime, wick below basis (long retest)
            if (newSig == 1 && p.Low < bC && !swUp && !swDn)
            {
                bool cool = (DotCooldown == 0)
                    || (_lastBullDot == int.MinValue)
                    || (barIndex - _lastBullDot >= DotCooldown);
                if (cool) { BullRetestOk = true; _lastBullDot = barIndex; }
            }

            // ── Advance state for next bar ─────────────────────────────────
            _prev      = newSig;
            _prevClose = p.Close;
            _prevUpper = upper;
            _prevLower = lower;

            IsReady = (_rCount >= Math.Min(fw, L));
        }

        // ── Math helpers (exact match to indicator source) ─────────────────
        private static double EmaStep(double x, double prev, int period)
        {
            if (period <= 1)        return x;
            if (double.IsNaN(prev)) return x;
            double a = 2.0 / (period + 1.0);
            return prev + a * (x - prev);
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }
    }

    // =========================================================================
    // SMFNativeBase_Full — shared plumbing for all three sets
    // =========================================================================

    public abstract class SMFNativeBase_Full : IConditionSet
    {
        public abstract string SetId { get; }
        public string LastDiagnostic => _bail;

        protected readonly SMFFullEngine _e;
        protected double _tickSize;
        protected double _tickValue;
        protected int    _lastFillBar = -1;
        protected string _bail        = "";

        protected SMFNativeBase_Full(SMFFullEngine engine) { _e = engine; }

        public void Initialise(double tickSize, double tickValue)
        { _tickSize = tickSize; _tickValue = tickValue; }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _lastFillBar = -1;
            _bail = "session_open";
            _e.Reset();
        }

        public void OnFill(SignalObject signal, double fillPrice)
        { if (signal.ConditionSetId == SetId) _lastFillBar = signal.BarIndex; }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        public abstract RawDecision Evaluate(MarketSnapshot snapshot);

        protected static bool IsRTH(BarSnapshot p)
            => true; // All sessions allowed
    }

    // =========================================================================
    // SMF_Full_Impulse
    // Source: ImpulseBreakout / ImpulseBreakdown series in the indicator.
    //         switchUpFinal AND strength >= ImpulseStrengthThreshold
    //         switchDnFinal AND strength >= ImpulseStrengthThreshold
    //
    // This is the HIGHEST CONVICTION signal. The regime flipped AND the money
    // flow strength confirms institutional participation is strong.
    //
    // Entry:  Close of the flip bar.
    // Stop:   0.5×ATR beyond the basis (basis is the SMF cloud midline).
    // T1:     1.5×ATR from entry.
    // T2:     3.0×ATR from entry (generous — impulse moves extend).
    // Score:  80 base. −5 if NonConfirmation present (warning but not a veto).
    // =========================================================================

    public class SMF_Full_Impulse : SMFNativeBase_Full
    {
        public override string SetId => "SMF_Full_Impulse_v1";
        private const int COOLDOWN = 5;

        public SMF_Full_Impulse(SMFFullEngine engine) : base(engine) { }

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid)     { _bail = "invalid";   return RawDecision.None; }
            var    p  = snapshot.Primary;
            double atr = snapshot.ATR;
            double ts  = p.TickSize > 0 ? p.TickSize : _tickSize;

            if (atr <= 0)            { _bail = "atr_zero"; return RawDecision.None; }
            if (!IsRTH(p))           { _bail = "non_rth";  return RawDecision.None; }
            if (_lastFillBar >= 0 && p.CurrentBar - _lastFillBar < COOLDOWN)
            { _bail = "cooldown"; return RawDecision.None; }

            _e.Update(p, atr, p.CurrentBar);

            if (!_e.IsReady)         
            { 
                _bail = "warmup";    
                return new RawDecision { Direction = SignalDirection.None, Label = "REJ:SMF Warmup", IsValid = false }; 
            }

            if (!_e.ImpulseUp && !_e.ImpulseDn)
            { 
                _bail = $"no_impulse swU={_e.SwitchUp} swD={_e.SwitchDn} str={_e.Strength:F2}";
                // If it was a switch but not an impulse, we don't show it here (Switch set will show it)
                return RawDecision.None; 
            }

            bool isLong = _e.ImpulseUp;

            double entry = p.Close;
            double basis = _e.BasisMain;
            double stop  = isLong
                ? basis - atr * 0.5
                : basis + atr * 0.5;
            double t1    = isLong ? entry + 1.5 * atr : entry - 1.5 * atr;
            double t2    = isLong ? entry + 3.0 * atr : entry - 3.0 * atr;

            // Stop sanity
            if (isLong  && stop >= entry) stop = entry - ts * 4;
            if (!isLong && stop <= entry) stop = entry + ts * 4;

            int score = 80;
            double risk = Math.Abs(entry - stop);
            double rew  = Math.Abs(t1    - entry);
            if (risk > 0 && rew / risk < 1.2) { 
                _bail = $"rr_penalty ({rew/risk:F2})"; 
                score -= 10; // Soft penalty for low RR
            }
            if (_e.NonConfLong || _e.NonConfShort) score -= 5;
            score = Math.Min(score, 90);

            _bail = "FIRED_" + (isLong ? "LONG" : "SHORT");

            return new RawDecision
            {
                Direction      = isLong ? SignalDirection.Long : SignalDirection.Short,
                Source         = SignalSource.SMF_Impulse,
                ConditionSetId = SetId,
                EntryPrice     = entry,
                StopPrice      = stop,
                TargetPrice    = t1,
                Target2Price   = t2,
                Label          = string.Format("SMF Impulse {0} str={1:F2}",
                                     isLong ? "long" : "short", _e.Strength),
                RawScore       = score,
                IsValid        = true,
                BarIndex       = p.CurrentBar,
                SignalId       = string.Format("{0}:{1:yyyyMMdd}:{2}", SetId, p.Time, p.CurrentBar)
            };
        }
    }

    // =========================================================================
    // SMF_Full_Switch
    // Source: SwitchUp / SwitchDown series in the indicator.
    //         Every regime flip that does NOT reach the impulse threshold.
    //         Skips when NonConfirmation is present — flow directly disagrees.
    //
    // Entry:  Close of the flip bar.
    // Stop:   2 ticks beyond the opposing band (the band that was just crossed
    //         IS the invalidation level — if price returns through it, the flip failed).
    // T1:     1.5×ATR.
    // T2:     2.5×ATR.
    // Score:  68 base.
    // =========================================================================

    public class SMF_Full_Switch : SMFNativeBase_Full
    {
        public override string SetId => "SMF_Full_Switch_v1";
        private const int COOLDOWN = 5;

        public SMF_Full_Switch(SMFFullEngine engine) : base(engine) { }

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) { _bail = "invalid";  return RawDecision.None; }
            var    p  = snapshot.Primary;
            double atr = snapshot.ATR;
            double ts  = p.TickSize > 0 ? p.TickSize : _tickSize;

            if (atr <= 0)  { _bail = "atr_zero"; return RawDecision.None; }
            if (!IsRTH(p)) { _bail = "non_rth";  return RawDecision.None; }
            if (_lastFillBar >= 0 && p.CurrentBar - _lastFillBar < COOLDOWN)
            { _bail = "cooldown"; return RawDecision.None; }

            _e.Update(p, atr, p.CurrentBar);

            if (!_e.IsReady)            { _bail = "warmup";            return RawDecision.None; }
            if (!_e.SwitchUp && !_e.SwitchDn) { _bail = "no_switch"; return RawDecision.None; }

            bool isLong = _e.SwitchUp;

            // If impulse-strength set handles this, defer
            if (_e.Strength >= _e.ImpulseStrengthThreshold)
            { _bail = "deferred_to_impulse"; return RawDecision.None; }

            // Non-confirmation veto: price flipped but flow disagrees — skip
            if (isLong  && _e.NonConfLong)  
            { 
                _bail = "veto_nonconf_long";  
                return new RawDecision
                {
                    Direction = SignalDirection.Long,
                    Source = SignalSource.SMF_Switch,
                    ConditionSetId = SetId,
                    EntryPrice = p.Close,
                    Label = "REJ:SMF NonConf",
                    IsValid = false
                };
            }
            if (!isLong && _e.NonConfShort) 
            { 
                _bail = "veto_nonconf_short"; 
                return new RawDecision
                {
                    Direction = SignalDirection.Short,
                    Source = SignalSource.SMF_Switch,
                    ConditionSetId = SetId,
                    EntryPrice = p.Close,
                    Label = "REJ:SMF NonConf",
                    IsValid = false
                };
            }

            double entry = p.Close;
            // Stop beyond the band that was just crossed
            double stop  = isLong
                ? _e.LowerBand - ts * 2
                : _e.UpperBand + ts * 2;

            if (isLong  && stop >= entry) stop = entry - ts * 4;
            if (!isLong && stop <= entry) stop = entry + ts * 4;

            double t1 = isLong ? entry + 1.5 * atr : entry - 1.5 * atr;
            double t2 = isLong ? entry + 2.5 * atr : entry - 2.5 * atr;

            double risk = Math.Abs(entry - stop);
            double rew  = Math.Abs(t1    - entry);
            if (risk > 0 && rew / risk < 1.2)
            { _bail = $"rr_low ({rew/risk:F2})"; return RawDecision.None; }

            _bail = "FIRED_" + (isLong ? "LONG" : "SHORT");

            return new RawDecision
            {
                Direction      = isLong ? SignalDirection.Long : SignalDirection.Short,
                Source         = SignalSource.SMF_Switch,
                ConditionSetId = SetId,
                EntryPrice     = entry,
                StopPrice      = stop,
                TargetPrice    = t1,
                Target2Price   = t2,
                Label          = string.Format("SMF Switch {0} str={1:F2}",
                                     isLong ? "long" : "short", _e.Strength),
                RawScore       = 68,
                IsValid        = true,
                BarIndex       = p.CurrentBar,
                SignalId       = string.Format("{0}:{1:yyyyMMdd}:{2}", SetId, p.Time, p.CurrentBar)
            };
        }
    }

    // =========================================================================
    // SMF_Full_Retest
    // Source: BullRetestOk / BearRetestOk series in the indicator.
    //         Regime UNCHANGED. Price wicks through the basis then closes back.
    //         Cooldown = 12 bars (matches indicator DotCooldown default).
    //
    // Bull regime (lastSignal=+1): Low < basisMain → wick below basis → buy dip.
    // Bear regime (lastSignal=−1): High > basisMain → wick above basis → sell bounce.
    //
    // The retest bar MUST close on the correct side of the basis:
    //   Long retest: Close must be ≥ basisMain (not closed below — rejected).
    //   Short retest: Close must be ≤ basisMain.
    //
    // Entry:  Close of the retest bar.
    // Stop:   0.4×ATR beyond basisMain (if basis fails to hold, thesis is broken).
    // T1:     1.5×ATR.
    // T2:     2.0×ATR (shorter — retest moves are mean-reversion, not trends).
    // Score:  70 base. +5 if strength > 0.5. +5 if no NonConf warning.
    // =========================================================================

    public class SMF_Full_Retest : SMFNativeBase_Full
    {
        public override string SetId => "SMF_Full_Retest_v1";
        private const int COOLDOWN = 5;

        public SMF_Full_Retest(SMFFullEngine engine) : base(engine) { }

        public override RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) { _bail = "invalid";  return RawDecision.None; }
            var    p  = snapshot.Primary;
            double atr = snapshot.ATR;
            double ts  = p.TickSize > 0 ? p.TickSize : _tickSize;

            if (atr <= 0)  { _bail = "atr_zero"; return RawDecision.None; }
            if (!IsRTH(p)) { _bail = "non_rth";  return RawDecision.None; }
            if (_lastFillBar >= 0 && p.CurrentBar - _lastFillBar < COOLDOWN)
            { _bail = "cooldown"; return RawDecision.None; }

            _e.Update(p, atr, p.CurrentBar);

            if (!_e.IsReady)
            { _bail = "warmup"; return RawDecision.None; }

            if (!_e.BullRetestOk && !_e.BearRetestOk)
            { _bail = $"no_retest sig={_e.LastSignal} L={p.Low:F2} H={p.High:F2} basis={_e.BasisMain:F2}";
              return RawDecision.None; }

            bool isLong = _e.BullRetestOk;

            // Close-side confirmation: bar must close on the regime side of the basis
            if (isLong  && p.Close < _e.BasisMain)
            { 
                _bail = "close_below_basis"; 
                return new RawDecision
                {
                    Direction = SignalDirection.Long,
                    Source = SignalSource.SMF_Retest,
                    ConditionSetId = SetId,
                    EntryPrice = p.Close,
                    Label = "REJ:SMF Basis",
                    IsValid = false
                };
            }
            if (!isLong && p.Close > _e.BasisMain)
            { 
                _bail = "close_above_basis"; 
                return new RawDecision
                {
                    Direction = SignalDirection.Short,
                    Source = SignalSource.SMF_Retest,
                    ConditionSetId = SetId,
                    EntryPrice = p.Close,
                    Label = "REJ:SMF Basis",
                    IsValid = false
                };
            }

            double entry = p.Close;
            double basis = _e.BasisMain;

            // Stop: 0.4×ATR beyond basis
            double stop = isLong
                ? basis - atr * 0.4
                : basis + atr * 0.4;

            if (isLong  && stop >= entry) stop = entry - ts * 4;
            if (!isLong && stop <= entry) stop = entry + ts * 4;

            double t1 = isLong ? entry + 1.5 * atr : entry - 1.5 * atr;
            double t2 = isLong ? entry + 2.0 * atr : entry - 2.0 * atr;

            double risk = Math.Abs(entry - stop);
            double rew  = Math.Abs(t1    - entry);
            if (risk > 0 && rew / risk < 1.2)
            { _bail = $"rr_low ({rew/risk:F2})"; return RawDecision.None; }

            int score = 70;
            if (_e.Strength > 0.5) score += 5;
            if (!_e.NonConfLong && !_e.NonConfShort) score += 5;
            score = Math.Min(score, 82);

            _bail = "FIRED_" + (isLong ? "LONG" : "SHORT");

            return new RawDecision
            {
                Direction      = isLong ? SignalDirection.Long : SignalDirection.Short,
                Source         = SignalSource.SMF_Retest,
                ConditionSetId = SetId,
                EntryPrice     = entry,
                StopPrice      = stop,
                TargetPrice    = t1,
                Target2Price   = t2,
                Label          = string.Format("SMF Retest {0} basis={1:F2} str={2:F2}",
                                     isLong ? "long" : "short", basis, _e.Strength),
                RawScore       = score,
                IsValid        = true,
                BarIndex       = p.CurrentBar,
                SignalId       = string.Format("{0}:{1:yyyyMMdd}:{2}", SetId, p.Time, p.CurrentBar)
            };
        }
    }
}