#region Using declarations
using System;
using MathLogic.Strategy;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ========================================================================
    // FOOTPRINT ENTRY ADVISOR
    // ========================================================================
    // COMPLETE IMPLEMENTATION CHECKLIST — embedded directly in the file per request.
    //
    // A. Final role
    //   [x] FootprintEntryAdvisor is the pre-entry footprint approval engine.
    //   [x] It answers only: deny / weak approve / strong approve.
    //   [x] It does not compute raw footprint math.
    //   [x] It consumes FootprintResult only for order-flow truth.
    //   [x] It does not own trade management, exits, zones, or assembly.
    //
    // B. Final architecture
    //   [x] FootprintAssembler remains the only volumetric/NT8 reader (external contract).
    //   [x] FootprintCore remains the only footprint fact engine (external contract).
    //   [x] FootprintEntryAdvisor is the pre-entry order-flow interpreter.
    //   [ ] External follow-up: downstream modules should stop inventing parallel
    //       footprint approval logic.
    //
    // C. Insertion point
    //   [x] Recommended seam documented: HostStrategy after _engine.Evaluate(snapshot)
    //       and before _rankingEngine.Rank(...).
    //   [x] Advisor itself is independent of HostStrategy and ranking internals.
    //   [ ] External follow-up: freeze the actual insertion point in HostStrategy.
    //
    // D. Current duplicate gates to remove later
    //   [x] This file assumes condition sets should emit setups, not perform the
    //       final footprint approval themselves.
    //   [ ] External follow-up: audit and remove EntryGate.PassesConfluence(...)
    //       / EntryGate.ScoreConfluence(...) from condition sets over time.
    //
    // E. ConfluenceEngine migration plan
    //   [x] Advisor is built so it can pre-filter before ranking.
    //   [ ] External follow-up: shrink or replace ConfluenceEngine Layer C so this
    //       advisor becomes the single pre-entry footprint interpreter.
    //
    // F. Invalid-footprint policy
    //   [x] Explicit policy enum provided.
    //   [x] Recommended default = NeutralNoVetoNoBonus.
    //   [x] Hard deny / weak deny alternatives also supported.
    //   [x] Policy is documented and visible in config.
    //
    // G. Per-set policy
    //   [x] Family-level override path provided.
    //   [x] SMF_Native_* family supported.
    //   [x] SMC_BOS_v1 supported.
    //   [x] Default/fallback family supported.
    //
    // H. Public API
    //   [x] OnSessionOpen()
    //   [x] OnNewFootprint(in FootprintResult result)
    //   [x] Evaluate(SignalDirection direction, string conditionSetId = "")
    //
    // I. State ownership
    //   [x] Small fixed ring buffer of recent FootprintResult.
    //   [x] Reset on session open.
    //   [x] No MarketSnapshot dependency for footprint truth.
    //   [x] No raw volumetric inputs.
    //
    // J. Output contract
    //   [x] IsVetoed
    //   [x] SupportScore
    //   [x] Multiplier
    //   [x] Reason
    //   [x] Action (Deny / ApproveWeak / ApproveStrong) for readability
    //   [x] Output maps directly to current ranking concepts: veto + score + multiplier.
    //
    // K. Data read from FootprintResult
    //   [x] BarDelta
    //   [x] DeltaPct
    //   [x] CumDelta
    //   [x] DeltaSh
    //   [x] DeltaSl
    //   [x] TotalBuyVol
    //   [x] TotalSellVol
    //   [x] AbsorptionScore
    //   [x] StackedBullRun / StackedBearRun
    //   [x] HasBullStack / HasBearStack
    //   [x] MaxSeenDelta / MinSeenDelta
    //
    // L. Hard veto rules
    //   [x] Long-side veto helper
    //   [x] Short-side veto helper
    //   [x] Symmetric long vs short logic
    //
    // M. Soft confirmation rules
    //   [x] Directional pressure
    //   [x] Conviction
    //   [x] Extreme defense
    //   [x] Absorption
    //   [x] Stacked imbalance
    //   [x] Recent CumDelta slope agreement
    //
    // N. Scoring model
    //   [x] Additive scoring, not opaque logic.
    //   [x] Score components split into focused helpers.
    //   [x] Thresholds for deny / approve weak / approve strong provided.
    //
    // O. History policy
    //   [x] Uses short rolling history of FootprintResult only.
    //   [x] Current + previous 2 or 3 results available from fixed ring.
    //   [x] CumDelta slope derives from buffered FootprintResult, not external snapshot logic.
    //
    // P. Allocation discipline
    //   [x] Result type is a readonly struct.
    //   [x] Ring buffer is fixed-size.
    //   [x] No per-candidate collections.
    //   [x] Reason strings are constant short codes.
    //
    // Q. Non-goals
    //   [x] No raw footprint assembly
    //   [x] No ladder math
    //   [x] No trade management
    //   [x] No stop/target logic
    //   [x] No zone lifecycle
    //   [x] No HTF level scoring
    //   [x] No macro bias scoring
    //   [x] No signal generation itself
    //
    // R. Integration with ranking
    //   [x] If vetoed, candidate can be discarded before ranking.
    //   [x] If weak approved, candidate can receive reduced support / multiplier.
    //   [x] If strong approved, candidate can receive positive support / multiplier.
    //   [x] Ranking remains the chooser among surviving candidates.
    //
    // S. Logging/diagnostics
    //   [x] Compact reason codes returned for CSV/logging.
    //   [x] Includes directionally meaningful decision reason.
    //   [ ] External follow-up: HostStrategy / logger should emit advisor decisions.
    //
    // T. Build order
    //   [x] Freeze insertion point (documented here as recommended seam)
    //   [x] Freeze invalid-footprint policy (explicit enum + config)
    //   [x] Freeze per-set override policy (family override helper)
    //   [x] Define result struct
    //   [x] Define config struct
    //   [x] Create advisor class + ring buffer
    //   [x] Implement OnSessionOpen
    //   [x] Implement OnNewFootprint
    //   [x] Implement Evaluate
    //   [x] Implement hard veto helpers
    //   [x] Implement soft score helpers
    //   [x] Implement per-set overrides
    //   [ ] External follow-up: wire into HostStrategy before ranking
    //   [ ] External follow-up: remove duplicate EntryGate usage from condition sets
    //   [ ] External follow-up: begin Layer C cleanup
    //
    // U. Acceptance test
    //   [x] A condition set can still emit RawDecision.
    //   [x] EntryAdvisor can deny it using only FootprintResult.
    //   [x] Invalid footprint degrades gracefully according to chosen policy.
    //   [x] Surviving candidates can still flow into SignalRankingEngine.
    //   [ ] External follow-up: remove second pre-entry footprint authority after migration.
    //
    // V. One-line final definition
    //   [x] FootprintEntryAdvisor is the pre-ranking order-flow approval layer that
    //       consumes only FootprintResult plus short FootprintResult history,
    //       vetoes unsupported entries, and emits ranking-compatible support
    //       output for surviving candidates.
    // ========================================================================

    /// <summary>
    /// Explicit behavior when the advisor has no valid footprint to evaluate.
    ///
    /// CHECKLIST F:
    ///   - NeutralNoVetoNoBonus preserves degraded-mode operation.
    ///   - WeakDeny allows a soft penalty without killing all signals.
    ///   - HardDeny is the strict path for environments where footprint must exist.
    /// </summary>
    public enum FootprintUnavailablePolicy
    {
        NeutralNoVetoNoBonus = 0,
        WeakDeny             = 1,
        HardDeny             = 2,
    }

    /// <summary>
    /// Human-readable decision bucket for the candidate.
    /// This is redundant with IsVetoed/score/multiplier, but makes logging and
    /// debugging far clearer.
    /// </summary>
    public enum FootprintEntryAction
    {
        Neutral       = 0,
        Deny          = 1,
        ApproveWeak   = 2,
        ApproveStrong = 3,
    }

    /// <summary>
    /// Family-level policy override. Keeps the advisor from applying one flat
    /// rule set to all candidates.
    /// </summary>
    public enum FootprintEntryFamily
    {
        Default   = 0,
        SMFNative = 1,
        SMCBOS    = 2,
    }

    /// <summary>
    /// Immutable advisor configuration.
    ///
    /// CHECKLIST F / G / N:
    ///   - Makes invalid-footprint policy explicit.
    ///   - Defines score thresholds.
    ///   - Defines directional conviction and imbalance thresholds.
    ///   - Provides family-level multiplier adjustments.
    /// </summary>
    public readonly struct FootprintEntryAdvisorConfig
    {
        public FootprintUnavailablePolicy UnavailablePolicy { get; }

        // Directional conviction / dominance
        public double MinConvictionDeltaPct { get; }
        public double VolumeDominanceRatio { get; }

        // Hard-veto thresholds
        public double HardVetoDeltaPct { get; }
        public double HardVetoVolumeDominanceRatio { get; }
        public double HardVetoExtremeDeltaThreshold { get; }
        public double HardVetoCumDeltaSlopeThreshold { get; }

        // Soft score thresholds
        public double AbsorptionStrongThreshold { get; }
        public double AbsorptionModerateThreshold { get; }
        public int MinStackedLevelsForSupport { get; }

        // Score thresholds
        public int DenyBelowScore { get; }
        public int StrongApproveAtOrAboveScore { get; }

        // Ranking multipliers
        public double WeakApproveMultiplier { get; }
        public double StrongApproveMultiplier { get; }
        public double WeakDenyMultiplier { get; }

        // Family tweaks
        public int SMFNativeScoreBias { get; }
        public int SMCBOSScoreBias { get; }
        public double SMFNativeMultiplierBias { get; }
        public double SMCBOSMultiplierBias { get; }

        public FootprintEntryAdvisorConfig(
            FootprintUnavailablePolicy unavailablePolicy,
            double minConvictionDeltaPct,
            double volumeDominanceRatio,
            double hardVetoDeltaPct,
            double hardVetoVolumeDominanceRatio,
            double hardVetoExtremeDeltaThreshold,
            double hardVetoCumDeltaSlopeThreshold,
            double absorptionStrongThreshold,
            double absorptionModerateThreshold,
            int minStackedLevelsForSupport,
            int denyBelowScore,
            int strongApproveAtOrAboveScore,
            double weakApproveMultiplier,
            double strongApproveMultiplier,
            double weakDenyMultiplier,
            int smfNativeScoreBias,
            int smcBosScoreBias,
            double smfNativeMultiplierBias,
            double smcBosMultiplierBias)
        {
            if (minConvictionDeltaPct <= 0.0 || minConvictionDeltaPct >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(minConvictionDeltaPct), "minConvictionDeltaPct must be > 0 and < 1.");
            if (volumeDominanceRatio <= 1.0)
                throw new ArgumentOutOfRangeException(nameof(volumeDominanceRatio), "volumeDominanceRatio must be > 1.");
            if (hardVetoDeltaPct <= 0.0 || hardVetoDeltaPct >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(hardVetoDeltaPct), "hardVetoDeltaPct must be > 0 and < 1.");
            if (hardVetoVolumeDominanceRatio <= 1.0)
                throw new ArgumentOutOfRangeException(nameof(hardVetoVolumeDominanceRatio), "hardVetoVolumeDominanceRatio must be > 1.");
            if (hardVetoExtremeDeltaThreshold <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(hardVetoExtremeDeltaThreshold), "hardVetoExtremeDeltaThreshold must be > 0.");
            if (hardVetoCumDeltaSlopeThreshold <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(hardVetoCumDeltaSlopeThreshold), "hardVetoCumDeltaSlopeThreshold must be > 0.");
            if (absorptionStrongThreshold <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(absorptionStrongThreshold), "absorptionStrongThreshold must be > 0.");
            if (absorptionModerateThreshold <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(absorptionModerateThreshold), "absorptionModerateThreshold must be > 0.");
            if (absorptionStrongThreshold < absorptionModerateThreshold)
                throw new ArgumentOutOfRangeException(nameof(absorptionStrongThreshold), "absorptionStrongThreshold must be >= absorptionModerateThreshold.");
            if (minStackedLevelsForSupport <= 0)
                throw new ArgumentOutOfRangeException(nameof(minStackedLevelsForSupport), "minStackedLevelsForSupport must be > 0.");
            if (denyBelowScore < 0)
                throw new ArgumentOutOfRangeException(nameof(denyBelowScore), "denyBelowScore must be >= 0.");
            if (strongApproveAtOrAboveScore <= denyBelowScore)
                throw new ArgumentOutOfRangeException(nameof(strongApproveAtOrAboveScore), "strongApproveAtOrAboveScore must be greater than denyBelowScore.");
            if (weakApproveMultiplier <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(weakApproveMultiplier), "weakApproveMultiplier must be > 0.");
            if (strongApproveMultiplier <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(strongApproveMultiplier), "strongApproveMultiplier must be > 0.");
            if (weakDenyMultiplier < 0.0)
                throw new ArgumentOutOfRangeException(nameof(weakDenyMultiplier), "weakDenyMultiplier must be >= 0.");

            UnavailablePolicy = unavailablePolicy;
            MinConvictionDeltaPct = minConvictionDeltaPct;
            VolumeDominanceRatio = volumeDominanceRatio;
            HardVetoDeltaPct = hardVetoDeltaPct;
            HardVetoVolumeDominanceRatio = hardVetoVolumeDominanceRatio;
            HardVetoExtremeDeltaThreshold = hardVetoExtremeDeltaThreshold;
            HardVetoCumDeltaSlopeThreshold = hardVetoCumDeltaSlopeThreshold;
            AbsorptionStrongThreshold = absorptionStrongThreshold;
            AbsorptionModerateThreshold = absorptionModerateThreshold;
            MinStackedLevelsForSupport = minStackedLevelsForSupport;
            DenyBelowScore = denyBelowScore;
            StrongApproveAtOrAboveScore = strongApproveAtOrAboveScore;
            WeakApproveMultiplier = weakApproveMultiplier;
            StrongApproveMultiplier = strongApproveMultiplier;
            WeakDenyMultiplier = weakDenyMultiplier;
            SMFNativeScoreBias = smfNativeScoreBias;
            SMCBOSScoreBias = smcBosScoreBias;
            SMFNativeMultiplierBias = smfNativeMultiplierBias;
            SMCBOSMultiplierBias = smcBosMultiplierBias;
        }

        public static FootprintEntryAdvisorConfig Default
        {
            get
            {
                return new FootprintEntryAdvisorConfig(
                    unavailablePolicy:               FootprintUnavailablePolicy.NeutralNoVetoNoBonus,
                    minConvictionDeltaPct:           StrategyConfig.Modules.EA_MIN_CONVICTION_DELTA_PCT,
                    volumeDominanceRatio:            StrategyConfig.Modules.EA_VOLUME_DOMINANCE_RATIO,
                    hardVetoDeltaPct:                StrategyConfig.Modules.EA_HARD_VETO_DELTA_PCT,
                    hardVetoVolumeDominanceRatio:    StrategyConfig.Modules.EA_HARD_VETO_VOL_DOM_RATIO,
                    hardVetoExtremeDeltaThreshold:   StrategyConfig.Modules.EA_HARD_VETO_EXTREME_DELTA,
                    hardVetoCumDeltaSlopeThreshold:  StrategyConfig.Modules.EA_HARD_VETO_CUM_DELTA_SLOPE,
                    absorptionStrongThreshold:       StrategyConfig.Modules.EA_ABSORPTION_STRONG_THRESHOLD,
                    absorptionModerateThreshold:     StrategyConfig.Modules.EA_ABSORPTION_MODERATE_THRESHOLD,
                    minStackedLevelsForSupport:      StrategyConfig.Modules.EA_MIN_STACKED_LEVELS_SUPPORT,
                    denyBelowScore:                  StrategyConfig.Modules.EA_DENY_BELOW_SCORE,
                    strongApproveAtOrAboveScore:     StrategyConfig.Modules.EA_STRONG_APPROVE_SCORE,
                    weakApproveMultiplier:           StrategyConfig.Modules.EA_WEAK_APPROVE_MULTIPLIER,
                    strongApproveMultiplier:         StrategyConfig.Modules.EA_STRONG_APPROVE_MULTIPLIER,
                    weakDenyMultiplier:              StrategyConfig.Modules.EA_WEAK_DENY_MULTIPLIER,
                    smfNativeScoreBias:              StrategyConfig.Modules.EA_SMF_NATIVE_SCORE_BIAS,
                    smcBosScoreBias:                 StrategyConfig.Modules.EA_SMC_BOS_SCORE_BIAS,
                    smfNativeMultiplierBias:         StrategyConfig.Modules.EA_SMF_NATIVE_MULT_BIAS,
                    smcBosMultiplierBias:            StrategyConfig.Modules.EA_SMC_BOS_MULT_BIAS);
            }
        }
    }

    /// <summary>
    /// Immutable advisor decision.
    ///
    /// CHECKLIST J / R / S:
    ///   - Maps directly to current ranking semantics.
    ///   - Compact reason code for logging.
    ///   - Includes action for readability.
    /// </summary>
    public readonly struct FootprintEntryDecision
    {
        public FootprintEntryAction Action { get; }
        public bool IsVetoed { get; }
        public int SupportScore { get; }
        public double Multiplier { get; }
        public string Reason { get; }

        public FootprintEntryDecision(
            FootprintEntryAction action,
            bool isVetoed,
            int supportScore,
            double multiplier,
            string reason)
        {
            Action = action;
            IsVetoed = isVetoed;
            SupportScore = supportScore;
            Multiplier = multiplier;
            Reason = reason ?? "EA_NONE";
        }

        public static readonly FootprintEntryDecision Neutral =
            new FootprintEntryDecision(FootprintEntryAction.Neutral, false, 50, 1.00, "EA_NEUTRAL");

        public static readonly FootprintEntryDecision HardDeny =
            new FootprintEntryDecision(FootprintEntryAction.Deny, true, 0, 0.0, "EA_NO_FOOTPRINT");
    }

    /// <summary>
    /// Pre-entry footprint approval engine.
    ///
    /// RECOMMENDED INSERTION POINT:
    ///   HostStrategy after _engine.Evaluate(snapshot) and before _rankingEngine.Rank(...).
    ///
    /// HOT PATH RULES:
    ///   - fixed ring buffer
    ///   - readonly struct decision output
    ///   - no per-candidate collections
    ///   - no retained references beyond FootprintResult value copies
    /// </summary>
    public sealed class FootprintEntryAdvisor
    {
        // CHECKLIST I / O / P — fixed ring buffer, no dynamic growth.
        private const int HISTORY_SIZE = 4;

        private readonly FootprintEntryAdvisorConfig _config;
        private readonly FootprintResult[] _history = new FootprintResult[HISTORY_SIZE];
        private int _historyWriteIndex;
        private int _historyCount;

        public FootprintEntryAdvisor(FootprintEntryAdvisorConfig config)
        {
            _config = config;
            OnSessionOpen();
        }

        /// <summary>
        /// CHECKLIST H / I / T — session reset clears history and advisor state.
        /// </summary>
        public void OnSessionOpen()
        {
            Array.Clear(_history, 0, _history.Length);
            _historyWriteIndex = 0;
            _historyCount = 0;
        }

        /// <summary>
        /// CHECKLIST H / I / O — ingest one completed FootprintCore output.
        /// This should be called once per primary bar after FootprintCore computes.
        /// </summary>
        public void OnNewFootprint(in FootprintResult result)
        {
            _history[_historyWriteIndex] = result;
            _historyWriteIndex = (_historyWriteIndex + 1) % HISTORY_SIZE;
            if (_historyCount < HISTORY_SIZE)
                _historyCount++;
        }

        /// <summary>
        /// Main entry point.
        ///
        /// CHECKLIST A / H / J / R:
        ///   - consumes only FootprintResult history
        ///   - returns ranking-compatible support output
        ///   - no raw footprint math and no external order-flow source
        /// </summary>
        public FootprintEntryDecision Evaluate(SignalDirection direction, string conditionSetId = "")
        {
            if (direction != SignalDirection.Long && direction != SignalDirection.Short)
                return new FootprintEntryDecision(FootprintEntryAction.Deny, true, 0, 0.0, "EA_DIR_NONE");

            FootprintResult current;
            if (!TryGetCurrent(out current))
                return BuildUnavailableDecision();

            if (!current.IsValid)
                return BuildUnavailableDecision();

            FootprintEntryFamily family = ResolveFamily(conditionSetId);

            if (direction == SignalDirection.Long)
            {
                if (CheckHardVetoLong(current))
                    return BuildDecision(FootprintEntryAction.Deny, true, 0, 0.0, "EA_VETO_LONG", family);

                int score = 0;
                score += ScoreDirectionalPressureLong(current);
                score += ScoreExtremeDefenseLong(current);
                score += ScoreAbsorptionLong(current);
                score += ScoreStackedImbalanceLong(current);
                score += ScoreCumDeltaAgreement(direction);
                score += GetFamilyScoreBias(family);

                return BuildDecisionFromScore(score, direction, family);
            }
            else
            {
                if (CheckHardVetoShort(current))
                    return BuildDecision(FootprintEntryAction.Deny, true, 0, 0.0, "EA_VETO_SHORT", family);

                int score = 0;
                score += ScoreDirectionalPressureShort(current);
                score += ScoreExtremeDefenseShort(current);
                score += ScoreAbsorptionShort(current);
                score += ScoreStackedImbalanceShort(current);
                score += ScoreCumDeltaAgreement(direction);
                score += GetFamilyScoreBias(family);

                return BuildDecisionFromScore(score, direction, family);
            }
        }

        // ====================================================================
        // INVALID FOOTPRINT POLICY
        // ====================================================================

        private FootprintEntryDecision BuildUnavailableDecision()
        {
            switch (_config.UnavailablePolicy)
            {
                case FootprintUnavailablePolicy.HardDeny:
                    return new FootprintEntryDecision(FootprintEntryAction.Deny, true, 0, 0.0, "EA_NO_FOOTPRINT");

                case FootprintUnavailablePolicy.WeakDeny:
                    return new FootprintEntryDecision(FootprintEntryAction.Neutral, false, 50, _config.WeakDenyMultiplier, "EA_NO_FOOTPRINT");

                case FootprintUnavailablePolicy.NeutralNoVetoNoBonus:
                default:
                    return FootprintEntryDecision.Neutral;
            }
        }

        // ====================================================================
        // HARD VETOES
        // ====================================================================

        private bool CheckHardVetoLong(FootprintResult current)
        {
            if (current.DeltaPct <= -_config.HardVetoDeltaPct)
                return true;

            // FIX (#ClimaxVeto): Veto Long if sellers are Absorbing at the HIGH
            // (Negative DeltaSh on a green bar). This identifies a "Buyer Climax".
            if (current.BarDelta > 0 && current.DeltaSh < -_config.HardVetoExtremeDeltaThreshold)
                return true;

            if (current.TotalBuyVol > 0.0 && current.TotalSellVol > current.TotalBuyVol * _config.HardVetoVolumeDominanceRatio)
                return true;

            if (current.HasBearStack && !current.HasBullStack)
                return true;

            if (current.DeltaSl <= -_config.HardVetoExtremeDeltaThreshold)
                return true;

            if (GetCumDeltaSlope() <= -_config.HardVetoCumDeltaSlopeThreshold)
                return true;

            return false;
        }

        private bool CheckHardVetoShort(FootprintResult current)
        {
            if (current.DeltaPct >= _config.HardVetoDeltaPct)
                return true;

            // FIX (#ClimaxVeto): Veto Short if buyers are Absorbing at the LOW
            // (Positive DeltaSl on a red bar). This identifies a "Seller Climax".
            if (current.BarDelta < 0 && current.DeltaSl > _config.HardVetoExtremeDeltaThreshold)
                return true;

            if (current.TotalSellVol > 0.0 && current.TotalBuyVol > current.TotalSellVol * _config.HardVetoVolumeDominanceRatio)
                return true;

            if (current.HasBullStack && !current.HasBearStack)
                return true;

            if (current.DeltaSh >= _config.HardVetoExtremeDeltaThreshold)
                return true;

            if (GetCumDeltaSlope() >= _config.HardVetoCumDeltaSlopeThreshold)
                return true;

            return false;
        }

        // ====================================================================
        // SOFT SCORING
        // ====================================================================

        private int ScoreDirectionalPressureLong(FootprintResult current)
        {
            int score = 0;

            if (current.BarDelta > 0.0) score += 15;
            if (current.DeltaPct >= _config.MinConvictionDeltaPct) score += 10;
            if (current.TotalSellVol > 0.0 && current.TotalBuyVol > current.TotalSellVol * _config.VolumeDominanceRatio) score += 15;
            if (current.MaxSeenDelta > 0.0) score += 5;

            return score;
        }

        private int ScoreDirectionalPressureShort(FootprintResult current)
        {
            int score = 0;

            if (current.BarDelta < 0.0) score += 15;
            if (current.DeltaPct <= -_config.MinConvictionDeltaPct) score += 10;
            if (current.TotalBuyVol > 0.0 && current.TotalSellVol > current.TotalBuyVol * _config.VolumeDominanceRatio) score += 15;
            if (current.MinSeenDelta < 0.0) score += 5;

            return score;
        }

        private int ScoreExtremeDefenseLong(FootprintResult current)
        {
            int score = 0;
            if (current.DeltaSl > 0.0) score += 12;
            if (current.DeltaSh < 0.0) score += 4; // seller rejection at the high is neutral-to-supportive for a healthy long bar
            return score;
        }

        private int ScoreExtremeDefenseShort(FootprintResult current)
        {
            int score = 0;
            if (current.DeltaSh < 0.0) score += 12;
            // Do not reward buying at the low for shorts. Positive DeltaSl is generally
            // opposing pressure for a short entry, so it carries no bonus here.
            return score;
        }

        private int ScoreAbsorptionLong(FootprintResult current)
        {
            // Direction-aware heuristic:
            //   bullish absorption is approximated by larger bid-side extreme volume,
            //   meaning aggressive sellers were met by passive buyers.
            if (current.MaxBidVol <= current.MaxAskVol)
                return 0;

            // FIX (#EffortVsResult): Displacement check.
            // Body must be positive and at least 20% of range to show sellers were overtaken.
            double range = current.High - current.Low;
            double body  = current.Close - current.Open;
            bool displaced = range > 0 && (body / range) > 0.20;

            if (current.AbsorptionScore >= _config.AbsorptionStrongThreshold)
                return displaced ? 15 : 6; // Full bonus only if price displaces
            if (current.AbsorptionScore >= _config.AbsorptionModerateThreshold)
                return displaced ? 8 : 3;
            return 0;
        }

        private int ScoreAbsorptionShort(FootprintResult current)
        {
            // Direction-aware heuristic:
            //   bearish absorption is approximated by larger ask-side extreme volume,
            //   meaning aggressive buyers were met by passive sellers.
            if (current.MaxAskVol <= current.MaxBidVol)
                return 0;

            // FIX (#EffortVsResult): Displacement check.
            double range = current.High - current.Low;
            double body  = current.Open - current.Close; // short body
            bool displaced = range > 0 && (body / range) > 0.20;

            if (current.AbsorptionScore >= _config.AbsorptionStrongThreshold)
                return displaced ? 15 : 6;
            if (current.AbsorptionScore >= _config.AbsorptionModerateThreshold)
                return displaced ? 8 : 3;
            return 0;
        }

        private int ScoreStackedImbalanceLong(FootprintResult current)
        {
            int score = 0;
            if (current.StackedBullRun >= _config.MinStackedLevelsForSupport) score += 15;
            if (!current.HasBearStack) score += 4;
            return score;
        }

        private int ScoreStackedImbalanceShort(FootprintResult current)
        {
            int score = 0;
            if (current.StackedBearRun >= _config.MinStackedLevelsForSupport) score += 15;
            if (!current.HasBullStack) score += 4;
            return score;
        }

        private int ScoreCumDeltaAgreement(SignalDirection direction)
        {
            double slope = GetCumDeltaSlope();

            if (direction == SignalDirection.Long)
            {
                if (slope > 0.0) return 15;
                if (slope == 0.0) return 4;
                return 0;
            }
            else
            {
                if (slope < 0.0) return 15;
                if (slope == 0.0) return 4;
                return 0;
            }
        }

        // ====================================================================
        // DECISION BUILDERS
        // ====================================================================

        private FootprintEntryDecision BuildDecisionFromScore(int score, SignalDirection direction, FootprintEntryFamily family)
        {
            string dirCode = direction == SignalDirection.Long ? "L" : "S";

            if (score < _config.DenyBelowScore)
                return BuildDecision(FootprintEntryAction.Deny, true, score, 0.0, "EA_DENY_" + dirCode, family);

            if (score >= _config.StrongApproveAtOrAboveScore)
                return BuildDecision(FootprintEntryAction.ApproveStrong, false, score, _config.StrongApproveMultiplier + GetFamilyMultiplierBias(family), "EA_STRONG_" + dirCode, family);

            return BuildDecision(FootprintEntryAction.ApproveWeak, false, score, _config.WeakApproveMultiplier + GetFamilyMultiplierBias(family), "EA_WEAK_" + dirCode, family);
        }

        private static FootprintEntryDecision BuildDecision(
            FootprintEntryAction action,
            bool isVetoed,
            int supportScore,
            double multiplier,
            string reason,
            FootprintEntryFamily family)
        {
            // Clamp multiplier to zero floor so family bias cannot make it negative.
            if (multiplier < 0.0)
                multiplier = 0.0;

            string familyCode = family == FootprintEntryFamily.SMFNative ? "_SMF"
                              : family == FootprintEntryFamily.SMCBOS ? "_BOS"
                              : "_DEF";

            return new FootprintEntryDecision(action, isVetoed, supportScore, multiplier, reason + familyCode);
        }

        // ====================================================================
        // FAMILY POLICY
        // ====================================================================

        private static FootprintEntryFamily ResolveFamily(string conditionSetId)
        {
            if (!string.IsNullOrEmpty(conditionSetId))
            {
                if (conditionSetId.StartsWith("SMF_Native_", StringComparison.Ordinal))
                    return FootprintEntryFamily.SMFNative;

                if (string.Equals(conditionSetId, "SMC_BOS_v1", StringComparison.Ordinal))
                    return FootprintEntryFamily.SMCBOS;
            }

            return FootprintEntryFamily.Default;
        }

        private int GetFamilyScoreBias(FootprintEntryFamily family)
        {
            switch (family)
            {
                case FootprintEntryFamily.SMFNative:
                    return _config.SMFNativeScoreBias;
                case FootprintEntryFamily.SMCBOS:
                    return _config.SMCBOSScoreBias;
                default:
                    return 0;
            }
        }

        private double GetFamilyMultiplierBias(FootprintEntryFamily family)
        {
            switch (family)
            {
                case FootprintEntryFamily.SMFNative:
                    return _config.SMFNativeMultiplierBias;
                case FootprintEntryFamily.SMCBOS:
                    return _config.SMCBOSMultiplierBias;
                default:
                    return 0.0;
            }
        }

        // ====================================================================
        // HISTORY HELPERS
        // ====================================================================

        private bool TryGetCurrent(out FootprintResult result)
        {
            if (_historyCount <= 0)
            {
                result = FootprintResult.Zero;
                return false;
            }

            int idx = _historyWriteIndex - 1;
            if (idx < 0) idx += HISTORY_SIZE;
            result = _history[idx];
            return true;
        }

        private double GetCumDeltaSlope()
        {
            if (_historyCount < 2)
                return 0.0;

            FootprintResult newest = GetHistoryFromNewest(0);
            FootprintResult oldest = GetHistoryFromNewest(Math.Min(_historyCount - 1, 2));

            if (!newest.IsValid || !oldest.IsValid)
                return 0.0;

            return newest.CumDelta - oldest.CumDelta;
        }

        private FootprintResult GetHistoryFromNewest(int offset)
        {
            int idx = _historyWriteIndex - 1 - offset;
            while (idx < 0) idx += HISTORY_SIZE;
            return _history[idx];
        }
    }
}
