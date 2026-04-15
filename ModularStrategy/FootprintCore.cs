#region Using declarations
using System;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Configuration for the custom semantics FootprintCore adds on top of the
    /// assembler's canonical primary-bar scalar aggregation.
    /// </summary>
    public readonly struct FootprintCoreConfig
    {
        public double AbsorptionRatio { get; }
        public double DiagonalImbalanceRatio { get; }
        public int MinStackedLevels { get; }
        public int LevelHistoryCapacityBars { get; }
        public int LevelHistoryMaxLevels { get; }

        public FootprintCoreConfig(
            double absorptionRatio, 
            double diagonalImbalanceRatio, 
            int minStackedLevels,
            int levelHistoryCapacityBars,
            int levelHistoryMaxLevels)
        {
            if (absorptionRatio <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(absorptionRatio), "absorptionRatio must be > 0.");
            if (diagonalImbalanceRatio <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(diagonalImbalanceRatio), "diagonalImbalanceRatio must be > 0.");
            if (minStackedLevels <= 0)
                throw new ArgumentOutOfRangeException(nameof(minStackedLevels), "minStackedLevels must be > 0.");
            if (levelHistoryCapacityBars <= 0)
                throw new ArgumentOutOfRangeException(nameof(levelHistoryCapacityBars), "levelHistoryCapacityBars must be > 0.");
            if (levelHistoryMaxLevels <= 0)
                throw new ArgumentOutOfRangeException(nameof(levelHistoryMaxLevels), "levelHistoryMaxLevels must be > 0.");

            AbsorptionRatio          = absorptionRatio;
            DiagonalImbalanceRatio   = diagonalImbalanceRatio;
            MinStackedLevels         = minStackedLevels;
            LevelHistoryCapacityBars = levelHistoryCapacityBars;
            LevelHistoryMaxLevels    = levelHistoryMaxLevels;
        }

        public static FootprintCoreConfig Default => new FootprintCoreConfig(2.0, 3.0, 3, 5, 128);
    }

    /// <summary>
    /// Immutable published order-flow result.
    /// Contains assembly metadata, canonical scalar facts, and Core-derived metrics.
    /// </summary>
    public readonly struct FootprintResult
    {
        public bool IsValid { get; }

        // Assembly Metadata
        public DateTime PrimaryBarStartTime { get; }
        public DateTime PrimaryBarEndTime { get; }
        public int FeederBarsUsed { get; }
        public int ExpectedFeederBars { get; }
        public FootprintAssemblyMode AssemblyMode { get; }

        // Bar Geometry
        public double TickSize { get; }
        public double Open { get; }
        public double High { get; }
        public double Low { get; }
        public double Close { get; }
        public int LevelCount { get; }

        // Canonical Scalar Facts
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

        // Extreme Level Volumes
        public double TopLevelAskVol { get; }
        public double TopLevelBidVol { get; }
        public double TopLevelTotalVol { get; }
        public double BottomLevelAskVol { get; }
        public double BottomLevelBidVol { get; }
        public double BottomLevelTotalVol { get; }

        // Core Detectors (Phase 2.2 / 2.3)
        public bool UnfinishedTop { get; }
        public bool UnfinishedBottom { get; }
        public bool BullExhaustion { get; }
        public bool BearExhaustion { get; }

        // Core-Owned Derived Metrics
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
            double topLevelAskVol,
            double topLevelBidVol,
            double topLevelTotalVol,
            double bottomLevelAskVol,
            double bottomLevelBidVol,
            double bottomLevelTotalVol,
            bool unfinishedTop,
            bool unfinishedBottom,
            bool bullExhaustion,
            bool bearExhaustion,
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
            TopLevelAskVol = topLevelAskVol;
            TopLevelBidVol = topLevelBidVol;
            TopLevelTotalVol = topLevelTotalVol;
            BottomLevelAskVol = bottomLevelAskVol;
            BottomLevelBidVol = bottomLevelBidVol;
            BottomLevelTotalVol = bottomLevelTotalVol;
            UnfinishedTop = unfinishedTop;
            UnfinishedBottom = unfinishedBottom;
            BullExhaustion = bullExhaustion;
            BearExhaustion = bearExhaustion;
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
            false,                          // isValid
            default(DateTime),               // primaryBarStartTime
            default(DateTime),               // primaryBarEndTime
            0,                              // feederBarsUsed
            0,                              // expectedFeederBars
            FootprintAssemblyMode.CompletedPrimaryBarOnly, // assemblyMode
            0.0,                            // tickSize
            0.0,                            // open
            0.0,                            // high
            0.0,                            // low
            0.0,                            // close
            0,                              // levelCount
            0.0,                            // barDelta
            0.0,                            // cumDelta
            0.0,                            // deltaSh
            0.0,                            // deltaSl
            0.0,                            // deltaPct
            0.0,                            // maxSeenDelta
            0.0,                            // minSeenDelta
            0.0,                            // totalBuyVol
            0.0,                            // totalSellVol
            0.0,                            // trades
            0.0,                            // maxPositiveDelta
            0.0,                            // maxNegativeDelta
            0.0,                            // maxAskVol
            0.0,                            // maxAskVolPrice
            0.0,                            // maxBidVol
            0.0,                            // maxBidVolPrice
            0.0,                            // maxCombinedVol
            0.0,                            // maxCombinedVolPrice
            0.0,                            // topLevelAskVol
            0.0,                            // topLevelBidVol
            0.0,                            // topLevelTotalVol
            0.0,                            // bottomLevelAskVol
            0.0,                            // bottomLevelBidVol
            0.0,                            // bottomLevelTotalVol
            false,                          // unfinishedTop
            false,                          // unfinishedBottom
            false,                          // bullExhaustion
            false,                          // bearExhaustion
            0.0,                            // absorptionScore
            0,                              // stackedBullRun
            0,                              // stackedBearRun
            0.0,                            // bullStackLow
            0.0,                            // bullStackHigh
            0.0,                            // bearStackLow
            0.0,                            // bearStackHigh
            false,                          // hasBullStack
            false                           // hasBearStack
        );
    }

    /// <summary>
    /// FootprintCore — single public order-flow boundary.
    /// Wraps exactly one FootprintAssembler and computes additional ladder-derived metrics.
    /// </summary>
    public sealed class FootprintCore
    {
        private readonly FootprintAssembler   _assembler;
        private readonly FootprintCoreConfig  _config;
        private readonly LevelHistoryTracker _levelHistory;
        private bool                          _initialized;

        public FootprintCore(FootprintAssembler assembler, FootprintCoreConfig config)
        {
            if (assembler == null) throw new ArgumentNullException(nameof(assembler));
            
            _assembler    = assembler;
            _config       = config;
            _levelHistory = new LevelHistoryTracker(config.LevelHistoryCapacityBars, config.LevelHistoryMaxLevels);
            _initialized  = false;
        }

        public bool IsReady => _initialized;

        // Diagnostic forwards
        public FootprintAssemblyFailureReason LastFailureReason  => _assembler.LastFailureReason;
        public string                         LastFailureMessage => _assembler.LastFailureMessage;
        public int                            FailureCount       => _assembler.FailureCount;

        public void Initialize(double tickSize, int maxLevels, BarsPeriodType feederType, int feederValue)
        {
            _assembler.Initialize(tickSize, maxLevels, feederType, feederValue);
            _initialized = true;
        }

        public bool IsSupportedPrimaryPeriod(BarsPeriod primaryBarsPeriod)
        {
            if (!_initialized) return false;
            return _assembler.IsSupportedPrimaryPeriod(primaryBarsPeriod);
        }

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
            if (!_initialized) return false;

            FootprintBar bar;
            if (!_assembler.TryAssembleCurrentBar(
                    volBarsType, feederBars, currentVolBarIndex, primaryBarsPeriod,
                    primaryBarStartTime, primaryBarEndTime, primaryOpen, primaryHigh,
                    primaryLow, primaryClose, mode, out bar))
            {
                return false;
            }

            result = ComputeFromBar(in bar, mode, in _config);

            if (result.IsValid)
            {
                _levelHistory.BeginBar(result.PrimaryBarEndTime, result.TickSize);
                for (int i = 0; i < bar.LevelCount; i++)
                {
                    _levelHistory.AppendLevel(bar.Prices[i], bar.Ask[i], bar.Bid[i]);
                }
                _levelHistory.EndBar();
            }

            return result.IsValid;
        }

        private static FootprintResult ComputeFromBar(
            in FootprintBar        bar,
            FootprintAssemblyMode  mode,
            in FootprintCoreConfig config)
        {
            if (!ValidateBar(in bar)) return BuildZeroResult(mode);

            FootprintResult baseResult = CopyScalarFacts(in bar, mode);
            double absorptionScore = ComputeAbsorptionScore(in bar, config.AbsorptionRatio);

            int stackedBullRun, stackedBearRun;
            double bullLow, bullHigh, bearLow, bearHigh;

            ComputeStackedDiagonalRuns(
                in bar, config.DiagonalImbalanceRatio,
                out stackedBullRun, out stackedBearRun,
                out bullLow, out bullHigh, out bearLow, out bearHigh);

            return FinalizeResult(
                in baseResult, absorptionScore, stackedBullRun, stackedBearRun,
                bullLow, bullHigh, bearLow, bearHigh, config.MinStackedLevels);
        }

        private static bool ValidateBar(in FootprintBar bar)
        {
            if (!bar.IsValid || bar.TickSize <= 0.0 || bar.LevelCount <= 0 || bar.Low > bar.High) return false;
            if (bar.Prices == null || bar.Ask == null || bar.Bid == null) return false;
            if (bar.Prices.Length < bar.LevelCount || bar.Ask.Length < bar.LevelCount || bar.Bid.Length < bar.LevelCount) return false;
            return true;
        }

        private static FootprintResult BuildZeroResult(FootprintAssemblyMode mode)
        {
            return new FootprintResult(
                false,                          // isValid
                default(DateTime),               // primaryBarStartTime
                default(DateTime),               // primaryBarEndTime
                0,                              // feederBarsUsed
                0,                              // expectedFeederBars
                mode,                           // assemblyMode
                0.0,                            // tickSize
                0.0,                            // open
                0.0,                            // high
                0.0,                            // low
                0.0,                            // close
                0,                              // levelCount
                0.0,                            // barDelta
                0.0,                            // cumDelta
                0.0,                            // deltaSh
                0.0,                            // deltaSl
                0.0,                            // deltaPct
                0.0,                            // maxSeenDelta
                0.0,                            // minSeenDelta
                0.0,                            // totalBuyVol
                0.0,                            // totalSellVol
                0.0,                            // trades
                0.0,                            // maxPositiveDelta
                0.0,                            // maxNegativeDelta
                0.0,                            // maxAskVol
                0.0,                            // maxAskVolPrice
                0.0,                            // maxBidVol
                0.0,                            // maxBidVolPrice
                0.0,                            // maxCombinedVol
                0.0,                            // maxCombinedVolPrice
                0.0,                            // topLevelAskVol
                0.0,                            // topLevelBidVol
                0.0,                            // topLevelTotalVol
                0.0,                            // bottomLevelAskVol
                0.0,                            // bottomLevelBidVol
                0.0,                            // bottomLevelTotalVol
                false,                          // unfinishedTop
                false,                          // unfinishedBottom
                false,                          // bullExhaustion
                false,                          // bearExhaustion
                0.0,                            // absorptionScore
                0,                              // stackedBullRun
                0,                              // stackedBearRun
                0.0,                            // bullStackLow
                0.0,                            // bullStackHigh
                0.0,                            // bearStackLow
                0.0,                            // bearStackHigh
                false,                          // hasBullStack
                false                           // hasBearStack
            );
        }

        private static FootprintResult CopyScalarFacts(in FootprintBar bar, FootprintAssemblyMode mode)
        {
            return new FootprintResult(
                false,                          // isValid
                bar.PrimaryBarStartTime,        // primaryBarStartTime
                bar.PrimaryBarEndTime,          // primaryBarEndTime
                bar.FeederBarsUsed,             // feederBarsUsed
                bar.ExpectedFeederBars,         // expectedFeederBars
                mode,                           // assemblyMode
                bar.TickSize,                   // tickSize
                bar.Open,                       // open
                bar.High,                       // high
                bar.Low,                        // low
                bar.Close,                      // close
                bar.LevelCount,                 // levelCount
                bar.BarDelta,                   // barDelta
                bar.CumDelta,                   // cumDelta
                bar.DeltaSh,                    // deltaSh
                bar.DeltaSl,                    // deltaSl
                bar.DeltaPct,                   // deltaPct
                bar.MaxSeenDelta,               // maxSeenDelta
                bar.MinSeenDelta,               // minSeenDelta
                bar.TotalBuyVol,                // totalBuyVol
                bar.TotalSellVol,               // totalSellVol
                bar.Trades,                     // trades
                bar.MaxPositiveDelta,           // maxPositiveDelta
                bar.MaxNegativeDelta,           // maxNegativeDelta
                bar.MaxAskVol,                  // maxAskVol
                bar.MaxAskVolPrice,             // maxAskVolPrice
                bar.MaxBidVol,                  // maxBidVol
                bar.MaxBidVolPrice,             // maxBidVolPrice
                bar.MaxCombinedVol,             // maxCombinedVol
                bar.MaxCombinedVolPrice,        // maxCombinedVolPrice
                bar.TopLevelAskVol,             // topLevelAskVol
                bar.TopLevelBidVol,             // topLevelBidVol
                bar.TopLevelTotalVol,           // topLevelTotalVol
                bar.BottomLevelAskVol,          // bottomLevelAskVol
                bar.BottomLevelBidVol,          // bottomLevelBidVol
                bar.BottomLevelTotalVol,        // bottomLevelTotalVol
                false,                          // unfinishedTop
                false,                          // unfinishedBottom
                false,                          // bullExhaustion
                false,                          // bearExhaustion
                0.0,                            // absorptionScore
                0,                              // stackedBullRun
                0,                              // stackedBearRun
                0.0,                            // bullStackLow
                0.0,                            // bullStackHigh
                0.0,                            // bearStackLow
                0.0,                            // bearStackHigh
                false,                          // hasBullStack
                false                           // hasBearStack
            );
        }

        private static double ComputeAbsorptionScore(in FootprintBar bar, double ratio)
        {
            if (bar.LevelCount <= 0) return 0.0;
            double total = 0.0;
            for (int i = 0; i < bar.LevelCount; i++)
            {
                double ask = bar.Ask[i], bid = bar.Bid[i];
                double minV = Math.Min(ask, bid);
                if (minV <= 0.0) continue;
                if (Math.Max(ask, bid) / minV >= ratio) total += Math.Abs(ask - bid);
            }
            return total / bar.LevelCount;
        }

        private static void ComputeStackedDiagonalRuns(
            in FootprintBar bar, double ratio,
            out int bullRun, out int bearRun,
            out double bullLow, out double bullHigh,
            out double bearLow, out double bearHigh)
        {
            bullRun = bearRun = 0; bullLow = bullHigh = bearLow = bearHigh = 0.0;
            if (bar.LevelCount < 2) return;

            int cBull = 0, cBear = 0;
            double cBullLow = 0.0, cBullHigh = 0.0, cBearLow = 0.0, cBearHigh = 0.0;

            for (int i = 1; i < bar.LevelCount; i++)
            {
                double prevAsk = bar.Ask[i - 1], prevBid = bar.Bid[i - 1];
                double ask = bar.Ask[i], bid = bar.Bid[i], price = bar.Prices[i];

                if (prevAsk > 0.0 && bid > prevAsk * ratio) // Bull
                {
                    if (cBull == 0) cBullLow = price;
                    cBullHigh = price; cBull++;
                    if (cBull > bullRun) { bullRun = cBull; bullLow = cBullLow; bullHigh = cBullHigh; }
                    cBear = 0;
                }
                else if (prevBid > 0.0 && ask > prevBid * ratio) // Bear
                {
                    if (cBear == 0) cBearLow = price;
                    cBearHigh = price; cBear++;
                    if (cBear > bearRun) { bearRun = cBear; bearLow = cBearLow; bearHigh = cBearHigh; }
                    cBull = 0;
                }
                else { cBull = cBear = 0; }
            }
        }

        private static FootprintResult FinalizeResult(
            in FootprintResult baseResult, double absorptionScore,
            int stackedBullRun, int stackedBearRun,
            double bullStackLow, double bullStackHigh,
            double bearStackLow, double bearStackHigh,
            int minStackedLevels)
        {
            bool hasBullStack = stackedBullRun >= minStackedLevels;
            bool hasBearStack = stackedBearRun >= minStackedLevels;

            // Phase 2.2 — Unfinished Auction
            // Both sides printed at extreme → neither aggressor "won" the auction at the high/low.
            // Historically these levels get revisited ("unfinished business").
            bool unfinishedTop    = baseResult.TopLevelAskVol    > 0.0 && baseResult.TopLevelBidVol    > 0.0;
            bool unfinishedBottom = baseResult.BottomLevelAskVol > 0.0 && baseResult.BottomLevelBidVol > 0.0;

            // Phase 2.3 — Exhaustion
            // Volume at the extreme is far below the bar's per-level average.
            // Signals failed thrust / loss of participation at the turn.
            const double LOW_VOL_RATIO = 0.5;          // ≤50% of avg-level vol
            const int    MIN_LEVELS_FOR_EXH = 4;        // need 4+ levels for meaningful "avg"

            double totalBarVol  = baseResult.TotalBuyVol + baseResult.TotalSellVol;
            double avgLevelVol  = baseResult.LevelCount > 0 ? totalBarVol / baseResult.LevelCount : 0.0;

            bool exhaustionCondMet = baseResult.LevelCount >= MIN_LEVELS_FOR_EXH && avgLevelVol > 0.0;

            bool bullExhaustion = exhaustionCondMet
                && baseResult.TopLevelTotalVol < LOW_VOL_RATIO * avgLevelVol;

            bool bearExhaustion = exhaustionCondMet
                && baseResult.BottomLevelTotalVol < LOW_VOL_RATIO * avgLevelVol;

            return new FootprintResult(
                true,                          // isValid
                baseResult.PrimaryBarStartTime, // primaryBarStartTime
                baseResult.PrimaryBarEndTime,   // primaryBarEndTime
                baseResult.FeederBarsUsed,      // feederBarsUsed
                baseResult.ExpectedFeederBars,  // expectedFeederBars
                baseResult.AssemblyMode,        // assemblyMode
                baseResult.TickSize,            // tickSize
                baseResult.Open,                // open
                baseResult.High,                // high
                baseResult.Low,                 // low
                baseResult.Close,               // close
                baseResult.LevelCount,          // levelCount
                baseResult.BarDelta,            // barDelta
                baseResult.CumDelta,            // cumDelta
                baseResult.DeltaSh,             // deltaSh
                baseResult.DeltaSl,             // deltaSl
                baseResult.DeltaPct,            // deltaPct
                baseResult.MaxSeenDelta,        // maxSeenDelta
                baseResult.MinSeenDelta,        // minSeenDelta
                baseResult.TotalBuyVol,         // totalBuyVol
                baseResult.TotalSellVol,        // totalSellVol
                baseResult.Trades,              // trades
                baseResult.MaxPositiveDelta,    // maxPositiveDelta
                baseResult.MaxNegativeDelta,    // maxNegativeDelta
                baseResult.MaxAskVol,           // maxAskVol
                baseResult.MaxAskVolPrice,      // maxAskVolPrice
                baseResult.MaxBidVol,           // maxBidVol
                baseResult.MaxBidVolPrice,      // maxBidVolPrice
                baseResult.MaxCombinedVol,      // maxCombinedVol
                baseResult.MaxCombinedVolPrice, // maxCombinedVolPrice
                baseResult.TopLevelAskVol,      // topLevelAskVol
                baseResult.TopLevelBidVol,      // topLevelBidVol
                baseResult.TopLevelTotalVol,    // topLevelTotalVol
                baseResult.BottomLevelAskVol,   // bottomLevelAskVol
                baseResult.BottomLevelBidVol,   // bottomLevelBidVol
                baseResult.BottomLevelTotalVol, // bottomLevelTotalVol
                unfinishedTop,                  // unfinishedTop
                unfinishedBottom,               // unfinishedBottom
                bullExhaustion,                 // bullExhaustion
                bearExhaustion,                 // bearExhaustion
                absorptionScore,                // absorptionScore
                stackedBullRun,                 // stackedBullRun
                stackedBearRun,                 // stackedBearRun
                bullStackLow,                   // bullStackLow
                bullStackHigh,                  // bullStackHigh
                bearStackLow,                   // bearStackLow
                bearStackHigh,                  // bearStackHigh
                hasBullStack,                   // hasBullStack
                hasBearStack                    // hasBearStack
            );
        }
    }
}
