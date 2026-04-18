#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    // =========================================================================
    // FailedAuction.cs — Failed Auction condition set
    //
    // CONCEPT (Market Profile / Auction Theory):
    //   A Failed Auction occurs when price reaches an extreme HIGH or LOW but
    //   fails to attract two-way participation. The auction process requires
    //   both buyers and sellers to engage at a price for that level to be
    //   "accepted." When only one side shows up, price immediately rejects and
    //   leaves the level without establishing fair value.
    //
    //   Evidence of a failed auction:
    //     (a) Long rejection wick — price reached the extreme and was immediately
    //         pushed away. The wick represents the failed probe.
    //     (b) Close in the opposing half of the bar — the timeframe that created
    //         the extreme did not accept it. Price returned to value same bar.
    //     (c) Delta disagrees with the direction — VolDeltaSh < 0 at a new high
    //         means sellers were aggressive AT the high (absorption). BarDelta < 0
    //         means the entire bar was net selling despite making a new high.
    //         Either confirms the auction failed: buyers exhausted at the extreme.
    //
    //   A failed auction creates an OBLIGATION for price to return. The level
    //   was never properly auctioned — no two-way trade confirmed fair value.
    //   The market will revisit it to complete the auction. This gives a
    //   reliable magnet target and, critically, a defined trade setup when
    //   price returns: if the same rejection occurs again, the level is acting
    //   as confirmed S/R and a trade in the opposing direction is warranted.
    //
    // ENTRY LOGIC:
    //   This set does NOT trade the bar that creates the failed auction.
    //   It marks the extreme price level and waits for price to return.
    //
    //   Return to a marked HIGH extreme:
    //     → Bearish candle on the return bar = SHORT (auction fails again)
    //     → Price breaks through cleanly = level consumed, discard
    //
    //   Return to a marked LOW extreme:
    //     → Bullish candle on the return bar = LONG (auction fails again)
    //
    // DETECTION CRITERIA for a failed auction HIGH:
    //   1. New N-bar high (highest in last LOOKBACK bars)
    //   2. Upper wick ≥ body × WICK_BODY_RATIO (rejection, not continuation)
    //   3. Close in the lower half of the bar range (timeframe rejected the high)
    //   4. VolDeltaSh < 0 OR BarDelta < 0 (delta confirms absorption / exhaustion)
    //   Mirror logic for failed auction LOW.
    //
    //   Without Volumetric data: criterion 4 uses BarDelta only. Set fires less
    //   frequently but does not generate false signals — correct degradation.
    //
    // ENTRY / STOP / TARGET:
    //   High extreme return → SHORT
    //     Entry:   close of the return + rejection bar
    //     Stop:    marked high + 2 ticks (above the failed auction extreme)
    //     T1:      VWAP (natural mean-reversion magnet)
    //     T2:      1.5×ATR from entry
    //
    //   Low extreme return → LONG
    //     Entry:   close of the return + rejection bar
    //     Stop:    marked low − 2 ticks
    //     T1:      VWAP
    //     T2:      1.5×ATR from entry
    //
    //   Score: 62 base + up to +18 (wick quality, delta confirmation, confluence)
    //   Cap:   80 — confirms a level but does not predict a new trend. Placed
    //          after Wyckoff in the engine to avoid priority conflicts.
    //
    // REGISTRATION:
    //   Add to HostStrategy.CreateLogic() after Wyckoff sets:
    //     new ConditionSets.FailedAuction(),
    // =========================================================================

    public class FailedAuction : IConditionSet
    {
        public string SetId => "FailedAuction_v1";
        public string LastDiagnostic => _lastBailReason;

        private string _lastBailReason = "";

        // ── Instrument params ─────────────────────────────────────────────
        private double _tickSize;
        private double _tickValue;

        // ── Detection constants ───────────────────────────────────────────
        private const int    LOOKBACK         = 10;   // bars back to define a "new extreme"
        private const double WICK_BODY_RATIO  = 1.5;  // wick must be 1.5× the body size
        private const int    MAX_AGE_BARS     = 100;  // marked level expires after N bars
        private const int    REENTRY_BARS     = 5;
        private const double RETURN_TOLERANCE = 0.3;  // fraction of ATR = "at the level"

        // ── Marked failed auction levels ──────────────────────────────────
        // One failed high and one failed low tracked at a time.
        // Replaced if a stronger (more extreme) failed auction forms before
        // the current one is triggered.
        private double _failedHigh    = 0.0;
        private int    _failedHighBar = -1;
        private double _failedLow     = 0.0;
        private int    _failedLowBar  = -1;

        // ── Re-entry suppression ──────────────────────────────────────────
        private int _lastFillBar = -1;

        // ── IConditionSet lifecycle ───────────────────────────────────────

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            // Failed auction levels reset each session.
            // Overnight extremes are handled by Wyckoff (PrevDayHigh/Low),
            // not here — this set is RTH intraday only.
            _failedHigh    = 0.0;
            _failedHighBar = -1;
            _failedLow     = 0.0;
            _failedLowBar  = -1;
            _lastFillBar   = -1;
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
                _lastFillBar = signal.BarIndex;
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        // ── Main evaluation ───────────────────────────────────────────────

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;
            var p   = snapshot.Primary;
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;

            if (p.Highs == null || p.Highs.Length < LOOKBACK) return RawDecision.None;
            if (p.Lows  == null || p.Lows.Length  < LOOKBACK) return RawDecision.None;

            // Re-entry suppression
            if (_lastFillBar > 0 && p.CurrentBar - _lastFillBar < REENTRY_BARS)
            {
                return new RawDecision { Direction = SignalDirection.None, Label = "REJ:FA Cooldown", IsValid = false };
            }

            double barDelta = snapshot.Get(SnapKeys.BarDelta);
            double deltaSh  = snapshot.Get(SnapKeys.VolDeltaSh);
            double deltaSl  = snapshot.Get(SnapKeys.VolDeltaSl);

            // ── Step 1: Detect and mark new failed auction extremes ────────
            TryMarkFailedHigh(p, atr, barDelta, deltaSh);
            TryMarkFailedLow (p, atr, barDelta, deltaSl);

            // Expire stale marks
            if (_failedHighBar > 0 && p.CurrentBar - _failedHighBar > MAX_AGE_BARS)
                _failedHigh = 0.0;
            if (_failedLowBar  > 0 && p.CurrentBar - _failedLowBar  > MAX_AGE_BARS)
                _failedLow  = 0.0;

            // ── Step 2: Check for return to marked level ──────────────────
            if (_failedHigh > 0)
            {
                var r = TryShortReturn(p, atr, snapshot);
                if (r.IsValid || r.Direction != SignalDirection.None) return r;
            }
            if (_failedLow > 0)
            {
                var r = TryLongReturn(p, atr, snapshot);
                if (r.IsValid || r.Direction != SignalDirection.None) return r;
            }

            return RawDecision.None;
        }

        // ── Failed auction detection ──────────────────────────────────────

        private void TryMarkFailedHigh(BarSnapshot p, double atr, double barDelta, double deltaSh)
        {
            // Must be a new N-bar high
            double highestRecent = 0.0;
            for (int i = 1; i < LOOKBACK && i < p.Highs.Length; i++)
                if (p.Highs[i] > highestRecent) highestRecent = p.Highs[i];
            
            if (p.High <= highestRecent) return;

            // Rejection wick: upper wick ≥ body × ratio
            double body      = Math.Abs(p.Close - p.Open);
            double upperWick = p.High - Math.Max(p.Close, p.Open);
            bool wickFail = (body > 0 && upperWick < body * WICK_BODY_RATIO) || (body <= 0 && upperWick < atr * 0.05);
            
            // Close in lower half of bar range — timeframe rejected the high
            double barMid = (p.High + p.Low) / 2.0;
            bool closeFail = p.Close > barMid;

            // Delta confirmation
            bool deltaConfirm = (deltaSh < 0) || (barDelta < 0);

            if (wickFail || closeFail || !deltaConfirm)
            {
                // Visible rejections for analysis
                string label = wickFail ? "REJ:FA Wick" : (closeFail ? "REJ:FA Close" : "REJ:FA Delta");
                _lastBailReason = label;
                // Note: These are 'internal' rejections, they don't return RawDecision yet because 
                // they are 'marks' for future returns. We only return RawDecision in Evaluate().
                return;
            }

            // Mark
            _failedHigh    = p.High;
            _failedHighBar = p.CurrentBar;
        }

        private void TryMarkFailedLow(BarSnapshot p, double atr, double barDelta, double deltaSl)
        {
            // Must be a new N-bar low
            double lowestRecent = double.MaxValue;
            for (int i = 1; i < LOOKBACK && i < p.Lows.Length; i++)
                if (p.Lows[i] < lowestRecent) lowestRecent = p.Lows[i];
            
            if (p.Low >= lowestRecent) return;

            // Rejection wick: lower wick ≥ body × ratio
            double body      = Math.Abs(p.Close - p.Open);
            double lowerWick = Math.Min(p.Close, p.Open) - p.Low;
            bool wickFail = (body > 0 && lowerWick < body * WICK_BODY_RATIO) || (body <= 0 && lowerWick < atr * 0.05);

            // Close in upper half of bar range — timeframe rejected the low
            double barMid = (p.High + p.Low) / 2.0;
            bool closeFail = p.Close < barMid;

            // Delta confirmation
            bool deltaConfirm = (deltaSl > 0) || (barDelta > 0);

            if (wickFail || closeFail || !deltaConfirm)
            {
                string label = wickFail ? "REJ:FA Wick" : (closeFail ? "REJ:FA Close" : "REJ:FA Delta");
                _lastBailReason = label;
                return;
            }

            _failedLow    = p.Low;
            _failedLowBar = p.CurrentBar;
        }

        // ── Return trade builders ─────────────────────────────────────────

        private RawDecision TryShortReturn(BarSnapshot p, double atr, MarketSnapshot snap)
        {
            double tolerance = atr * RETURN_TOLERANCE;
            // Price must return into the vicinity of the failed high
            if (!(p.High >= _failedHigh - tolerance && p.High <= _failedHigh + tolerance))
                return RawDecision.None;

            // Bearish confirmation — auction failing again on the return
            if (p.Close > (p.High + p.Low) / 2.0) 
            {
                return new RawDecision { Direction = SignalDirection.Short, Label = "REJ:FA ReturnWeak", IsValid = false };
            }

            // Score
            int score = 62;
            double barDelta = snap.Get(SnapKeys.BarDelta);
            if (barDelta < 0)                       score += 5;   // return bar net selling
            if (snap.Get(SnapKeys.VolDeltaSh) < 0)  score += 5;   // absorption confirmed at high
            score  = Math.Min(score, 80);

            double t1 = snap.VWAP > 0 && snap.VWAP < p.Close
                ? snap.VWAP
                : p.Close - atr * 1.0;

            // Capture before clearing
            double markedHigh = _failedHigh;
            _failedHigh    = 0.0;
            _failedHighBar = -1;

            return new RawDecision
            {
                Direction    = SignalDirection.Short,
                Source       = SignalSource.FailedAuction,
                EntryPrice   = p.Close,
                StopPrice    = markedHigh + 2 * _tickSize,
                TargetPrice  = t1,
                Target2Price = p.Close - atr * 1.5,
                Label        = $"FailedAuction short @ {markedHigh:F2} [{SetId}]",
                RawScore     = score,
                IsValid      = true,
                SignalId     = $"{SetId}:Hi:{p.CurrentBar}"
            };
        }

        private RawDecision TryLongReturn(BarSnapshot p, double atr, MarketSnapshot snap)
        {
            double tolerance = atr * RETURN_TOLERANCE;
            if (!(p.Low <= _failedLow + tolerance && p.Low >= _failedLow - tolerance))
                return RawDecision.None;

            // Bullish confirmation — auction failing again on the return
            if (p.Close < (p.High + p.Low) / 2.0) 
            {
                return new RawDecision { Direction = SignalDirection.Long, Label = "REJ:FA ReturnWeak", IsValid = false };
            }

            int score = 62;
            double barDelta = snap.Get(SnapKeys.BarDelta);
            if (barDelta > 0)                       score += 5;   // return bar net buying
            if (snap.Get(SnapKeys.VolDeltaSl) > 0)  score += 5;   // absorption confirmed at low
            score  = Math.Min(score, 80);

            double t1 = snap.VWAP > 0 && snap.VWAP > p.Close
                ? snap.VWAP
                : p.Close + atr * 1.0;

            // Capture before clearing
            double markedLow = _failedLow;
            _failedLow    = 0.0;
            _failedLowBar = -1;

            return new RawDecision
            {
                Direction    = SignalDirection.Long,
                Source       = SignalSource.FailedAuction,
                EntryPrice   = p.Close,
                StopPrice    = markedLow - 2 * _tickSize,
                TargetPrice  = t1,
                Target2Price = p.Close + atr * 1.5,
                Label        = $"FailedAuction long  @ {markedLow:F2} [{SetId}]",
                RawScore     = score,
                IsValid      = true,
                SignalId     = $"{SetId}:Lo:{p.CurrentBar}"
            };
        }
    }
}
