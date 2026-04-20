#region Using declarations
using System;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// CENTRAL STRATEGY CONFIGURATION — Single source of truth for all weights, penalties, and thresholds.
    /// </summary>
    public static class StrategyConfig
    {
        // =========================================================================
        // 1. INSTRUMENT SPECIFICATIONS (Single source for tick/point facts)
        // =========================================================================
        public static class Instruments
        {
            public struct Spec { public string Name; public double TickSize; public double TickValue; public double PointValue; public double RoundInterval; }

            public static readonly Spec MNQ = new Spec { Name="MNQ", TickSize=0.25, TickValue=0.50,  PointValue=2.0,  RoundInterval=100.0 };
            public static readonly Spec NQ  = new Spec { Name="NQ",  TickSize=0.25, TickValue=5.00,  PointValue=20.0, RoundInterval=100.0 };
            public static readonly Spec ES  = new Spec { Name="ES",  TickSize=0.25, TickValue=12.50, PointValue=50.0, RoundInterval=25.0  };
            public static readonly Spec MES = new Spec { Name="MES", TickSize=0.25, TickValue=1.25,  PointValue=5.0,  RoundInterval=25.0  };
        }

        // =========================================================================
        // 2. CONFLUENCE ENGINE WEIGHTS
        // =========================================================================
        public static class Confluence
        {
            // ── Layer A: MTFA Macro Bias (0–30) ──
            public const int LAYER_A_H4 = 14;
            public const int LAYER_A_H2 = 10;
            public const int LAYER_A_H1 = 6;

            // ── Layer B: Level Context (SR weights) ──
            public const int SR_AT_FAVORABLE          = 18;
            public const int SR_NEAR_FAV_CLOSE        = 12; 
            public const int SR_NEAR_FAV_MID          = 8;  
            public const int SR_NEAR_FAV_FAR          = 4;  
            
            public const int SR_STRONG_FAV_S3         = 8;  
            public const int SR_STRONG_FAV_S2         = 5;  
            public const int SR_STRONG_FAV_S1         = 3;  
            public const int SR_STACKED_BONUS         = 6;

            public const int SR_NEAR_ADV_CLOSE        = 10; 
            public const int SR_NEAR_ADV_MID          = 6;  
            public const int SR_NEAR_ADV_FAR          = 3;  
            public const int LAYER_B_MAX_CAP          = 40;

            // ── Layer C: OrderFlow Conviction (0–30 Cap) ──
            public const int LAYER_C_DIVERGENCE       = 15;
            public const int LAYER_C_TRAPPED_AGREE    = 8;
            public const int LAYER_C_ICEBERG_AGREE    = 8;
            public const int LAYER_C_EXHAUSTION_AGREE = 8;
            public const int LAYER_C_UNFINISHED_AGREE = 4;
            
            public const int LAYER_C_DELTA_SL         = 8;
            public const int LAYER_C_ABS_MAX          = 7;
            public const int LAYER_C_DELTA_EXHST      = 8;
            public const int LAYER_C_IMBAL_ZONE       = 6;

            public const int PROXY_BAR_DELTA          = 7;
            public const int PROXY_REGIME             = 6;
            public const int PROXY_VWAP_SIDE          = 5;
            public const int PROXY_H1_BAR_DIR         = 4;

            public const int LAYER_C_MAX_CAP          = 30;

            // ── Layer D: Price Action Structure (0–15) ──
            public const int LAYER_D_FULL_STRUCT      = 12;
            public const int LAYER_D_TREND_ONLY       = 8;
            
            public const int PENALTY_ABOVE_FAIR       = 15;
            public const int BONUS_DEEP_DISCOUNT      = 10;
        }

        // =========================================================================
        // 3. PENALTY WEIGHTS
        // =========================================================================
        public static class Penalties
        {
            public const int PENALTY_H4           = 8;
            public const int PENALTY_H2           = 5;
            public const int PENALTY_BOTH_EXTRA   = 5;
            public const int RANK_CONFLICT        = 15;
        }

        // =========================================================================
        // 4a. VETO THRESHOLDS
        // =========================================================================
        public static class Vetoes
        {
            public const double CUMDELTA_EXHAUSTED = 2500.0;
            public const double WEAK_STACK_COUNT   = 3.0;
            public const double BRICK_WALL_ATR     = 0.20;
        }

        // =========================================================================
        // 4b. SCORE FLOORS
        // =========================================================================
        public static class Floors
        {
            public const int MIN_NET_VOLUMETRIC    = 40;
            public const int MIN_NET_STRUCTURE     = 25;
            public const int BOS_FLOOR_VOLUMETRIC  = 20;
            public const int BOS_FLOOR_STRUCTURE   = 10;
            public const int BOSWAVE_FLOOR         = 0;
            public const int LONG_H4_BEARISH_FLOOR = 60;
            public const int LONG_H4_NEUTRAL_FLOOR = 70;
        }

        // =========================================================================
        // 4c. SIGNAL COOLDOWN DEFAULTS (bars)
        // =========================================================================
        public static class Defaults
        {
            public const int SIGNAL_COOLDOWN_EMA = 8;   // EMA is slow — longer cooldown
            public const int SIGNAL_COOLDOWN_ADX = 10;  // ADX is slow — longer cooldown
            public const int SIGNAL_COOLDOWN_ORB = 5;   // ORB reentry cooldown
        }

        // =========================================================================
        // 5. MODULE SETTINGS
        // =========================================================================
        public static class Modules
        {
            // ── DataFeed & Indicators ──
            public const int    DF_ATR_PERIOD         = 14;
            public const int    DF_ORB_BARS           = 30;
            public const double DF_VWAP_SD2_MULT      = 2.0;

            // ── Feed & Session ──
            public const int SNAPSHOT_DEPTH         = 100;
            public const int BARS_REQUIRED_TO_TRADE = 50;

            // ── BigPrint Detector ──
            public const int    BP_VOL_RING_SIZE      = 2000;
            public const int    BP_RECOMPUTE_INTERVAL = 200;
            public const double BP_PERCENTILE         = 0.95;
            public const int    BP_RING_SIZE          = 500;
            public const long   BP_WINDOW_MS          = 30000;

            // ── Delta Divergence ──
            public const double DD_MIN_ABSORPTION_SCORE = 5.0;
            public const double DD_ATR_STOP_BUFFER      = 0.08;
            public const double DD_MIN_STOP_ATR_MULT    = 0.20;
            public const int    DD_STOP_LOOKBACK_BARS   = 3;
            public const int    DD_COOLDOWN_BARS        = 8;
            public const int    DD_BASE_SCORE           = 78;

            // ── EMA Cross ──
            public const int EMA_FAST_PERIOD    = 9;
            public const int EMA_SLOW_PERIOD    = 21;
            public const int EMA_TREND_PERIOD   = 200;
            public const int EMA_BASE_SCORE     = 60;
            public const int EMA_PENALTY_REGIME = 12;
            public const int EMA_PENALTY_VWAP   = 10;

            // ── SMC (Smart Money Concepts) ──
            public const int    SMC_SWING_STRENGTH           = 3;
            public const double SMC_MIN_BOS_TICKS            = 2.0;
            public const double SMC_MIN_CHOCH_STRENGTH       = 0.30;
            public const int    SMC_OB_LOOKBACK              = 5;
            public const double SMC_OB_INV_BUFFER            = 2.0;
            public const int    SMC_COOLDOWN_BARS            = 5;
            public const double SMC_STOP_BUFFER_TICKS        = 2.0;

            public const int    SMC_FVG_MAX_AGE_BARS         = 200;
            public const int    SMC_FVG_BASE_SCORE           = 55;
            public const int    SMC_FVG_BONUS_SIZE           = 5;
            public const int    SMC_FVG_PENALTY_WEAK_CLOSE   = 8;

            public const int    SMC_LIQ_PIVOT_COOLDOWN       = 5;
            public const double SMC_LIQ_MAX_LEVEL_DIFF_ATR   = 0.3;
            public const int    SMC_LIQ_MAX_AGE_BARS         = 100;
            public const int    SMC_LIQ_BASE_SCORE           = 60;

            public const int    SMC_SESS_BASE_SCORE          = 58;
            public const int    SMC_SESS_PENALTY_WEAK_CLOSE  = 8;

            public const int    SMC_IB_MINUTES               = 60;
            public const int    SMC_IB_POST_WAIT_BARS        = 3;
            public const double SMC_IB_RETEST_BUFFER_ATR     = 0.1;
            public const int    SMC_IB_BASE_SCORE            = 52;

            // ── VWAP Fading ──
            public const double VWAP_REVERSION_SD_THRESHOLD = 1.8;
            public const double VWAP_RECLAIM_BUFFER_PCT    = 0.15;

            // ── ADX / Trend ──
            public const double ADX_MIN_TREND  = 25.0;
            public const int    ADX_PERIOD     = 14;

            // ── ORB (Opening Range) ──
            public const int    ORB_MINUTES            = 30;
            public const double ORB_BREAKOUT_BUFFER    = 1.5;
            public const double ORB_RETEST_ATR_FRAC    = 0.30;
            public const double ORB_RELOAD_BUFFER_ATR  = 0.25;
            public const double ORB_MIN_STOP_TICKS     = 100.0;
            public const double ORB_STOP_ATR_MULT      = 1.0;
            public const int    ORB_BASE_SCORE         = 85;

            // ── ORB Measure (Instrumentation) ──
            public const int    OM_OBSERVATION_WINDOW  = 60;
            public const int    OM_POST_TOUCH_WINDOW   = 12;
            public const double OM_RELOAD_BUFFER_ATR   = 0.25;

            // ── Failed Auction ──
            public const int    FA_LOOKBACK          = 10;
            public const double FA_WICK_BODY_RATIO   = 1.5;
            public const int    FA_MAX_AGE_BARS      = 100;
            public const int    FA_COOLDOWN_BARS     = 5;
            public const double FA_RETURN_TOLERANCE  = 0.3;
            public const int    FA_BASE_SCORE        = 62;
            public const int    FA_BONUS_DELTA       = 5;
            public const int    FA_BONUS_ABSORPTION  = 5;

            // ── Footprint Assembler ──
            public const double FA_MIN_FEEDER_COVERAGE = 0.80;

            // ── Footprint Core ──
            public const double FC_DEFAULT_ABSORPTION_RATIO = 2.0;
            public const double FC_DEFAULT_IMBALANCE_RATIO  = 3.0;
            public const int    FC_DEFAULT_MIN_STACKED     = 3;
            public const int    FC_DEFAULT_HISTORY_CAP     = 5;
            public const int    FC_DEFAULT_MAX_LEVELS      = 128;

            public const double FC_EXH_LOW_VOL_RATIO       = 0.5;
            public const int    FC_EXH_MIN_LEVELS          = 4;

            public const double FC_ICE_CURR_ABS_RATIO      = 2.0;
            public const double FC_ICE_PRIOR_FLOOR_RATIO   = 1.0;
            public const int    FC_ICE_MIN_RECURRENCES     = 2;
            public const int    FC_ICE_LOOKBACK_BARS       = 2;

            // ── Footprint Divergence ──
            public const int    FD_PIVOT_K             = 3;
            public const int    FD_FLAG_LIFETIME_BARS  = 5;
            public const double FD_MIN_SWING_ATR_MULT  = 0.5;

            // ── Footprint Entry Advisor ──
            public const double EA_MIN_CONVICTION_DELTA_PCT     = 0.18;
            public const double EA_VOLUME_DOMINANCE_RATIO       = 1.30;
            public const double EA_HARD_VETO_DELTA_PCT          = 0.15;
            public const double EA_HARD_VETO_VOL_DOM_RATIO      = 1.50;
            public const double EA_HARD_VETO_EXTREME_DELTA      = 25.0;
            public const double EA_HARD_VETO_CUM_DELTA_SLOPE    = 50.0;
            public const double EA_ABSORPTION_STRONG_THRESHOLD  = 50.0;
            public const double EA_ABSORPTION_MODERATE_THRESHOLD = 30.0;
            public const int    EA_MIN_STACKED_LEVELS_SUPPORT   = 3;
            public const int    EA_DENY_BELOW_SCORE             = 35;
            public const int    EA_STRONG_APPROVE_SCORE         = 65;
            public const double EA_WEAK_APPROVE_MULTIPLIER      = 0.90;
            public const double EA_STRONG_APPROVE_MULTIPLIER    = 1.10;
            public const double EA_WEAK_DENY_MULTIPLIER         = 0.75;
            public const int    EA_SMF_NATIVE_SCORE_BIAS        = -5;
            public const int    EA_SMC_BOS_SCORE_BIAS           = 5;
            public const double EA_SMF_NATIVE_MULT_BIAS         = 0.00;
            public const double EA_SMC_BOS_MULT_BIAS            = 0.00;

            // ── Footprint Trade Advisor ──
            public const double TA_MIN_CONVICTION_DELTA_PCT     = 0.10;
            public const double TA_VOLUME_DOMINANCE_RATIO       = 1.30;
            public const double TA_HARD_EXIT_DELTA_PCT          = 0.18;
            public const double TA_HARD_EXIT_VOL_DOM_RATIO      = 1.60;
            public const double TA_HARD_EXIT_EXTREME_DELTA      = 25.0;
            public const double TA_TIGHTEN_CUM_DELTA_SLOPE      = 25.0;
            public const double TA_HARD_EXIT_CUM_DELTA_SLOPE    = 50.0;
            public const double TA_ABSORPTION_STRONG_THRESHOLD  = 50.0;
            public const double TA_ABSORPTION_MODERATE_THRESHOLD = 30.0;
            public const int    TA_MIN_STACKED_LEVELS_CONCERN   = 3;
            public const int    TA_PRE_T1_TIGHTEN_SCORE         = 40;
            public const int    TA_PRE_T1_EXIT_SCORE            = 70;
            public const int    TA_POST_T1_TIGHTEN_SCORE        = 50;
            public const int    TA_POST_T1_EXIT_SCORE           = 80;
            public const double TA_MIN_PROFIT_TICKS_EXIT_EARLY  = 4.0;
            public const double TA_TIGHTEN_FACTOR               = 1.20;
            public const double TA_STRONG_TIGHTEN_FACTOR        = 1.35;
            public const int    TA_SMF_NATIVE_SCORE_BIAS        = -5;
            public const int    TA_BOS_SCORE_BIAS               = 0;
            public const int    TA_RETEST_SCORE_BIAS            = 5;

            // ── Signal Generator ──
            public const double SG_THIN_MARKET_RATIO   = 0.40;
            public const int    SG_ATR_PERIOD          = 14;

            // ── Forward Return Tracker ──
            public const int    FR_BAR_WINDOW          = 5;

            // ── Fvg Zone Registry ──
            public const int    FVG_MAX_ZONES          = 4;
            public const double FVG_MIN_GAP_ATR        = 0.15;

            // ── Host Strategy Pipeline ──
            public const int    HS_VOLUMETRIC_BAR_INDEX    = 6;
            public const bool   HS_LOG_FOOTPRINT_PIPELINE   = true;
            public const int    HS_AVG_TRADES_PERIOD       = 20;
            public const int    HS_CVD_DIVERGENCE_PERIOD   = 10;
            public const int    HS_EMA50_PERIOD            = 50;

            public const int    HS_ENTRY_BLOCK_START_HR    = 15;
            public const int    HS_ENTRY_BLOCK_START_MIN   = 45;
            public const int    HS_ENTRY_BLOCK_END_HR      = 18;
            public const int    HS_ENTRY_BLOCK_END_MIN     = 0;

            public const int    HS_UI_ZONE_BUFFER_SIZE     = 120;
            public const int    HS_FILTERED_CANDIDATE_CAP  = 16;

            // ── Sweep Detector ──
            public const long   SW_WINDOW_MS           = 200;
            public const int    SW_SWEEP_LEVELS        = 3;
            public const int    SW_RING_CAPACITY       = 512;

            // ── Tape Iceberg Detector ──
            public const long   TI_WINDOW_MS           = 5000;
            public const int    TI_ICEBERG_MIN_HITS    = 8;
            public const int    TI_SLOT_CAPACITY       = 16;

            // ── Tape Recorder ──
            public const int    TR_DEFAULT_CAPACITY    = 45000;
            public const long   TR_DEFAULT_WINDOW_MS   = 30000;

            // ── Trapped Traders Detector ──
            public const double TT_CLUSTER_RATIO       = 2.0;
            public const double TT_REJECTION_FRAC      = 0.5;

            // ── Velocity Detector ──
            public const long   VD_WINDOW_MS           = 1000;
            public const long   VD_SAMPLE_INTERVAL_MS  = 100;
            public const double VD_EMA_ALPHA           = 0.05;
            public const double VD_SPIKE_MULTIPLIER    = 3.0;
            public const double VD_EMA_MIN             = 1.0;
            public const int    VD_RING_CAPACITY       = 2000;

            // ── Volume Profile Processor ──
            public const double VP_VALUE_AREA_COVERAGE = 0.70;
            public const int    VP_INITIAL_CAPACITY    = 512;

            // ── VWAP RTH Reclaim ──
            public const int    VW_REENTRY_COOLDOWN    = 10;
            public const double VW_STOP_BUFFER_TICKS   = 2.0;
            public const double VW_T1_ATR_DIST         = 1.5;
            public const double VW_T2_ATR_DIST         = 3.0;
            public const int    VW_BASE_SCORE          = 65;

            // ── Wyckoff Signals ──
            public const int    WY_COOLDOWN_BARS       = 10;
            public const int    WY_BASE_SCORE          = 65;
            public const int    WY_BONUS_ABSORPTION    = 10;
            public const double WY_STOP_BUFFER_TICKS   = 2.0;
            public const double WY_T1_ATR_DIST         = 1.5;
            public const double WY_T2_ATR_DIST         = 3.0;

            // ── Structural Labeler ──
            public const int    SL_FRACTAL_N           = 2;
            public const int    SL_MAX_SWINGS          = 8;

            // ── Support Resistance Engine ──
            public const int    SRE_FACT_BUFFER_CAPACITY    = 64;
            public const int    SRE_PROFILE_DICT_CAPACITY   = 512;
            public const int    SRE_SWING_STRENGTH          = 2;
            public const int    SRE_ROUND_HALF_RANGE        = 5;

            // ── Strategy Engine ──
            public const int    SE_REENTRY_SUPPRESSION_BARS = 5;
            public const int    SE_MAX_SETS                 = 32;

            // ── Signal Ranking Engine ──
            public const int    RE_MAX_CANDIDATES          = 32;

            // ── Iceberg Absorption ──
            public const double ICE_MIN_ABSORPTION_SCORE   = 7.0;
            public const double ICE_HIGH_ABSORPTION_SCORE  = 12.0;
            public const double ICE_ALT_ICE_ABS_SCORE      = 9.0;
            public const double ICE_ALT_ICE_CLOSE_PCT      = 0.6;
            public const double ICE_ATR_STOP_BUFFER        = 0.06;
            public const double ICE_MIN_STOP_ATR_MULT      = 0.18;
            public const int    ICE_STOP_LOOKBACK_BARS     = 2;
            public const int    ICE_REENTRY_COOLDOWN       = 10;
            public const double ICE_MIN_T1_ATR_DIST        = 0.5;
            public const double ICE_MAX_T1_ATR_DIST        = 3.0;

            public const int    ICE_BASE_SCORE             = 76;
            public const int    ICE_BONUS_DUAL_ICE         = 5;
            public const int    ICE_PENALTY_ALT_ICE        = 2;
            public const int    ICE_BONUS_TRAPPED          = 5;
            public const int    ICE_BONUS_DIVERGENCE       = 5;
            public const int    ICE_BONUS_DUAL_EXH         = 4;
            public const int    ICE_BONUS_HIGH_ABS         = 3;
            public const int    ICE_BONUS_ZONE_OVERLAP     = 3;
            public const int    ICE_BONUS_UNFINISHED       = 3;
            public const int    ICE_SCORE_CAP              = 92;

            // ── Imbalance Zone Registry ──
            public const int    IZ_MAX_BULL_ZONES      = 50;
            public const int    IZ_MAX_BEAR_ZONES      = 50;
            public const int    IZ_MAX_ZONE_AGE_BARS   = 100;
            public const double IZ_IMBAL_RATIO         = 3.0;
            public const int    IZ_MIN_STACKED_LEVELS  = 3;
            public const double IZ_PROXIMITY_TICKS     = 4.0;

            // ── Hybrid Scalp ──
            public const int    HYBRID_WINDOW_BARS           = 10;
            public const double HYBRID_MIN_ABSORP            = 20.0;
            public const double HYBRID_ATR_STOP_BUFFER       = 0.06;
            public const double HYBRID_MIN_STOP_ATR_MULT     = 0.18;
            public const int    HYBRID_STOP_LOOKBACK_BARS    = 3;
            public const int    HYBRID_REENTRY_COOLDOWN      = 10;
            public const double HYBRID_MIN_T1_ATR_DIST       = 0.5;
            public const double HYBRID_MAX_T1_ATR_DIST       = 4.0;
            public const double HYBRID_H4_BLOCK_THRESHOLD    = -0.5;
            
            public const int    HYBRID_BASE_SCORE            = 80;
            public const int    HYBRID_PENALTY_LOW_ABS       = 5;
            public const int    HYBRID_PENALTY_WEAK_CLOSE    = 8;
            public const int    HYBRID_PENALTY_LOW_RR        = 10;
            
            public const int    HYBRID_BONUS_DIVERGENCE      = 10;
            public const int    HYBRID_BONUS_IMBAL_ZONE      = 8;
            public const int    HYBRID_BONUS_TRAPPED         = 5;
            public const int    HYBRID_BONUS_CHOCH           = 4;
            public const int    HYBRID_BONUS_TICE            = 3;
            public const int    HYBRID_BONUS_EXHAUSTION      = 3;
            public const int    HYBRID_SCORE_CAP             = 95;

            // ── Cooldowns (Bars) ──
            public const int COOLDOWN_SMC   = 5;
            public const int COOLDOWN_EMA   = 8;
            public const int COOLDOWN_ADX   = 10;
            public const int COOLDOWN_ORB   = 5;
        }

        // =========================================================================
        // 6. RISK & POLICY
        // =========================================================================
        public static class Policy
        {
            // ── Sizing & Modes ──
            public const bool   USE_FIXED_SIZE_ONE           = true;
            public const bool   TRADE_ADVISOR_COMPARE_ONLY   = true;

            // ── Signal Classification Defaults (Magic Numbers) ──
            public const double T1_FIXED_TICKS               = 15.0;
            public const double MIN_STOP_ATR_MULT_INST       = 1.5;

            // ── Trail Veto Thresholds ──
            public const double VETO_CONVICTION_PRE_T1       = 0.08;
            public const double VETO_CONVICTION_POST_T1      = 0.05;
            public const double VETO_VOL_RATIO               = 0.60;
            public const double VETO_MAX_RANGE_ATR           = 0.30;
            public const int    MAX_CONSECUTIVE_VETOES       = 2;
            public const double MAX_VETO_DRAWDOWN_PCT        = 0.50;
            public const double DSL_DSH_OVERRIDE             = 50.0;

            // ── Dynamic Exit Triggers ──
            public const double CVD_SLOPE_BE_THRESHOLD       = 100.0;
            public const double EXIT_CONVICTION              = 0.20;
            public const double MIN_PROFIT_EXIT_TICKS        = 8.0;
            public const double EXIT_LEVEL_PROXIMITY_ATR     = 0.40;

            // ── Set-Specific: BE Arm ATR Fraction ──
            public const double BE_ARM_RETEST                = 0.15;
            public const double BE_ARM_BOS                   = 0.20;
            public const double BE_ARM_BAND_RECLAIM          = 0.20;
            public const double BE_ARM_IMPULSE               = 0.30;
            public const double BE_ARM_DEFAULT               = 0.20;
            public const double BE_ARM_FLOOR_TICKS           = 4.0;

            // ── Set-Specific: MFE Lock Start ATR Fraction ──
            public const double MFE_LOCK_START_RETEST        = 0.40;
            public const double MFE_LOCK_START_BOS           = 0.50;
            public const double MFE_LOCK_START_BAND_RECLAIM  = 0.55;
            public const double MFE_LOCK_START_IMPULSE       = 0.70;
            public const double MFE_LOCK_START_DEFAULT       = 0.50;

            // ── Set-Specific: MFE Lock Percentage ──
            public const double MFE_LOCK_PCT_RETEST          = 0.45;
            public const double MFE_LOCK_PCT_BOS             = 0.35;
            public const double MFE_LOCK_PCT_BAND_RECLAIM    = 0.35;
            public const double MFE_LOCK_PCT_IMPULSE         = 0.25;
            public const double MFE_LOCK_PCT_DEFAULT         = 0.30;

            // ── Set-Specific: T1 Partial Percentage ──
            public const double T1_PARTIAL_BOS               = 0.70;
            public const double T1_PARTIAL_RETEST            = 0.70;
            public const double T1_PARTIAL_BAND_RECLAIM      = 0.60;
            public const double T1_PARTIAL_IMPULSE           = 0.50;
            public const double T1_PARTIAL_DEFAULT           = 0.50;

            // ── Global Policy Constants ──
            public const int SCORE_AGGRESSIVE_ENTRY = 80;
            public const int SCORE_GRADE_A_PLUS     = 85;
            public const int SCORE_GRADE_A          = 75;
            public const int SCORE_GRADE_B          = 65;
            public const int SCORE_REJECT           = 60;

            public const double RISK_PCT_DEFAULT     = 0.01;
            public const int    MAX_CONTRACTS        = 5;
            public const double MAX_DAILY_LOSS       = 500.0;
            public const double MIN_RR_RATIO         = 1.5;
            public const int    MAX_CONSECUTIVE_LOSS = 5;
            public const double MIN_STOP_TICKS       = 4.0;
            public const int    LIMIT_FALLBACK_BARS      = 3;
            public const double T1_PARTIAL_PCT           = 0.5;
            public const int    PERF_MIN_LIFETIME_TRADES = 10;
        }
    }
}
