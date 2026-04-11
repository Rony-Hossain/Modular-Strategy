#region Using declarations
using System;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ========================================================================
    // FOOTPRINT CORE
    // ========================================================================
    // COMPLETE IMPLEMENTATION CHECKLIST — embedded directly in the file per request.
    //
    // A. Final architecture
    //   [x] FootprintCore is a sealed instance class, not a static class.
    //   [x] FootprintCore wraps exactly one FootprintAssembler.
    //   [x] FootprintCore is the single public order-flow boundary.
    //   [ ] External architecture follow-up: no other module should compute an
    //       independent footprint truth (HostStrategy / ImbalanceZoneRegistry /
    //       ConfluenceEngine / advisors must consume FootprintResult only).
    //
    // B. Class shape
    //   [x] private readonly FootprintAssembler _assembler;
    //   [x] private readonly FootprintCoreConfig _config;
    //   [x] private bool _initialized;
    //
    // C. Public API
    //   [x] Initialize(...)
    //   [x] IsReady
    //   [x] IsSupportedPrimaryPeriod(...)
    //   [x] TryComputeCurrentBar(...)
    //
    // D. Diagnostic forwarding
    //   [x] LastFailureReason forwards to assembler
    //   [x] LastFailureMessage forwards to assembler
    //   [x] FailureCount forwards to assembler
    //   [x] No parallel diagnostic subsystem added here
    //
    // E. Initialization behavior
    //   [x] Initialize() calls _assembler.Initialize(...)
    //   [x] _initialized flips true only after successful init
    //   [x] Core fails closed before initialization
    //
    // F. Input contract
    //   [x] ComputeFromBar(...) takes in FootprintBar directly
    //   [x] No FootprintInput struct
    //   [x] No FootprintBar -> FootprintInput mapping layer
    //
    // G. Result contract
    //   [x] FootprintResult defined in this file
    //   [x] Assembly metadata preserved in result
    //   [x] AssemblyMode preserved in result
    //   [x] IsValid + Zero sentinel included
    //
    // H. Scalar facts copied through unchanged
    //   [x] BarDelta
    //   [x] CumDelta
    //   [x] DeltaSh
    //   [x] DeltaSl
    //   [x] DeltaPct
    //   [x] MaxSeenDelta
    //   [x] MinSeenDelta
    //   [x] TotalBuyVol
    //   [x] TotalSellVol
    //   [x] Trades
    //   [x] MaxPositiveDelta
    //   [x] MaxNegativeDelta
    //   [x] MaxAskVol
    //   [x] MaxAskVolPrice
    //   [x] MaxBidVol
    //   [x] MaxBidVolPrice
    //   [x] MaxCombinedVol
    //   [x] MaxCombinedVolPrice
    //
    // I. Remove redundant scalar math
    //   [x] No ComputeBarDelta(...)
    //   [x] No ComputeDeltaPct(...)
    //   [x] Core does not recompute assembler-canonical scalar truths
    //
    // J. Core-owned derived metrics
    //   [x] AbsorptionScore
    //   [x] StackedBullRun
    //   [x] StackedBearRun
    //   [x] HasBullStack
    //   [x] HasBearStack
    //
    // K. Internal helper functions
    //   [x] ValidateBar(...)
    //   [x] BuildZeroResult(...)
    //   [x] CopyScalarFacts(...)
    //   [x] ComputeAbsorptionScore(...)
    //   [x] ComputeStackedDiagonalRuns(...)
    //   [x] IsBullDiagonalLevel(...)
    //   [x] IsBearDiagonalLevel(...)
    //   [x] FinalizeResult(...)
    //
    // L. Validation rules in ValidateBar
    //   [x] bar.IsValid == true
    //   [x] bar.TickSize > 0
    //   [x] bar.LevelCount > 0
    //   [x] bar.Low <= bar.High
    //   [x] Prices / Ask / Bid not null
    //   [x] Required arrays length >= LevelCount
    //
    // M. Absorption rules
    //   [x] Configurable AbsorptionRatio
    //   [x] Default = 2.0
    //   [x] Current-level ask/bid only
    //   [x] Normalize by LevelCount
    //
    // N. Stacked imbalance rules
    //   [x] Diagonal imbalance only
    //   [x] Bull rule: bid[i] > ask[i - 1] * ratio
    //   [x] Bear rule: ask[i] > bid[i - 1] * ratio
    //   [x] Configurable diagonal ratio
    //   [x] Default = 3.0
    //   [x] Strongest consecutive bull/bear run tracked
    //   [x] Configurable MinStackedLevels
    //   [x] Default = 3
    //
    // O. Result finalization rules
    //   [x] HasBullStack = StackedBullRun >= MinStackedLevels
    //   [x] HasBearStack = StackedBearRun >= MinStackedLevels
    //   [x] Valid quiet bar remains IsValid = true
    //
    // P. Assembly-mode handling
    //   [x] TryComputeCurrentBar accepts FootprintAssemblyMode
    //   [x] Result carries AssemblyMode
    //   [x] Completed-bar vs forming-bar truth kept explicit
    //
    // Q. Memory and ownership rules
    //   [x] Core never persists bar.Prices / Ask / Bid / Delta / Total references
    //   [x] Compute is synchronous and value-result only
    //
    // R. Non-goals
    //   [x] No signal scoring
    //   [x] No entry approval
    //   [x] No hold / tighten / exit advice
    //   [x] No divergence logic
    //   [x] No zone lifecycle
    //   [x] No regime logic
    //   [x] No snapshot publishing
    //   [x] No direct volumetric aggregation outside FootprintAssembler
    //
    // S. External cleanup required after Core exists
    //   [ ] External follow-up: HostStrategy should publish FootprintResult-derived
    //       values only, not independent footprint math.
    //   [ ] External follow-up: ImbalanceZoneRegistry should stop being the
    //       canonical stacked imbalance source.
    //   [ ] External follow-up: Confluence and advisors should consume
    //       FootprintResult instead of multiple raw order-flow sources.
    //
    // T. Final corrected build order
    //   [x] FootprintCoreConfig
    //   [x] FootprintResult
    //   [x] FootprintCore sealed class
    //   [x] Constructor + fields
    //   [x] Initialize
    //   [x] IsReady
    //   [x] Diagnostic forwards
    //   [x] IsSupportedPrimaryPeriod
    //   [x] TryComputeCurrentBar
    //   [x] ComputeFromBar
    //   [x] ValidateBar
    //   [x] BuildZeroResult
    //   [x] CopyScalarFacts
    //   [x] ComputeAbsorptionScore
    //   [x] ComputeStackedDiagonalRuns
    //   [x] Helper methods
    //   [x] FinalizeResult
    //   [x] No redundant scalar math
    //   [x] No FootprintInput
    //   [x] No static FootprintCore variant
    //
    // U. Acceptance test
    //   [x] FootprintCore wraps FootprintAssembler, exposes init/mode/diagnostics,
    //       copies assembler-canonical scalar facts, computes only the missing
    //       one-bar footprint semantics, and returns one immutable FootprintResult.
    // ========================================================================

    /// <summary>
    /// Configuration for the custom semantics FootprintCore adds on top of the
    /// assembler's canonical primary-bar scalar aggregation.
    ///
    /// IMPORTANT:
    ///   - These settings do NOT change the assembler's scalar semantics.
    ///   - These settings ONLY control the additional ladder-derived facts that
    ///     FootprintCore owns: absorption and stacked diagonal imbalance.
    /// </summary>
    public readonly struct FootprintCoreConfig
    {
        // CHECKLIST M — configurable absorption ratio, default 2.0
        public double AbsorptionRatio { get; }

        // CHECKLIST N — configurable diagonal imbalance ratio, default 3.0
        public double DiagonalImbalanceRatio { get; }

        // CHECKLIST N / O — configurable minimum stacked levels, default 3
        public int MinStackedLevels { get; }

        public FootprintCoreConfig(double absorptionRatio, double diagonalImbalanceRatio, int minStackedLevels)
        {
            if (absorptionRatio <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(absorptionRatio), "absorptionRatio must be > 0.");
            if (diagonalImbalanceRatio <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(diagonalImbalanceRatio), "diagonalImbalanceRatio must be > 0.");
            if (minStackedLevels <= 0)
                throw new ArgumentOutOfRangeException(nameof(minStackedLevels), "minStackedLevels must be > 0.");

            AbsorptionRatio        = absorptionRatio;
            DiagonalImbalanceRatio = diagonalImbalanceRatio;
            MinStackedLevels       = minStackedLevels;
        }

        public static FootprintCoreConfig Default
        {
            get { return new FootprintCoreConfig(2.0, 3.0, 3); }
        }
    }

    /// <summary>
    /// Immutable published order-flow result.
    ///
    /// CHECKLIST G / H / J / O / P:
    ///   - Carries assembly metadata.
    ///   - Carries assembler-canonical scalar facts unchanged.
    ///   - Carries FootprintCore-owned derived facts.
    ///   - Distinguishes completed vs forming bar via AssemblyMode.
    ///   - Zero sentinel is safe default for failure paths.
    /// </summary>
    public readonly struct FootprintResult
    {
        // CHECKLIST G / O — validity and failure distinction
        public bool IsValid { get; }

        // CHECKLIST G / P — assembly metadata
        public DateTime PrimaryBarStartTime { get; }
        public DateTime PrimaryBarEndTime { get; }
        public int FeederBarsUsed { get; }
        public int ExpectedFeederBars { get; }
        public FootprintAssemblyMode AssemblyMode { get; }

        // Optional echo of bar geometry / ladder span for downstream read-only use.
        public double TickSize { get; }
        public double Open { get; }
        public double High { get; }
        public double Low { get; }
        public double Close { get; }
        public int LevelCount { get; }

        // CHECKLIST H — assembler-canonical scalar facts copied unchanged.
        public double BarDelta { get; }
        public double CumDelta { get; }
        public double DeltaSh { get; }
        public double DeltaSl { get; }
        public double DeltaPct { get; }
        public double MaxSeenDelta { get; }
        public double MinSeenDelta { get; }
        public double TotalBuyVol { get; }
        public double TotalSellVol { get; }
        public double Trades { get; }
        public double MaxPositiveDelta { get; }
        public double MaxNegativeDelta { get; }
        public double MaxAskVol { get; }
        public double MaxAskVolPrice { get; }
        public double MaxBidVol { get; }
        public double MaxBidVolPrice { get; }
        public double MaxCombinedVol { get; }
        public double MaxCombinedVolPrice { get; }

        // CHECKLIST J — FootprintCore-owned derived metrics.
        public double AbsorptionScore { get; }
        public int StackedBullRun { get; }
        public int StackedBearRun { get; }
        public double BullStackLow { get; }
        public double BullStackHigh { get; }
        public double BearStackLow { get; }
        public double BearStackHigh { get; }
        public bool HasBullStack { get; }
        public bool HasBearStack { get; }

        public FootprintResult(
            bool isValid,
            DateTime primaryBarStartTime,
            DateTime primaryBarEndTime,
            int feederBarsUsed,
            int expectedFeederBars,
            FootprintAssemblyMode assemblyMode,
            double tickSize,
            double open,
            double high,
            double low,
            double close,
            int levelCount,
            double barDelta,
            double cumDelta,
            double deltaSh,
            double deltaSl,
            double deltaPct,
            double maxSeenDelta,
            double minSeenDelta,
            double totalBuyVol,
            double totalSellVol,
            double trades,
            double maxPositiveDelta,
            double maxNegativeDelta,
            double maxAskVol,
            double maxAskVolPrice,
            double maxBidVol,
            double maxBidVolPrice,
            double maxCombinedVol,
            double maxCombinedVolPrice,
            double absorptionScore,
            int stackedBullRun,
            int stackedBearRun,
            double bullStackLow,
            double bullStackHigh,
            double bearStackLow,
            double bearStackHigh,
            bool hasBullStack,
            bool hasBearStack)
        {
            IsValid = isValid;
            PrimaryBarStartTime = primaryBarStartTime;
            PrimaryBarEndTime = primaryBarEndTime;
            FeederBarsUsed = feederBarsUsed;
            ExpectedFeederBars = expectedFeederBars;
            AssemblyMode = assemblyMode;
            TickSize = tickSize;
            Open = open;
            High = high;
            Low = low;
            Close = close;
            LevelCount = levelCount;
            BarDelta = barDelta;
            CumDelta = cumDelta;
            DeltaSh = deltaSh;
            DeltaSl = deltaSl;
            DeltaPct = deltaPct;
            MaxSeenDelta = maxSeenDelta;
            MinSeenDelta = minSeenDelta;
            TotalBuyVol = totalBuyVol;
            TotalSellVol = totalSellVol;
            Trades = trades;
            MaxPositiveDelta = maxPositiveDelta;
            MaxNegativeDelta = maxNegativeDelta;
            MaxAskVol = maxAskVol;
            MaxAskVolPrice = maxAskVolPrice;
            MaxBidVol = maxBidVol;
            MaxBidVolPrice = maxBidVolPrice;
            MaxCombinedVol = maxCombinedVol;
            MaxCombinedVolPrice = maxCombinedVolPrice;
            AbsorptionScore = absorptionScore;
            StackedBullRun = stackedBullRun;
            StackedBearRun = stackedBearRun;
            BullStackLow = bullStackLow;
            BullStackHigh = bullStackHigh;
            BearStackLow = bearStackLow;
            BearStackHigh = bearStackHigh;
            HasBullStack = hasBullStack;
            HasBearStack = hasBearStack;
        }

        public static readonly FootprintResult Zero = new FootprintResult(
            false,
            default(DateTime),
            default(DateTime),
            0,
            0,
            FootprintAssemblyMode.CompletedPrimaryBarOnly,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0,
            0,
            0.0,
            0.0,
            0.0,
            0.0,
            false,
            false);
    }

    /// <summary>
    /// FootprintCore — single public order-flow boundary.
    ///
    /// DESIGN CONTRACT:
    ///   - Wraps exactly one FootprintAssembler.
    ///   - Exposes readiness, compatibility, compute, and forwarded diagnostics.
    ///   - Consumes FootprintBar immediately and NEVER stores ladder references.
    ///   - Treats assembler scalar facts as canonical input truth.
    ///   - Adds ONLY the missing one-bar footprint semantics:
    ///       * AbsorptionScore
    ///       * StackedBullRun / StackedBearRun
    ///       * HasBullStack / HasBearStack
    ///
    /// NON-GOALS:
    ///   - No signal scoring.
    ///   - No entry / exit decisions.
    ///   - No divergence or history-based interpretation.
    ///   - No snapshot publishing.
    ///   - No zone, regime, or trade-management logic.
    /// </summary>
    public sealed class FootprintCore
    {
        // CHECKLIST B — exact field shape.
        private readonly FootprintAssembler  _assembler;
        private readonly FootprintCoreConfig _config;
        private bool                         _initialized;

        public FootprintCore(FootprintAssembler assembler, FootprintCoreConfig config)
        {
            if (assembler == null)
                throw new ArgumentNullException(nameof(assembler));
            if (config.AbsorptionRatio <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(config), "config.AbsorptionRatio must be > 0.");
            if (config.DiagonalImbalanceRatio <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(config), "config.DiagonalImbalanceRatio must be > 0.");
            if (config.MinStackedLevels <= 0)
                throw new ArgumentOutOfRangeException(nameof(config), "config.MinStackedLevels must be > 0.");

            _assembler   = assembler;
            _config      = config;
            _initialized = false;
        }

        // CHECKLIST C / E — public readiness surface.
        public bool IsReady => _initialized;

        // CHECKLIST D — direct diagnostic forwards to assembler.
        public FootprintAssemblyFailureReason LastFailureReason  => _assembler.LastFailureReason;
        public string                         LastFailureMessage => _assembler.LastFailureMessage;
        public int                            FailureCount       => _assembler.FailureCount;

        /// <summary>
        /// CHECKLIST C / E — initialize the upstream assembler and mark core ready.
        ///
        /// IMPORTANT:
        ///   - Core owns its own _initialized flag because the assembler's
        ///     internal _initialized field is private.
        ///   - Core does not expose or depend on any other hidden setup state.
        /// </summary>
        public void Initialize(
            double         tickSize,
            int            maxLevels,
            BarsPeriodType feederType,
            int            feederValue)
        {
            _assembler.Initialize(tickSize, maxLevels, feederType, feederValue);
            _initialized = true;
        }

        /// <summary>
        /// CHECKLIST C / E — compatibility check delegates to the assembler.
        /// Fails closed before Initialize() is called.
        /// </summary>
        public bool IsSupportedPrimaryPeriod(BarsPeriod primaryBarsPeriod)
        {
            if (!_initialized)
                return false;

            return _assembler.IsSupportedPrimaryPeriod(primaryBarsPeriod);
        }

        /// <summary>
        /// CHECKLIST C / P — single public compute entry point.
        ///
        /// FLOW:
        ///   1. Fail closed if Core was not initialized.
        ///   2. Ask FootprintAssembler to assemble one primary-bar FootprintBar.
        ///   3. If assembly fails, return FootprintResult.Zero.
        ///   4. If assembly succeeds, immediately consume the reused arrays and
        ///      publish a value-only FootprintResult.
        ///
        /// MEMORY RULE:
        ///   The assembler owns and reuses Prices/Ask/Bid/Delta/Total buffers.
        ///   This method must never store those references beyond the call.
        /// </summary>
        public bool TryComputeCurrentBar(
            VolumetricBarsType    volBarsType,
            Bars                  feederBars,
            int                   currentVolBarIndex,
            BarsPeriod            primaryBarsPeriod,
            DateTime              primaryBarStartTime,
            DateTime              primaryBarEndTime,
            double                primaryOpen,
            double                primaryHigh,
            double                primaryLow,
            double                primaryClose,
            FootprintAssemblyMode mode,
            out FootprintResult   result)
        {
            result = BuildZeroResult(mode);

            if (!_initialized)
                return false;

            FootprintBar bar;
            if (!_assembler.TryAssembleCurrentBar(
                    volBarsType,
                    feederBars,
                    currentVolBarIndex,
                    primaryBarsPeriod,
                    primaryBarStartTime,
                    primaryBarEndTime,
                    primaryOpen,
                    primaryHigh,
                    primaryLow,
                    primaryClose,
                    mode,
                    out bar))
            {
                return false;
            }

            result = ComputeFromBar(in bar, mode, in _config);
            return result.IsValid;
        }

        /// <summary>
        /// CHECKLIST T — private pure-math stage.
        ///
        /// IMPORTANT:
        ///   - Input is FootprintBar directly.
        ///   - No FootprintInput struct exists.
        ///   - No scalar recomputation for BarDelta / DeltaPct occurs here.
        /// </summary>
        private static FootprintResult ComputeFromBar(
            in FootprintBar        bar,
            FootprintAssemblyMode  mode,
            in FootprintCoreConfig config)
        {
            if (!ValidateBar(in bar))
                return BuildZeroResult(mode);

            // CHECKLIST G / H — preserve assembly metadata and scalar truth.
            FootprintResult baseResult = CopyScalarFacts(in bar, mode);

            // CHECKLIST J / M — absorption is Core-owned derived math.
            double absorptionScore = ComputeAbsorptionScore(in bar, config.AbsorptionRatio);

            // CHECKLIST J / N — diagonal stacked imbalance is Core-owned derived math.
            int stackedBullRun;
            int stackedBearRun;
            double bullStackLow;
            double bullStackHigh;
            double bearStackLow;
            double bearStackHigh;

            ComputeStackedDiagonalRuns(
                in bar,
                config.DiagonalImbalanceRatio,
                out stackedBullRun,
                out stackedBearRun,
                out bullStackLow,
                out bullStackHigh,
                out bearStackLow,
                out bearStackHigh);

            // CHECKLIST O — finalize stack booleans and mark valid.
            return FinalizeResult(
                in baseResult,
                absorptionScore,
                stackedBullRun,
                stackedBearRun,
                bullStackLow,
                bullStackHigh,
                bearStackLow,
                bearStackHigh,
                config.MinStackedLevels);
        }

        /// <summary>
        /// CHECKLIST K / L — structural validation only.
        ///
        /// RULES:
        ///   - Validates bar integrity, not signal quality.
        ///   - Delta[] and Total[] exist on FootprintBar, but they are not required
        ///     for the current Core-owned semantics, so they are not hard-required here.
        /// </summary>
        private static bool ValidateBar(in FootprintBar bar)
        {
            if (!bar.IsValid)
                return false;

            if (bar.TickSize <= 0.0)
                return false;

            if (bar.LevelCount <= 0)
                return false;

            if (bar.Low > bar.High)
                return false;

            if (bar.Prices == null || bar.Ask == null || bar.Bid == null)
                return false;

            if (bar.Prices.Length < bar.LevelCount)
                return false;

            if (bar.Ask.Length < bar.LevelCount)
                return false;

            if (bar.Bid.Length < bar.LevelCount)
                return false;

            return true;
        }

        /// <summary>
        /// CHECKLIST K / G — safe invalid default.
        /// </summary>
        private static FootprintResult BuildZeroResult(FootprintAssemblyMode mode)
        {
            return new FootprintResult(
                false,
                default(DateTime),
                default(DateTime),
                0,
                0,
                mode,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0,
                0,
                0.0,
                0.0,
                0.0,
                0.0,
                false,
                false);
        }

        /// <summary>
        /// CHECKLIST K / G / H — passthrough of assembler-canonical truth.
        ///
        /// IMPORTANT:
        ///   - Core does NOT recompute BarDelta.
        ///   - Core does NOT recompute DeltaPct.
        ///   - Core treats assembler scalar aggregation as canonical input truth.
        /// </summary>
        private static FootprintResult CopyScalarFacts(in FootprintBar bar, FootprintAssemblyMode mode)
        {
            return new FootprintResult(
                false,
                bar.PrimaryBarStartTime,
                bar.PrimaryBarEndTime,
                bar.FeederBarsUsed,
                bar.ExpectedFeederBars,
                mode,
                bar.TickSize,
                bar.Open,
                bar.High,
                bar.Low,
                bar.Close,
                bar.LevelCount,
                bar.BarDelta,
                bar.CumDelta,
                bar.DeltaSh,
                bar.DeltaSl,
                bar.DeltaPct,
                bar.MaxSeenDelta,
                bar.MinSeenDelta,
                bar.TotalBuyVol,
                bar.TotalSellVol,
                bar.Trades,
                bar.MaxPositiveDelta,
                bar.MaxNegativeDelta,
                bar.MaxAskVol,
                bar.MaxAskVolPrice,
                bar.MaxBidVol,
                bar.MaxBidVolPrice,
                bar.MaxCombinedVol,
                bar.MaxCombinedVolPrice,
                0.0,
                0,
                0,
                0.0,
                0.0,
                0.0,
                0.0,
                false,
                false);
        }

        /// <summary>
        /// CHECKLIST K / M — absorption score from assembled price ladder.
        ///
        /// v1 RULE:
        ///   For each level, if max(ask,bid) / min(ask,bid) >= AbsorptionRatio,
        ///   accumulate abs(ask - bid). Normalize by LevelCount.
        ///
        /// CONFIG CONTRACT:
        ///   ratio is validated at construction time. No silent fallback here.
        ///
        /// NOTES:
        ///   - Uses same-level ask/bid only.
        ///   - This is one-bar ladder semantics, not history interpretation.
        /// </summary>
        private static double ComputeAbsorptionScore(in FootprintBar bar, double ratio)
        {
            if (bar.LevelCount <= 0)
                return 0.0;

            double total = 0.0;

            for (int i = 0; i < bar.LevelCount; i++)
            {
                double ask = bar.Ask[i];
                double bid = bar.Bid[i];

                double minV = ask < bid ? ask : bid;
                if (minV <= 0.0)
                    continue;

                double maxV = ask > bid ? ask : bid;
                double levelRatio = maxV / minV;
                if (levelRatio >= ratio)
                    total += Math.Abs(ask - bid);
            }

            return total / bar.LevelCount;
        }

        /// <summary>
        /// CHECKLIST K / N — strongest consecutive diagonal imbalance runs.
        ///
        /// CANONICAL MODEL:
        ///   Bull: bid[i] > ask[i - 1] * ratio
        ///   Bear: ask[i] > bid[i - 1] * ratio
        ///
        /// IMPORTANT:
        ///   - This is diagonal imbalance only.
        ///   - Same-level ask-vs-bid is NOT the canonical stacked model here.
        ///   - ratio is validated at construction time. No silent fallback here.
        /// </summary>
        private static void ComputeStackedDiagonalRuns(
            in FootprintBar bar,
            double          ratio,
            out int         bullRun,
            out int         bearRun,
            out double      bullLow,
            out double      bullHigh,
            out double      bearLow,
            out double      bearHigh)
        {
            bullRun  = 0;
            bearRun  = 0;
            bullLow  = 0.0;
            bullHigh = 0.0;
            bearLow  = 0.0;
            bearHigh = 0.0;

            if (bar.LevelCount < 2)
                return;

            int currentBull = 0;
            int currentBear = 0;
            double currentBullLow = 0.0;
            double currentBullHigh = 0.0;
            double currentBearLow = 0.0;
            double currentBearHigh = 0.0;

            for (int i = 1; i < bar.LevelCount; i++)
            {
                double prevAsk = bar.Ask[i - 1];
                double prevBid = bar.Bid[i - 1];
                double ask     = bar.Ask[i];
                double bid     = bar.Bid[i];
                double price   = bar.Prices[i];

                bool bull = IsBullDiagonalLevel(prevAsk, bid, ratio);
                bool bear = IsBearDiagonalLevel(prevBid, ask, ratio);

                if (bull)
                {
                    if (currentBull == 0)
                        currentBullLow = price;

                    currentBullHigh = price;
                    currentBull++;

                    if (currentBull > bullRun)
                    {
                        bullRun  = currentBull;
                        bullLow  = currentBullLow;
                        bullHigh = currentBullHigh;
                    }

                    currentBear = 0;
                    currentBearLow = 0.0;
                    currentBearHigh = 0.0;
                }
                else if (bear)
                {
                    if (currentBear == 0)
                        currentBearLow = price;

                    currentBearHigh = price;
                    currentBear++;

                    if (currentBear > bearRun)
                    {
                        bearRun  = currentBear;
                        bearLow  = currentBearLow;
                        bearHigh = currentBearHigh;
                    }

                    currentBull = 0;
                    currentBullLow = 0.0;
                    currentBullHigh = 0.0;
                }
                else
                {
                    currentBull = 0;
                    currentBear = 0;
                    currentBullLow = 0.0;
                    currentBullHigh = 0.0;
                    currentBearLow = 0.0;
                    currentBearHigh = 0.0;
                }
            }
        }

        /// <summary>
        /// CHECKLIST K / N — isolated bullish diagonal helper.
        /// </summary>
        private static bool IsBullDiagonalLevel(double prevAsk, double bid, double ratio)
        {
            return prevAsk > 0.0 && bid > prevAsk * ratio;
        }

        /// <summary>
        /// CHECKLIST K / N — isolated bearish diagonal helper.
        /// </summary>
        private static bool IsBearDiagonalLevel(double prevBid, double ask, double ratio)
        {
            return prevBid > 0.0 && ask > prevBid * ratio;
        }

        /// <summary>
        /// CHECKLIST K / O — final stack booleans and validity.
        ///
        /// IMPORTANT:
        ///   A quiet but structurally valid bar remains IsValid = true.
        ///   Zero absorption and zero stacks are not failures.
        ///   minStackedLevels is validated at construction time. No silent fallback here.
        /// </summary>
        private static FootprintResult FinalizeResult(
            in FootprintResult baseResult,
            double absorptionScore,
            int stackedBullRun,
            int stackedBearRun,
            double bullStackLow,
            double bullStackHigh,
            double bearStackLow,
            double bearStackHigh,
            int minStackedLevels)
        {
            bool hasBullStack = stackedBullRun >= minStackedLevels;
            bool hasBearStack = stackedBearRun >= minStackedLevels;

            return new FootprintResult(
                true,
                baseResult.PrimaryBarStartTime,
                baseResult.PrimaryBarEndTime,
                baseResult.FeederBarsUsed,
                baseResult.ExpectedFeederBars,
                baseResult.AssemblyMode,
                baseResult.TickSize,
                baseResult.Open,
                baseResult.High,
                baseResult.Low,
                baseResult.Close,
                baseResult.LevelCount,
                baseResult.BarDelta,
                baseResult.CumDelta,
                baseResult.DeltaSh,
                baseResult.DeltaSl,
                baseResult.DeltaPct,
                baseResult.MaxSeenDelta,
                baseResult.MinSeenDelta,
                baseResult.TotalBuyVol,
                baseResult.TotalSellVol,
                baseResult.Trades,
                baseResult.MaxPositiveDelta,
                baseResult.MaxNegativeDelta,
                baseResult.MaxAskVol,
                baseResult.MaxAskVolPrice,
                baseResult.MaxBidVol,
                baseResult.MaxBidVolPrice,
                baseResult.MaxCombinedVol,
                baseResult.MaxCombinedVolPrice,
                absorptionScore,
                stackedBullRun,
                stackedBearRun,
                bullStackLow,
                bullStackHigh,
                bearStackLow,
                bearStackHigh,
                hasBullStack,
                hasBearStack);
        }
    }

    // ========================================================================
    // EXTERNAL FOLLOW-UP CHECKLIST (INTENTIONALLY LEFT AS COMMENTS)
    // ========================================================================
    // These are architecture actions that belong outside this file. They are kept
    // here so the full checklist lives next to the code exactly as requested.
    //
    // [ ] HostStrategy should call FootprintCore.TryComputeCurrentBar(...) and publish
    //     FootprintResult fields only. HostStrategy should stop owning independent
    //     footprint math once Core is wired in.
    //
    // [ ] ImbalanceZoneRegistry should stop being the canonical source of stacked
    //     imbalance semantics. It should consume FootprintResult or share the exact
    //     same helper implementation rather than owning a second truth model.
    //
    // [ ] ConfluenceEngine Layer C should consume FootprintResult-derived fields
    //     rather than independent raw order-flow sources scattered across the host.
    //
    // [ ] FootprintEntryAdvisor / FootprintTradeAdvisor, if added later, must read
    //     FootprintResult and must NOT read VolumetricBarsType or assemble footprint
    //     facts independently.
    // ========================================================================
}
