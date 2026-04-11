#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    // =========================================================================
    // ICT_ModelEntry.cs — ICT Power of Three (PO3) Composite Condition Set
    //
    // This is NOT another isolated indicator signal. It is a STATE MACHINE that
    // implements the full ICT narrative sequence:
    //
    //   1. ESTABLISH HTF BIAS    → 4H/2H EMA alignment sets directional filter
    //   2. IDENTIFY LIQUIDITY    → Track session extremes + equal highs/lows
    //   3. DETECT MANIPULATION   → Sweep of liquidity AGAINST the HTF bias
    //   4. CONFIRM REVERSAL      → CHoCH on execution TF after the sweep
    //   5. ENTER AT PD ARRAY     → FVG or OB retest in the new direction
    //   6. TARGET OPPOSING LIQ   → Take profit at the draw on liquidity
    //
    // No state transition fires without the previous phase completing.
    // The ConfluenceEngine and FootprintEntryAdvisor still score and veto
    // the final signal — this set produces the structural narrative.
    //
    // Integration:
    //   Register in HostStrategy.CreateLogic():
    //     new ICT_ModelEntry()
    //
    // Data consumed from MarketSnapshot:
    //   SnapKeys.H4HrEmaBias, H2HrEmaBias, H1EmaBias     (Layer A — HTF bias)
    //   SnapKeys.SwingTrend, CHoCHFiredLong/Short          (StructuralLabeler)
    //   SnapKeys.LastSwingHigh, LastSwingLow                (swing references)
    //   SnapKeys.ConfirmedSwings                            (structure validity)
    //   snapshot.Tokyo, snapshot.London                     (session levels)
    //   snapshot.PrevDayHigh, PrevDayLow                   (daily levels)
    //   SnapKeys.HasVolumetric, BarDelta, BullDivergence   (order flow)
    //   snapshot.ATR                                        (volatility scaling)
    //
    // DESIGN PRINCIPLES:
    //   - Zero new math. Consumes existing MathSMC + StructuralLabeler output.
    //   - State resets on session open. No cross-session bleed.
    //   - One entry per narrative sequence. Cooldown prevents chasing.
    //   - Stop at manipulation wick, not arbitrary ATR multiples.
    //   - RawScore scaled by narrative completeness (65–92 range).
    // =========================================================================

    /// <summary>
    /// State machine phases for the ICT PO3 model.
    /// Each phase REQUIRES the previous phase to have completed.
    /// </summary>
    internal enum ICTPhase
    {
        /// <summary>Waiting for HTF bias to establish.</summary>
        WaitingForBias    = 0,

        /// <summary>Bias established. Monitoring for liquidity sweep against bias.</summary>
        BiasEstablished   = 1,

        /// <summary>Sweep detected. Waiting for LTF CHoCH to confirm reversal.</summary>
        SweepDetected     = 2,

        /// <summary>CHoCH confirmed. Scanning for entry at FVG/OB in new direction.</summary>
        WaitingForEntry   = 3,

        /// <summary>Entry emitted. Cooldown active.</summary>
        Cooldown          = 4,
    }

    /// <summary>
    /// Tracks one liquidity pool — a price level where stops are clustered.
    /// Session extremes, equal highs/lows, and previous day H/L all qualify.
    /// </summary>
    internal struct LiquidityPool
    {
        public double Price;
        public int    CreatedBar;
        public bool   IsHighSide;  // true = buy-side liquidity (above price), false = sell-side
        public string Label;
        public bool   IsValid => Price > 0;

        public static readonly LiquidityPool Empty = new LiquidityPool();
    }

    /// <summary>
    /// Tracks the displacement candle that caused the CHoCH.
    /// The FVG and OB from this candle are the highest-quality entry zones.
    /// </summary>
    internal struct DisplacementRecord
    {
        public double High;
        public double Low;
        public double Open;
        public double Close;
        public int    BarIndex;
        public bool   IsBullish;   // true = bullish displacement (close > open significantly)
        public bool   IsValid;

        public static readonly DisplacementRecord Empty = new DisplacementRecord();
    }

    public class ICT_ModelEntry : IConditionSet
    {
        public string SetId => "ICT_PO3_v1";
        public string LastDiagnostic => _lastDiag;

        // ── Config ──────────────────────────────────────────────────────
        private double _tickSize;
        private double _tickValue;

        // ── State Machine ───────────────────────────────────────────────
        private ICTPhase _phase;
        private int      _phaseBar;         // bar when current phase was entered
        private string   _lastDiag = "";

        // ── Phase 1: HTF Bias ───────────────────────────────────────────
        private int _htfBiasDirection;       // +1 = bullish, -1 = bearish, 0 = undefined
        private int _htfBiasBar;

        // ── Phase 2: Liquidity Pools ────────────────────────────────────
        // Track the two most relevant pools: session extremes + PDH/PDL
        private LiquidityPool _primaryPool;   // highest-priority target
        private LiquidityPool _secondaryPool;

        // ── Phase 3: Sweep Record ───────────────────────────────────────
        private double _sweepWickPrice;       // the extreme of the sweep candle
        private int    _sweepBar;
        private bool   _sweepWasBuySide;      // true if swept buy-side (highs), false if sell-side

        // ── Phase 4: CHoCH + Displacement ───────────────────────────────
        private int    _chochBar;
        private DisplacementRecord _displacement;

        // FVG tracking — built from the displacement zone
        private FairValueGap _entryFVG;

        // OB tracking — the last opposing candle before displacement
        private double _obHigh, _obLow;
        private int    _obBar;
        private bool   _obIsValid;

        // ── Phase 5: Cooldown ───────────────────────────────────────────
        private int _lastFillBar = -1;
        private const int COOLDOWN_BARS = 12;
        private const int MAX_ENTRY_WAIT_BARS = 20; // max bars to wait for entry after CHoCH
        private const int MAX_SWEEP_WAIT_BARS = 30; // max bars after sweep to see CHoCH

        // ── Scoring constants ───────────────────────────────────────────
        private const int BASE_SCORE          = 65;  // minimum for a complete narrative
        private const int BONUS_STRONG_BIAS   = 5;   // all 3 HTF TFs aligned
        private const int BONUS_SESSION_SWEEP = 5;   // sweep was a session extreme (not just swing)
        private const int BONUS_DISPLACEMENT  = 5;   // displacement candle was > 0.5 ATR body
        private const int BONUS_FVG_ENTRY     = 5;   // entry at FVG (not just OB)
        private const int BONUS_ORDERFLOW     = 7;   // footprint confirms at entry

        // ── Session filter ──────────────────────────────────────────────
        // ICT models fire primarily in the NY AM session (9:30–11:00 ET)
        // and the London/NY overlap. Outside these windows, signals degrade.
        private const int SESSION_BONUS_PRIME   = 0;  // no bonus, just allowed
        private const int SESSION_PENALTY_LATE  = -8; // after 14:00 ET
        private const int SESSION_PENALTY_LUNCH = -5; // 11:00–14:00 ET dead zone


        // =================================================================
        // IConditionSet IMPLEMENTATION
        // =================================================================

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _phase            = ICTPhase.WaitingForBias;
            _phaseBar         = 0;
            _htfBiasDirection = 0;
            _htfBiasBar       = 0;
            _primaryPool      = LiquidityPool.Empty;
            _secondaryPool    = LiquidityPool.Empty;
            _sweepWickPrice   = 0;
            _sweepBar         = 0;
            _chochBar         = 0;
            _displacement     = DisplacementRecord.Empty;
            _entryFVG         = FairValueGap.Empty;
            _obHigh           = 0;
            _obLow            = 0;
            _obBar            = 0;
            _obIsValid        = false;
            _lastFillBar      = -1;
            _lastDiag         = "session_reset";
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
            {
                _lastFillBar = signal.BarIndex;
                _phase       = ICTPhase.Cooldown;
                _phaseBar    = signal.BarIndex;
            }
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }


        // =================================================================
        // MAIN EVALUATION — the narrative state machine
        // =================================================================

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return RawDecision.None;

            var    p   = snapshot.Primary;
            double atr = snapshot.ATR > 0 ? snapshot.ATR : _tickSize * 10;
            int    bar = p.CurrentBar;

            // ── Global cooldown after fill ───────────────────────────────
            if (_lastFillBar >= 0 && bar - _lastFillBar < COOLDOWN_BARS)
            {
                _lastDiag = "cooldown";
                return RawDecision.None;
            }

            // ── Run the state machine ────────────────────────────────────
            switch (_phase)
            {
                case ICTPhase.WaitingForBias:
                    return Phase1_EstablishBias(snapshot, p, bar);

                case ICTPhase.BiasEstablished:
                    return Phase2_MonitorForSweep(snapshot, p, bar, atr);

                case ICTPhase.SweepDetected:
                    return Phase3_WaitForCHoCH(snapshot, p, bar, atr);

                case ICTPhase.WaitingForEntry:
                    return Phase4_ScanForEntry(snapshot, p, bar, atr);

                case ICTPhase.Cooldown:
                    if (bar - _phaseBar >= COOLDOWN_BARS)
                    {
                        _phase    = ICTPhase.WaitingForBias;
                        _phaseBar = bar;
                    }
                    _lastDiag = "cooldown_phase";
                    return RawDecision.None;

                default:
                    return RawDecision.None;
            }
        }


        // =================================================================
        // PHASE 1 — Establish HTF Bias
        //
        // Requirement: H4 EMA bias must be defined (non-zero).
        //              H2 or H1 must AGREE with H4.
        //              Structure must have >= 4 confirmed swings.
        //
        // This is the "dealer's hand" — we only play when we can see
        // where smart money is positioned on the macro timeframe.
        // =================================================================

        private RawDecision Phase1_EstablishBias(MarketSnapshot snap, BarSnapshot p, int bar)
        {
            double h4b = snap.Get(SnapKeys.H4HrEmaBias);
            double h2b = snap.Get(SnapKeys.H2HrEmaBias);
            double h1b = snap.Get(SnapKeys.H1EmaBias);
            double confirmedSwings = snap.Get(SnapKeys.ConfirmedSwings);

            // Need the macro anchor
            if (h4b == 0)
            {
                _lastDiag = "ph1:h4_undefined";
                return RawDecision.None;
            }

            // Need at least one confirming lower TF
            bool h2Agrees = (h4b > 0 && h2b > 0) || (h4b < 0 && h2b < 0);
            bool h1Agrees = (h4b > 0 && h1b > 0) || (h4b < 0 && h1b < 0);

            if (!h2Agrees && !h1Agrees)
            {
                _lastDiag = "ph1:no_htf_agreement";
                return RawDecision.None;
            }

            // Need minimum structure
            if (confirmedSwings < 4)
            {
                _lastDiag = "ph1:insufficient_swings";
                return RawDecision.None;
            }

            // ── Bias established ─────────────────────────────────────────
            _htfBiasDirection = h4b > 0 ? 1 : -1;
            _htfBiasBar       = bar;

            // Build liquidity pools from session data
            UpdateLiquidityPools(snap, p, bar);

            _phase    = ICTPhase.BiasEstablished;
            _phaseBar = bar;
            _lastDiag = $"ph1→ph2:bias={(_htfBiasDirection > 0 ? "BULL" : "BEAR")}";

            return RawDecision.None;
        }


        // =================================================================
        // PHASE 2 — Monitor for Liquidity Sweep
        //
        // A sweep is manipulation — price runs BEYOND a liquidity pool
        // to trigger stops, then reverses. The sweep MUST be AGAINST
        // the HTF bias:
        //
        //   Bullish bias → we want a SELL-SIDE sweep (price dips below
        //                  session lows to trap shorts, then reverses up)
        //
        //   Bearish bias → we want a BUY-SIDE sweep (price spikes above
        //                  session highs to trap longs, then reverses down)
        //
        // The candle must WICK beyond the pool but CLOSE back inside.
        // A close beyond = genuine breakout, not manipulation.
        // =================================================================

        private RawDecision Phase2_MonitorForSweep(MarketSnapshot snap, BarSnapshot p, int bar, double atr)
        {
            // Continuously update liquidity pools (session data evolves)
            UpdateLiquidityPools(snap, p, bar);

            // Re-evaluate bias — if H4 flips, restart
            double h4b = snap.Get(SnapKeys.H4HrEmaBias);
            if (h4b == 0 || (_htfBiasDirection > 0 && h4b < 0) || (_htfBiasDirection < 0 && h4b > 0))
            {
                _phase    = ICTPhase.WaitingForBias;
                _phaseBar = bar;
                _lastDiag = "ph2:bias_flipped_restart";
                return RawDecision.None;
            }

            // ── Check for sell-side sweep (bullish bias wants this) ──────
            if (_htfBiasDirection > 0)
            {
                // Look for wick below sell-side liquidity (lows)
                LiquidityPool target = GetBestSellSidePool();
                if (target.IsValid)
                {
                    // Sweep condition: Low goes below pool, Close stays above
                    if (p.Low < target.Price - _tickSize && p.Close > target.Price)
                    {
                        _sweepWickPrice  = p.Low;
                        _sweepBar        = bar;
                        _sweepWasBuySide = false; // swept sell-side
                        _phase           = ICTPhase.SweepDetected;
                        _phaseBar        = bar;
                        _lastDiag        = $"ph2→ph3:sweep_sell@{target.Price:F2}({target.Label})";
                        return RawDecision.None;
                    }
                }
            }

            // ── Check for buy-side sweep (bearish bias wants this) ───────
            if (_htfBiasDirection < 0)
            {
                LiquidityPool target = GetBestBuySidePool();
                if (target.IsValid)
                {
                    // Sweep condition: High goes above pool, Close stays below
                    if (p.High > target.Price + _tickSize && p.Close < target.Price)
                    {
                        _sweepWickPrice  = p.High;
                        _sweepBar        = bar;
                        _sweepWasBuySide = true; // swept buy-side
                        _phase           = ICTPhase.SweepDetected;
                        _phaseBar        = bar;
                        _lastDiag        = $"ph2→ph3:sweep_buy@{target.Price:F2}({target.Label})";
                        return RawDecision.None;
                    }
                }
            }

            _lastDiag = "ph2:waiting_for_sweep";
            return RawDecision.None;
        }


        // =================================================================
        // PHASE 3 — Wait for CHoCH Confirmation
        //
        // After the sweep, smart money reverses. The FIRST structural
        // break against the prior LTF trend = CHoCH = confirmation.
        //
        // StructuralLabeler already publishes CHoCHFiredLong/Short to
        // the snapshot bag. We just read it.
        //
        // Also track the displacement candle — if the CHoCH bar itself
        // is a strong displacement (body > 50% of range, range > 0.5 ATR),
        // it creates the FVG and OB we'll use for entry.
        //
        // Timeout: if no CHoCH within MAX_SWEEP_WAIT_BARS, the sweep
        // was a genuine breakout, not manipulation. Reset to Phase 1.
        // =================================================================

        private RawDecision Phase3_WaitForCHoCH(MarketSnapshot snap, BarSnapshot p, int bar, double atr)
        {
            // Timeout — sweep was genuine, not manipulation
            if (bar - _sweepBar > MAX_SWEEP_WAIT_BARS)
            {
                _phase    = ICTPhase.WaitingForBias;
                _phaseBar = bar;
                _lastDiag = "ph3:sweep_timeout";
                return RawDecision.None;
            }

            bool wantLong  = _htfBiasDirection > 0; // bullish bias → we want a bullish CHoCH
            bool chochLong = snap.GetFlag(SnapKeys.CHoCHFiredLong);
            bool chochShrt = snap.GetFlag(SnapKeys.CHoCHFiredShort);

            bool confirmed = wantLong ? chochLong : chochShrt;

            if (!confirmed)
            {
                _lastDiag = $"ph3:awaiting_choch_{(wantLong ? "bull" : "bear")}";
                return RawDecision.None;
            }

            // ── CHoCH confirmed! Record displacement ─────────────────────
            _chochBar = bar;

            double body  = Math.Abs(p.Close - p.Open);
            double range = p.High - p.Low;
            bool   isDisplacement = range > atr * 0.4 && (range > 0 ? body / range > 0.45 : false);

            _displacement = new DisplacementRecord
            {
                High      = p.High,
                Low       = p.Low,
                Open      = p.Open,
                Close     = p.Close,
                BarIndex  = bar,
                IsBullish = wantLong,
                IsValid   = isDisplacement
            };

            // ── Build FVG from displacement ──────────────────────────────
            // The displacement candle creates a gap with the candle 2 bars ago.
            // For a bullish displacement: FVG = gap between current Low and High[2]
            if (p.Highs != null && p.Highs.Length >= 3 && p.Lows != null && p.Lows.Length >= 3)
            {
                if (wantLong && p.Lows[0] > p.Highs[2] + _tickSize)
                {
                    _entryFVG = new FairValueGap
                    {
                        Type       = FVGType.Bullish,
                        Upper      = p.Lows[0],
                        Lower      = p.Highs[2],
                        CreatedBar = bar
                    };
                }
                else if (!wantLong && p.Highs[0] < p.Lows[2] - _tickSize)
                {
                    _entryFVG = new FairValueGap
                    {
                        Type       = FVGType.Bearish,
                        Upper      = p.Lows[2],
                        Lower      = p.Highs[0],
                        CreatedBar = bar
                    };
                }
                else
                {
                    _entryFVG = FairValueGap.Empty;
                }
            }

            // ── Build OB from pre-displacement candle ────────────────────
            // Bullish OB = last BEARISH candle before the bullish displacement
            // Bearish OB = last BULLISH candle before the bearish displacement
            BuildOrderBlockFromDisplacement(p, bar, wantLong);

            _phase    = ICTPhase.WaitingForEntry;
            _phaseBar = bar;
            _lastDiag = $"ph3→ph4:choch_{(wantLong ? "bull" : "bear")}_disp={isDisplacement}";

            return RawDecision.None;
        }


        // =================================================================
        // PHASE 4 — Scan for Entry at Premium/Discount Array
        //
        // After CHoCH, price should retrace into one of:
        //   1. The FVG created by the displacement candle (highest quality)
        //   2. The Order Block (last opposing candle before displacement)
        //   3. The 50% retracement of the displacement move (fallback)
        //
        // Entry fires on the FIRST touch. Stop goes below the sweep wick
        // (the manipulation low/high). Target is the opposing liquidity.
        //
        // Timeout: if price doesn't retrace within MAX_ENTRY_WAIT_BARS,
        // the move has already left without us. Don't chase.
        // =================================================================

        private RawDecision Phase4_ScanForEntry(MarketSnapshot snap, BarSnapshot p, int bar, double atr)
        {
            bool isLong = _htfBiasDirection > 0;

            // ── Timeout — move left without us ──────────────────────────
            if (bar - _chochBar > MAX_ENTRY_WAIT_BARS)
            {
                _phase    = ICTPhase.WaitingForBias;
                _phaseBar = bar;
                _lastDiag = "ph4:entry_timeout";
                return RawDecision.None;
            }

            // ── Session filter ──────────────────────────────────────────
            // Don't enter during dead zone or after-hours
            if (p.Session == SessionPhase.AfterHours || p.Session == SessionPhase.PreMarket)
            {
                _lastDiag = "ph4:outside_session";
                return RawDecision.None;
            }

            // ── Try FVG entry first (highest quality) ───────────────────
            bool fvgEntry = false;
            if (_entryFVG.IsValid && !_entryFVG.IsFilled)
            {
                if (isLong && p.Low <= _entryFVG.Upper && p.Close >= _entryFVG.Lower)
                {
                    fvgEntry = true;
                    // Mark as used
                    _entryFVG.IsFilled = true;
                }
                else if (!isLong && p.High >= _entryFVG.Lower && p.Close <= _entryFVG.Upper)
                {
                    fvgEntry = true;
                    _entryFVG.IsFilled = true;
                }
            }

            // ── Try OB entry (second quality) ───────────────────────────
            bool obEntry = false;
            if (!fvgEntry && _obIsValid)
            {
                if (isLong && p.Low <= _obHigh && p.Close >= _obLow)
                {
                    obEntry    = true;
                    _obIsValid = false; // consumed
                }
                else if (!isLong && p.High >= _obLow && p.Close <= _obHigh)
                {
                    obEntry    = true;
                    _obIsValid = false;
                }
            }

            // ── Try 50% retracement entry (fallback) ────────────────────
            bool retEntry = false;
            if (!fvgEntry && !obEntry && _displacement.IsValid)
            {
                double mid = (_displacement.High + _displacement.Low) / 2.0;

                if (isLong && p.Low <= mid + 2 * _tickSize && p.Close > mid)
                    retEntry = true;
                else if (!isLong && p.High >= mid - 2 * _tickSize && p.Close < mid)
                    retEntry = true;
            }

            if (!fvgEntry && !obEntry && !retEntry)
            {
                _lastDiag = "ph4:no_entry_zone_touched";
                return RawDecision.None;
            }

            // =============================================================
            // BUILD THE SIGNAL
            // =============================================================

            // ── Score calculation ────────────────────────────────────────
            int score = BASE_SCORE;

            // HTF alignment bonus
            double h2b = snap.Get(SnapKeys.H2HrEmaBias);
            double h1b = snap.Get(SnapKeys.H1EmaBias);
            bool allAligned = (isLong && h2b > 0 && h1b > 0) || (!isLong && h2b < 0 && h1b < 0);
            if (allAligned) score += BONUS_STRONG_BIAS;

            // Session sweep quality
            if (_primaryPool.Label != null &&
                (_primaryPool.Label.StartsWith("Asia") || _primaryPool.Label.StartsWith("London")))
                score += BONUS_SESSION_SWEEP;

            // Displacement quality
            if (_displacement.IsValid) score += BONUS_DISPLACEMENT;

            // Entry zone quality
            if (fvgEntry) score += BONUS_FVG_ENTRY;

            // Order flow confirmation
            if (snap.GetFlag(SnapKeys.HasVolumetric))
            {
                double barDelta = snap.Get(SnapKeys.BarDelta);
                bool flowAgrees = (isLong && barDelta > 0) || (!isLong && barDelta < 0);
                if (flowAgrees) score += BONUS_ORDERFLOW;

                // CVD divergence bonus — strongest confirmation
                bool bullDiv = snap.GetFlag(SnapKeys.BullDivergence);
                bool bearDiv = snap.GetFlag(SnapKeys.BearDivergence);
                if ((isLong && bullDiv) || (!isLong && bearDiv)) score += 3;
            }

            // Session penalty
            if (p.Session == SessionPhase.LateSession)  score += SESSION_PENALTY_LATE;
            if (p.Session == SessionPhase.MidSession)   score += SESSION_PENALTY_LUNCH;

            // Clamp
            if (score < 50) score = 50;
            if (score > 95) score = 95;

            // ── Stop: below/above the sweep wick ────────────────────────
            // This is the KEY ICT principle — the manipulation candle's
            // wick IS the stop. Smart money swept that level to accumulate;
            // if price returns there, the thesis is wrong.
            double stop;
            if (isLong)
            {
                stop = _sweepWickPrice - 2 * _tickSize;
                // Safety floor: at least 0.3 ATR from entry
                double minStop = p.Close - 0.3 * atr;
                if (stop > minStop) stop = minStop;
            }
            else
            {
                stop = _sweepWickPrice + 2 * _tickSize;
                double maxStop = p.Close + 0.3 * atr;
                if (stop < maxStop) stop = maxStop;
            }

            // ── Validate R:R ────────────────────────────────────────────
            double risk = Math.Abs(p.Close - stop);
            if (risk < RiskDefaults.MIN_STOP_TICKS * _tickSize)
            {
                _lastDiag = "ph4:stop_too_tight";
                return RawDecision.None;
            }

            // ── Target: opposing liquidity ──────────────────────────────
            double target1, target2;
            if (isLong)
            {
                // Target 1: nearest buy-side liquidity (session high)
                LiquidityPool buySide = GetBestBuySidePool();
                target1 = buySide.IsValid ? buySide.Price : p.Close + 2.0 * atr;

                // Target 2: previous day high or 3x ATR, whichever is further
                target2 = snap.PrevDayHigh > p.Close
                    ? Math.Max(snap.PrevDayHigh, p.Close + 3.0 * atr)
                    : p.Close + 3.0 * atr;
            }
            else
            {
                LiquidityPool sellSide = GetBestSellSidePool();
                target1 = sellSide.IsValid ? sellSide.Price : p.Close - 2.0 * atr;

                target2 = snap.PrevDayLow > 0 && snap.PrevDayLow < p.Close
                    ? Math.Min(snap.PrevDayLow, p.Close - 3.0 * atr)
                    : p.Close - 3.0 * atr;
            }

            // Ensure minimum R:R of 1.5
            double reward1 = Math.Abs(target1 - p.Close);
            if (risk > 0 && reward1 / risk < RiskDefaults.MIN_RR_RATIO)
            {
                target1 = isLong
                    ? p.Close + risk * RiskDefaults.MIN_RR_RATIO
                    : p.Close - risk * RiskDefaults.MIN_RR_RATIO;
            }

            // ── Build entry label ───────────────────────────────────────
            string entryType = fvgEntry ? "FVG" : (obEntry ? "OB" : "50%RT");
            string label = $"ICT_PO3 {(isLong ? "Long" : "Short")} " +
                           $"{entryType} post-{(_sweepWasBuySide ? "BSL" : "SSL")}-sweep " +
                           $"[{SetId}]";

            // ── Transition to cooldown ───────────────────────────────────
            _phase    = ICTPhase.Cooldown;
            _phaseBar = bar;
            _lastDiag = $"ph4→SIGNAL:{label} score={score}";

            return new RawDecision
            {
                Direction      = isLong ? SignalDirection.Long : SignalDirection.Short,
                Source         = SignalSource.SMC_CHoCH,  // reversal entry
                EntryPrice     = p.Close,
                StopPrice      = stop,
                TargetPrice    = target1,
                Target2Price   = target2,
                RawScore       = score,
                IsValid        = true,
                Label          = label,
                SignalId       = $"{SetId}:{bar}",
                ConditionSetId = SetId,
                BarIndex       = bar
            };
        }


        // =================================================================
        // LIQUIDITY POOL MANAGEMENT
        //
        // ICT liquidity = where stops are clustered.
        // Buy-side (BSL): above swing highs, session highs, equal highs
        // Sell-side (SSL): below swing lows, session lows, equal lows
        //
        // We track the NEAREST pools on each side. The sweep detection
        // checks if price runs through these pools against the HTF bias.
        // =================================================================

        private void UpdateLiquidityPools(MarketSnapshot snap, BarSnapshot p, int bar)
        {
            // ── Sell-side liquidity pools (below price) ─────────────────
            // Priority: Asia Low > London Low > PrevDay Low > Swing Low
            double asiaLo   = snap.Tokyo.IsValid  ? snap.Tokyo.Low  : 0;
            double londonLo = snap.London.IsValid ? snap.London.Low : 0;
            double pdl      = snap.PrevDayLow;
            double swingLo  = snap.Get(SnapKeys.LastSwingLow);

            // Pick the nearest and most relevant sell-side pool
            _primaryPool   = LiquidityPool.Empty;
            _secondaryPool = LiquidityPool.Empty;

            // Find best sell-side (below)
            LiquidityPool bestSell = LiquidityPool.Empty;
            LiquidityPool nextSell = LiquidityPool.Empty;

            if (asiaLo > 0 && asiaLo < p.Close)
                bestSell = new LiquidityPool { Price = asiaLo, CreatedBar = bar, IsHighSide = false, Label = "AsiaLow" };
            if (londonLo > 0 && londonLo < p.Close)
            {
                var lp = new LiquidityPool { Price = londonLo, CreatedBar = bar, IsHighSide = false, Label = "LondonLow" };
                if (!bestSell.IsValid || Math.Abs(p.Close - londonLo) < Math.Abs(p.Close - bestSell.Price))
                    { nextSell = bestSell; bestSell = lp; }
                else
                    nextSell = lp;
            }
            if (pdl > 0 && pdl < p.Close)
            {
                var lp = new LiquidityPool { Price = pdl, CreatedBar = bar, IsHighSide = false, Label = "PDL" };
                if (!bestSell.IsValid) bestSell = lp;
                else if (!nextSell.IsValid) nextSell = lp;
            }
            if (swingLo > 0 && swingLo < p.Close && !bestSell.IsValid)
                bestSell = new LiquidityPool { Price = swingLo, CreatedBar = bar, IsHighSide = false, Label = "SwingLow" };

            // Find best buy-side (above)
            double asiaHi   = snap.Tokyo.IsValid  ? snap.Tokyo.High  : 0;
            double londonHi = snap.London.IsValid ? snap.London.High : 0;
            double pdh      = snap.PrevDayHigh;
            double swingHi  = snap.Get(SnapKeys.LastSwingHigh);

            LiquidityPool bestBuy = LiquidityPool.Empty;

            if (asiaHi > 0 && asiaHi > p.Close)
                bestBuy = new LiquidityPool { Price = asiaHi, CreatedBar = bar, IsHighSide = true, Label = "AsiaHigh" };
            if (londonHi > 0 && londonHi > p.Close)
            {
                var lp = new LiquidityPool { Price = londonHi, CreatedBar = bar, IsHighSide = true, Label = "LondonHigh" };
                if (!bestBuy.IsValid || Math.Abs(londonHi - p.Close) < Math.Abs(bestBuy.Price - p.Close))
                    bestBuy = lp;
            }
            if (pdh > 0 && pdh > p.Close)
            {
                var lp = new LiquidityPool { Price = pdh, CreatedBar = bar, IsHighSide = true, Label = "PDH" };
                if (!bestBuy.IsValid) bestBuy = lp;
            }
            if (swingHi > 0 && swingHi > p.Close && !bestBuy.IsValid)
                bestBuy = new LiquidityPool { Price = swingHi, CreatedBar = bar, IsHighSide = true, Label = "SwingHigh" };

            // Store pools based on bias direction
            if (_htfBiasDirection > 0)
            {
                // Bullish bias: primary target is sell-side (we want it swept)
                _primaryPool   = bestSell;
                _secondaryPool = bestBuy; // buy-side becomes our take-profit reference
            }
            else
            {
                // Bearish bias: primary target is buy-side (we want it swept)
                _primaryPool   = bestBuy;
                _secondaryPool = bestSell;
            }
        }

        private LiquidityPool GetBestSellSidePool()
        {
            return (!_primaryPool.IsHighSide && _primaryPool.IsValid) ? _primaryPool
                 : (!_secondaryPool.IsHighSide && _secondaryPool.IsValid) ? _secondaryPool
                 : LiquidityPool.Empty;
        }

        private LiquidityPool GetBestBuySidePool()
        {
            return (_primaryPool.IsHighSide && _primaryPool.IsValid) ? _primaryPool
                 : (_secondaryPool.IsHighSide && _secondaryPool.IsValid) ? _secondaryPool
                 : LiquidityPool.Empty;
        }


        // =================================================================
        // ORDER BLOCK DETECTION
        //
        // The OB is the last candle with an OPPOSING body before the
        // displacement candle. It represents where smart money accumulated.
        //
        // Bullish displacement → look for last bearish candle (close < open)
        //                        in the 1–3 bars before the displacement.
        // Bearish displacement → look for last bullish candle.
        // =================================================================

        private void BuildOrderBlockFromDisplacement(BarSnapshot p, int bar, bool isLong)
        {
            _obIsValid = false;

            // Need bar arrays with enough depth
            if (p.Opens == null || p.Opens.Length < 4 ||
                p.Closes == null || p.Closes.Length < 4 ||
                p.Highs == null || p.Highs.Length < 4 ||
                p.Lows == null || p.Lows.Length < 4)
                return;

            // Search bars 1, 2, 3 ago for the opposing candle
            for (int offset = 1; offset <= 3 && offset < p.Opens.Length; offset++)
            {
                double bOpen  = p.Opens[offset];
                double bClose = p.Closes[offset];
                double bHigh  = p.Highs[offset];
                double bLow   = p.Lows[offset];

                bool isBearCandle = bClose < bOpen;
                bool isBullCandle = bClose > bOpen;

                if (isLong && isBearCandle)
                {
                    _obHigh    = bHigh;
                    _obLow     = bLow;
                    _obBar     = bar - offset;
                    _obIsValid = true;
                    return;
                }
                if (!isLong && isBullCandle)
                {
                    _obHigh    = bHigh;
                    _obLow     = bLow;
                    _obBar     = bar - offset;
                    _obIsValid = true;
                    return;
                }
            }
        }
    }
}
