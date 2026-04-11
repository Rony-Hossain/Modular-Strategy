#region Using declarations
using System;
using System.Threading;
#endregion

namespace MathLogic
{
    // ===================================================================
    // COMPUTATION TIER SYSTEM
    // ===================================================================
    // ComputationTier enum is defined in CommonTypes.cs (canonical location).

    /// <summary>
    /// Computation tier enforcement and validation.
    /// </summary>
    public static class ComputationControl
    {
        private static volatile ComputationTier _currentTier = ComputationTier.CORE_ONLY;
        private static volatile bool _isLiveTrading = false;

        /// <summary>
        /// Set current computation tier. 
        /// Will throw exception if trying to set high tier during live trading.
        /// </summary>
        public static void SetTier(ComputationTier tier, bool isLive)
        {
            _isLiveTrading = isLive;

            // SAFETY: Prevent full analysis in live trading
            if (isLive && tier == ComputationTier.FULL_ANALYSIS)
            {
                throw new InvalidOperationException(
                    "FULL_ANALYSIS tier cannot be used in live trading. " +
                    "Set to STRUCTURE_ONLY or ORDERFLOW_ENABLED maximum.");
            }

            _currentTier = tier;
        }

        /// <summary>
        /// Check if specific tier is enabled
        /// </summary>
        public static bool IsTierEnabled(ComputationTier required) => _currentTier >= required;

        /// <summary>
        /// Check if currently in live trading mode
        /// </summary>
        public static bool IsLiveTrading() => _isLiveTrading;

        /// <summary>
        /// Validate tier before expensive operation
        /// </summary>
        public static void ValidateTier(ComputationTier required, string operationName)
        {
            if (!IsTierEnabled(required))
            {
                throw new InvalidOperationException(
                    $"Operation '{operationName}' requires {required} tier, " +
                    $"but current tier is {_currentTier}. " +
                    $"Call ComputationControl.SetTier() to enable.");
            }
        }

        /// <summary>
        /// Block replay-only operations in live trading
        /// </summary>
        public static void BlockIfLive(string operationName)
        {
            if (_isLiveTrading)
            {
                throw new InvalidOperationException(
                    $"Operation '{operationName}' is REPLAY-ONLY and cannot be used in live trading.");
            }
        }
    }

    // ===================================================================
    // IMMUTABLE RESULT OBJECTS
    // ===================================================================

    /// <summary>
    /// VWAP Session Result - Immutable
    /// </summary>
    public sealed class VWAPSessionResult : IVWAPSessionResult
    {
        public double VWAP { get; }
        public double PVSum { get; }
        public double VolumeSum { get; }
        public bool IsValid { get; }

        public VWAPSessionResult(double vwap, double pvSum, double volumeSum, bool isValid)
        {
            VWAP = vwap;
            PVSum = pvSum;
            VolumeSum = volumeSum;
            IsValid = isValid;
        }

        // Singleton for invalid/default case - avoids allocation
        public static readonly VWAPSessionResult Invalid = new VWAPSessionResult(0, 0, 0, false);
    }

    /// <summary>
    /// SD Hybrid Result - Immutable
    /// </summary>
    public sealed class SDHybridResult : ISDHybridResult
    {
        public double SDHybrid { get; }
        public double SDTicks { get; }
        public double WeightSession { get; }
        public double WeightRolling { get; }
        public bool IsTradeable { get; }

        public SDHybridResult(double sdHybrid, double sdTicks, double weightSession, 
            double weightRolling, bool isTradeable)
        {
            SDHybrid = sdHybrid;
            SDTicks = sdTicks;
            WeightSession = weightSession;
            WeightRolling = weightRolling;
            IsTradeable = isTradeable;
        }

        public static readonly SDHybridResult Invalid = new SDHybridResult(0, 0, 0, 0, false);
    }

    /// <summary>
    /// POC Result - Immutable
    /// </summary>
    public sealed class POCResult : IPOCResult
    {
        public double POC { get; }
        public double MaxVolume { get; }
        public bool IsStable { get; }
        public int DriftDirection { get; } // 0=STABLE, 1=UP, 2=DOWN

        public POCResult(double poc, double maxVolume, bool isStable, int driftDirection)
        {
            POC = poc;
            MaxVolume = maxVolume;
            IsStable = isStable;
            DriftDirection = driftDirection;
        }

        public static readonly POCResult Invalid = new POCResult(0, 0, false, 0);
    }

    /// <summary>
    /// Value Area Result - Immutable
    /// </summary>
    public sealed class ValueAreaResult : IValueAreaResult
    {
        public double ValueAreaHigh { get; }
        public double ValueAreaLow { get; }
        public double ValueAreaWidth { get; }

        public ValueAreaResult(double vaHigh, double vaLow, double vaWidth)
        {
            ValueAreaHigh = vaHigh;
            ValueAreaLow = vaLow;
            ValueAreaWidth = vaWidth;
        }

        public static readonly ValueAreaResult Invalid = new ValueAreaResult(0, 0, 0);
    }

    /// <summary>
    /// Delta Divergence Result - Immutable
    /// </summary>
    public sealed class DeltaDivergenceResult : IDeltaDivergenceResult
    {
        public bool IsBullish { get; }
        public bool IsBearish { get; }
        public double Strength { get; }

        public DeltaDivergenceResult(bool isBullish, bool isBearish, double strength)
        {
            IsBullish = isBullish;
            IsBearish = isBearish;
            Strength = strength;
        }

        public static readonly DeltaDivergenceResult None = new DeltaDivergenceResult(false, false, 0);
    }

    /// <summary>
    /// Absorption Result - Immutable
    /// </summary>
    public sealed class AbsorptionResult : IAbsorptionResult
    {
        public double Score { get; }
        public int StackedBidCount { get; }
        public int StackedAskCount { get; }
        public bool AbsorptionLong { get; }
        public bool AbsorptionShort { get; }

        public AbsorptionResult(double score, int stackedBid, int stackedAsk, 
            bool absLong, bool absShort)
        {
            Score = score;
            StackedBidCount = stackedBid;
            StackedAskCount = stackedAsk;
            AbsorptionLong = absLong;
            AbsorptionShort = absShort;
        }

        public static readonly AbsorptionResult None = new AbsorptionResult(0, 0, 0, false, false);
    }

    /// <summary>
    /// Stop Management Result - Immutable
    /// </summary>
    public sealed class StopResult : IStopResult
    {
        public double StopPrice { get; }
        public double DistanceTicks { get; }

        public StopResult(double stopPrice, double distanceTicks)
        {
            StopPrice = stopPrice;
            DistanceTicks = distanceTicks;
        }

        public static readonly StopResult Invalid = new StopResult(0, 0);
    }

    /// <summary>
    /// Target Management Result - Immutable
    /// </summary>
    public sealed class TargetResult : ITargetResult
    {
        public double T1Price { get; }
        public double T2Price { get; }

        public TargetResult(double t1, double t2)
        {
            T1Price = t1;
            T2Price = t2;
        }

        public static readonly TargetResult Invalid = new TargetResult(0, 0);
    }

    /// <summary>
    /// Position Sizing Result - Immutable
    /// </summary>
    public sealed class PositionSizeResult : IPositionSizeResult
    {
        public int Contracts { get; }
        public double RiskDollars { get; }
        public bool AtMaxLimit { get; }

        public PositionSizeResult(int contracts, double riskDollars, bool atMaxLimit)
        {
            Contracts = contracts;
            RiskDollars = riskDollars;
            AtMaxLimit = atMaxLimit;
        }

        public static readonly PositionSizeResult Zero = new PositionSizeResult(0, 0, false);
    }

    // ===================================================================
    // OBJECT POOL FOR HIGH-FREQUENCY OPERATIONS
    // ===================================================================

    /// <summary>
    /// Simple object pool to reduce allocations in hot paths.
    /// Use for temporary calculation buffers, NOT for result objects.
    /// </summary>
    public static class ArrayPool
    {
        private const int POOL_SIZE = 10;
        private static readonly double[][] _doubleArrayPool = new double[POOL_SIZE][];
        private static readonly bool[] _inUse = new bool[POOL_SIZE];
        private static readonly object _lock = new object();

        static ArrayPool()
        {
            // Pre-allocate arrays of common sizes
            for (int i = 0; i < POOL_SIZE; i++)
            {
                _doubleArrayPool[i] = new double[100]; // Adjust size as needed
                _inUse[i] = false;
            }
        }

        /// <summary>
        /// Rent an array from the pool. MUST call Return() when done.
        /// </summary>
        public static double[] RentDoubleArray(int minLength)
        {
            lock (_lock)
            {
                for (int i = 0; i < POOL_SIZE; i++)
                {
                    if (!_inUse[i] && _doubleArrayPool[i].Length >= minLength)
                    {
                        _inUse[i] = true;
                        return _doubleArrayPool[i];
                    }
                }
            }

            // Pool exhausted - allocate new (not ideal but safe)
            return new double[minLength];
        }

        /// <summary>
        /// Return array to pool
        /// </summary>
        public static void ReturnDoubleArray(double[] array)
        {
            if (array == null) return;

            lock (_lock)
            {
                for (int i = 0; i < POOL_SIZE; i++)
                {
                    if (_doubleArrayPool[i] == array)
                    {
                        _inUse[i] = false;
                        return;
                    }
                }
            }
            // Array wasn't from pool - will be GC'd
        }
    }

    // ===================================================================
    // PERFORMANCE MONITORING
    // ===================================================================

    /// <summary>
    /// Simple performance counter for detecting computation bottlenecks.
    /// Use in development/testing, disable in production.
    /// </summary>
    public static class PerformanceMonitor
    {
        private static bool _enabled = false;
        private static long _coreCallCount = 0;
        private static long _structureCallCount = 0;
        private static long _orderFlowCallCount = 0;

        public static void Enable() => _enabled = true;
        public static void Disable() => _enabled = false;

        public static void IncrementCore()      { if (_enabled) Interlocked.Increment(ref _coreCallCount); }
        public static void IncrementStructure() { if (_enabled) Interlocked.Increment(ref _structureCallCount); }
        public static void IncrementOrderFlow() { if (_enabled) Interlocked.Increment(ref _orderFlowCallCount); }

        public static void Reset()
        {
            _coreCallCount = 0;
            _structureCallCount = 0;
            _orderFlowCallCount = 0;
        }

        public static string GetReport() => $"Core: {_coreCallCount}, Structure: {_structureCallCount}, OrderFlow: {_orderFlowCallCount}";
    }
}
