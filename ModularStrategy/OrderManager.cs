#region Using declarations
using System;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// ORDER MANAGER — execution and position management layer.
    ///
    /// Responsibilities:
    ///   1. Submit market entry orders (fixed 1-contract baseline mode)
    ///   2. Set-specific partial profit taking at T1
    ///   3. ATR-proportional two-mode trailing stop (pre-T1 / post-T1)
    ///   4. Set-specific dynamic breakeven arm threshold
    ///   5. Continuous pre-T1 MFE lock (DISABLED — ATR trail is tighter)
    ///   6. T1 proximity + CVD divergence + regime flip + CVD accel BE triggers
    ///   7. T2 limit order for runner contracts
    ///   8. Session-open cleanup of orphaned positions
    ///   9. Footprint trail veto — suppress trail on noise bounces (NEW)
    ///  10. Conviction exit — exit on structural counter-delta (NEW)
    ///
    /// NT8 order interaction is confined entirely to this class.
    ///
    /// ── Sizing mode ───────────────────────────────────────────────────────────
    ///   USE_FIXED_SIZE_ONE = true  → always 1 contract, Gate 4 bypassed.
    ///   USE_FIXED_SIZE_ONE = false → uses signal.Contracts from SignalGenerator.
    ///   Keep true until exit behavior is validated. Enable Gate 4 in the
    ///   second test run so sizing changes are isolated from execution changes.
    ///
    /// ── BE arm thresholds (set-specific) ─────────────────────────────────────
    ///   BOS:        ATR × 0.20  — moderate. BOS entries have wide stops, need
    ///               meaningful profit before protecting.
    ///   Retest:     ATR × 0.15  — earliest arm. Retest is the most fragile set
    ///               (lowest WR when momentum is exhausted on approach).
    ///   BandReclaim:ATR × 0.20  — same as BOS.
    ///   Impulse:    ATR × 0.30  — latest arm. Best-performing set; needs room
    ///               to breathe. Premature BE would kill runners.
    ///   Default:    ATR × 0.20  — for any other condition set.
    ///   Floor:      4 ticks minimum regardless of ATR (prevents instant arming
    ///               on dead Globex sessions where ATR is near zero).
    ///
    /// ── Pre-T1 MFE lock (new Stage 3) ────────────────────────────────────────
    ///   Once MFE exceeds a set-specific ATR threshold, trail the stop to lock
    ///   a fraction of the peak profit. Updates every bar — continuous protection.
    ///   Does NOT fire once and stop: recalculates each bar and only moves stop
    ///   if the proposed level improves the current stop (TryImproveStop guards).
    ///
    ///   BOS:        lock start ATR × 0.50, lock 35% of peak MFE
    ///   Retest:     lock start ATR × 0.40, lock 45% of peak MFE
    ///   BandReclaim:lock start ATR × 0.55, lock 35% of peak MFE
    ///   Impulse:    lock start ATR × 0.70, lock 25% of peak MFE
    ///
    ///   Example — BOS long, entry 25,000, ATR 40t, MFE peak 22t:
    ///     lock start = 0.50 × 40 = 20t  → 22t >= 20t, lock fires
    ///     lock amount = 0.35 × 22t = 7.7t
    ///     lock stop   = 25,000 + 7.7t × $5 = $38.50 above entry
    ///     If current stop is still at initial -120t below, this moves it
    ///     to +7.7t — not breakeven, but recovering some profit.
    ///
    /// ── T1 partial (set-specific) ─────────────────────────────────────────────
    ///   BOS:        70%  — monetize fast. BOS is aggressive entry, prone to reversal.
    ///   Retest:     70%  — same rationale.
    ///   BandReclaim:60%  — slightly stronger setup, leave more runner.
    ///   Impulse:    50%  — best setup, leave largest runner for T2.
    ///   NOTE: with USE_FIXED_SIZE_ONE = true, all 1-contract trades skip the
    ///   partial (correct existing behaviour) and just move stop to BE at T1.
    ///   Set-specific pct takes full effect when Gate 4 sizing is enabled.
    ///
    /// ── Exchange stop sync ───────────────────────────────────────────────────
    ///   UpdateStopOrder() uses ChangeOrder() — atomic price update.
    ///   Never cancel + resubmit (async race: two live stops during the gap).
    ///   UpdateT2Order()  uses ChangeOrder() — same reason for T2 limit.
    ///   TryImproveStop() gates on `improves` before calling ChangeOrder —
    ///   no unnecessary round-trips to the exchange.
    /// </summary>
    public class OrderManager : IOrderManager
    {
        // ===================================================================
        // SIZING MODE
        // ===================================================================

        /// <summary>
        /// Phase 1 test flag: when true, all entries use 1 contract regardless
        /// of signal.Contracts. Set false to enable Gate 4 risk-based sizing.
        /// Keep true until exit behaviour (MFE lock, set-specific trails) is
        /// validated in backtest — separates execution quality from size effects.
        /// </summary>
        private const bool USE_FIXED_SIZE_ONE = true;

        /// <summary>
        /// Validation mode for FootprintTradeAdvisor integration.
        /// true  = advisor runs in shadow mode and only logs what it would do.
        /// false = advisor decisions are allowed to change live trade management.
        ///
        /// Keep true during comparison against legacy Stage 6.5 / 6.75 so you
        /// can review TA_*_SHADOW rows without already changing behavior.
        /// Flip to false only after validation is complete, then retire 6.5 / 6.75.
        /// </summary>
        private const bool TRADE_ADVISOR_COMPARE_ONLY = true;

        // ===================================================================
        // DEPENDENCIES
        // ===================================================================

        private readonly Strategy       _host;
        private readonly StrategyLogger _log;
        private readonly ISlippageModel _slippageModel;

        // ===================================================================
        // ORDER REFERENCES
        // ===================================================================

        private Order _entryOrder   = null;
        private Order _stopOrder    = null;
        private Order _target2Order = null;
        private Order _exitOrder    = null;

        // ===================================================================
        // POSITION STATE
        // ===================================================================

        private bool         _hasOpenPosition    = false;
        private bool         _hasPendingEntry    = false;
        private SignalObject  _activeSignal       = null;
        // Stop price for T1 (scalp) and T2 (runner) — can drift independently
        private double _currentStopT1      = 0.0;
        private double _currentStopT2      = 0.0;
        private bool   _t1Hit              = false;

        private int          _entryBar           = 0;
        private double       _fillPrice          = 0.0;
        private int          _contractsTotal     = 0;
        private int          _contractsRemaining = 0;

        // Peak unrealised profit in ticks — updated every bar.
        // Used by MFE lock, T1 proximity trigger, and MathPolicy trailing stop.
        private double       _maxMFETicks        = 0.0;

        // ── PERFORMANCE TUNING: Reference Point Tracker ───────────────
        // FIX (#idea2): Track the price level with the highest aggressive
        // volume seen DURING the trade. This is the "Tipping Point".
        private double _waveLevel = 0.0;
        private double _waveMaxVol = 0.0;

        // ── Dynamic breakeven state ───────────────────────────────────────

        // _dynamicBeArmed:     true once MFE >= set-specific arm threshold.
        // _dynamicBeTriggered: true once stop has been moved to BE by any trigger.
        //                      Prevents repeated BE moves from CVD/regime re-firing.
        private int  _entryRegime        = 0;
        private bool _dynamicBeArmed     = false;
        private bool _dynamicBeTriggered = false;

        // ── Footprint trail veto state ────────────────────────────────────
        // Tracks consecutive veto bars to prevent slow-grind exploit.
        // Reset on any non-veto bar and in ResetPosition().
        private int _consecutiveVetoes = 0;
        private readonly FootprintTradeAdvisor _tradeAdvisor;

        // ===================================================================
        // FOOTPRINT ENGINE CONSTANTS
        // ===================================================================
        //
        // ALL thresholds below are INITIAL VALUES requiring validation.
        //
        // VALIDATION PROTOCOL (mandatory before live deployment):
        //   1. Pull every STOP_MOVE row from the backtest CSV.
        //   2. For each, compute conviction, volRatio, efficiency from the
        //      matching FLOW row at the same timestamp.
        //   3. Label each: "trail was premature" (trade continued 3+ bars
        //      profitably after the stop move) vs "trail was correct."
        //   4. Plot distributions for both groups. Set thresholds at the
        //      separation point. If no separation → feature is noise, disable.
        //   5. Walk-forward: train on month 1-2, test on month 3.
        //      If threshold shifts >30% between windows → overfit, disable.
        //
        // DO NOT DEPLOY these values to live trading without completing
        // steps 1-5 above. The initial values are educated starting points,
        // not validated parameters.
        // ===================================================================

        // ── Trail veto thresholds ─────────────────────────────────────────
        // Conviction = |BarDelta| / TotalVolume. Low conviction = no directional
        // commitment on the bar. The bounce is noise, not a reversal signal.
        // Pre-T1: standard threshold. Post-T1: stricter (lower) because you're
        // protecting confirmed profit — higher cost of a false positive.
        private const double VETO_CONVICTION_PRE_T1  = 0.08;   // validate: distribution analysis
        private const double VETO_CONVICTION_POST_T1 = 0.05;   // validate: must be stricter

        // Volume ratio: VolTrades / AvgTrades. Below this = thin bar.
        // Only veto on thin bars — high-volume bars with low conviction
        // are absorption battles (buyers and sellers fighting), not noise.
        private const double VETO_VOL_RATIO = 0.60;             // validate: distribution analysis

        // Range filter: bar range in ticks / ATR ticks. Small range = no movement.
        // Combined with thin volume, this confirms the bar is noise.
        // High volume + small range = absorption (DON'T veto).
        private const double VETO_MAX_RANGE_ATR = 0.30;         // validate: distribution analysis

        // Maximum consecutive veto bars. After this many in a row, force trail update.
        // 2 bars × 5 min = 10 minutes of frozen trail. 3+ consecutive weak bars
        // is not a pullback — it's a trend change.
        private const int MAX_CONSECUTIVE_VETOES = 2;

        // MFE drawdown override: if you've given back more than this fraction
        // of peak MFE, the veto is overridden regardless of conviction.
        // Safety valve against the veto holding through a real reversal.
        private const double MAX_VETO_DRAWDOWN_PCT = 0.50;      // validate: distribution analysis

        // Delta tail override: DeltaSl/DeltaSh magnitude that overrides the veto.
        // Strong DeltaSl (buying at bar low) on a short's bounce bar = buyers
        // defending the bounce. That's not noise — cancel the veto.
        private const double DSL_DSH_OVERRIDE = 50.0;           // validate: raw delta magnitude

        // ── CVD slope BE trigger (Trigger D) ──────────────────────────────
        // When CVD slope (from SnapKeys.CvdSlope) exceeds this magnitude
        // AGAINST the position direction, arm breakeven early.
        // Fills the gap where Regime_Flip produced 0 events in 217 trades.
        private const double CVD_SLOPE_BE_THRESHOLD = 100.0;    // validate: distribution analysis

        // ── Conviction exit (Stage 6.75) ──────────────────────────────────
        // Exit at market when strong counter-delta appears while in profit
        // AND price is near a structural level working against the trade.
        // Addresses the 88% stop-exit rate — adds intelligence between
        // "trail tightens" and "hold for T1 which rarely hits."
        private const double EXIT_CONVICTION = 0.20;            // validate: distribution analysis
        private const double MIN_PROFIT_EXIT_TICKS = 8.0;       // minimum profit to activate
        private const double EXIT_LEVEL_PROXIMITY_ATR = 0.40;   // structural level proximity

        // ===================================================================
        // CONSTRUCTION
        // ===================================================================

        public OrderManager(Strategy host, StrategyLogger log, ISlippageModel slippageModel)
        {
            _host          = host;
            _log           = log;
            _slippageModel = slippageModel;

            _tradeAdvisor = new FootprintTradeAdvisor(FootprintTradeAdvisorConfig.Default);
        }

        // ===================================================================
        // IOrderManager
        // ===================================================================

        public bool HasOpenPosition => _hasOpenPosition;
        public bool HasPendingEntry => _hasPendingEntry;

        /// <summary>
        /// Session open: kill orphaned positions, cancel stale pending, reset state.
        /// </summary>
        public void OnSessionOpen()
        {
            _tradeAdvisor.OnSessionOpen();

            if (_hasOpenPosition && _activeSignal != null)
            {
                if (_activeSignal.Direction == SignalDirection.Long)
                    _host.ExitLong(0, _contractsRemaining, "SessionClose", "");
                else
                    _host.ExitShort(0, _contractsRemaining, "SessionClose", "");
            }
            CancelPending();
            ResetPosition();
        }

        /// <summary>
        /// Phase 3.8 — force-flatten any open position and cancel pending orders.
        /// Used by the HostStrategy time-window gate (no-overnight rule at 15:45 ET).
        /// Mirrors OnSessionOpen's cleanup but is idempotent and callable mid-session.
        /// </summary>
        public void ForceFlatten(string reason)
        {
            if (_hasOpenPosition && _activeSignal != null)
            {
                if (_activeSignal.Direction == SignalDirection.Long)
                    _host.ExitLong(0, _contractsRemaining, reason, "");
                else
                    _host.ExitShort(0, _contractsRemaining, reason, "");
            }
            CancelPending();
            ResetPosition();
        }

        /// <summary>
        /// Submit a market entry order.
        /// When USE_FIXED_SIZE_ONE is true, overrides signal.Contracts to 1.
        /// This isolates execution quality testing from position sizing effects.
        /// </summary>
        public void SubmitEntry(SignalObject signal)
        {
            if (signal == null || !signal.IsActive)  return;
            if (_hasOpenPosition || _hasPendingEntry) return;

            // FIX (#N2): Clone the signal object so that internal price adjustments
            // (gap risk, split stops) do not corrupt the original signal held by
            // HostStrategy and logging modules.
            _activeSignal = signal.Clone();

            // ── Phase 1: fixed-size baseline ──────────────────────────────
            // Overwrite contracts before storing so _contractsTotal is consistent.
            // Remove this block (or flip USE_FIXED_SIZE_ONE) to enable Gate 4.
            if (USE_FIXED_SIZE_ONE)
                _activeSignal.Contracts = 1;

            _contractsTotal     = Math.Max(1, _activeSignal.Contracts);
            _contractsRemaining = _contractsTotal;

            // FIX: "Vampire" Give-back Fix
            // Force a static 15-tick T1 target to bank profits early.
            double t1Ticks = 15.0;
            _activeSignal.Target1Price = _activeSignal.Direction == SignalDirection.Long
                ? _activeSignal.EntryPrice + (t1Ticks * _host.TickSize)
                : _activeSignal.EntryPrice - (t1Ticks * _host.TickSize);

            // FIX: Survival Stop Floor (Institutional Swing buffer)
            // Force a minimum stop of 1.5x ATR to survive noise wicks.
            double atrTicks = _host.ATR(14)[0] / _host.TickSize;
            double minStopTicks = atrTicks * 1.5;
            double currentStopTicks = Math.Abs(_activeSignal.EntryPrice - _activeSignal.StopPrice) / _host.TickSize;
            
            if (currentStopTicks < minStopTicks)
            {
                _activeSignal.StopPrice = _activeSignal.Direction == SignalDirection.Long 
                    ? _activeSignal.EntryPrice - (minStopTicks * _host.TickSize)
                    : _activeSignal.EntryPrice + (minStopTicks * _host.TickSize);
            }

            _currentStopT1      = _activeSignal.StopPrice;
            _currentStopT2      = _activeSignal.StopPrice;
            _t1Hit              = false;

            string orderName = !string.IsNullOrEmpty(_activeSignal.SignalId)
                ? _activeSignal.SignalId
                : string.Format("{0}_{1}", _activeSignal.Source, _activeSignal.BarIndex);

            if (_activeSignal.Direction == SignalDirection.Long)
                _entryOrder = _host.EnterLong(0, _contractsTotal, orderName);
            else
                _entryOrder = _host.EnterShort(0, _contractsTotal, orderName);

            _hasPendingEntry = true;
            _log?.OrderSubmitted(_activeSignal, isMarketFallback: false);
        }

        /// <summary>
        /// Called every bar while a position is open or pending.
        ///
        /// Stage sequence:
        ///   1.    Update MFE tracker
        ///   2.    Capture entry regime
        ///   2.5   FootprintTradeAdvisor — Hold / Tighten / ExitEarly
        ///   3.    BE arm (set-specific threshold)
        ///   4.    BE triggers (T1 proximity, CVD divergence, regime flip, CVD acceleration)
        ///   5.    Pre-T1 MFE lock (DISABLED — ATR trail is geometrically tighter)
        ///   6.    T1 partial exit (set-specific percentage)
        ///   6.5   Footprint trail veto — suppress trail on noise bounces
        ///   6.75  Conviction exit — exit at market on structural counter-delta
        ///   7.    ATR-proportional trailing stop (gated by trail veto)
        ///   8.    T2 exit check
        /// </summary>
        public void ManagePosition(MarketSnapshot snapshot, SignalObject activeSignal)
            => ManagePositionCore(snapshot, activeSignal, in FootprintResult.Zero, in SupportResistanceResult.Empty);

        public void ManagePosition(MarketSnapshot snapshot, SignalObject activeSignal, in FootprintResult fpResult)
            => ManagePositionCore(snapshot, activeSignal, in fpResult, in SupportResistanceResult.Empty);

        public void ManagePosition(MarketSnapshot snapshot, SignalObject activeSignal, in FootprintResult fpResult, in SupportResistanceResult srResult)
            => ManagePositionCore(snapshot, activeSignal, in fpResult, in srResult);

        private void ManagePositionCore(MarketSnapshot snapshot, SignalObject activeSignal, in FootprintResult fpResult, in SupportResistanceResult srResult)
        {
            double advisorTrailFactor = 1.0;

            if (activeSignal == null) return;

            // Market entries resolve on the same bar. If still pending after one bar,
            // cancel and reset so the next signal can be accepted.
            if (_hasPendingEntry && !_hasOpenPosition)
            {
                if (snapshot.Primary.CurrentBar > _entryBar)
                    CancelPending();
                return;
            }

            if (!_hasOpenPosition) return;

            double close    = snapshot.Primary.Close;
            double tickSize = snapshot.Primary.TickSize;
            double atrTicks = snapshot.ATRTicks;
            bool   isLong   = activeSignal.Direction == SignalDirection.Long;

            // ── Stage 1: Update MFE tracker ───────────────────────────────
            double currentProfitTicks = isLong
                ? (close - _fillPrice) / tickSize
                : (_fillPrice - close) / tickSize;
            if (currentProfitTicks > _maxMFETicks)
                _maxMFETicks = currentProfitTicks;

            // ── PERFORMANCE TUNING: Wave Tracking ─────────────────────────
            // FIX (#idea2): Identify the "Control Point" of the current wave.
            if (fpResult.IsValid)
            {
                double barMaxVol = Math.Max(fpResult.MaxAskVol, fpResult.MaxBidVol);
                if (barMaxVol > _waveMaxVol)
                {
                    _waveMaxVol = barMaxVol;
                    _waveLevel  = fpResult.MaxCombinedVolPrice;
                }
            }

            // Determine if strategy uses hands-off fixed targets (mean reversion)
            bool isFixedTarget = activeSignal.ConditionSetId != null && (
                activeSignal.ConditionSetId.StartsWith("SMC_FVG") ||
                activeSignal.ConditionSetId.StartsWith("FailedAuction") ||
                activeSignal.ConditionSetId.StartsWith("SMF_Native_Impulse") ||
                activeSignal.ConditionSetId.StartsWith("IcebergAbsorption")
            );

            // Action 2: Fix the "Exit Strangling" for ORB
            bool isORB = activeSignal.ConditionSetId != null && activeSignal.ConditionSetId.StartsWith("ORB_");

            // ── Stage 2: Time-Based Exit (Stop the 'Slow Death') ─────────
            int barsInTrade = _host.CurrentBar - _entryBar;
            if (barsInTrade >= 15) // 75 minutes in a 5-min chart
            {
                _log?.Warn(snapshot.Primary.Time, "TIME_EXIT: Closing after 15 bars to protect capital.");
                ExitAll(activeSignal, "TimeExit");
                return;
            }

            // ── Stage 3: THE VOLATILITY NOOSE (2-Bar H/L Trail) ───────────
            // Activate 'The Noose' once we are up at least 0.75 ATR.
            // This trails much tighter than standard ATR logic.
            if (_maxMFETicks >= (atrTicks * 0.75))
            {
                if (isLong)
                {
                    // Trail behind the lowest of the last 2 completed bars
                    double lowOf2 = Math.Min(_host.Low[1], _host.Low[2]);
                    double nooseStop = lowOf2 - (tickSize * 2); // 2-tick buffer
                    if (nooseStop > _currentStopT1)
                    {
                        _currentStopT1 = nooseStop;
                        _currentStopT2 = nooseStop;
                        TryImproveLegStop(activeSignal, snapshot.Primary.Time, nooseStop, tickSize, "T1", "NOOSE");
                        TryImproveLegStop(activeSignal, snapshot.Primary.Time, nooseStop, tickSize, "T2", "NOOSE");
                    }
                }
                else
                {
                    double highOf2 = Math.Max(_host.High[1], _host.High[2]);
                    double nooseStop = highOf2 + (tickSize * 2);
                    if (nooseStop < _currentStopT1)
                    {
                        _currentStopT1 = nooseStop;
                        _currentStopT2 = nooseStop;
                        TryImproveLegStop(activeSignal, snapshot.Primary.Time, nooseStop, tickSize, "T1", "NOOSE");
                        TryImproveLegStop(activeSignal, snapshot.Primary.Time, nooseStop, tickSize, "T2", "NOOSE");
                    }
                }
            }

            if (!isFixedTarget)
            {

                // Stage 2.5 advisor (Skipped for ORB to prevent strangling)
                if (!isORB)
                {
                    double exitSlippageTicks = _slippageModel.EstimateExitSlippage(snapshot);
                    double netProfitTicks = currentProfitTicks - exitSlippageTicks;

                    _tradeAdvisor.OnNewFootprint(in fpResult);

                    var taContext = new FootprintTradeContext(
                        activeSignal.ConditionSetId ?? string.Empty,
                        activeSignal.SignalId ?? string.Empty,
                        _fillPrice,
                        close,
                        netProfitTicks,
                        _maxMFETicks,
                        _t1Hit,
                        _host.CurrentBar - _entryBar,
                        _entryRegime);

                    FootprintTradeDecision taDecision = _tradeAdvisor.Evaluate(
                        activeSignal.Direction,
                        in taContext,
                        activeSignal.ConditionSetId ?? string.Empty);

                    string taDiag = _tradeAdvisor.BuildDiagnostics(
                            activeSignal.Direction,
                            in taContext,
                            activeSignal.ConditionSetId ?? string.Empty);

                    _log?.TA_Decision(snapshot.Primary.Time,
                        activeSignal.SignalId ?? "NA",
                        taDecision.Action.ToString(),
                        taDecision.SeverityScore,
                        taDecision.Reason,
                        taDiag);

                    if (taDecision.Action == FootprintTradeAction.ExitEarly)
                    {
                        _log?.Warn(snapshot.Primary.Time,
                            TRADE_ADVISOR_COMPARE_ONLY
                                ? "TA_EXIT_SHADOW sid={0} set={1} sev={2} reason={3} profit={4:F1}t mfe={5:F1}t"
                                : "TA_EXIT sid={0} set={1} sev={2} reason={3} profit={4:F1}t mfe={5:F1}t",
                            activeSignal.SignalId ?? "NA",
                            activeSignal.ConditionSetId ?? "?",
                            taDecision.SeverityScore,
                            taDecision.Reason,
                            currentProfitTicks,
                            _maxMFETicks);

                        if (!TRADE_ADVISOR_COMPARE_ONLY)
                        {
                            ExitAll(activeSignal, "FootprintExit");
                            return;
                        }
                    }

                    if (taDecision.Action == FootprintTradeAction.Tighten &&
                        taDecision.TightenFactor > 1.0)
                    {
                        _log?.Warn(snapshot.Primary.Time,
                            TRADE_ADVISOR_COMPARE_ONLY
                                ? "TA_TIGHTEN_SHADOW sid={0} set={1} sev={2} factor={3:F2} reason={4}"
                                : "TA_TIGHTEN sid={0} set={1} sev={2} factor={3:F2} reason={4}",
                            activeSignal.SignalId ?? "NA",
                            activeSignal.ConditionSetId ?? "?",
                            taDecision.SeverityScore,
                            taDecision.TightenFactor,
                            taDecision.Reason);

                        if (!TRADE_ADVISOR_COMPARE_ONLY)
                            advisorTrailFactor = taDecision.TightenFactor;
                    }
                }

                // ── HARD PROFIT GUARD: Stop the "Give Back" ─────────────────
                // If we reach 80% of Target 1, we MUST move to Breakeven.
                if (!_t1Hit && !_dynamicBeTriggered)
                {
                    double t1DistTicks = Math.Abs(activeSignal.Target1Price - _fillPrice) / tickSize;
                    if (t1DistTicks > 10 && _maxMFETicks >= t1DistTicks * 0.80)
                    {
                        _dynamicBeTriggered = true; 
                        double beStop = isLong ? _fillPrice + 2 * tickSize : _fillPrice - 2 * tickSize;
                        TryImproveLegStop(activeSignal, snapshot.Primary.Time, beStop, tickSize, "T1", "PROFIT_GUARD_BE");
                        TryImproveLegStop(activeSignal, snapshot.Primary.Time, beStop, tickSize, "T2", "PROFIT_GUARD_BE");
                        _log?.Warn(snapshot.Primary.Time, "PROFIT_GUARD_BE: Locked BE+2 at 80% of T1");
                    }
                }

                // ── PERFORMANCE TUNING: One-ATR Profit Lock (STRICTER) ────────
                // Once we have 1 ATR of open profit, we never allow it to go back to BE.
                if (_maxMFETicks >= atrTicks && atrTicks > 0)
                {
                    double lockGainTicks = _maxMFETicks * 0.50; // Lock half the peak gain
                    double lockPrice = isLong ? _fillPrice + (lockGainTicks * tickSize) : _fillPrice - (lockGainTicks * tickSize);
                    TryImproveLegStop(_activeSignal, snapshot.Primary.Time, lockPrice, tickSize, "T1", "ATR_LOCK_50");
                    TryImproveLegStop(_activeSignal, snapshot.Primary.Time, lockPrice, tickSize, "T2", "ATR_LOCK_50");
                }

                // ── PERFORMANCE TUNING: Aggressive Wave Stop Move ────────────
                if (_waveLevel > 0)
                {
                    bool cleared = isLong ? (close > _waveLevel + atrTicks * tickSize) 
                                          : (close < _waveLevel - atrTicks * tickSize);
                    if (cleared)
                    {
                        TryImproveLegStop(activeSignal, snapshot.Primary.Time, _waveLevel, tickSize, "T1", "WAVE_DEFENSE");
                        TryImproveLegStop(activeSignal, snapshot.Primary.Time, _waveLevel, tickSize, "T2", "WAVE_DEFENSE");
                    }
                }
            }

            // ── Stage 6: T1 partial exit (set-specific percentage) ────────
            if (!_t1Hit)
            {
                bool hitT1 = isLong
                    ? close >= activeSignal.Target1Price
                    : close <= activeSignal.Target1Price;

                if (hitT1)
                {
                    _t1Hit = true;
                    int t1Contracts = GetT1PartialContracts(activeSignal, _contractsRemaining);

                    if (t1Contracts >= _contractsRemaining)
                    {
                        // 1-contract (or rounding) case: no partial — just protect at T1.
                        _log?.T1Hit(snapshot.Primary.Time, close, 0, _contractsRemaining);
                    }
                    else
                    {
                        // Multi-contract: take the set-specific partial.
                        ExitPartial(activeSignal, t1Contracts, "T1");
                        _contractsRemaining -= t1Contracts;
                        _log?.T1Hit(snapshot.Primary.Time, close, t1Contracts, _contractsRemaining);
                        UpdateT2Order(activeSignal);
                    }

                    if (!isFixedTarget)
                    {
                        // After T1: move both stops to BE+2t. 
                        // The ATR runner trail in Stage 7 takes over from here.
                        double beAfterT1 = isLong
                            ? _fillPrice + 2.0 * tickSize
                            : _fillPrice - 2.0 * tickSize;
                        
                        TryImproveLegStop(activeSignal, snapshot.Primary.Time, beAfterT1, tickSize, "T1", "T1_BE");
                        TryImproveLegStop(activeSignal, snapshot.Primary.Time, beAfterT1, tickSize, "T2", "T1_BE");
                    }
                }
            }

            // ── Stage 7: ATR-proportional trailing stop ────────────────────
            if (!isFixedTarget)
            {
                double beThresh = GetBeArmTicks(activeSignal, atrTicks);
                
                // T1 Trail: Aggressive, follows the Advisor's tighten factor
                double atrTicksT1 = advisorTrailFactor > 1.0 ? atrTicks / advisorTrailFactor : atrTicks;
                double newStopT1 = isLong
                    ? MathPolicy.TrailingStop_Long(_fillPrice, close, _currentStopT1, _t1Hit, tickSize, _maxMFETicks, beThresh, atrTicksT1)
                    : MathPolicy.TrailingStop_Short(_fillPrice, close, _currentStopT1, _t1Hit, tickSize, _maxMFETicks, beThresh, atrTicksT1);

                // T2 Trail: Conservative, ignores tighten factor to allow runners to run
                double newStopT2 = isLong
                    ? MathPolicy.TrailingStop_Long(_fillPrice, close, _currentStopT2, _t1Hit, tickSize, _maxMFETicks, beThresh, atrTicks)
                    : MathPolicy.TrailingStop_Short(_fillPrice, close, _currentStopT2, _t1Hit, tickSize, _maxMFETicks, beThresh, atrTicks);

                if (Math.Abs(newStopT1 - _currentStopT1) > tickSize * 0.5)
                    TryImproveLegStop(activeSignal, snapshot.Primary.Time, newStopT1, tickSize, "T1", "Trail");

                if (Math.Abs(newStopT2 - _currentStopT2) > tickSize * 0.5)
                    TryImproveLegStop(activeSignal, snapshot.Primary.Time, newStopT2, tickSize, "T2", "Trail");
            }

            // ── Stage 8: T2 exit check ────────────────────────────────────
            bool hitT2 = isLong
                ? close >= activeSignal.Target2Price
                : close <= activeSignal.Target2Price;

            if (hitT2 && _contractsRemaining > 0)
            {
                _log?.T2Hit(snapshot.Primary.Time, close);
                ExitAll(activeSignal, "T2");
            }
        }

        public void CancelPending()
        {
            if (_entryOrder != null &&
                (_entryOrder.OrderState == OrderState.Working ||
                 _entryOrder.OrderState == OrderState.Accepted))
            {
                _host.CancelOrder(_entryOrder);
            }
            _hasPendingEntry = false;
            _entryOrder      = null;
        }

        public void OnOrderUpdate(string orderName, NinjaTrader.Cbi.OrderState orderState,
            double fillPrice, int qty)
        {
            bool isFilled   = orderState == NinjaTrader.Cbi.OrderState.Filled;
            bool isPartFill = orderState == NinjaTrader.Cbi.OrderState.PartFilled;
            bool wasOpen    = _hasOpenPosition;

            // Entry fill → position open
            if (_entryOrder != null && _entryOrder.Name == orderName &&
                (isFilled || isPartFill))
            {
                _fillPrice       = fillPrice;
                _hasOpenPosition = true;
                _hasPendingEntry = false;

                if (!wasOpen && _activeSignal != null)
                {
                    _entryBar = _host.CurrentBar; // Fix: anchor clock to fill bar

                    // FIX (#15): Gap risk correction.
                    // Recalculate targets and stops relative to ACTUAL fill price
                    // to maintain the original Risk:Reward ratio.
                    double originalRisk = Math.Abs(_activeSignal.StopPrice - _activeSignal.EntryPrice);
                    double originalT1   = Math.Abs(_activeSignal.Target1Price - _activeSignal.EntryPrice);
                    double originalT2   = Math.Abs(_activeSignal.Target2Price - _activeSignal.EntryPrice);

                    if (_activeSignal.Direction == SignalDirection.Long)
                    {
                        _activeSignal.StopPrice    = fillPrice - originalRisk;
                        _activeSignal.Target1Price = fillPrice + originalT1;
                        _activeSignal.Target2Price = fillPrice + originalT2;
                    }
                    else
                    {
                        _activeSignal.StopPrice    = fillPrice + originalRisk;
                        _activeSignal.Target1Price = fillPrice - originalT1;
                        _activeSignal.Target2Price = fillPrice - originalT2;
                    }
                    _currentStopT1 = _activeSignal.StopPrice;
                    _currentStopT2 = _activeSignal.StopPrice;

                    var entryCtx = new FootprintTradeContext(
                        _activeSignal.ConditionSetId ?? string.Empty,
                        _activeSignal.SignalId ?? string.Empty,
                        fillPrice,
                        fillPrice,
                        0.0,
                        0.0,
                        false,
                        0,
                        0);

                    _tradeAdvisor.OnTradeOpened(in entryCtx);

                    _log?.Warn(_host.Time[0],
                        "FP_TRADE_OPEN sid={0} set={1} dir={2} fill={3:F2} stop={4:F2} t1={5:F2} t2={6:F2} qty={7}",
                        _activeSignal.SignalId ?? "NA",
                        _activeSignal.ConditionSetId ?? "?",
                        _activeSignal.Direction,
                        fillPrice,
                        _currentStopT1,
                        _activeSignal.Target1Price,
                        _activeSignal.Target2Price,
                        _contractsTotal);
                }

                if (isFilled && _activeSignal != null)
                    _activeSignal.IsFilled = true;

                PlaceInitialOrders();
                return;
            }

            // Manual ExitAll fill (FootprintExit / ConvictionExit)
            if (_exitOrder != null && _exitOrder.Name == orderName && isFilled)
            {
                ResetPosition();
                return;
            }

            // Stop fill → position closed
            if (_stopOrder != null && _stopOrder.Name == orderName && isFilled)
            {
                ResetPosition();
                return;
            }

            // T2 fill → position fully closed
            if (_target2Order != null && _target2Order.Name == orderName && isFilled)
                ResetPosition();
        }

        // ===================================================================
        // PRIVATE ORDER HELPERS
        // ===================================================================

        /// <summary>
        /// After entry fill: place initial stop (full position) and T2 limit order.
        /// Stop quantity = _contractsTotal (full). T2 quantity = _contractsRemaining.
        /// These are equal at fill time — they diverge after T1 partial.
        /// </summary>
        private void PlaceInitialOrders()
        {
            if (_activeSignal == null) return;
            bool isLong = _activeSignal.Direction == SignalDirection.Long;

            if (isLong)
                _stopOrder = _host.ExitLongStopMarket(
                    0, true, _contractsTotal, _currentStopT1, "Stop", "");
            else
                _stopOrder = _host.ExitShortStopMarket(
                    0, true, _contractsTotal, _currentStopT1, "Stop", "");

            if (isLong)
                _target2Order = _host.ExitLongLimit(
                    0, true, _contractsRemaining, _activeSignal.Target2Price, "T2", "");
            else
                _target2Order = _host.ExitShortLimit(
                    0, true, _contractsRemaining, _activeSignal.Target2Price, "T2", "");
        }

        /// <summary>Partial exit — used for T1.</summary>
        private void ExitPartial(SignalObject signal, int qty, string label)
        {
            if (qty <= 0) return;
            if (signal.Direction == SignalDirection.Long)
                _host.ExitLong(0, qty, label, "");
            else
                _host.ExitShort(0, qty, label, "");
        }

        /// <summary>
        /// Cancel working stop and T2 orders, then exit all remaining contracts.
        /// Cancel first to prevent two live orders racing to close the same position.
        /// </summary>
        private void ExitAll(SignalObject signal, string label)
        {
            if (_target2Order != null && _target2Order.OrderState == OrderState.Working)
                _host.CancelOrder(_target2Order);
            if (_stopOrder != null && _stopOrder.OrderState == OrderState.Working)
                _host.CancelOrder(_stopOrder);

            if (signal.Direction == SignalDirection.Long)
                _exitOrder = _host.ExitLong(0, _contractsRemaining, label, "");
            else
                _exitOrder = _host.ExitShort(0, _contractsRemaining, label, "");
        }

        /// <summary>
        /// Atomically move stop to a new price using ChangeOrder.
        /// ChangeOrder modifies the working order in-place — no cancel/resubmit race.
        /// Contract count passed as _contractsRemaining (reduced after T1 partial).
        /// </summary>
        private void UpdateStopOrder(SignalObject signal, double newStop)
        {
            if (_stopOrder != null && _stopOrder.OrderState == OrderState.Working)
                _host.ChangeOrder(_stopOrder, _contractsRemaining, 0, newStop);
        }

        /// <summary>
        /// After T1 partial, reduce T2 limit quantity to match remaining contracts.
        /// ChangeOrder is atomic — avoids the cancel/resubmit race.
        /// If nothing remains, cancel T2 entirely.
        /// </summary>
        private void UpdateT2Order(SignalObject signal)
        {
            if (_contractsRemaining <= 0)
            {
                if (_target2Order != null && _target2Order.OrderState == OrderState.Working)
                    _host.CancelOrder(_target2Order);
                return;
            }

            if (_target2Order != null && _target2Order.OrderState == OrderState.Working)
                _host.ChangeOrder(_target2Order, _contractsRemaining, signal.Target2Price, 0);
        }

        /// <summary>
        /// Propose a new stop level for a specific leg (T1 or T2). 
        /// Only applies the move if it improves the leg's current stop.
        /// </summary>
        private void TryImproveLegStop(SignalObject signal, DateTime time,
            double proposedStop, double tickSize, string legLabel, string reason)
        {
            bool isLong  = signal.Direction == SignalDirection.Long;
            bool isT1    = legLabel == "T1";
            double current = isT1 ? _currentStopT1 : _currentStopT2;

            bool improves = isLong
                ? proposedStop > current
                : proposedStop < current;

            if (!improves) return;

            double oldStop = current;
            if (isT1) _currentStopT1 = proposedStop;
            else      _currentStopT2 = proposedStop;

            // Sync the physical exchange order:
            // FIX (#N1): Reordered logic. 
            // 1. If T1 is hit → Follow T2 (runner) wider trail.
            // 2. If T1 is NOT hit → Follow T1 (aggressive scalp) trail to protect the position.
            //    This ensures the T2 runner is only allowed its wide berth AFTER T1 confirms profit.
            double actualStop = _t1Hit ? _currentStopT2 : _currentStopT1;
            UpdateStopOrder(signal, actualStop);

            _log?.StopUpdated(time, oldStop, proposedStop, tickSize, _t1Hit);
        }

        private void ResetPosition()
        {
            if (_activeSignal != null)
            {
                _log?.ResetTrade(_host.Time[0], _activeSignal, _fillPrice, _currentStopT1, 
                    _contractsRemaining, _t1Hit, _maxMFETicks);
            }

            _tradeAdvisor.OnTradeClosed();
            _hasOpenPosition    = false;
            _hasPendingEntry    = false;
            _entryOrder         = null;
            _stopOrder          = null;
            _target2Order       = null;
            _exitOrder          = null;
            _currentStopT1      = 0.0;
            _currentStopT2      = 0.0;
            _waveLevel          = 0.0;
            _waveMaxVol         = 0.0;
            _t1Hit              = false;
            _contractsTotal     = 0;
            _contractsRemaining = 0;
            _fillPrice          = 0.0;
            _maxMFETicks        = 0.0;
            _entryRegime        = 0;
            _dynamicBeArmed     = false;
            _dynamicBeTriggered = false;
            _consecutiveVetoes  = 0;

            // Null active signal LAST so OnExecutionUpdate has a chance to log trade results
            _activeSignal       = null;
        }

        // ===================================================================
        // SET-CLASSIFICATION HELPERS
        // ===================================================================

        private static bool IsBOS(SignalObject s)
            => s != null && (s.ConditionSetId ?? "") == "SMC_BOS_v1";

        private static bool IsRetest(SignalObject s)
            => s != null &&
               ((s.ConditionSetId ?? "") == "SMF_Native_Retest_v1" ||
                (s.ConditionSetId ?? "") == "SMF_Retest_v1");

        private static bool IsBandReclaim(SignalObject s)
            => s != null &&
               ((s.ConditionSetId ?? "") == "SMF_Native_BandReclaim_v1" ||
                (s.ConditionSetId ?? "") == "SMF_BandReclaim_v1");

        private static bool IsImpulse(SignalObject s)
            => s != null &&
               ((s.ConditionSetId ?? "") == "SMF_Native_Impulse_v1" ||
                (s.ConditionSetId ?? "") == "SMF_Impulse_v1");

        // ===================================================================
        // SET-AWARE PARAMETER HELPERS
        // ===================================================================

        /// <summary>
        /// Minimum MFE in ticks before BE arm activates for this signal's set.
        /// Floor: 4 ticks absolute minimum (prevents instant arming on Globex).
        ///
        ///   Retest:      ATR × 0.15  — earliest arm (most fragile set)
        ///   BOS:         ATR × 0.20
        ///   BandReclaim: ATR × 0.20
        ///   Impulse:     ATR × 0.30  — latest arm (best set, needs room)
        ///   Default:     ATR × 0.20
        /// </summary>
        private static double GetBeArmTicks(SignalObject s, double atrTicks)
        {
            double fraction;
            if      (IsRetest(s))      fraction = 0.15;
            else if (IsBOS(s))         fraction = 0.20;
            else if (IsBandReclaim(s)) fraction = 0.20;
            else if (IsImpulse(s))     fraction = 0.30;
            else                       fraction = 0.20;

            return Math.Max(atrTicks * fraction, 4.0);
        }

        /// <summary>
        /// MFE threshold (in ticks) at which the pre-T1 MFE lock activates.
        /// Once _maxMFETicks crosses this, the lock trails stop to preserve
        /// GetMfeLockPct() of peak profit. Returns large value when ATR is zero
        /// so the lock never fires without valid ATR data.
        ///
        ///   Retest:      ATR × 0.40  — earliest lock (most fragile)
        ///   BOS:         ATR × 0.50
        ///   BandReclaim: ATR × 0.55
        ///   Impulse:     ATR × 0.70  — latest lock (most room)
        ///   Default:     ATR × 0.50
        /// </summary>
        private static double GetMfeLockStartTicks(SignalObject s, double atrTicks)
        {
            if (atrTicks <= 0) return double.MaxValue;

            double fraction;
            if      (IsRetest(s))      fraction = 0.40;
            else if (IsBOS(s))         fraction = 0.50;
            else if (IsBandReclaim(s)) fraction = 0.55;
            else if (IsImpulse(s))     fraction = 0.70;
            else                       fraction = 0.50;

            return atrTicks * fraction;
        }

        /// <summary>
        /// Fraction of peak MFE to lock once the lock threshold is crossed.
        /// The locked stop = fillPrice ± (maxMFETicks × lockPct × tickSize).
        /// TryImproveStop ensures this only ever moves the stop in the profitable
        /// direction — never back toward the original stop.
        ///
        ///   Retest:      45%  — most aggressive lock (most fragile set)
        ///   BOS:         35%
        ///   BandReclaim: 35%
        ///   Impulse:     25%  — least aggressive (deserves most runner room)
        ///   Default:     30%
        /// </summary>
        private static double GetMfeLockPct(SignalObject s)
        {
            if (IsRetest(s))      return 0.45;
            if (IsBOS(s))         return 0.35;
            if (IsBandReclaim(s)) return 0.35;
            if (IsImpulse(s))     return 0.25;
            return 0.30;
        }

        /// <summary>
        /// Number of contracts to exit at T1 for this signal's set.
        /// Enforces: at least 1, and never the full remaining position
        /// (always leaves at least 1 contract as the runner for T2).
        ///
        /// With USE_FIXED_SIZE_ONE = true, remaining = 1 → returns 1 →
        /// the caller's "skip partial" guard fires and no exit is submitted.
        /// This is correct — 1-contract trades skip T1 partial by design.
        ///
        ///   BOS:         70%  — fast monetise (fragile entry type)
        ///   Retest:      70%  — same rationale
        ///   BandReclaim: 60%
        ///   Impulse:     50%  — leave maximum runner
        ///   Default:     50%
        /// </summary>
        private static int GetT1PartialContracts(SignalObject s, int remaining)
        {
            if (remaining <= 1) return remaining;  // caller's skip-partial guard handles this

            double pct;
            if      (IsBOS(s))         pct = 0.70;
            else if (IsRetest(s))      pct = 0.70;
            else if (IsBandReclaim(s)) pct = 0.60;
            else if (IsImpulse(s))     pct = 0.50;
            else                       pct = 0.50;

            int qty = (int)Math.Round(remaining * pct, MidpointRounding.AwayFromZero);

            // Safety: always leave at least 1 contract for T2.
            if (qty < 1)           qty = 1;
            if (qty >= remaining)  qty = remaining - 1;

            return qty;
        }
    }
}
