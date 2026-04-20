#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    // =========================================================================
    // EMA_ADX_Signals.cs — EMA Cross and ADX Trend condition sets
    //
    // Two condition sets filling the previously placeholder SignalSource entries:
    //
    //   EMA_Cross_v1     9/21 EMA cross in the direction of the SMF regime.
    //                    Incremental EMA state maintained internally (no NT8 API).
    //                    Both fast (9) and slow (21) EMAs computed via MathFlow.ZLEmaStep.
    //                    Fires on the exact bar of crossover, not after.
    //                    Uses VWAP side + SMF regime as mandatory filters.
    //                    Score: 60–78. Cap at 78 — pure EMA cross without structure
    //                    context is a low-quality signal. Requires regime alignment.
    //
    //   ADX_Trend_v1     ADX > 25 + fresh +DI/-DI crossover in a trending market.
    //                    Full Wilder-smoothed ADX computed incrementally via
    //                    MathIndicators.DM_Calculate + MathIndicators.Wilder_Smooth.
    //                    Fires when ADX crosses above threshold AND the DI lines
    //                    just crossed (onset of trend, not deep inside one).
    //                    Score: 62–80. Cap at 80 — ADX lags, so it confirms
    //                    trending conditions rather than predicting reversals.
    //
    // DESIGN NOTES:
    //   Both sets maintain their own incremental indicator state — no NT8 indicator
    //   objects, no BarsArray references. They are fully self-contained.
    //
    //   EMA warm-up: first bar seeds the EMA with Close[0].
    //   ADX warm-up: requires ADX_PERIOD × 2 bars before IsTrending is trustworthy.
    //   Both guards are handled — no signals fire before warm-up is complete.
    //
    //   Neither set fires in the same direction consecutively within SIGNAL_COOLDOWN
    //   bars — prevents a sideways chop from generating a train of EMA wiggles.
    //
    //   Both sets use PassesConfluence as a mandatory gate. An EMA cross or ADX
    //   signal without regime + VWAP agreement is noise, not edge.
    //
    // REGISTRATION:
    //   Add to HostStrategy.CreateLogic() — placed AFTER Wyckoff and before
    //   ORB/VWAP since they are lower-conviction:
    //     new ConditionSets.EMA_Cross(),
    //     new ConditionSets.ADX_Trend(),
    // =========================================================================


    // =========================================================================
    // EMA_Cross — 9/21 EMA crossover with regime + VWAP filter
    // =========================================================================

    /// <summary>
    /// Fires on the bar where the 9-period EMA crosses the 21-period EMA,
    /// but only when the cross is ALIGNED with the SMF regime and price is on
    /// the correct side of VWAP. Without those two context gates, EMA crosses
    /// are random and should not be traded.
    /// </summary>
    public class EMA_Cross : IConditionSet
    {
        public string SetId => "EMA_Cross_v1";
        public string LastDiagnostic => "";

        private double _tickSize, _tickValue;

        // ── EMA state ─────────────────────────────────────────────────────
        private double _ema9Prev  = double.NaN;
        private double _ema21Prev = double.NaN;
        private double _ema9Now   = double.NaN;
        private double _ema21Now  = double.NaN;
        private int    _warmupBars = 0;
        private const int WARMUP_REQUIRED = 25;   // 21 + buffer

        // Price ring buffers for ZLEMA lag correction
        // lag(9)  = (9-1)/2  = 4  → ring depth 5
        // lag(21) = (21-1)/2 = 10 → ring depth 11
        private const int LAG9  = (9  - 1) / 2;   // 4
        private const int LAG21 = (21 - 1) / 2;   // 10
        private readonly double[] _ring9  = new double[LAG9  + 1];
        private readonly double[] _ring21 = new double[LAG21 + 1];
        private int _ring9Idx  = 0, _ring9Count  = 0;
        private int _ring21Idx = 0, _ring21Count = 0;

        // ── Cooldown ──────────────────────────────────────────────────────
        private int _lastSignalBar  = -1;
        private const int SIGNAL_COOLDOWN = 8;    // longer than SMC — EMA is slower

        // ── IConditionSet lifecycle ───────────────────────────────────────

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            // Reset EMA state at session open so overnight drift doesn't
            // contaminate the RTH calculation.
            _ema9Prev   = double.NaN;
            _ema21Prev  = double.NaN;
            _ema9Now    = double.NaN;
            _ema21Now   = double.NaN;
            _warmupBars = 0;
            _lastSignalBar = -1;
            // Reset ZLEMA price rings
            System.Array.Clear(_ring9,  0, _ring9.Length);
            System.Array.Clear(_ring21, 0, _ring21.Length);
            _ring9Idx  = 0; _ring9Count  = 0;
            _ring21Idx = 0; _ring21Count = 0;
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
                _lastSignalBar = signal.BarIndex;
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;

            // Update EMAs before checking cross
            UpdateEMAs(p.Close);

            // Not enough bars for reliable EMA
            if (_warmupBars < WARMUP_REQUIRED) return RawDecision.None;

            // Need both previous and current values
            if (double.IsNaN(_ema9Prev) || double.IsNaN(_ema21Prev)) return RawDecision.None;

            // Cooldown
            if (_lastSignalBar >= 0 && p.CurrentBar - _lastSignalBar < SIGNAL_COOLDOWN)
                return RawDecision.None;

            // Detect cross
            EMACross cross = MathIndicators.EMA_CrossDetect(
                _ema9Now, _ema9Prev, _ema21Now, _ema21Prev);

            if (cross == EMACross.None) return RawDecision.None;

            bool isLong = (cross == EMACross.Bullish);

            // ── Context gates ─────────────────────────────────────────────
            // Gate 1: SMF regime must agree (or be undefined — neutral regime OK)
            int regime = (int)snapshot.Get(SnapKeys.Regime);
            if (isLong  && regime < 0) return RawDecision.None;  // bearish regime: no long cross
            if (!isLong && regime > 0) return RawDecision.None;  // bullish regime: no short cross

            // Gate 2: VWAP side must agree
            if (snapshot.VWAP > 0)
            {
                if (isLong  && p.Close < snapshot.VWAP) return RawDecision.None;
                if (!isLong && p.Close > snapshot.VWAP) return RawDecision.None;
            }

            // ── Score ─────────────────────────────────────────────────────
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;
            int score = 60;

            // EMA separation — wide cross = stronger momentum
            double emaSep = Math.Abs(_ema9Now - _ema21Now);
            if (emaSep > atr * 0.1) score += 4;
            if (emaSep > atr * 0.3) score += 4;

            // Regime alignment bonus
            if ((isLong && regime > 0) || (!isLong && regime < 0)) score += 5;

            score  = Math.Min(score, 78);

            // ── Decision ─────────────────────────────────────────────────
            double stop = isLong
                ? Math.Min(_ema21Now - atr * 0.5, p.Close - atr * 0.8)
                : Math.Max(_ema21Now + atr * 0.5, p.Close + atr * 0.8);

            double t1 = isLong
                ? p.Close + atr * 1.0
                : p.Close - atr * 1.0;
            double t2 = isLong
                ? p.Close + atr * 2.0
                : p.Close - atr * 2.0;

            return new RawDecision
            {
                Direction    = isLong ? SignalDirection.Long : SignalDirection.Short,
                Source       = SignalSource.EMA_CrossSignal,
                EntryPrice   = p.Close,
                StopPrice    = stop,
                TargetPrice  = t1,
                Target2Price = t2,
                Label        = $"EMA9x21 {(isLong ? "bull" : "bear")} sep={emaSep:F2} [{SetId}]",
                RawScore     = score,
                IsValid      = true,
                SignalId     = $"{SetId}:{p.CurrentBar}"
            };
        }

        private void UpdateEMAs(double close)
        {
            // ── Advance ZLEMA price ring buffers ──────────────────────────
            // Ring stores recent closes so we can retrieve price[lag] bars ago.
            // Oldest value lives at ringIdx after the write-then-advance.
            _ring9[_ring9Idx]   = close;
            _ring9Idx  = (_ring9Idx  + 1) % _ring9.Length;
            if (_ring9Count  < _ring9.Length) _ring9Count++;

            _ring21[_ring21Idx] = close;
            _ring21Idx = (_ring21Idx + 1) % _ring21.Length;
            if (_ring21Count < _ring21.Length) _ring21Count++;

            // Lag prices for error-correction term.
            // Before ring is full: pass current price → degrades to standard EMA.
            double lag9Price  = (_ring9Count  >= _ring9.Length)  ? _ring9[_ring9Idx]   : close;
            double lag21Price = (_ring21Count >= _ring21.Length) ? _ring21[_ring21Idx] : close;

            // Seed on first bar
            if (double.IsNaN(_ema9Now))
            {
                _ema9Now    = close;
                _ema21Now   = close;
                _ema9Prev   = double.NaN;
                _ema21Prev  = double.NaN;
                _warmupBars = 1;
                return;
            }

            _ema9Prev  = _ema9Now;
            _ema21Prev = _ema21Now;
            _ema9Now   = MathFlow.ZLEmaStep(close, lag9Price,  _ema9Prev,  9);
            _ema21Now  = MathFlow.ZLEmaStep(close, lag21Price, _ema21Prev, 21);
            _warmupBars++;
        }
    }


    // =========================================================================
    // ADX_Trend — ADX > 25 + fresh DI crossover
    // =========================================================================

    /// <summary>
    /// Fires when ADX crosses above the trending threshold AND the directional
    /// lines (+DI/-DI) have just crossed, indicating the ONSET of a trend rather
    /// than deep inside one where most of the move has already occurred.
    ///
    /// Uses full Wilder-smoothed ADX computed incrementally — no NT8 indicator.
    /// </summary>
    public class ADX_Trend : IConditionSet
    {
        public string SetId => "ADX_Trend_v1";
        public string LastDiagnostic => "";

        private double _tickSize, _tickValue;

        // ── ADX incremental state ─────────────────────────────────────────
        private const int ADX_PERIOD = 14;

        // Wilder smoothing state for +DM, -DM, TR
        private double _smoothPlusDM  = double.NaN;
        private double _smoothMinusDM = double.NaN;
        private double _smoothTR      = double.NaN;
        private double _adxPrev       = double.NaN;

        // Previous bar's OHLC for DM calculation
        private double _prevHigh = double.NaN;
        private double _prevLow  = double.NaN;
        private double _prevClose = double.NaN;

        // Previous bar's DI values for cross detection
        // _prevPlusDI/_prevMinusDI track the CURRENT running DI used in smoothing.
        // _lastSavedPlusDI/_lastSavedMinusDI are snapshotted at the START of
        // UpdateADX so Evaluate can compare them against this bar's result.PlusDI.
        private double _prevPlusDI  = 0.0;
        private double _prevMinusDI = 0.0;
        private double _lastSavedPlusDI  = 0.0;
        private double _lastSavedMinusDI = 0.0;

        // Warm-up guard
        private int _warmupBars = 0;
        private const int WARMUP_REQUIRED = ADX_PERIOD * 2;

        // Cooldown
        private int _lastSignalBar = -1;
        private const int SIGNAL_COOLDOWN = 10;   // ADX is slow — longer cooldown

        // ── IConditionSet lifecycle ───────────────────────────────────────

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            // Reset all ADX state at session open
            _smoothPlusDM     = double.NaN;
            _smoothMinusDM    = double.NaN;
            _smoothTR         = double.NaN;
            _adxPrev          = double.NaN;
            _prevHigh         = double.NaN;
            _prevLow          = double.NaN;
            _prevClose        = double.NaN;
            _prevPlusDI       = 0.0;
            _prevMinusDI      = 0.0;
            _lastSavedPlusDI  = 0.0;
            _lastSavedMinusDI = 0.0;
            _warmupBars       = 0;
            _lastSignalBar    = -1;
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
                _lastSignalBar = signal.BarIndex;
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p = snapshot.Primary;

            // Update ADX state every bar so it is warm
            ADXResult adx = UpdateADX(p);

            if (!adx.IsValid)    return RawDecision.None;
            if (!adx.IsTrending) return RawDecision.None;   // ADX < 25

            // Cooldown
            if (_lastSignalBar >= 0 && p.CurrentBar - _lastSignalBar < SIGNAL_COOLDOWN)
                return RawDecision.None;

            // ── DI cross detection ────────────────────────────────────────
            // _lastSavedPlusDI/_lastSavedMinusDI = previous bar's DI values,
            // captured at the START of UpdateADX before this bar's smoothing ran.
            // adx.PlusDI/MinusDI = this bar's freshly computed DI values.
            // Comparing them gives a genuine one-bar-lag cross signal.
            bool bullCross = _lastSavedPlusDI <= _lastSavedMinusDI && adx.PlusDI > adx.MinusDI;
            bool bearCross = _lastSavedPlusDI >= _lastSavedMinusDI && adx.PlusDI < adx.MinusDI;

            if (!bullCross && !bearCross) return RawDecision.None;

            bool isLong = bullCross;

            // ── Context gates ─────────────────────────────────────────────
            int regime = (int)snapshot.Get(SnapKeys.Regime);
            if (isLong  && regime < 0) return RawDecision.None;
            if (!isLong && regime > 0) return RawDecision.None;

            if (snapshot.VWAP > 0)
            {
                if (isLong  && p.Close < snapshot.VWAP) return RawDecision.None;
                if (!isLong && p.Close > snapshot.VWAP) return RawDecision.None;
            }

            // Swing structure alignment (optional bonus — no block if absent)
            double swingTrend = snapshot.Get(SnapKeys.SwingTrend);

            // ── Score ─────────────────────────────────────────────────────
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;
            int score = 62;

            // ADX strength bonus
            if (adx.ADX >= 30) score += 4;
            if (adx.ADX >= 40) score += 4;

            // DI separation — wide separation = confirmed trend
            double diSep = Math.Abs(adx.PlusDI - adx.MinusDI);
            if (diSep > 5)  score += 3;
            if (diSep > 10) score += 3;

            // Swing alignment bonus
            if ((isLong && swingTrend > 0) || (!isLong && swingTrend < 0)) score += 4;

            score  = Math.Min(score, 80);

            // ── Decision ─────────────────────────────────────────────────
            double stop = isLong
                ? Math.Min(p.Low - atr * 0.5,  p.Close - atr)
                : Math.Max(p.High + atr * 0.5, p.Close + atr);

            return new RawDecision
            {
                Direction    = isLong ? SignalDirection.Long : SignalDirection.Short,
                Source       = SignalSource.ADX_TrendSignal,
                EntryPrice   = p.Close,
                StopPrice    = stop,
                TargetPrice  = isLong ? p.Close + atr * 1.5 : p.Close - atr * 1.5,
                Target2Price = isLong ? p.Close + atr * 3.0 : p.Close - atr * 3.0,
                Label        = $"ADX {adx.ADX:F1} DI+={adx.PlusDI:F1} DI-={adx.MinusDI:F1} [{SetId}]",
                RawScore     = score,
                IsValid      = true,
                SignalId     = $"{SetId}:{p.CurrentBar}"
            };
        }

        private ADXResult UpdateADX(BarSnapshot p)
        {
            // Seed on first bar
            if (double.NaN.Equals(_prevHigh))
            {
                _prevHigh  = p.High;
                _prevLow   = p.Low;
                _prevClose = p.Close;
                _warmupBars++;
                return ADXResult.Invalid;
            }

            // Save last bar's DI values BEFORE updating smoothed components.
            // These are what Evaluate() will compare against adx.PlusDI/MinusDI
            // to detect a fresh cross. If we save them after ADX_Calculate,
            // _prevPlusDI == adx.PlusDI and the cross is never detected.
            double savedPlusDI  = _prevPlusDI;
            double savedMinusDI = _prevMinusDI;

            // Calculate raw DM and TR for this bar
            double plusDM, minusDM;
            MathIndicators.DM_Calculate(p.High, _prevHigh, p.Low, _prevLow,
                out plusDM, out minusDM);

            double tr = MathIndicators.TrueRange(p.High, p.Low, _prevClose);

            // Wilder-smooth DM and TR
            _smoothPlusDM  = MathIndicators.Wilder_Smooth(_smoothPlusDM,  plusDM,  ADX_PERIOD);
            _smoothMinusDM = MathIndicators.Wilder_Smooth(_smoothMinusDM, minusDM, ADX_PERIOD);
            _smoothTR      = MathIndicators.Wilder_Smooth(_smoothTR,      tr,      ADX_PERIOD);

            // Full ADX result
            ADXResult result = ADXResult.Invalid;
            if (_warmupBars >= WARMUP_REQUIRED &&
                !double.IsNaN(_smoothPlusDM) && !double.IsNaN(_smoothTR))
            {
                result = MathIndicators.ADX_Calculate(
                    _smoothPlusDM, _smoothMinusDM, _smoothTR, _adxPrev, ADX_PERIOD);
                _adxPrev = result.ADX;
                // Update current DI for next bar's savedPlusDI/savedMinusDI
                _prevPlusDI  = result.PlusDI;
                _prevMinusDI = result.MinusDI;
            }
            else if (!double.IsNaN(_smoothTR) && _smoothTR >= 1e-12)
            {
                // Update DI even during warm-up so the saved values are meaningful
                _prevPlusDI  = (_smoothPlusDM  / _smoothTR) * 100.0;
                _prevMinusDI = (_smoothMinusDM / _smoothTR) * 100.0;
            }

            // Advance prev bar OHLC state
            _prevHigh  = p.High;
            _prevLow   = p.Low;
            _prevClose = p.Close;
            _warmupBars++;

            // Attach the saved (previous-bar) DI values to the result so Evaluate
            // can compare them against result.PlusDI/MinusDI for cross detection.
            // We do this by returning them through a wrapper — but ADXResult is
            // immutable and sealed. Instead, store them as instance fields that
            // Evaluate reads directly.
            _lastSavedPlusDI  = savedPlusDI;
            _lastSavedMinusDI = savedMinusDI;

            return result;
        }
    }
}
