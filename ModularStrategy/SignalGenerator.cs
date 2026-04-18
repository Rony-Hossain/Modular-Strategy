#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// SIGNAL GENERATOR — the scoring and risk gate.
    ///
    /// Receives a RawDecision from strategy logic.
    /// Applies:
    ///   1. MathPolicy.Grade_Master() — quality scoring
    ///   2. ISlippageModel.Apply()    — realistic entry price adjustment  ← NEW
    ///   3. Risk filters (circuit breaker, directional loss limit, VIX, gap)
    ///   4. Position sizing via MathPolicy.PositionSize_Calculate()
    ///   5. Target calculation via MathPolicy.Targets_Partial()
    ///   6. RR ratio validation (minimum 1.5:1)
    ///
    /// Emits a typed SignalObject or null if the trade is rejected.
    /// </summary>
    public class SignalGenerator : ISignalGenerator
    {
        // ===================================================================
        // CONFIGURATION
        // ===================================================================

        private readonly InstrumentKind _instrument;
        private readonly double         _accountSize;
        private readonly double         _riskPctPerTrade;   // e.g. 0.01 = 1%
        private readonly int            _maxContracts;
        private readonly double         _maxDailyLossDollars;
        private readonly double         _minRRRatio;        // minimum risk:reward (default 1.5)
        private readonly int            _minScore;          // minimum grade to trade (default 60)

        // Slippage model — injected, never constructed here.
        // Defaults to NullSlippageModel if none provided.
        private readonly ISlippageModel _slippage;

        // ===================================================================
        // SESSION STATE
        // ===================================================================

        private double _dailyPnL           = 0.0;
        private int    _consecutiveLosses  = 0;
        private int    _totalTradesDay     = 0;
        private bool   _circuitBreakerHit  = false;

        // ===================================================================
        // CONSTRUCTION
        // ===================================================================

        private readonly StrategyLogger _log;

        public SignalGenerator(
            InstrumentKind  instrument,
            double          accountSize,
            double          riskPctPerTrade  = RiskDefaults.RISK_PCT,
            int             maxContracts     = RiskDefaults.MAX_CONTRACTS,
            double          maxDailyLoss     = RiskDefaults.MAX_DAILY_LOSS,
            double          minRRRatio       = RiskDefaults.MIN_RR_RATIO,
            int             minScore         = GradeThresholds.REJECT,
            StrategyLogger  logger           = null,
            ISlippageModel  slippageModel    = null)   // ← NEW optional param, last position
        {
            _instrument          = instrument;
            _accountSize         = accountSize;
            _riskPctPerTrade     = riskPctPerTrade;
            _maxContracts        = maxContracts;
            _maxDailyLossDollars = maxDailyLoss;
            _minRRRatio          = minRRRatio;
            _minScore            = minScore;
            _log                 = logger;
            // Null object pattern — safe default if caller provides nothing.
            _slippage            = slippageModel ?? new NullSlippageModel();
        }

        // ===================================================================
        // ISignalGenerator
        // ===================================================================

        public bool IsBlocked => _circuitBreakerHit;

        public void OnSessionOpen()
        {
            _dailyPnL          = 0.0;
            _consecutiveLosses = 0;
            _totalTradesDay    = 0;
            _circuitBreakerHit = false;
            LastRejectedSignal = null;
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            _totalTradesDay++;
        }

        public void OnClose(SignalObject signal, double pnl, DateTime exitTime)
        {
            _dailyPnL += pnl;

            if (pnl < 0)
                _consecutiveLosses++;
            else
                _consecutiveLosses = 0;

            // Check circuit breakers after each close
            if (MathPolicy.CircuitBreaker_Daily(_dailyPnL, _maxDailyLossDollars))
            {
                _circuitBreakerHit = true;
                _log?.RiskCircuitBreaker(exitTime, "DailyLoss", _dailyPnL, _maxDailyLossDollars);
            }

            if (MathPolicy.LossLimit_Directional(_consecutiveLosses, RiskDefaults.MAX_CONSECUTIVE_LOSSES))
            {
                _circuitBreakerHit = true;
                _log?.RiskCircuitBreaker(exitTime, "ConsecutiveLoss", _dailyPnL, _maxDailyLossDollars);
            }

            _log?.RiskConsecutiveLoss(exitTime, _consecutiveLosses, RiskDefaults.MAX_CONSECUTIVE_LOSSES);
        }

        /// <summary>
        /// Main entry point. Returns null if rejected.
        /// </summary>
        public SignalObject Process(RawDecision decision, MarketSnapshot snapshot, string confluenceDetail)
        {
            _lastDecision = decision;
            _lastSnapshot = snapshot;
            _lastBarTime  = snapshot.Primary.Time;
            LastRejectedSignal = null;

            // ── Gate 1: circuit breaker ──
            if (_circuitBreakerHit)                              return Reject("G1:CircuitBreaker");
            if (!decision.IsValid)                               return Reject("G1:DecisionInvalid");
            if (decision.Direction == SignalDirection.None)      return Reject("G1:DirectionNone");
            if (!snapshot.IsValid)                               return Reject("G1:SnapshotInvalid");

            double tickSize  = snapshot.Primary.TickSize;
            double tickValue = snapshot.Primary.TickValue;

            // ── Gate 2: stop must be defined ──
            if (decision.StopPrice <= 0.0)
                return Reject("G2:StopZero");

            // ── Gate 3: scoring ──
            int    gradeIndex = MathPolicy.Grade_Master(decision.RawScore);
            bool   isReject   = MathPolicy.Grade_IsReject(decision.RawScore);
            string grade      = GradeLabel(gradeIndex);

            if (isReject || decision.RawScore < _minScore)
                return Reject($"G3:Score({decision.RawScore}<{_minScore})");

            // ── Gate 3.5: thin-market suppression ─────────────────────────
            // Rejects entries when current bar volume is well below the 20-bar
            // rolling average. Addresses the stop-hunt bucket (-$18,420 on 27
            // trades) and bleeding hours (4-6 AM: -$5,943, 4-5 PM: -$3,484)
            // where signals fire in dead liquidity with no institutional backing.
            //
            // THRESHOLD: THIN_MARKET_RATIO — must come from data analysis.
            //   Start at 0.40 (current bar < 40% of average = suppress).
            //   Validate: pull every entry, compute VolTrades/AvgTrades at entry bar,
            //   split into winners/losers. If losers cluster below 0.40, threshold
            //   is correct. If separation point is different, adjust.
            //
            // WARM-UP: AvgTrades = 0 for first 20 bars after session open.
            //   Gate is inactive when AvgTrades = 0 — degrades to no filter.
            //   This is acceptable: the first 100 minutes are RTH opening range
            //   where volume is typically high. The bleeding hours (4-6 AM) are
            //   deep into the session where AvgTrades is fully warm.
            {
                const double THIN_MARKET_RATIO = 0.40;

                double volTrades = snapshot.Get(SnapKeys.VolTrades);
                double avgTrades = snapshot.Get(SnapKeys.AvgTrades);

                if (avgTrades > 0 && volTrades < avgTrades * THIN_MARKET_RATIO)
                    return Reject(string.Format(
                        "G3.5:ThinMarket(vt={0:F0}<{1:F0}={2:F1}×{3:F0})",
                        volTrades, avgTrades * THIN_MARKET_RATIO, THIN_MARKET_RATIO, avgTrades));
            }

            // ── Slippage adjustment (after scoring, before sizing) ──────────
            // Apply after Gate 3 because score drives slippage tier.
            // Apply before Gate 4 (sizing) so stop width and RR are realistic.
            // The model never rejects a trade — it only adjusts entry price.
            SlippageResult slip = _slippage.Apply(decision, snapshot);
            double realisticEntry = slip.IsValid
                ? slip.AdjustedEntryPrice
                : decision.EntryPrice;  // fallback: theoretical price if model failed

            // Stop width and RR are now calculated from the realistic fill price,
            // not the theoretical close price. This is the core fix.
            double stopTicks = MathCore.DistanceInTicks(realisticEntry, decision.StopPrice, tickSize);
            if (stopTicks < RiskDefaults.MIN_STOP_TICKS)
                return Reject($"G2:StopTooTight({stopTicks:F1}ticks<{RiskDefaults.MIN_STOP_TICKS}) [post-slip]");

            // ── Gate 4: position sizing ───────────────────────────────────
            // Risk-based sizing: contracts = floor(accountSize × riskPct / (stopTicks × tickValue))
            //
            // Example — NQ, $100k account, 1% risk, 120-tick stop ($5/tick):
            //   riskDollars = $1,000
            //   contracts   = floor($1,000 / (120 × $5)) = floor(1.67) = 1
            //
            // Example — tight stop, 24 ticks:
            //   contracts = floor($1,000 / (24 × $5)) = floor(8.33) = 5 (capped at MaxContracts)
            //
            // If formula produces 0 (riskPct set too low for this stop width),
            // fall back to 1 contract and warn. User should raise RiskPct.
            // Previous setting: RiskPct = 0.0005 (0.05%) → $50 risk → always 0 → always 1.
            // Correct setting:  RiskPct = 0.01   (1.0%)  → risk-proportional sizing.
            int contracts = MathPolicy.PositionSize_Calculate(
                _accountSize,
                _riskPctPerTrade,
                stopTicks,
                tickValue,
                _maxContracts);

            if (contracts <= 0)
            {
                contracts = 1;
                _log?.Warn(_lastBarTime,
                    "G4:SizeFloor — risk=${0:F0} stop={1:F0}t×${2:F0}=${3:F0} → 1 contract (raise RiskPct={4:P2})",
                    _accountSize * _riskPctPerTrade, stopTicks, tickValue,
                    stopTicks * tickValue, _riskPctPerTrade);
            }

            // ── Gate 5: targets ──
            double t1, t2;
            bool   isLong = decision.Direction == SignalDirection.Long;

            if (decision.TargetPrice > 0.0)
            {
                t1 = decision.TargetPrice;
                t2 = decision.Target2Price > 0.0
                    ? decision.Target2Price
                    : t1;
            }
            else
            {
                double sessionSD = snapshot.VWAPUpperSD1 - snapshot.VWAP;
                if (sessionSD <= 0) sessionSD = snapshot.ATR;

                MathPolicy.Targets_Partial(
                    realisticEntry,  // ← was decision.EntryPrice
                    snapshot.VWAP,
                    sessionSD,
                    isLong,
                    out t1, out t2);
            }

            // ORB signals have pre-calibrated targets (T1=1R, T2=2R) — skip RR adjustment
            bool isORB = (decision.ConditionSetId ?? "").StartsWith("ORB_");
            if (!isORB)
            {
                // Minimum target distance based on realistic entry, not theoretical
                double minTargetDistance = (decision.StopPrice > 0)
                    ? Math.Abs(realisticEntry - decision.StopPrice) * _minRRRatio
                    : snapshot.ATR;

                if (isLong  && t1 <= realisticEntry + minTargetDistance * 0.5)
                    t1 = realisticEntry + minTargetDistance;
                else if (!isLong && t1 >= realisticEntry - minTargetDistance * 0.5)
                    t1 = realisticEntry - minTargetDistance;
            }

            // ── Gate 6: minimum RR ratio — DISABLED ──────────────────────
            // BOSWave uses ATR-based stops that are structurally wide. The
            // VWAP-distance target formula underestimates targets for these
            // stops, causing valid signals to fail this gate. Disabled so
            // BOSWave signals trade at whatever RR the target formula produces.
            // Re-enable once target formula is calibrated for BOSWave.
            double rewardTicks = MathCore.DistanceInTicks(realisticEntry, t1, tickSize);
            double rrRatio     = (stopTicks > 0.0) ? rewardTicks / stopTicks : 0.0;

            // ── Gate 7: gap filter — DISABLED ────────────────────────────
            // Disabled for baseline test. G7 was killing 48 signals at the
            // RTH open after overnight gaps >1%. Re-enable for live trading.
            // if (snapshot.PrevDayClose > 0.0 && snapshot.NewYork.IsValid)
            // {
            //     bool firstHourComplete = snapshot.Primary.Session >= SessionPhase.EarlySession;
            //     bool gapRestricted = MathPolicy.GapFilter_IsRestricted(
            //         snapshot.NewYork.Open, snapshot.PrevDayClose, firstHourComplete);
            //     if (gapRestricted)
            //         return Reject($"G7:GapFilter(open={snapshot.NewYork.Open:F2} prevClose={snapshot.PrevDayClose:F2})");
            // }

            // ── All gates passed — build signal ──
            string signalId = string.Format("{0}:{1:yyyyMMdd}:{2}", 
                decision.ConditionSetId ?? "SIG", _lastBarTime, snapshot.Primary.CurrentBar);

            var accepted = new SignalObject
            {
                Direction    = decision.Direction,
                Source       = decision.Source,
                EntryPrice   = realisticEntry,         // ← realistic fill price
                StopPrice    = decision.StopPrice,
                Target1Price = t1,
                Target2Price = t2,
                StopTicks    = stopTicks,
                RRRatio      = rrRatio,
                Score        = decision.RawScore,
                Grade        = grade,
                Label          = decision.Label,
                ConditionSetId = decision.ConditionSetId ?? "",
                SignalId       = signalId,
                BarIndex       = snapshot.Primary.CurrentBar,
                SignalTime     = snapshot.Primary.Time,
                CandleHigh     = snapshot.Primary.High,
                CandleLow      = snapshot.Primary.Low,
                Contracts      = contracts,
                Method         = OrderMethod.LimitWithFallback,
                IsActive       = true,
                IsFilled       = false,
                Detail         = confluenceDetail ?? ""
            };


            // Log slippage context alongside signal acceptance.
            // Warn() is the correct public method — Print() is private on StrategyLogger.
            _log?.SignalAccepted(accepted, snapshot);
            if (slip.IsValid && slip.EntrySlippageTicks > 0.0)
                _log?.Warn(snapshot.Primary.Time,
                    "SLIP entry={0:F1}t exit={1:F1}t {2}",
                    slip.EntrySlippageTicks, slip.ExitSlippageTicks, slip.Reason);

            return accepted;
        }

        // ===================================================================
        // UTILITIES
        // ===================================================================

        private static string GradeLabel(int gradeIndex) => GradeLabels.Get(gradeIndex);

        public string LastRejectReason { get; private set; } = "";
        public SignalObject LastRejectedSignal { get; private set; }

        private RawDecision _lastDecision;
        private MarketSnapshot _lastSnapshot;
        private DateTime    _lastBarTime;

        private SignalObject Reject(string reason)
        {
            LastRejectReason = reason;
            
            // Capture the rejected signal for UI rendering
            if (_lastDecision.IsValid && _lastSnapshot.IsValid)
            {
                string signalId = string.Format("{0}:{1:yyyyMMdd}:{2}", 
                    _lastDecision.ConditionSetId ?? "REJ", _lastBarTime, _lastSnapshot.Primary.CurrentBar);

                LastRejectedSignal = new SignalObject
                {
                    SignalId     = signalId,
                    Direction    = _lastDecision.Direction,
                    Source       = _lastDecision.Source,
                    EntryPrice   = _lastDecision.EntryPrice,
                    StopPrice    = _lastDecision.StopPrice,
                    Score        = _lastDecision.RawScore,
                    Label        = _lastDecision.Label,
                    ConditionSetId = _lastDecision.ConditionSetId ?? "",
                    BarIndex     = _lastSnapshot.Primary.CurrentBar,
                    SignalTime   = _lastSnapshot.Primary.Time,
                    CandleHigh   = _lastSnapshot.Primary.High,
                    CandleLow    = _lastSnapshot.Primary.Low,
                    IsActive     = false,
                    IsRejected   = true,
                    Detail       = reason
                };
            }

            _log?.SignalRejected(
                _lastBarTime,
                _lastDecision.Source, _lastDecision.Direction,
                _lastDecision.RawScore, reason);
            return null;
        }
    }
}
