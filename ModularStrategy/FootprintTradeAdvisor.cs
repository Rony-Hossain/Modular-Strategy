#region Using declarations
using System;
using MathLogic.Strategy;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ========================================================================
    // FOOTPRINT TRADE ADVISOR
    // ========================================================================
    // COMPLETE IMPLEMENTATION CHECKLIST — embedded directly in the file per request.
    //
    // A. Final role
    //   [x] FootprintTradeAdvisor is the post-entry order-flow supervision layer.
    //   [x] It answers only Hold / Tighten / ExitEarly.
    //   [x] It does not place orders.
    //   [x] It does not own stop math, T1/T2 logic, or BE/trail formulas.
    //   [x] It interprets whether the trade story is healthy, weakening, or broken.
    //
    // B. Final architecture
    //   [x] FootprintAssembler remains the only volumetric/NT8 reader (external contract).
    //   [x] FootprintCore remains the single one-bar footprint fact engine (external contract).
    //   [x] FootprintTradeAdvisor consumes only FootprintResult for order-flow truth.
    //   [x] OrderManager remains the execution owner.
    //   [ ] External follow-up: no other module should independently decide hold /
    //       tighten / exit from raw footprint state.
    //
    // C. Integration point
    //   [x] Recommended seam documented: inside OrderManager.ManagePosition(...).
    //   [x] Advisor is intended to run only when _hasOpenPosition == true.
    //   [x] Advisor is intended to run after Stage 1 / 2 and before legacy post-entry
    //       footprint logic is migrated out.
    //   [ ] External follow-up: freeze the exact insertion line in ManagePosition(...).
    //
    // D. Per-trade lifecycle hooks
    //   [x] OnSessionOpen()
    //   [x] OnTradeOpened(...)
    //   [x] OnTradeClosed()
    //   [x] Trade state is cleared on session open / trade close.
    //   [x] History is session-local and fixed-size.
    //
    // E. Input contract
    //   [x] Direction required.
    //   [x] Current + recent FootprintResult history.
    //   [x] FootprintTradeContext required.
    //   [x] Context carries ConditionSetId / SignalId / EntryPrice / CurrentPrice /
    //       CurrentProfitTicks / MaxMFETicks / IsT1Hit / BarsInTrade / EntryRegime.
    //   [x] BarsInTrade source documented: host.CurrentBar - _entryBar at call site.
    //   [x] IsT1Hit timing documented: if advisor runs before Stage 6 on the T1 bar,
    //       IsT1Hit will still be false on that bar.
    //
    // F. Output contract
    //   [x] FootprintTradeAction enum: Hold / Tighten / ExitEarly.
    //   [x] FootprintTradeDecision readonly struct.
    //   [x] Output includes Action / SeverityScore / TightenFactor / Reason.
    //
    // G. Invalid-footprint policy
    //   [x] Explicit policy enum provided.
    //   [x] Recommended default = HoldNeutral.
    //   [x] Missing footprint does not force exit by default.
    //
    // H. State ownership
    //   [x] Small fixed ring buffer of FootprintResult.
    //   [x] Trade-open flag owned locally.
    //   [x] No MarketSnapshot dependency for footprint truth.
    //   [x] No raw external CVD arrays.
    //
    // I. Trade-stage awareness
    //   [x] Pre-T1 vs post-T1 behavior is explicit.
    //   [x] Pre-T1 is more sensitive to deterioration.
    //   [x] Post-T1 is more tolerant and runner-protective.
    //
    // J. Per-set policy
    //   [x] Family override path provided.
    //   [x] SMF_Native_* family supported.
    //   [x] BOS / continuation family supported.
    //   [x] Retest / fragile family supported.
    //   [x] Default fallback family supported.
    //
    // K. Hard-exit rules
    //   [x] Long hard-exit helper.
    //   [x] Short hard-exit helper.
    //   [x] Requires stronger, more persistent evidence than simple tighten.
    //
    // L. Tighten rules
    //   [x] Middle state between healthy pullback and broken story.
    //   [x] Directional helpers implemented symmetrically.
    //
    // M. Hold rules
    //   [x] Missing or insufficient deterioration resolves to Hold.
    //   [x] One noisy opposing bar should not force an exit.
    //
    // N. Scoring model
    //   [x] Additive deterioration scoring.
    //   [x] Score components split into focused helpers.
    //   [x] Separate tighten vs exit thresholds.
    //
    // O. History policy
    //   [x] Uses only short rolling FootprintResult history.
    //   [x] Current + previous 2 or 3 results available from fixed ring.
    //   [x] History used to distinguish one-bar noise vs persistence.
    //
    // P. Data allowed from FootprintResult
    //   [x] BarDelta / DeltaPct / CumDelta / DeltaSh / DeltaSl
    //   [x] TotalBuyVol / TotalSellVol
    //   [x] AbsorptionScore
    //   [x] StackedBullRun / StackedBearRun / HasBullStack / HasBearStack
    //   [x] MaxSeenDelta / MinSeenDelta / MaxAskVol / MaxBidVol
    //
    // Q. Long/short symmetry
    //   [x] Every long-side helper has a short-side mirror.
    //   [x] No direction-blind absorption scoring.
    //   [x] No direction-blind extreme-defense scoring.
    //
    // R. Allocation discipline
    //   [x] Fixed ring buffer only.
    //   [x] Readonly struct output.
    //   [x] No per-bar collections.
    //   [x] Compact reason strings only.
    //
    // S. Logging/diagnostics
    //   [x] Compact reason codes returned for logging.
    //   [ ] External follow-up: add StrategyLogger.TradeAdvisorAction(...).
    //
    // T. Non-goals
    //   [x] No order submission.
    //   [x] No direct stop mutation.
    //   [x] No T1/T2 quantity logic.
    //   [x] No slippage logic.
    //   [x] No signal generation.
    //   [x] No raw footprint assembly.
    //   [x] No zone lifecycle.
    //   [x] No macro confluence scoring.
    //
    // U. Mapping from advisor output to OrderManager
    //   [x] Hold -> do nothing special; let normal OrderManager path continue.
    //   [x] Tighten -> request stricter stop behavior through OrderManager.
    //   [x] ExitEarly -> intended mapping documented as:
    //         ExitAll(activeSignal, "FootprintExit"); ResetPosition(); return;
    //   [ ] External follow-up: wire the exact mapping in OrderManager.
    //
    // V. Legacy OrderManager migration plan
    //   [x] Stage 6.5 named for retirement as legacy Hold authority.
    //   [x] Stage 6.75 named for retirement as legacy ExitEarly authority.
    //   [x] _consecutiveVetoes and VETO_* / EXIT_* constants should move out of
    //       OrderManager when those stages are retired.
    //   [ ] External follow-up: complete the actual OrderManager migration.
    //
    // W. Existing duplicate post-entry authorities to retire
    //   [x] Stage 4 BE triggers flagged for review/migration.
    //   [x] Stage 6.5 footprint trail veto flagged for retirement.
    //   [x] Stage 6.75 conviction exit flagged for retirement.
    //
    // X. Public API
    //   [x] OnSessionOpen()
    //   [x] OnTradeOpened(...)
    //   [x] OnTradeClosed()
    //   [x] OnNewFootprint(in FootprintResult result)
    //   [x] Evaluate(SignalDirection direction, in FootprintTradeContext context, string conditionSetId = "")
    //
    // Y. Build order
    //   [x] Policy enum + config struct.
    //   [x] Trade context + decision structs.
    //   [x] Advisor class + ring buffer.
    //   [x] Lifecycle hooks.
    //   [x] Evaluate + helpers.
    //   [ ] External follow-up: wire into OrderManager and retire legacy stages.
    //
    // Z. Acceptance test
    //   [x] Minor absorbed pullback stays alive.
    //   [x] Weakening but not broken flow can tighten.
    //   [x] Persistent clear deterioration can exit early.
    //   [x] Missing footprint does not force exit by default.
    //   [ ] External follow-up: no post-entry footprint side-channel remains after migration.
    // ========================================================================

    /// <summary>
    /// Behavior when the advisor has no valid footprint to evaluate.
    /// </summary>
    public enum FootprintTradeUnavailablePolicy
    {
        HoldNeutral = 0,
        TightenWeak = 1,
        ExitStrict  = 2,
    }

    /// <summary>
    /// Human-readable action bucket for post-entry supervision.
    /// </summary>
    public enum FootprintTradeAction
    {
        Hold      = 0,
        Tighten   = 1,
        ExitEarly = 2,
    }

    /// <summary>
    /// Family-level override for different trade archetypes.
    /// </summary>
    public enum FootprintTradeFamily
    {
        Default   = 0,
        SMFNative = 1,
        BOS       = 2,
        Retest    = 3,
    }

    /// <summary>
    /// Minimal trade context passed in by OrderManager.
    ///
    /// NOTE:
    ///   BarsInTrade should be computed by the caller as host.CurrentBar - _entryBar.
    ///   If the advisor is evaluated before Stage 6 on the exact T1 bar,
    ///   IsT1Hit will still be false on that bar and flip true from the next bar.
    /// </summary>
    public readonly struct FootprintTradeContext
    {
        public string ConditionSetId { get; }
        public string SignalId { get; }
        public double EntryPrice { get; }
        public double CurrentPrice { get; }
        public double CurrentProfitTicks { get; }
        public double MaxMFETicks { get; }
        public bool IsT1Hit { get; }
        public int BarsInTrade { get; }
        public int EntryRegime { get; }

        public FootprintTradeContext(
            string conditionSetId,
            string signalId,
            double entryPrice,
            double currentPrice,
            double currentProfitTicks,
            double maxMfeTicks,
            bool isT1Hit,
            int barsInTrade,
            int entryRegime)
        {
            if (barsInTrade < 0)
                throw new ArgumentOutOfRangeException(nameof(barsInTrade), "barsInTrade must be >= 0.");

            ConditionSetId = conditionSetId ?? string.Empty;
            SignalId = signalId ?? string.Empty;
            EntryPrice = entryPrice;
            CurrentPrice = currentPrice;
            CurrentProfitTicks = currentProfitTicks;
            MaxMFETicks = maxMfeTicks;
            IsT1Hit = isT1Hit;
            BarsInTrade = barsInTrade;
            EntryRegime = entryRegime;
        }
    }

    /// <summary>
    /// Immutable configuration for the trade advisor.
    ///
    /// The score here is deterioration severity. Higher score = worse order-flow health.
    /// </summary>
    public readonly struct FootprintTradeAdvisorConfig
    {
        public FootprintTradeUnavailablePolicy UnavailablePolicy { get; }

        // Directional deterioration thresholds
        public double MinConvictionDeltaPct { get; }
        public double VolumeDominanceRatio { get; }
        public double HardExitDeltaPct { get; }
        public double HardExitVolumeDominanceRatio { get; }
        public double HardExitExtremeDeltaThreshold { get; }
        public double TightenCumDeltaSlopeThreshold { get; }
        public double HardExitCumDeltaSlopeThreshold { get; }

        // Absorption / stacked imbalance thresholds
        public double AbsorptionStrongThreshold { get; }
        public double AbsorptionModerateThreshold { get; }
        public int MinStackedLevelsForConcern { get; }

        // Stage thresholds
        public int PreT1TightenAtScore { get; }
        public int PreT1ExitAtScore { get; }
        public int PostT1TightenAtScore { get; }
        public int PostT1ExitAtScore { get; }

        // Early-exit gating
        public double MinProfitTicksForExitEarly { get; }

        // Tighten factors
        public double TightenFactor { get; }
        public double StrongTightenFactor { get; }

        // Family score bias
        public int SMFNativeScoreBias { get; }
        public int BOSScoreBias { get; }
        public int RetestScoreBias { get; }

        public FootprintTradeAdvisorConfig(
            FootprintTradeUnavailablePolicy unavailablePolicy,
            double minConvictionDeltaPct,
            double volumeDominanceRatio,
            double hardExitDeltaPct,
            double hardExitVolumeDominanceRatio,
            double hardExitExtremeDeltaThreshold,
            double tightenCumDeltaSlopeThreshold,
            double hardExitCumDeltaSlopeThreshold,
            double absorptionStrongThreshold,
            double absorptionModerateThreshold,
            int minStackedLevelsForConcern,
            int preT1TightenAtScore,
            int preT1ExitAtScore,
            int postT1TightenAtScore,
            int postT1ExitAtScore,
            double minProfitTicksForExitEarly,
            double tightenFactor,
            double strongTightenFactor,
            int smfNativeScoreBias,
            int bosScoreBias,
            int retestScoreBias)
        {
            if (minConvictionDeltaPct <= 0.0 || minConvictionDeltaPct >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(minConvictionDeltaPct), "minConvictionDeltaPct must be > 0 and < 1.");
            if (volumeDominanceRatio <= 1.0)
                throw new ArgumentOutOfRangeException(nameof(volumeDominanceRatio), "volumeDominanceRatio must be > 1.");
            if (hardExitDeltaPct <= 0.0 || hardExitDeltaPct >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(hardExitDeltaPct), "hardExitDeltaPct must be > 0 and < 1.");
            if (hardExitVolumeDominanceRatio <= 1.0)
                throw new ArgumentOutOfRangeException(nameof(hardExitVolumeDominanceRatio), "hardExitVolumeDominanceRatio must be > 1.");
            if (hardExitExtremeDeltaThreshold <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(hardExitExtremeDeltaThreshold), "hardExitExtremeDeltaThreshold must be > 0.");
            if (tightenCumDeltaSlopeThreshold <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(tightenCumDeltaSlopeThreshold), "tightenCumDeltaSlopeThreshold must be > 0.");
            if (hardExitCumDeltaSlopeThreshold < tightenCumDeltaSlopeThreshold)
                throw new ArgumentOutOfRangeException(nameof(hardExitCumDeltaSlopeThreshold), "hardExitCumDeltaSlopeThreshold must be >= tightenCumDeltaSlopeThreshold.");
            if (absorptionStrongThreshold <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(absorptionStrongThreshold), "absorptionStrongThreshold must be > 0.");
            if (absorptionModerateThreshold <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(absorptionModerateThreshold), "absorptionModerateThreshold must be > 0.");
            if (absorptionStrongThreshold < absorptionModerateThreshold)
                throw new ArgumentOutOfRangeException(nameof(absorptionStrongThreshold), "absorptionStrongThreshold must be >= absorptionModerateThreshold.");
            if (minStackedLevelsForConcern <= 0)
                throw new ArgumentOutOfRangeException(nameof(minStackedLevelsForConcern), "minStackedLevelsForConcern must be > 0.");
            if (preT1TightenAtScore < 0)
                throw new ArgumentOutOfRangeException(nameof(preT1TightenAtScore), "preT1TightenAtScore must be >= 0.");
            if (preT1ExitAtScore <= preT1TightenAtScore)
                throw new ArgumentOutOfRangeException(nameof(preT1ExitAtScore), "preT1ExitAtScore must be > preT1TightenAtScore.");
            if (postT1TightenAtScore < 0)
                throw new ArgumentOutOfRangeException(nameof(postT1TightenAtScore), "postT1TightenAtScore must be >= 0.");
            if (postT1ExitAtScore <= postT1TightenAtScore)
                throw new ArgumentOutOfRangeException(nameof(postT1ExitAtScore), "postT1ExitAtScore must be > postT1TightenAtScore.");
            if (minProfitTicksForExitEarly < 0.0)
                throw new ArgumentOutOfRangeException(nameof(minProfitTicksForExitEarly), "minProfitTicksForExitEarly must be >= 0.");
            if (tightenFactor <= 1.0)
                throw new ArgumentOutOfRangeException(nameof(tightenFactor), "tightenFactor must be > 1.0.");
            if (strongTightenFactor < tightenFactor)
                throw new ArgumentOutOfRangeException(nameof(strongTightenFactor), "strongTightenFactor must be >= tightenFactor.");

            UnavailablePolicy = unavailablePolicy;
            MinConvictionDeltaPct = minConvictionDeltaPct;
            VolumeDominanceRatio = volumeDominanceRatio;
            HardExitDeltaPct = hardExitDeltaPct;
            HardExitVolumeDominanceRatio = hardExitVolumeDominanceRatio;
            HardExitExtremeDeltaThreshold = hardExitExtremeDeltaThreshold;
            TightenCumDeltaSlopeThreshold = tightenCumDeltaSlopeThreshold;
            HardExitCumDeltaSlopeThreshold = hardExitCumDeltaSlopeThreshold;
            AbsorptionStrongThreshold = absorptionStrongThreshold;
            AbsorptionModerateThreshold = absorptionModerateThreshold;
            MinStackedLevelsForConcern = minStackedLevelsForConcern;
            PreT1TightenAtScore = preT1TightenAtScore;
            PreT1ExitAtScore = preT1ExitAtScore;
            PostT1TightenAtScore = postT1TightenAtScore;
            PostT1ExitAtScore = postT1ExitAtScore;
            MinProfitTicksForExitEarly = minProfitTicksForExitEarly;
            TightenFactor = tightenFactor;
            StrongTightenFactor = strongTightenFactor;
            SMFNativeScoreBias = smfNativeScoreBias;
            BOSScoreBias = bosScoreBias;
            RetestScoreBias = retestScoreBias;
        }

        public static FootprintTradeAdvisorConfig Default
        {
            get
            {
                return new FootprintTradeAdvisorConfig(
                    unavailablePolicy: FootprintTradeUnavailablePolicy.HoldNeutral,
                    minConvictionDeltaPct: 0.10,
                    volumeDominanceRatio: 1.30,
                    hardExitDeltaPct: 0.18,
                    hardExitVolumeDominanceRatio: 1.60,
                    hardExitExtremeDeltaThreshold: 25.0,
                    tightenCumDeltaSlopeThreshold: 25.0,
                    hardExitCumDeltaSlopeThreshold: 50.0,
                    absorptionStrongThreshold: 50.0,
                    absorptionModerateThreshold: 30.0,
                    minStackedLevelsForConcern: 3,
                    preT1TightenAtScore: 40,
                    preT1ExitAtScore: 70,
                    postT1TightenAtScore: 50,
                    postT1ExitAtScore: 80,
                    minProfitTicksForExitEarly: 4.0,
                    tightenFactor: 1.20,
                    strongTightenFactor: 1.35,
                    smfNativeScoreBias: -5,
                    bosScoreBias: 0,
                    retestScoreBias: 5);
            }
        }
    }

    /// <summary>
    /// Immutable post-entry advisory decision.
    ///
    /// SeverityScore is deterioration severity, not support. Higher = worse.
    /// TightenFactor is only meaningful when Action == Tighten.
    /// </summary>
    public readonly struct FootprintTradeDecision
    {
        public FootprintTradeAction Action { get; }
        public int SeverityScore { get; }
        public double TightenFactor { get; }
        public string Reason { get; }

        public FootprintTradeDecision(
            FootprintTradeAction action,
            int severityScore,
            double tightenFactor,
            string reason)
        {
            Action = action;
            SeverityScore = severityScore;
            TightenFactor = tightenFactor;
            Reason = reason ?? "TA_NONE";
        }

        public static readonly FootprintTradeDecision HoldNeutral =
            new FootprintTradeDecision(FootprintTradeAction.Hold, 50, 1.0, "TA_NEUTRAL");
    }

    /// <summary>
    /// Post-entry order-flow supervision engine.
    ///
    /// RECOMMENDED INSERTION POINT:
    ///   Inside OrderManager.ManagePosition(...), after Stage 1 / Stage 2 and only
    ///   when _hasOpenPosition == true.
    ///
    /// LEGACY MIGRATION NOTES:
    ///   - Stage 6.5 should be retired when Hold logic is owned here.
    ///   - Stage 6.75 should be retired when ExitEarly logic is owned here.
    ///   - ExitEarly integration should map to:
    ///       ExitAll(activeSignal, "FootprintExit");
    ///       ResetPosition();
    ///       return;
    /// </summary>
    public sealed class FootprintTradeAdvisor
    {
        private const int HISTORY_SIZE = 4;

        private readonly FootprintTradeAdvisorConfig _config;
        private readonly FootprintResult[] _history = new FootprintResult[HISTORY_SIZE];
        private int _historyWriteIndex;
        private int _historyCount;
        private bool _tradeOpen;

        public FootprintTradeAdvisor(FootprintTradeAdvisorConfig config)
        {
            _config = config;
            OnSessionOpen();
        }

        public void OnSessionOpen()
        {
            ResetTradeHistory();
            _tradeOpen = false;
        }

        public void OnTradeOpened(in FootprintTradeContext context)
        {
            ResetTradeHistory();
            _tradeOpen = true;
        }

        public void OnTradeClosed()
        {
            ResetTradeHistory();
            _tradeOpen = false;
        }

        private void ResetTradeHistory()
        {
            Array.Clear(_history, 0, _history.Length);
            _historyWriteIndex = 0;
            _historyCount = 0;
        }

        public void OnNewFootprint(in FootprintResult result)
        {
            _history[_historyWriteIndex] = result;
            _historyWriteIndex = (_historyWriteIndex + 1) % HISTORY_SIZE;
            if (_historyCount < HISTORY_SIZE)
                _historyCount++;
        }

        public FootprintTradeDecision Evaluate(SignalDirection direction, in FootprintTradeContext context, string conditionSetId = "")
        {
            if (!_tradeOpen)
                return new FootprintTradeDecision(FootprintTradeAction.Hold, 50, 1.0, "TA_NO_TRADE");

            if (direction != SignalDirection.Long && direction != SignalDirection.Short)
                return new FootprintTradeDecision(FootprintTradeAction.Hold, 50, 1.0, "TA_DIR_NONE");

            FootprintResult current;
            if (!TryGetCurrent(out current) || !current.IsValid)
                return BuildUnavailableDecision();

            string setId = !string.IsNullOrEmpty(conditionSetId) ? conditionSetId : context.ConditionSetId;
            FootprintTradeFamily family = ResolveFamily(setId);

            // ── PERFORMANCE TUNING: Gestation Period ───────────────────────
            // FIX (#Layer3): Prevent choking winners by over-reacting to noise.
            // DIRECTIONAL SPECIALIZATION: Longs move fast (0 bars), Shorts follow through (1 bar).
            bool inGestation = direction == SignalDirection.Long ? false : (context.BarsInTrade <= 1);

            if (direction == SignalDirection.Long)
            {
                if (CheckHardExitLong(current, context))
                    return BuildDecision(FootprintTradeAction.ExitEarly, 100, 0.0, "TA_EXIT_LONG_HARD", family);

                // Skip soft tightening during gestation
                if (inGestation)
                    return new FootprintTradeDecision(FootprintTradeAction.Hold, 20, 1.0, "TA_GESTATION");

                int severity = 0;
                severity += ScoreDirectionalDeteriorationLong(current);
                severity += ScoreExtremeFailureLong(current);
                severity += ScoreAbsorptionLong(current);
                severity += ScoreStackedImbalanceLong(current);
                severity += ScoreCumDeltaDeterioration(direction);
                severity += ScorePersistence(direction);
                severity += ScoreStageSensitivity(context);
                severity += GetFamilyScoreBias(family);

                return BuildDecisionFromSeverity(severity, context, family);
            }
            else
            {
                if (CheckHardExitShort(current, context))
                    return BuildDecision(FootprintTradeAction.ExitEarly, 100, 0.0, "TA_EXIT_SHORT_HARD", family);

                // Skip soft tightening during gestation
                if (inGestation)
                    return new FootprintTradeDecision(FootprintTradeAction.Hold, 20, 1.0, "TA_GESTATION");

                int severity = 0;
                severity += ScoreDirectionalDeteriorationShort(current);
                severity += ScoreExtremeFailureShort(current);
                severity += ScoreAbsorptionShort(current);
                severity += ScoreStackedImbalanceShort(current);
                severity += ScoreCumDeltaDeterioration(direction);
                severity += ScorePersistence(direction);
                severity += ScoreStageSensitivity(context);
                severity += GetFamilyScoreBias(family);

                return BuildDecisionFromSeverity(severity, context, family);
            }
        }

        // ====================================================================
        // INVALID FOOTPRINT POLICY
        // ====================================================================

        private FootprintTradeDecision BuildUnavailableDecision()
        {
            switch (_config.UnavailablePolicy)
            {
                case FootprintTradeUnavailablePolicy.ExitStrict:
                    return new FootprintTradeDecision(FootprintTradeAction.ExitEarly, 100, 0.0, "TA_NO_FOOTPRINT");

                case FootprintTradeUnavailablePolicy.TightenWeak:
                    return new FootprintTradeDecision(FootprintTradeAction.Tighten, 60, _config.TightenFactor, "TA_NO_FOOTPRINT");

                case FootprintTradeUnavailablePolicy.HoldNeutral:
                default:
                    return new FootprintTradeDecision(FootprintTradeAction.Hold, 50, 1.0, "TA_NO_FOOTPRINT");
            }
        }

        // ====================================================================
        // HARD EXIT HELPERS
        // ====================================================================

        private bool CheckHardExitLong(FootprintResult current, FootprintTradeContext context)
        {
            int severeCount = 0;

            if (current.DeltaPct <= -_config.HardExitDeltaPct)
                severeCount++;
            if (current.TotalBuyVol > 0.0 && current.TotalSellVol > current.TotalBuyVol * _config.HardExitVolumeDominanceRatio)
                severeCount++;
            if (current.DeltaSl <= -_config.HardExitExtremeDeltaThreshold)
                severeCount++;
            if (current.HasBearStack && !current.HasBullStack)
                severeCount++;
            if (GetCumDeltaSlope() <= -_config.HardExitCumDeltaSlopeThreshold)
                severeCount++;
            if (IsBearishAbsorption(current))
                severeCount++;

            return severeCount >= 2
                && GetAdversePersistenceCount(SignalDirection.Long) >= 2
                && (context.CurrentProfitTicks >= _config.MinProfitTicksForExitEarly || context.IsT1Hit);
        }

        private bool CheckHardExitShort(FootprintResult current, FootprintTradeContext context)
        {
            int severeCount = 0;

            if (current.DeltaPct >= _config.HardExitDeltaPct)
                severeCount++;
            if (current.TotalSellVol > 0.0 && current.TotalBuyVol > current.TotalSellVol * _config.HardExitVolumeDominanceRatio)
                severeCount++;
            if (current.DeltaSh >= _config.HardExitExtremeDeltaThreshold)
                severeCount++;
            if (current.HasBullStack && !current.HasBearStack)
                severeCount++;
            if (GetCumDeltaSlope() >= _config.HardExitCumDeltaSlopeThreshold)
                severeCount++;
            if (IsBullishAbsorption(current))
                severeCount++;

            return severeCount >= 2
                && GetAdversePersistenceCount(SignalDirection.Short) >= 2
                && (context.CurrentProfitTicks >= _config.MinProfitTicksForExitEarly || context.IsT1Hit);
        }

        // ====================================================================
        // SOFT DETERIORATION SCORING
        // ====================================================================

        private int ScoreDirectionalDeteriorationLong(FootprintResult current)
        {
            int score = 0;

            if (current.BarDelta < 0.0) score += 15;
            if (current.DeltaPct <= -_config.MinConvictionDeltaPct) score += 10;
            if (current.TotalBuyVol > 0.0 && current.TotalSellVol > current.TotalBuyVol * _config.VolumeDominanceRatio) score += 15;
            if (current.MinSeenDelta < 0.0) score += 5;

            return score;
        }

        private int ScoreDirectionalDeteriorationShort(FootprintResult current)
        {
            int score = 0;

            if (current.BarDelta > 0.0) score += 15;
            if (current.DeltaPct >= _config.MinConvictionDeltaPct) score += 10;
            if (current.TotalSellVol > 0.0 && current.TotalBuyVol > current.TotalSellVol * _config.VolumeDominanceRatio) score += 15;
            if (current.MaxSeenDelta > 0.0) score += 5;

            return score;
        }

        private int ScoreExtremeFailureLong(FootprintResult current)
        {
            int score = 0;
            if (current.DeltaSl < 0.0) score += 12;
            if (current.DeltaSh < 0.0) score += 4;
            return score;
        }

        private int ScoreExtremeFailureShort(FootprintResult current)
        {
            int score = 0;
            if (current.DeltaSh > 0.0) score += 12;
            if (current.DeltaSl > 0.0) score += 4;
            return score;
        }

        private int ScoreAbsorptionLong(FootprintResult current)
        {
            if (!IsBearishAbsorption(current))
                return 0;

            if (current.AbsorptionScore >= _config.AbsorptionStrongThreshold) return 10;
            if (current.AbsorptionScore >= _config.AbsorptionModerateThreshold) return 6;
            return 0;
        }

        private int ScoreAbsorptionShort(FootprintResult current)
        {
            if (!IsBullishAbsorption(current))
                return 0;

            if (current.AbsorptionScore >= _config.AbsorptionStrongThreshold) return 10;
            if (current.AbsorptionScore >= _config.AbsorptionModerateThreshold) return 6;
            return 0;
        }

        private int ScoreStackedImbalanceLong(FootprintResult current)
        {
            int score = 0;
            if (current.StackedBearRun >= _config.MinStackedLevelsForConcern) score += 15;
            if (!current.HasBullStack) score += 4;
            return score;
        }

        private int ScoreStackedImbalanceShort(FootprintResult current)
        {
            int score = 0;
            if (current.StackedBullRun >= _config.MinStackedLevelsForConcern) score += 15;
            if (!current.HasBearStack) score += 4;
            return score;
        }

        private int ScoreCumDeltaDeterioration(SignalDirection direction)
        {
            double slope = GetCumDeltaSlope();

            if (direction == SignalDirection.Long)
            {
                if (slope <= -_config.HardExitCumDeltaSlopeThreshold) return 15;
                if (slope <= -_config.TightenCumDeltaSlopeThreshold) return 8;
                return 0;
            }
            else
            {
                if (slope >= _config.HardExitCumDeltaSlopeThreshold) return 15;
                if (slope >= _config.TightenCumDeltaSlopeThreshold) return 8;
                return 0;
            }
        }

        private int ScorePersistence(SignalDirection direction)
        {
            int count = GetAdversePersistenceCount(direction);
            if (count >= 3) return 15;
            if (count == 2) return 8;
            return 0;
        }

        private static int ScoreStageSensitivity(FootprintTradeContext context)
        {
            // Pre-T1 is intentionally more sensitive because the trade has not yet
            // paid itself. Post-T1 runner gets a little more room.
            return context.IsT1Hit ? 0 : 5;
        }

        // ====================================================================
        // DECISION BUILDERS
        // ====================================================================

        private FootprintTradeDecision BuildDecisionFromSeverity(int severity, FootprintTradeContext context, FootprintTradeFamily family)
        {
            int tightenAt = context.IsT1Hit ? _config.PostT1TightenAtScore : _config.PreT1TightenAtScore;
            int exitAt = context.IsT1Hit ? _config.PostT1ExitAtScore : _config.PreT1ExitAtScore;

            if (severity >= exitAt)
            {
                if (context.CurrentProfitTicks >= _config.MinProfitTicksForExitEarly || context.IsT1Hit)
                    return BuildDecision(FootprintTradeAction.ExitEarly, severity, 0.0, "TA_EXIT", family);

                // Not enough profit buffer for early exit yet -> tighten hard instead.
                return BuildDecision(FootprintTradeAction.Tighten, severity, _config.StrongTightenFactor, "TA_TIGHTEN_PROTECT", family);
            }

            if (severity >= tightenAt)
            {
                double tighten = severity >= (tightenAt + exitAt) / 2
                    ? _config.StrongTightenFactor
                    : _config.TightenFactor;
                return BuildDecision(FootprintTradeAction.Tighten, severity, tighten, "TA_TIGHTEN", family);
            }

            return BuildDecision(FootprintTradeAction.Hold, severity, 1.0, "TA_HOLD", family);
        }

        private FootprintTradeDecision BuildDecision(FootprintTradeAction action, int severity, double tightenFactor, string reason, FootprintTradeFamily family)
        {
            return new FootprintTradeDecision(action, severity, ApplyFamilyTightenFactor(tightenFactor, family, action), reason);
        }

        // ====================================================================
        // FAMILY POLICY
        // ====================================================================

        private static FootprintTradeFamily ResolveFamily(string conditionSetId)
        {
            string setId = conditionSetId ?? string.Empty;

            if (setId == "SMF_Native_Retest_v1" ||
                setId == "SMF_Retest_v1")
                return FootprintTradeFamily.Retest;

            if (setId.StartsWith("SMF_Native_", StringComparison.Ordinal))
                return FootprintTradeFamily.SMFNative;

            if (setId == "SMC_BOS_v1")
                return FootprintTradeFamily.BOS;

            return FootprintTradeFamily.Default;
        }

        private int GetFamilyScoreBias(FootprintTradeFamily family)
        {
            switch (family)
            {
                case FootprintTradeFamily.SMFNative: return _config.SMFNativeScoreBias;
                case FootprintTradeFamily.BOS:       return _config.BOSScoreBias;
                case FootprintTradeFamily.Retest:    return _config.RetestScoreBias;
                default:                             return 0;
            }
        }

        private double ApplyFamilyTightenFactor(double tightenFactor, FootprintTradeFamily family, FootprintTradeAction action)
        {
            if (action != FootprintTradeAction.Tighten)
                return tightenFactor;

            // Retests are most fragile -> keep requested tighten factor.
            // SMFNative runners are strongest -> soften tighten slightly.
            switch (family)
            {
                case FootprintTradeFamily.SMFNative:
                    return tightenFactor > 1.0 ? Math.Max(1.0, tightenFactor - 0.05) : tightenFactor;
                default:
                    return tightenFactor;
            }
        }

        // ====================================================================
        // DIAGNOSTIC HELPERS
        // ====================================================================

        public string BuildDiagnostics(
            SignalDirection direction,
            in FootprintTradeContext context,
            string conditionSetId = "")
        {
            if (!_tradeOpen)
                return "state=NO_TRADE";

            FootprintResult current;
            if (!TryGetCurrent(out current) || !current.IsValid)
                return string.Format(
                    "state=NO_FOOTPRINT hist={0} t1={1} pft={2:F1} mfe={3:F1}",
                    _historyCount,
                    context.IsT1Hit ? 1 : 0,
                    context.CurrentProfitTicks,
                    context.MaxMFETicks);

            string setId = !string.IsNullOrEmpty(conditionSetId)
                ? conditionSetId
                : context.ConditionSetId;

            FootprintTradeFamily family = ResolveFamily(setId);

            int dirScore = direction == SignalDirection.Long
                ? ScoreDirectionalDeteriorationLong(current)
                : ScoreDirectionalDeteriorationShort(current);

            int extremeScore = direction == SignalDirection.Long
                ? ScoreExtremeFailureLong(current)
                : ScoreExtremeFailureShort(current);

            int absScore = direction == SignalDirection.Long
                ? ScoreAbsorptionLong(current)
                : ScoreAbsorptionShort(current);

            int stackScore = direction == SignalDirection.Long
                ? ScoreStackedImbalanceLong(current)
                : ScoreStackedImbalanceShort(current);

            int slopeScore = ScoreCumDeltaDeterioration(direction);
            int persistScore = ScorePersistence(direction);
            int stageScore = ScoreStageSensitivity(context);
            int familyBias = GetFamilyScoreBias(family);
            int severity = dirScore + extremeScore + absScore + stackScore
                         + slopeScore + persistScore + stageScore + familyBias;

            int tightenAt = context.IsT1Hit
                ? _config.PostT1TightenAtScore
                : _config.PreT1TightenAtScore;

            int exitAt = context.IsT1Hit
                ? _config.PostT1ExitAtScore
                : _config.PreT1ExitAtScore;

            int hardSignals = CountHardExitSignals(direction, current);
            int adverseCount = GetAdversePersistenceCount(direction);
            double slope = GetCumDeltaSlope();

            FootprintTradeDecision decision = direction == SignalDirection.Long
                ? (CheckHardExitLong(current, context)
                    ? BuildDecision(FootprintTradeAction.ExitEarly, 100, 0.0, "TA_EXIT_LONG_HARD", family)
                    : BuildDecisionFromSeverity(severity, context, family))
                : (CheckHardExitShort(current, context)
                    ? BuildDecision(FootprintTradeAction.ExitEarly, 100, 0.0, "TA_EXIT_SHORT_HARD", family)
                    : BuildDecisionFromSeverity(severity, context, family));

            return string.Format(
                "fam={0} act={1} reason={2} sev={3} thr={4}/{5} hard={6} adv={7} dir={8} ext={9} abs={10} stk={11} slopeScore={12} pers={13} stage={14} bias={15} bpct={16:F3} bd={17:F0} cd={18:F0} slope={19:F1} dsh={20:F0} dsl={21:F0} buy={22:F0} sell={23:F0} absv={24:F1} bull={25}/{26} bear={27}/{28} hist={29} t1={30} pft={31:F1} mfe={32:F1}",
                family,
                decision.Action,
                decision.Reason,
                decision.SeverityScore,
                tightenAt,
                exitAt,
                hardSignals,
                adverseCount,
                dirScore,
                extremeScore,
                absScore,
                stackScore,
                slopeScore,
                persistScore,
                stageScore,
                familyBias,
                current.DeltaPct,
                current.BarDelta,
                current.CumDelta,
                slope,
                current.DeltaSh,
                current.DeltaSl,
                current.TotalBuyVol,
                current.TotalSellVol,
                current.AbsorptionScore,
                current.StackedBullRun,
                current.HasBullStack ? 1 : 0,
                current.StackedBearRun,
                current.HasBearStack ? 1 : 0,
                _historyCount,
                context.IsT1Hit ? 1 : 0,
                context.CurrentProfitTicks,
                context.MaxMFETicks);
        }

        private int CountHardExitSignals(SignalDirection direction, FootprintResult current)
        {
            int severeCount = 0;

            if (direction == SignalDirection.Long)
            {
                if (current.DeltaPct <= -_config.HardExitDeltaPct)
                    severeCount++;
                if (current.TotalBuyVol > 0.0 && current.TotalSellVol > current.TotalBuyVol * _config.HardExitVolumeDominanceRatio)
                    severeCount++;
                if (current.DeltaSl <= -_config.HardExitExtremeDeltaThreshold)
                    severeCount++;
                if (current.HasBearStack && !current.HasBullStack)
                    severeCount++;
                if (GetCumDeltaSlope() <= -_config.HardExitCumDeltaSlopeThreshold)
                    severeCount++;
                if (IsBearishAbsorption(current))
                    severeCount++;
            }
            else
            {
                if (current.DeltaPct >= _config.HardExitDeltaPct)
                    severeCount++;
                if (current.TotalSellVol > 0.0 && current.TotalBuyVol > current.TotalSellVol * _config.HardExitVolumeDominanceRatio)
                    severeCount++;
                if (current.DeltaSh >= _config.HardExitExtremeDeltaThreshold)
                    severeCount++;
                if (current.HasBullStack && !current.HasBearStack)
                    severeCount++;
                if (GetCumDeltaSlope() >= _config.HardExitCumDeltaSlopeThreshold)
                    severeCount++;
                if (IsBullishAbsorption(current))
                    severeCount++;
            }

            return severeCount;
        }

        // ====================================================================
        // HISTORY HELPERS
        // ====================================================================

        private bool TryGetCurrent(out FootprintResult result)
        {
            result = FootprintResult.Zero;
            if (_historyCount <= 0)
                return false;

            int newest = _historyWriteIndex - 1;
            if (newest < 0) newest += HISTORY_SIZE;
            result = _history[newest];
            return true;
        }

        private double GetCumDeltaSlope()
        {
            FootprintResult newest;
            if (!TryGetCurrent(out newest))
                return 0.0;

            FootprintResult previous;
            if (!TryGetHistoryFromNewest(1, out previous) || !previous.IsValid)
                return 0.0;

            return newest.CumDelta - previous.CumDelta;
        }

        private int GetAdversePersistenceCount(SignalDirection direction)
        {
            int count = 0;
            int maxLookback = Math.Min(_historyCount, 3);

            for (int i = 0; i < maxLookback; i++)
            {
                FootprintResult item;
                if (!TryGetHistoryFromNewest(i, out item) || !item.IsValid)
                    break;

                bool adverse = direction == SignalDirection.Long
                    ? (item.BarDelta < 0.0 && item.DeltaPct <= -_config.MinConvictionDeltaPct)
                    : (item.BarDelta > 0.0 && item.DeltaPct >= _config.MinConvictionDeltaPct);

                if (!adverse)
                    break;

                count++;
            }

            return count;
        }

        private bool TryGetHistoryFromNewest(int offset, out FootprintResult result)
        {
            result = FootprintResult.Zero;
            if (offset < 0 || offset >= _historyCount)
                return false;

            int index = _historyWriteIndex - 1 - offset;
            while (index < 0)
                index += HISTORY_SIZE;

            result = _history[index];
            return true;
        }

        // ====================================================================
        // DIRECTIONAL INTERPRETATION HELPERS
        // ====================================================================

        private static bool IsBearishAbsorption(FootprintResult current)
        {
            return current.MaxAskVol > current.MaxBidVol;
        }

        private static bool IsBullishAbsorption(FootprintResult current)
        {
            return current.MaxBidVol > current.MaxAskVol;
        }
    }
}
