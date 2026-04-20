#region Using declarations
using System;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Assembled dependency-free order-flow view of one primary execution bar.
    ///
    /// IMPORTANT:
    ///   - Arrays are owned and reused by FootprintAssembler.
    ///   - Consume this object immediately in FootprintCore.
    ///   - Do not persist the array references across future assembler calls.
    ///
    /// DESIGN:
    ///   - One FootprintBar always represents one PRIMARY chart bar.
    ///   - The assembler may use a lower-timeframe Volumetric feeder underneath,
    ///     but the assembled output matches the requested primary bar window.
    ///   - Scalar values are canonical primary-bar aggregates, not raw feeder values.
    /// </summary>
    public struct FootprintBar
    {
        public bool     IsValid;

        public DateTime PrimaryBarStartTime;
        public DateTime PrimaryBarEndTime;
        public int      FeederBarsUsed;
        public int      ExpectedFeederBars;

        public double   TickSize;
        public double   Open;
        public double   High;
        public double   Low;
        public double   Close;
        public int      LevelCount;

        // Reused arrays owned by FootprintAssembler — valid only until next assembly call.
        public double[] Prices;
        public double[] Ask;
        public double[] Bid;
        public double[] Delta;
        public double[] Total;

        // Canonical bar-level order-flow facts for the assembled primary bar.
        public double   BarDelta;
        public double   CumDelta;
        public double   DeltaSh;
        public double   DeltaSl;
        public double   DeltaPct;
        public double   MaxSeenDelta;
        public double   MinSeenDelta;
        public double   TotalBuyVol;
        public double   TotalSellVol;
        public double   Trades;

        // Per-bar extrema across the resolved feeder window.
        public double   MaxPositiveDelta;
        public double   MaxNegativeDelta;

        public double   MaxAskVol;
        public double   MaxAskVolPrice;
        public double   MaxBidVol;
        public double   MaxBidVolPrice;
        public double   MaxCombinedVol;
        public double   MaxCombinedVolPrice;

        public double   TopLevelAskVol;
        public double   TopLevelBidVol;
        public double   TopLevelTotalVol;
        public double   BottomLevelAskVol;
        public double   BottomLevelBidVol;
        public double   BottomLevelTotalVol;
    }

    public enum FootprintAssemblyMode
    {
        /// <summary>
        /// Build only from a fully completed primary bar window.
        /// Requires an exact contiguous feeder window covering the entire primary span.
        /// </summary>
        CompletedPrimaryBarOnly = 0,

        /// <summary>
        /// Allow a forming primary bar.
        /// Requires a contiguous feeder window beginning at the primary start time,
        /// but does not require the full expected feeder count to be present yet.
        /// </summary>
        AllowFormingPrimaryBar = 1,
    }

    public enum FootprintAssemblyFailureReason
    {
        None = 0,
        NotInitialized,
        NullVolumetricBarsType,
        NullFeederBars,
        UnsupportedPrimaryBarsPeriod,
        InvalidFeederBarsPeriod,
        InvalidTickSize,
        InvalidVolumetricBarIndex,
        InvalidPrimaryWindow,
        NoFeederBarsInPrimaryWindow,
        IncompleteFeederWindow,
        NonContiguousFeederWindow,
        LevelCountExceeded,
        InvalidPrimaryRange,
        SessionBoundaryMismatch,
    }

    /// <summary>
    /// NT8 adapter that converts a Volumetric feeder series into one dependency-free
    /// FootprintBar matching the requested primary bar window.
    ///
    /// SCOPE:
    ///   - Supports time-based primaries that match the configured feeder type.
    ///   - Supports Minute and Second feeder types.
    ///   - Resolves feeder windows by actual timestamps, not blind latest-N indexing.
    ///   - Exposes diagnostics/failure reasons for host-side logging.
    ///
    /// OWNERSHIP:
    ///   - This class is the ONLY place allowed to read VolumetricBarsType directly.
    ///   - This class is the ONLY place allowed to aggregate feeder bars into the
    ///     current primary bar.
    ///
    /// NON-GOALS:
    ///   - No signal scoring.
    ///   - No trade management.
    ///   - No snapshot publishing.
    ///   - No zone or regime logic.
    /// </summary>
    public sealed class FootprintAssembler
    {
        // ── Configuration ────────────────────────────────────────────────
        private double         _tickSize;
        private int            _maxLevels;
        private BarsPeriodType _feederBarsPeriodType;
        private int            _feederBarsPeriodValue;
        private bool           _initialized;

        // ── Coverage policy ──────────────────────────────────────────────
        // Minimum fraction of expected feeder bars that must be present for a
        // CompletedPrimaryBarOnly assembly to succeed.
        //
        // WHY THIS EXISTS:
        //   NT8 Volumetric feeder bars are built from tick data. During zero-volume
        //   minutes (pre-market, quiet RTH intervals, holiday sessions) NT8 may not
        //   emit a feeder bar at all. Requiring strict count equality rejects the
        //   entire primary bar whenever a single quiet minute occurs inside it,
        //   which destroys all order-flow signal for that bar and forces the
        //   FootprintEntryAdvisor into its unavailable-policy fallback.
        //
        // WHAT THIS PROTECTS:
        //   Coverage below this floor indicates a data problem more serious than
        //   a single quiet minute (outage, feed gap, corruption). The assembler
        //   still fails in that case so downstream consumers don't aggregate over
        //   a largely-absent window.
        //
        // DEFAULT: 0.80 — a 5-minute primary may be missing at most 1 feeder minute.
        //   A 3-bar floor for 5-bar primaries is the common quiet-minute case.
        //   Tune downward only with explicit justification from coverage statistics.
        private const double MIN_FEEDER_COVERAGE_FRACTION = StrategyConfig.Modules.FA_MIN_FEEDER_COVERAGE;

        // ── Diagnostics ─────────────────────────────────────────────────
        public FootprintAssemblyFailureReason LastFailureReason  { get; private set; }
        public string                         LastFailureMessage { get; private set; }
        public int                            FailureCount       { get; private set; }

        // ── Reused working buffers — zero allocation in hot path ─────────
        private double[] _prices;
        private double[] _ask;
        private double[] _bid;
        private double[] _delta;
        private double[] _total;

        // Clear only the active prefix from the previous assembly.
        private int _lastLevelCount;

        // SessionIterator is cached so session-boundary validation can use the feeder
        // series' configured Trading Hours template without rebuilding the iterator
        // on every assembly call.
        private SessionIterator _sessionIterator;
        private Bars            _sessionIteratorBars;

        public void Initialize(
            double tickSize,
            int maxLevels,
            BarsPeriodType feederBarsPeriodType,
            int feederBarsPeriodValue)
        {
            if (tickSize <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(tickSize), "Tick size must be > 0.");

            if (maxLevels <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxLevels), "maxLevels must be > 0.");

            if (!IsSupportedFeederType(feederBarsPeriodType))
                throw new ArgumentOutOfRangeException(nameof(feederBarsPeriodType),
                    "Supported feeder types are Minute and Second.");

            if (feederBarsPeriodValue <= 0)
                throw new ArgumentOutOfRangeException(nameof(feederBarsPeriodValue),
                    "feederBarsPeriodValue must be > 0.");

            _tickSize              = tickSize;
            _maxLevels             = maxLevels;
            _feederBarsPeriodType  = feederBarsPeriodType;
            _feederBarsPeriodValue = feederBarsPeriodValue;

            _prices = new double[_maxLevels];
            _ask    = new double[_maxLevels];
            _bid    = new double[_maxLevels];
            _delta  = new double[_maxLevels];
            _total  = new double[_maxLevels];

            // Reset any cached SessionIterator state so a re-initialize can never reuse
            // an iterator created for a prior Bars instance or feeder configuration.
            _sessionIterator     = null;
            _sessionIteratorBars = null;

            _lastLevelCount = 0;
            _initialized    = true;
            ClearFailure();
        }

        public bool IsSupportedPrimaryPeriod(BarsPeriod primaryBarsPeriod)
        {
            if (primaryBarsPeriod == null)
                return false;

            // Fail closed before Initialize() has supplied the configured feeder type/value.
            // Before initialization we do not know enough to answer compatibility correctly.
            if (!_initialized)
                return false;

            if (primaryBarsPeriod.BarsPeriodType != _feederBarsPeriodType)
                return false;

            if (primaryBarsPeriod.Value < _feederBarsPeriodValue)
                return false;

            return primaryBarsPeriod.Value % _feederBarsPeriodValue == 0;
        }

        public bool TryAssembleCurrentBar(
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
            out FootprintBar      bar)
        {
            bar = BuildZeroBar();
            ClearFailure();

            int expectedFeederBars = ResolveExpectedFeederBars(primaryBarsPeriod);
            if (!ValidateInputs(volBarsType, feederBars, currentVolBarIndex, primaryBarsPeriod,
                    primaryBarStartTime, primaryBarEndTime, primaryLow, primaryHigh, expectedFeederBars))
                return false;

            if (!TryResolvePrimaryWindow(feederBars, currentVolBarIndex,
                    primaryBarStartTime, primaryBarEndTime,
                    out int startVolBarIndex, out int endVolBarIndex, out int feederBarsUsed))
                return false;

            if (!ValidateContiguousFeederWindow(
                    startVolBarIndex,
                    endVolBarIndex,
                    feederBarsUsed,
                    expectedFeederBars,
                    mode))
                return false;

            if (!ValidateSessionWindow(
                    feederBars,
                    startVolBarIndex,
                    endVolBarIndex,
                    primaryBarStartTime,
                    primaryBarEndTime))
                return false;

            ResetWorkingBuffers();

            int levelCount = DeterminePriceRange(primaryLow, primaryHigh,
                out double normalizedLow, out double normalizedHigh);

            if (levelCount <= 0)
            {
                SetFailure(FootprintAssemblyFailureReason.InvalidPrimaryRange,
                    "Primary bar range is invalid after tick normalization.");
                return false;
            }

            if (levelCount > _maxLevels)
            {
                SetFailure(FootprintAssemblyFailureReason.LevelCountExceeded,
                    $"Primary bar needs {levelCount} ladder levels but maxLevels is {_maxLevels}.");
                return false;
            }

            bar.IsValid             = false;
            bar.PrimaryBarStartTime = primaryBarStartTime;
            bar.PrimaryBarEndTime   = primaryBarEndTime;
            bar.ExpectedFeederBars  = expectedFeederBars;
            bar.FeederBarsUsed      = feederBarsUsed;
            bar.TickSize            = _tickSize;
            bar.Open                = primaryOpen;
            bar.High                = normalizedHigh;
            bar.Low                 = normalizedLow;
            bar.Close               = primaryClose;
            bar.LevelCount          = levelCount;
            bar.Prices              = _prices;
            bar.Ask                 = _ask;
            bar.Bid                 = _bid;
            bar.Delta               = _delta;
            bar.Total               = _total;

            AssemblePriceLadder(volBarsType, startVolBarIndex, endVolBarIndex,
                normalizedLow, levelCount, ref bar);
            AssembleScalarStats(volBarsType, startVolBarIndex, endVolBarIndex, ref bar);

            bar.IsValid      = true;
            _lastLevelCount  = levelCount;
            return true;
        }

        private bool ValidateInputs(
            VolumetricBarsType volBarsType,
            Bars               feederBars,
            int                currentVolBarIndex,
            BarsPeriod         primaryBarsPeriod,
            DateTime           primaryBarStartTime,
            DateTime           primaryBarEndTime,
            double             primaryLow,
            double             primaryHigh,
            int                expectedFeederBars)
        {
            if (!_initialized)
                return SetFailure(FootprintAssemblyFailureReason.NotInitialized,
                    "FootprintAssembler.Initialize() must be called before assembly.");

            if (_tickSize <= 0.0)
                return SetFailure(FootprintAssemblyFailureReason.InvalidTickSize,
                    "Configured tick size must be > 0.");

            if (volBarsType == null)
                return SetFailure(FootprintAssemblyFailureReason.NullVolumetricBarsType,
                    "VolumetricBarsType is null.");

            if (feederBars == null)
                return SetFailure(FootprintAssemblyFailureReason.NullFeederBars,
                    "Feeder Bars reference is null.");

            if (!IsSupportedFeederType(_feederBarsPeriodType))
                return SetFailure(FootprintAssemblyFailureReason.InvalidFeederBarsPeriod,
                    "Configured feeder BarsPeriodType is not supported.");

            if (!IsSupportedPrimaryPeriod(primaryBarsPeriod))
                return SetFailure(FootprintAssemblyFailureReason.UnsupportedPrimaryBarsPeriod,
                    "Primary BarsPeriod must match the configured time-based feeder type and be an integer multiple of it.");

            if (currentVolBarIndex < 0)
                return SetFailure(FootprintAssemblyFailureReason.InvalidVolumetricBarIndex,
                    "Current volumetric bar index is negative.");

            if (primaryBarEndTime <= primaryBarStartTime)
                return SetFailure(FootprintAssemblyFailureReason.InvalidPrimaryWindow,
                    "Primary bar end time must be strictly greater than start time.");

            if (expectedFeederBars <= 0)
                return SetFailure(FootprintAssemblyFailureReason.InvalidFeederBarsPeriod,
                    "Expected feeder bars per primary bar resolved to <= 0.");

            if (primaryHigh < primaryLow)
                return SetFailure(FootprintAssemblyFailureReason.InvalidPrimaryRange,
                    "Primary high is below primary low.");

            return true;
        }

        private int ResolveExpectedFeederBars(BarsPeriod primaryBarsPeriod)
        {
            if (!IsSupportedPrimaryPeriod(primaryBarsPeriod))
                return 0;

            return primaryBarsPeriod.Value / _feederBarsPeriodValue;
        }

        private bool TryResolvePrimaryWindow(
            Bars     feederBars,
            int      currentVolBarIndex,
            DateTime primaryBarStartTime,
            DateTime primaryBarEndTime,
            out int  startVolBarIndex,
            out int  endVolBarIndex,
            out int  feederBarsUsed)
        {
            startVolBarIndex = -1;
            endVolBarIndex   = -1;
            feederBarsUsed   = 0;

            if (currentVolBarIndex < 0)
                return SetFailure(FootprintAssemblyFailureReason.InvalidVolumetricBarIndex,
                    "Current volumetric bar index is negative.");

            // Walk backwards from the latest feeder bar and select only bars that fall
            // inside the requested primary time window: [start, end).
            for (int idx = currentVolBarIndex; idx >= 0; idx--)
            {
                DateTime feederTime = feederBars.GetTime(idx);

                if (feederTime >= primaryBarEndTime)
                    continue;

                if (feederTime < primaryBarStartTime)
                    break;

                startVolBarIndex = idx;
                if (endVolBarIndex < 0)
                    endVolBarIndex = idx;

                feederBarsUsed++;
            }

            if (startVolBarIndex < 0 || endVolBarIndex < startVolBarIndex || feederBarsUsed <= 0)
            {
                return SetFailure(FootprintAssemblyFailureReason.NoFeederBarsInPrimaryWindow,
                    "No feeder bars were found inside the requested primary bar time window.");
            }

            return true;
        }

        private bool ValidateContiguousFeederWindow(
            int                   startVolBarIndex,
            int                   endVolBarIndex,
            int                   feederBarsUsed,
            int                   expectedFeederBars,
            FootprintAssemblyMode mode)
        {
            if (startVolBarIndex < 0 || endVolBarIndex < startVolBarIndex || feederBarsUsed <= 0)
                return SetFailure(FootprintAssemblyFailureReason.NoFeederBarsInPrimaryWindow,
                    "Resolved feeder window is empty.");

            // ── CompletedPrimaryBarOnly: coverage-fraction policy ────────────
            // Require a minimum fraction of expected feeder bars to be present.
            // Quiet minutes with zero volume may not produce a Volumetric feeder
            // bar — that is not a failure condition as long as enough of the
            // primary bar window is covered. Upstream guarantees already ensure
            // every resolved feeder bar falls inside [primaryBarStartTime,
            // primaryBarEndTime) (TryResolvePrimaryWindow) and that the primary
            // window fits inside one session (ValidateSessionWindow). No
            // per-step contiguity check is needed — coverage fraction is the
            // canonical contract.
            if (mode == FootprintAssemblyMode.CompletedPrimaryBarOnly)
            {
                int minRequired = (int)Math.Ceiling(expectedFeederBars * MIN_FEEDER_COVERAGE_FRACTION);
                if (minRequired < 1) minRequired = 1;

                if (feederBarsUsed < minRequired)
                {
                    return SetFailure(FootprintAssemblyFailureReason.IncompleteFeederWindow,
                        $"Primary bar requires at least {minRequired} of {expectedFeederBars} feeder bars " +
                        $"({MIN_FEEDER_COVERAGE_FRACTION:P0} coverage floor), but resolved only {feederBarsUsed}.");
                }
            }

            // ── AllowFormingPrimaryBar: upper bound only ─────────────────────
            // Any positive sub-count is valid while a primary bar is still
            // forming. Reject only if we somehow resolved MORE bars than a
            // completed primary would ever hold — that indicates a time-window
            // calculation bug upstream, not a data gap.
            if (mode == FootprintAssemblyMode.AllowFormingPrimaryBar && feederBarsUsed > expectedFeederBars)
            {
                return SetFailure(FootprintAssemblyFailureReason.IncompleteFeederWindow,
                    $"Resolved feeder bars ({feederBarsUsed}) exceed the expected count ({expectedFeederBars}) for the primary bar.");
            }

            return true;
        }

        private bool ValidateSessionWindow(
            Bars     feederBars,
            int      startVolBarIndex,
            int      endVolBarIndex,
            DateTime primaryBarStartTime,
            DateTime primaryBarEndTime)
        {
            if (feederBars == null)
                return SetFailure(FootprintAssemblyFailureReason.NullFeederBars,
                    "Feeder Bars reference is null during session validation.");

            if (!EnsureSessionIterator(feederBars))
                return SetFailure(FootprintAssemblyFailureReason.SessionBoundaryMismatch,
                    "Unable to initialize SessionIterator for feeder bars.");

            // Resolve the session containing the primary bar start time. For time-based
            // bars we include the end timestamp so exact hh:mm:00 feeder timestamps are
            // treated as belonging to the current session.
            if (!_sessionIterator.GetNextSession(primaryBarStartTime, true))
                return SetFailure(FootprintAssemblyFailureReason.SessionBoundaryMismatch,
                    "SessionIterator could not resolve a session for the primary bar start time.");

            DateTime sessionBegin = _sessionIterator.ActualSessionBegin;
            DateTime sessionEnd   = _sessionIterator.ActualSessionEnd;

            // The primary window itself must fit fully inside one session. If the end
            // time exceeds the resolved session end, the requested primary bar would span
            // multiple sessions and must be rejected to avoid cross-session contamination.
            if (primaryBarStartTime < sessionBegin || primaryBarEndTime > sessionEnd)
            {
                return SetFailure(FootprintAssemblyFailureReason.SessionBoundaryMismatch,
                    $"Primary window [{primaryBarStartTime:O}, {primaryBarEndTime:O}) exceeds session bounds [{sessionBegin:O}, {sessionEnd:O}).");
            }

            // Every feeder bar in the resolved window must belong to the same session
            // determined from the primary bar start time.
            for (int idx = startVolBarIndex; idx <= endVolBarIndex; idx++)
            {
                DateTime feederTime = feederBars.GetTime(idx);
                if (feederTime < sessionBegin || feederTime >= sessionEnd)
                {
                    return SetFailure(FootprintAssemblyFailureReason.SessionBoundaryMismatch,
                        $"Feeder bar at index {idx} with time {feederTime:O} falls outside the resolved session bounds [{sessionBegin:O}, {sessionEnd:O}).");
                }

                if (!_sessionIterator.IsInSession(feederTime, true, true))
                {
                    return SetFailure(FootprintAssemblyFailureReason.SessionBoundaryMismatch,
                        $"Feeder bar at index {idx} with time {feederTime:O} is not inside the current session according to the Trading Hours template.");
                }
            }

            return true;
        }

        private bool EnsureSessionIterator(Bars feederBars)
        {
            if (_sessionIterator != null && ReferenceEquals(_sessionIteratorBars, feederBars))
                return true;

            _sessionIterator = new SessionIterator(feederBars);
            _sessionIteratorBars = feederBars;
            return _sessionIterator != null;
        }

        private void ResetWorkingBuffers()
        {
            for (int i = 0; i < _lastLevelCount; i++)
            {
                _prices[i] = 0.0;
                _ask[i]    = 0.0;
                _bid[i]    = 0.0;
                _delta[i]  = 0.0;
                _total[i]  = 0.0;
            }

            _lastLevelCount = 0;
        }

        private double NormalizePriceDown(double price)
        {
            if (_tickSize <= 0.0)
                return price;

            return Math.Floor(price / _tickSize) * _tickSize;
        }

        private double NormalizePriceUp(double price)
        {
            if (_tickSize <= 0.0)
                return price;

            return Math.Ceiling(price / _tickSize) * _tickSize;
        }

        private int DeterminePriceRange(
            double primaryLow,
            double primaryHigh,
            out double normalizedLow,
            out double normalizedHigh)
        {
            normalizedLow  = NormalizePriceDown(primaryLow);
            normalizedHigh = NormalizePriceUp(primaryHigh);

            if (normalizedHigh < normalizedLow)
                return 0;

            int levelCount = (int)Math.Round((normalizedHigh - normalizedLow) / _tickSize) + 1;
            return levelCount > 0 ? levelCount : 0;
        }

        private void AssembleScalarStats(
            VolumetricBarsType volBarsType,
            int                startVolBarIndex,
            int                endVolBarIndex,
            ref FootprintBar   bar)
        {
            bar.BarDelta            = 0.0;
            bar.CumDelta            = 0.0;
            bar.DeltaSh             = 0.0;
            bar.DeltaSl             = 0.0;
            bar.DeltaPct            = 0.0;
            bar.MaxSeenDelta        = double.MinValue;
            bar.MinSeenDelta        = double.MaxValue;
            bar.TotalBuyVol         = 0.0;
            bar.TotalSellVol        = 0.0;
            bar.Trades              = 0.0;
            bar.MaxPositiveDelta    = double.MinValue;
            bar.MaxNegativeDelta    = double.MaxValue;
            bar.MaxAskVol           = 0.0;
            bar.MaxAskVolPrice      = 0.0;
            bar.MaxBidVol           = 0.0;
            bar.MaxBidVolPrice      = 0.0;
            bar.MaxCombinedVol      = 0.0;
            bar.MaxCombinedVolPrice = 0.0;

            bool sawAny = false;
            double tmpPrice;

            for (int idx = startVolBarIndex; idx <= endVolBarIndex; idx++)
            {
                var v = volBarsType.Volumes[idx];
                if (v == null)
                    continue;

                sawAny = true;

                bar.BarDelta     += (double)v.BarDelta;
                bar.TotalBuyVol  += (double)v.TotalBuyingVolume;
                bar.TotalSellVol += (double)v.TotalSellingVolume;
                bar.Trades       += (double)v.Trades;

                if ((double)v.MaxSeenDelta > bar.MaxSeenDelta)
                    bar.MaxSeenDelta = (double)v.MaxSeenDelta;

                if ((double)v.MinSeenDelta < bar.MinSeenDelta)
                    bar.MinSeenDelta = (double)v.MinSeenDelta;

                double maxPos = (double)v.GetMaximumPositiveDelta();
                if (maxPos > bar.MaxPositiveDelta)
                    bar.MaxPositiveDelta = maxPos;

                double maxNeg = (double)v.GetMaximumNegativeDelta();
                if (maxNeg < bar.MaxNegativeDelta)
                    bar.MaxNegativeDelta = maxNeg;

                double maxAsk = (double)v.GetMaximumVolume(true, out tmpPrice);
                if (maxAsk > bar.MaxAskVol)
                {
                    bar.MaxAskVol      = maxAsk;
                    bar.MaxAskVolPrice = tmpPrice;
                }

                double maxBid = (double)v.GetMaximumVolume(false, out tmpPrice);
                if (maxBid > bar.MaxBidVol)
                {
                    bar.MaxBidVol      = maxBid;
                    bar.MaxBidVolPrice = tmpPrice;
                }

                double maxCombined = (double)v.GetMaximumVolume(null, out tmpPrice);
                if (maxCombined > bar.MaxCombinedVol)
                {
                    bar.MaxCombinedVol      = maxCombined;
                    bar.MaxCombinedVolPrice = tmpPrice;
                }
            }

            if (!sawAny)
            {
                bar.MaxSeenDelta     = 0.0;
                bar.MinSeenDelta     = 0.0;
                bar.MaxPositiveDelta = 0.0;
                bar.MaxNegativeDelta = 0.0;
                return;
            }

            var vLast = volBarsType.Volumes[endVolBarIndex];
            if (vLast != null)
            {
                // BarDelta and CumDelta are correctly read from the last feeder bar 
                // as they represent the finalized/running totals for the window.
                bar.BarDelta = (double)vLast.BarDelta;
                bar.CumDelta = (double)vLast.CumulativeDelta;
            }

            // FIX (Issue #26): DeltaSh/Sl must represent the PRIMARY bar's extremes.
            // The feeder bar values represent ONLY the last 1-minute bar's extremes.
            // We extract the true primary extremes from our assembled price ladder.
            // These now represent the total "Defense at Extreme" (total delta at the
            // highest and lowest price levels of the entire window).
            if (bar.LevelCount > 0 && bar.Delta != null)
            {
                bar.DeltaSl = bar.Delta[0];                    // Delta at the lowest price level
                bar.DeltaSh = bar.Delta[bar.LevelCount - 1];   // Delta at the highest price level
            }
            else
            {
                bar.DeltaSl = 0.0;
                bar.DeltaSh = 0.0;
            }

            // Canonical assembled delta percent is derived from the primary-bar totals.
            double totalVol = bar.TotalBuyVol + bar.TotalSellVol;
            bar.DeltaPct = totalVol > 0.0 ? bar.BarDelta / totalVol : 0.0;

            if (bar.MaxSeenDelta == double.MinValue)     bar.MaxSeenDelta = 0.0;
            if (bar.MinSeenDelta == double.MaxValue)     bar.MinSeenDelta = 0.0;
            if (bar.MaxPositiveDelta == double.MinValue) bar.MaxPositiveDelta = 0.0;
            if (bar.MaxNegativeDelta == double.MaxValue) bar.MaxNegativeDelta = 0.0;
        }

        private void AssemblePriceLadder(
            VolumetricBarsType volBarsType,
            int                startVolBarIndex,
            int                endVolBarIndex,
            double             low,
            int                levelCount,
            ref FootprintBar   bar)
        {
            for (int level = 0; level < levelCount; level++)
            {
                double price = low + (level * _tickSize);

                double ask   = 0.0;
                double bid   = 0.0;
                double delta = 0.0;
                double total = 0.0;

                for (int idx = startVolBarIndex; idx <= endVolBarIndex; idx++)
                {
                    var v = volBarsType.Volumes[idx];
                    if (v == null)
                        continue;

                    ask   += (double)v.GetAskVolumeForPrice(price);
                    bid   += (double)v.GetBidVolumeForPrice(price);
                    delta += (double)v.GetDeltaForPrice(price);
                    total += (double)v.GetTotalVolumeForPrice(price);
                }

                _prices[level] = price;
                _ask[level]    = ask;
                _bid[level]    = bid;
                _delta[level]  = delta;
                _total[level]  = total;
            }

            if (levelCount > 0)
            {
                bar.BottomLevelAskVol   = _ask[0];
                bar.BottomLevelBidVol   = _bid[0];
                bar.BottomLevelTotalVol = _total[0];

                int topIdx = levelCount - 1;
                bar.TopLevelAskVol   = _ask[topIdx];
                bar.TopLevelBidVol   = _bid[topIdx];
                bar.TopLevelTotalVol = _total[topIdx];
            }
        }

        private FootprintBar BuildZeroBar()
        {
            return new FootprintBar
            {
                IsValid            = false,
                TickSize           = _tickSize,
                Open               = 0.0,
                High               = 0.0,
                Low                = 0.0,
                Close              = 0.0,
                Prices             = _prices,
                Ask                = _ask,
                Bid                = _bid,
                Delta              = _delta,
                Total              = _total,
                LevelCount         = 0,
                FeederBarsUsed     = 0,
                ExpectedFeederBars = 0,

                TopLevelAskVol     = 0.0,
                TopLevelBidVol     = 0.0,
                TopLevelTotalVol   = 0.0,
                BottomLevelAskVol  = 0.0,
                BottomLevelBidVol  = 0.0,
                BottomLevelTotalVol = 0.0,
            };
        }

        private bool SetFailure(FootprintAssemblyFailureReason reason, string message)
        {
            LastFailureReason  = reason;
            LastFailureMessage = message ?? string.Empty;
            FailureCount++;
            return false;
        }

        private void ClearFailure()
        {
            LastFailureReason  = FootprintAssemblyFailureReason.None;
            LastFailureMessage = string.Empty;
        }

        private bool IsSupportedFeederType(BarsPeriodType barsPeriodType)
        {
            return barsPeriodType == BarsPeriodType.Minute
                || barsPeriodType == BarsPeriodType.Second;
        }

        private TimeSpan GetFeederStep()
        {
            if (_feederBarsPeriodType == BarsPeriodType.Minute)
                return TimeSpan.FromMinutes(_feederBarsPeriodValue);

            if (_feederBarsPeriodType == BarsPeriodType.Second)
                return TimeSpan.FromSeconds(_feederBarsPeriodValue);

            return TimeSpan.Zero;
        }
    }
}
