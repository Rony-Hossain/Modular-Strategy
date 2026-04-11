#region Using declarations
using System;
using System.Collections.Generic;
#endregion

namespace MathLogic
{
    // ===================================================================
    // PARAMETER INTERFACES - Input Visibility
    // ===================================================================

    /// <summary>
    /// Equation 2: Enhanced Typical Price - Parameters
    /// </summary>
    public interface ITPAdaptiveParams
    {
        double High { get; }
        double Low { get; }
        double Close { get; }
        double ATR { get; }
        double RangeThresholdMultiplier { get; }
    }

    /// <summary>
    /// Equation 3: Session VWAP - Parameters
    /// </summary>
    public interface IVWAPSessionParams
    {
        double[] TPAdaptive { get; }
        double[] Volume { get; }
        int Length { get; }
    }

    /// <summary>
    /// Equation 4: Session SD - Parameters
    /// </summary>
    public interface ISDSessionParams
    {
        double[] Deviations { get; }
        int MinBarsRequired { get; }
    }

    /// <summary>
    /// Equation 6: Hybrid SD - Parameters
    /// </summary>
    public interface ISDHybridParams
    {
        double SDSession { get; }
        double SDRolling { get; }
        double SessionPct { get; }
        double TickSize { get; }
    }

    /// <summary>
    /// Equation 7: POC - Parameters
    /// </summary>
    public interface IPOCParams
    {
        Dictionary<double, double> VolumeAtPrice { get; }
    }

    /// <summary>
    /// Equation 8: Value Area - Parameters
    /// </summary>
    public interface IValueAreaParams
    {
        Dictionary<double, double> VolumeAtPrice { get; }
        double POC { get; }
        double TargetCoverage { get; }
    }

    /// <summary>
    /// Equation 10: POC Drift - Parameters
    /// </summary>
    public interface IPOCDriftParams
    {
        double POCCurrent { get; }
        double POC5BarsAgo { get; }
        double POC30MinAgo { get; }
        double TickSize { get; }
        double DriftThresholdTicks { get; }
    }

    /// <summary>
    /// Delta Divergence - Parameters
    /// </summary>
    public interface IDeltaDivergenceParams
    {
        double[] Lows { get; }
        double[] Highs { get; }
        double[] CDTick { get; }
        double MinDivergenceStrength { get; }
    }

    /// <summary>
    /// Absorption - Parameters
    /// </summary>
    public interface IAbsorptionParams
    {
        double[] BidVolumes { get; }
        double[] AskVolumes { get; }
        double High { get; }
        double Low { get; }
        double TickSize { get; }
    }

    /// <summary>
    /// Effort/Result - Parameters
    /// </summary>
    public interface IEffortResultParams
    {
        double[] Volumes { get; }
        double[] Deltas { get; }
        double CloseCurrent { get; }
        double ClosePast { get; }
        double TickSize { get; }
        double ATR { get; }
        int Lookback { get; }
    }

    /// <summary>
    /// Spring Detection - Parameters
    /// </summary>
    public interface ISpringParams
    {
        double Low { get; }
        double Close { get; }
        double Open { get; }
        double RangeLow { get; }
        double Buffer { get; }
        int OutsideBars { get; }
        double OutsideVolPct { get; }
        int MaxOutsideBars { get; }
        double MaxOutsideVolPct { get; }
        bool RequireBullishClose { get; }
    }

    /// <summary>
    /// Upthrust Detection - Parameters
    /// </summary>
    public interface IUpthrustParams
    {
        double High { get; }
        double Close { get; }
        double Open { get; }
        double RangeHigh { get; }
        double Buffer { get; }
        int OutsideBars { get; }
        double OutsideVolPct { get; }
        int MaxOutsideBars { get; }
        double MaxOutsideVolPct { get; }
        bool RequireBearishClose { get; }
    }

    /// <summary>
    /// Acceptance Metrics - Parameters
    /// </summary>
    public interface IAcceptanceParams
    {
        double[] Closes { get; }
        double[] Volumes { get; }
        double Boundary { get; }
        bool IsAbove { get; }
        int Length { get; }
    }

    /// <summary>
    /// Balance Quality - Parameters
    /// </summary>
    public interface IBalanceQualityParams
    {
        double[] Highs { get; }
        double[] Lows { get; }
        double[] Ranges { get; }
        double[] VWAPs { get; }
        double[] VAWidths { get; }
        int Length { get; }
        double TouchThreshold { get; }
    }

    /// <summary>
    /// Position Sizing - Parameters
    /// </summary>
    public interface IPositionSizeParams
    {
        double AccountSize { get; }
        double RiskPct { get; }
        double StopDistanceTicks { get; }
        double TickValue { get; }
        int MaxLimit { get; }
    }

    /// <summary>
    /// Stop Placement - Parameters
    /// </summary>
    public interface IStopParams
    {
        double EntryPrice { get; }
        double SwingLow5 { get; }
        double SwingLow10 { get; }
        double SwingHigh5 { get; }
        double SwingHigh10 { get; }
        double BufferPrice { get; }
        double ATRTicks { get; }
        double TickSize { get; }
    }

    /// <summary>
    /// Target Calculation - Parameters
    /// </summary>
    public interface ITargetParams
    {
        double Entry { get; }
        double VWAP { get; }
        double POC { get; }
        double SDHybrid { get; }
    }

    // ===================================================================
    // RESULT INTERFACES - Output Visibility
    // ===================================================================

    /// <summary>
    /// VWAP Session Result - Interface
    /// </summary>
    public interface IVWAPSessionResult
    {
        double VWAP { get; }
        double PVSum { get; }
        double VolumeSum { get; }
        bool IsValid { get; }
    }

    /// <summary>
    /// SD Hybrid Result - Interface
    /// </summary>
    public interface ISDHybridResult
    {
        double SDHybrid { get; }
        double SDTicks { get; }
        double WeightSession { get; }
        double WeightRolling { get; }
        bool IsTradeable { get; }
    }

    /// <summary>
    /// POC Result - Interface
    /// </summary>
    public interface IPOCResult
    {
        double POC { get; }
        double MaxVolume { get; }
        bool IsStable { get; }
        int DriftDirection { get; }
    }

    /// <summary>
    /// Value Area Result - Interface
    /// </summary>
    public interface IValueAreaResult
    {
        double ValueAreaHigh { get; }
        double ValueAreaLow { get; }
        double ValueAreaWidth { get; }
    }

    /// <summary>
    /// Delta Divergence Result - Interface
    /// </summary>
    public interface IDeltaDivergenceResult
    {
        bool IsBullish { get; }
        bool IsBearish { get; }
        double Strength { get; }
    }

    /// <summary>
    /// Absorption Result - Interface
    /// </summary>
    public interface IAbsorptionResult
    {
        double Score { get; }
        int StackedBidCount { get; }
        int StackedAskCount { get; }
        bool AbsorptionLong { get; }
        bool AbsorptionShort { get; }
    }

    /// <summary>
    /// Effort/Result Result - Interface
    /// </summary>
    public interface IEffortResultResult
    {
        double Effort { get; }
        double Result { get; }
        double Ratio { get; }
        int Condition { get; }
    }

    /// <summary>
    /// Spring Result - Interface
    /// </summary>
    public interface ISpringResult
    {
        bool IsDetected { get; }
        double Strength { get; }
        double PenetrationTicks { get; }
        int ReentrySpeed { get; }
    }

    /// <summary>
    /// Upthrust Result - Interface
    /// </summary>
    public interface IUpthrustResult
    {
        bool IsDetected { get; }
        double Strength { get; }
        double PenetrationTicks { get; }
        int ReentrySpeed { get; }
    }

    /// <summary>
    /// Stopping Volume Result - Interface
    /// </summary>
    public interface IStoppingVolumeResult
    {
        int Location { get; }
        double EffortResultRatio { get; }
        bool IsSignificant { get; }
    }

    /// <summary>
    /// Acceptance Result - Interface
    /// </summary>
    public interface IAcceptanceResult
    {
        int BarsOutside { get; }
        double VolumeOutsidePct { get; }
        int ReentrySpeed { get; }
        int Classification { get; }
    }

    /// <summary>
    /// Balance Quality Result - Interface
    /// </summary>
    public interface IBalanceQualityResult
    {
        double OverlapRatio { get; }
        double Compression { get; }
        double VWAPTouchRate { get; }
        double VAStability { get; }
        double QualityScore { get; }
    }

    /// <summary>
    /// Profile Shape Result - Interface
    /// </summary>
    public interface IProfileShapeResult
    {
        double POCSkew { get; }
        int ExcessTailSize { get; }
        int PeakCount { get; }
    }

    /// <summary>
    /// Stop Result - Interface
    /// </summary>
    public interface IStopResult
    {
        double StopPrice { get; }
        double DistanceTicks { get; }
    }

    /// <summary>
    /// Target Result - Interface
    /// </summary>
    public interface ITargetResult
    {
        double T1Price { get; }
        double T2Price { get; }
    }

    /// <summary>
    /// Position Size Result - Interface
    /// </summary>
    public interface IPositionSizeResult
    {
        int Contracts { get; }
        double RiskDollars { get; }
        bool AtMaxLimit { get; }
    }

    // ===================================================================
    // COMPOSITE INTERFACES - Complete Workflows
    // ===================================================================

    /// <summary>
    /// Complete Market Snapshot - All Context Data
    /// </summary>
    public interface IMarketSnapshot
    {
        // Core
        IVWAPSessionResult VWAPData { get; }
        ISDHybridResult VolatilityData { get; }
        
        // Structure
        IPOCResult POCData { get; }
        IValueAreaResult ValueAreaData { get; }
        
        // Wyckoff
        IEffortResultResult EffortResult { get; }
        ISpringResult SpringData { get; }
        IUpthrustResult UpthrustData { get; }
        IStoppingVolumeResult StoppingVolume { get; }
        
        // AMT
        IAcceptanceResult AcceptanceAboveVAH { get; }
        IAcceptanceResult AcceptanceBelowVAL { get; }
        
        // Balance
        IBalanceQualityResult BalanceQuality { get; }
        
        // Profile
        IProfileShapeResult ProfileShape { get; }
    }

    /// <summary>
    /// Complete Trade Setup Analysis
    /// </summary>
    public interface ITradeSetup
    {
        IMarketSnapshot Context { get; }
        int MasterScore { get; }
        string Grade { get; }
        bool IsReject { get; }
    }

    /// <summary>
    /// Complete Trade Execution Plan
    /// </summary>
    public interface ITradeExecution
    {
        IPositionSizeResult Sizing { get; }
        IStopResult StopData { get; }
        ITargetResult TargetData { get; }
        double EntryPrice { get; }
        int Retries { get; }
        bool IsAggressive { get; }
    }

    // ===================================================================
    // PARAMETER IMPLEMENTATION CLASSES
    // ===================================================================

    /// <summary>
    /// TP Adaptive Parameters - Implementation
    /// </summary>
    public class TPAdaptiveParams : ITPAdaptiveParams
    {
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public double ATR { get; set; }
        public double RangeThresholdMultiplier { get; set; } = 1.5;
    }

    /// <summary>
    /// VWAP Session Parameters - Implementation
    /// </summary>
    public class VWAPSessionParams : IVWAPSessionParams
    {
        public double[] TPAdaptive { get; set; }
        public double[] Volume { get; set; }
        public int Length { get; set; }
    }

    /// <summary>
    /// SD Session Parameters - Implementation
    /// </summary>
    public class SDSessionParams : ISDSessionParams
    {
        public double[] Deviations { get; set; }
        public int MinBarsRequired { get; set; } = 20;
    }

    /// <summary>
    /// SD Hybrid Parameters - Implementation
    /// </summary>
    public class SDHybridParams : ISDHybridParams
    {
        public double SDSession { get; set; }
        public double SDRolling { get; set; }
        public double SessionPct { get; set; }
        public double TickSize { get; set; }
    }

    /// <summary>
    /// POC Parameters - Implementation
    /// </summary>
    public class POCParams : IPOCParams
    {
        public Dictionary<double, double> VolumeAtPrice { get; set; }
    }

    /// <summary>
    /// Value Area Parameters - Implementation
    /// </summary>
    public class ValueAreaParams : IValueAreaParams
    {
        public Dictionary<double, double> VolumeAtPrice { get; set; }
        public double POC { get; set; }
        public double TargetCoverage { get; set; } = 0.70;
    }

    /// <summary>
    /// POC Drift Parameters - Implementation
    /// </summary>
    public class POCDriftParams : IPOCDriftParams
    {
        public double POCCurrent { get; set; }
        public double POC5BarsAgo { get; set; }
        public double POC30MinAgo { get; set; }
        public double TickSize { get; set; }
        public double DriftThresholdTicks { get; set; } = 3.0;
    }

    /// <summary>
    /// Delta Divergence Parameters - Implementation
    /// </summary>
    public class DeltaDivergenceParams : IDeltaDivergenceParams
    {
        public double[] Lows { get; set; }
        public double[] Highs { get; set; }
        public double[] CDTick { get; set; }
        public double MinDivergenceStrength { get; set; } = 400.0;
    }

    /// <summary>
    /// Absorption Parameters - Implementation
    /// </summary>
    public class AbsorptionParams : IAbsorptionParams
    {
        public double[] BidVolumes { get; set; }
        public double[] AskVolumes { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double TickSize { get; set; }
    }

    /// <summary>
    /// Effort/Result Parameters - Implementation
    /// </summary>
    public class EffortResultParams : IEffortResultParams
    {
        public double[] Volumes { get; set; }
        public double[] Deltas { get; set; }
        public double CloseCurrent { get; set; }
        public double ClosePast { get; set; }
        public double TickSize { get; set; }
        public double ATR { get; set; }
        public int Lookback { get; set; }
    }

    /// <summary>
    /// Spring Parameters - Implementation
    /// </summary>
    public class SpringParams : ISpringParams
    {
        public double Low { get; set; }
        public double Close { get; set; }
        public double Open { get; set; }
        public double RangeLow { get; set; }
        public double Buffer { get; set; }
        public int OutsideBars { get; set; }
        public double OutsideVolPct { get; set; }
        public int MaxOutsideBars { get; set; } = 3;
        public double MaxOutsideVolPct { get; set; } = 0.30;
        public bool RequireBullishClose { get; set; } = true;
    }

    /// <summary>
    /// Upthrust Parameters - Implementation
    /// </summary>
    public class UpthrustParams : IUpthrustParams
    {
        public double High { get; set; }
        public double Close { get; set; }
        public double Open { get; set; }
        public double RangeHigh { get; set; }
        public double Buffer { get; set; }
        public int OutsideBars { get; set; }
        public double OutsideVolPct { get; set; }
        public int MaxOutsideBars { get; set; } = 3;
        public double MaxOutsideVolPct { get; set; } = 0.30;
        public bool RequireBearishClose { get; set; } = true;
    }

    /// <summary>
    /// Acceptance Parameters - Implementation
    /// </summary>
    public class AcceptanceParams : IAcceptanceParams
    {
        public double[] Closes { get; set; }
        public double[] Volumes { get; set; }
        public double Boundary { get; set; }
        public bool IsAbove { get; set; }
        public int Length { get; set; }
    }

    /// <summary>
    /// Balance Quality Parameters - Implementation
    /// </summary>
    public class BalanceQualityParams : IBalanceQualityParams
    {
        public double[] Highs { get; set; }
        public double[] Lows { get; set; }
        public double[] Ranges { get; set; }
        public double[] VWAPs { get; set; }
        public double[] VAWidths { get; set; }
        public int Length { get; set; }
        public double TouchThreshold { get; set; }
    }

    /// <summary>
    /// Position Size Parameters - Implementation
    /// </summary>
    public class PositionSizeParams : IPositionSizeParams
    {
        public double AccountSize { get; set; }
        public double RiskPct { get; set; }
        public double StopDistanceTicks { get; set; }
        public double TickValue { get; set; }
        public int MaxLimit { get; set; }
    }

    /// <summary>
    /// Stop Parameters - Implementation
    /// </summary>
    public class StopParams : IStopParams
    {
        public double EntryPrice { get; set; }
        public double SwingLow5 { get; set; }
        public double SwingLow10 { get; set; }
        public double SwingHigh5 { get; set; }
        public double SwingHigh10 { get; set; }
        public double BufferPrice { get; set; }
        public double ATRTicks { get; set; }
        public double TickSize { get; set; }
    }

    /// <summary>
    /// Target Parameters - Implementation
    /// </summary>
    public class TargetParams : ITargetParams
    {
        public double Entry { get; set; }
        public double VWAP { get; set; }
        public double POC { get; set; }
        public double SDHybrid { get; set; }
    }

    // ===================================================================
    // COMPOSITE IMPLEMENTATION CLASSES
    // ===================================================================

    /// <summary>
    /// Complete Market Snapshot - Implementation
    /// </summary>
    public class MarketAnalysisSnapshot : IMarketSnapshot
    {
        public IVWAPSessionResult VWAPData { get; set; }
        public ISDHybridResult VolatilityData { get; set; }
        public IPOCResult POCData { get; set; }
        public IValueAreaResult ValueAreaData { get; set; }
        public IEffortResultResult EffortResult { get; set; }
        public ISpringResult SpringData { get; set; }
        public IUpthrustResult UpthrustData { get; set; }
        public IStoppingVolumeResult StoppingVolume { get; set; }
        public IAcceptanceResult AcceptanceAboveVAH { get; set; }
        public IAcceptanceResult AcceptanceBelowVAL { get; set; }
        public IBalanceQualityResult BalanceQuality { get; set; }
        public IProfileShapeResult ProfileShape { get; set; }
    }

    /// <summary>
    /// Complete Trade Setup - Implementation
    /// </summary>
    public class TradeSetup : ITradeSetup
    {
        public IMarketSnapshot Context { get; set; }
        public int MasterScore { get; set; }
        public string Grade { get; set; }
        public bool IsReject { get; set; }
    }

    /// <summary>
    /// Complete Trade Execution - Implementation
    /// </summary>
    public class TradeExecution : ITradeExecution
    {
        public IPositionSizeResult Sizing { get; set; }
        public IStopResult StopData { get; set; }
        public ITargetResult TargetData { get; set; }
        public double EntryPrice { get; set; }
        public int Retries { get; set; }
        public bool IsAggressive { get; set; }
    }
}
