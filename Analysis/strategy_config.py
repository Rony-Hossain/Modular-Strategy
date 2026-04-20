"""
strategy_config.py  —  Python mirror of ModularStrategy/StrategyConfig.cs
Single source of truth for all weights, thresholds and module constants.

The optimizer writes back to this file when it finds a better configuration.
To apply changes to NinjaTrader: copy the updated values into StrategyConfig.cs.
"""

# ─────────────────────────────────────────────────────────────────────────────
# INSTRUMENTS
# ─────────────────────────────────────────────────────────────────────────────
INSTRUMENTS = {
    "MNQ": dict(tick_size=0.25, tick_value=0.50,  point_value=2.0,  round_interval=100.0),
    "NQ":  dict(tick_size=0.25, tick_value=5.00,  point_value=20.0, round_interval=100.0),
    "ES":  dict(tick_size=0.25, tick_value=12.50, point_value=50.0, round_interval=25.0),
    "MES": dict(tick_size=0.25, tick_value=1.25,  point_value=5.0,  round_interval=25.0),
}

# ─────────────────────────────────────────────────────────────────────────────
# CONFLUENCE ENGINE WEIGHTS
# ─────────────────────────────────────────────────────────────────────────────
CONFLUENCE = dict(
    # Layer A: MTFA Macro Bias (0-30)
    LAYER_A_H4=14,
    LAYER_A_H2=10,
    LAYER_A_H1=6,

    # Layer B: S/R Level Context (capped at 40)
    SR_AT_FAVORABLE=18,
    SR_NEAR_FAV_CLOSE=12,
    SR_NEAR_FAV_MID=8,
    SR_NEAR_FAV_FAR=4,
    SR_STRONG_FAV_S3=8,
    SR_STRONG_FAV_S2=5,
    SR_STRONG_FAV_S1=3,
    SR_STACKED_BONUS=6,
    SR_NEAR_ADV_CLOSE=10,
    SR_NEAR_ADV_MID=6,
    SR_NEAR_ADV_FAR=3,
    LAYER_B_MAX_CAP=40,

    # Layer C: OrderFlow Conviction (capped at 30)
    LAYER_C_DIVERGENCE=15,
    LAYER_C_TRAPPED_AGREE=8,
    LAYER_C_ICEBERG_AGREE=8,
    LAYER_C_EXHAUSTION_AGREE=8,
    LAYER_C_UNFINISHED_AGREE=4,
    LAYER_C_DELTA_SL=8,
    LAYER_C_ABS_MAX=7,
    LAYER_C_DELTA_EXHST=8,
    LAYER_C_IMBAL_ZONE=6,
    PROXY_BAR_DELTA=7,
    PROXY_REGIME=6,
    PROXY_VWAP_SIDE=5,
    PROXY_H1_BAR_DIR=4,
    LAYER_C_MAX_CAP=30,

    # Layer D: Price Action Structure (0-15)
    LAYER_D_FULL_STRUCT=12,
    LAYER_D_TREND_ONLY=8,

    # Fair value
    PENALTY_ABOVE_FAIR=15,
    BONUS_DEEP_DISCOUNT=10,
)

# ─────────────────────────────────────────────────────────────────────────────
# PENALTIES
# ─────────────────────────────────────────────────────────────────────────────
PENALTIES = dict(
    PENALTY_H4=8,
    PENALTY_H2=5,
    PENALTY_BOTH_EXTRA=5,
    RANK_CONFLICT=15,
)

# ─────────────────────────────────────────────────────────────────────────────
# VETO THRESHOLDS
# ─────────────────────────────────────────────────────────────────────────────
VETOES = dict(
    CUMDELTA_EXHAUSTED=2500.0,
    WEAK_STACK_COUNT=3.0,
    BRICK_WALL_ATR=0.20,
)

# ─────────────────────────────────────────────────────────────────────────────
# SCORE FLOORS
# ─────────────────────────────────────────────────────────────────────────────
FLOORS = dict(
    MIN_NET_VOLUMETRIC=40,
    MIN_NET_STRUCTURE=25,
    BOS_FLOOR_VOLUMETRIC=20,
    BOS_FLOOR_STRUCTURE=10,
    BOSWAVE_FLOOR=0,
    LONG_H4_BEARISH_FLOOR=60,
    LONG_H4_NEUTRAL_FLOOR=70,
)

# ─────────────────────────────────────────────────────────────────────────────
# MODULE CONSTANTS
# ─────────────────────────────────────────────────────────────────────────────
MODULES = dict(
    # DataFeed
    DF_ATR_PERIOD=14,
    DF_ORB_BARS=30,
    DF_VWAP_SD2_MULT=2.0,
    SNAPSHOT_DEPTH=100,
    BARS_REQUIRED_TO_TRADE=50,

    # BigPrint
    BP_VOL_RING_SIZE=2000,
    BP_RECOMPUTE_INTERVAL=200,
    BP_PERCENTILE=0.95,
    BP_RING_SIZE=500,
    BP_WINDOW_MS=30000,

    # Delta Divergence
    DD_MIN_ABSORPTION_SCORE=5.0,
    DD_ATR_STOP_BUFFER=0.08,
    DD_MIN_STOP_ATR_MULT=0.20,
    DD_STOP_LOOKBACK_BARS=3,
    DD_COOLDOWN_BARS=8,
    DD_BASE_SCORE=78,

    # EMA Cross
    EMA_FAST_PERIOD=9,
    EMA_SLOW_PERIOD=21,
    EMA_TREND_PERIOD=200,
    EMA_BASE_SCORE=60,
    EMA_PENALTY_REGIME=12,
    EMA_PENALTY_VWAP=10,

    # SMC
    SMC_SWING_STRENGTH=3,
    SMC_MIN_BOS_TICKS=2.0,
    SMC_MIN_CHOCH_STRENGTH=0.30,
    SMC_OB_LOOKBACK=5,
    SMC_OB_INV_BUFFER=2.0,
    SMC_COOLDOWN_BARS=5,
    SMC_STOP_BUFFER_TICKS=2.0,
    SMC_FVG_MAX_AGE_BARS=200,
    SMC_FVG_BASE_SCORE=55,
    SMC_FVG_BONUS_SIZE=5,
    SMC_FVG_PENALTY_WEAK_CLOSE=8,
    SMC_LIQ_PIVOT_COOLDOWN=5,
    SMC_LIQ_MAX_LEVEL_DIFF_ATR=0.3,
    SMC_LIQ_MAX_AGE_BARS=100,
    SMC_LIQ_BASE_SCORE=60,
    SMC_SESS_BASE_SCORE=58,
    SMC_SESS_PENALTY_WEAK_CLOSE=8,
    SMC_IB_MINUTES=60,
    SMC_IB_POST_WAIT_BARS=3,
    SMC_IB_RETEST_BUFFER_ATR=0.1,
    SMC_IB_BASE_SCORE=52,

    # VWAP
    VWAP_REVERSION_SD_THRESHOLD=1.8,
    VWAP_RECLAIM_BUFFER_PCT=0.15,

    # ADX
    ADX_MIN_TREND=25.0,
    ADX_PERIOD=14,

    # ORB
    ORB_MINUTES=30,
    ORB_BREAKOUT_BUFFER=1.5,
    ORB_RETEST_ATR_FRAC=0.30,
    ORB_RELOAD_BUFFER_ATR=0.25,
    ORB_MIN_STOP_TICKS=100.0,
    ORB_STOP_ATR_MULT=1.0,
    ORB_BASE_SCORE=85,

    # Failed Auction
    FA_LOOKBACK=10,
    FA_WICK_BODY_RATIO=1.5,
    FA_MAX_AGE_BARS=100,
    FA_COOLDOWN_BARS=5,
    FA_RETURN_TOLERANCE=0.3,
    FA_BASE_SCORE=62,
    FA_BONUS_DELTA=5,
    FA_BONUS_ABSORPTION=5,

    # Iceberg Absorption
    ICE_MIN_ABSORPTION_SCORE=7.0,
    ICE_HIGH_ABSORPTION_SCORE=12.0,
    ICE_BASE_SCORE=76,
    ICE_BONUS_DUAL_ICE=5,
    ICE_BONUS_TRAPPED=5,
    ICE_BONUS_DIVERGENCE=5,
    ICE_SCORE_CAP=92,

    # Hybrid Scalp
    HYBRID_BASE_SCORE=80,
    HYBRID_PENALTY_LOW_ABS=5,
    HYBRID_PENALTY_WEAK_CLOSE=8,
    HYBRID_SCORE_CAP=95,

    # Cooldowns
    COOLDOWN_SMC=5,
    COOLDOWN_EMA=8,
    COOLDOWN_ADX=10,
    COOLDOWN_ORB=5,
)

# ─────────────────────────────────────────────────────────────────────────────
# RISK & POLICY
# ─────────────────────────────────────────────────────────────────────────────
POLICY = dict(
    # Grade thresholds
    SCORE_REJECT=60,
    SCORE_GRADE_B=65,
    SCORE_GRADE_A=75,
    SCORE_GRADE_A_PLUS=85,
    SCORE_AGGRESSIVE_ENTRY=80,

    # Risk sizing
    RISK_PCT_DEFAULT=0.01,
    MAX_CONTRACTS=5,
    MAX_DAILY_LOSS=500.0,
    MIN_RR_RATIO=1.5,
    MAX_CONSECUTIVE_LOSS=5,
    MIN_STOP_TICKS=4.0,
    LIMIT_FALLBACK_BARS=3,
    T1_PARTIAL_PCT=0.5,

    # Trail/exit
    VETO_CONVICTION_PRE_T1=0.08,
    VETO_CONVICTION_POST_T1=0.05,
    CVD_SLOPE_BE_THRESHOLD=100.0,
    EXIT_CONVICTION=0.20,
    MIN_PROFIT_EXIT_TICKS=8.0,

    # MFE/BE locks by set type
    BE_ARM_RETEST=0.15,
    BE_ARM_BOS=0.20,
    BE_ARM_IMPULSE=0.30,
    BE_ARM_DEFAULT=0.20,
    MFE_LOCK_START_RETEST=0.40,
    MFE_LOCK_START_BOS=0.50,
    MFE_LOCK_START_IMPULSE=0.70,
    MFE_LOCK_PCT_RETEST=0.45,
    MFE_LOCK_PCT_BOS=0.35,
    MFE_LOCK_PCT_IMPULSE=0.25,
)

# ─────────────────────────────────────────────────────────────────────────────
# SIGNAL COOLDOWN DEFAULTS
# ─────────────────────────────────────────────────────────────────────────────
DEFAULTS = dict(
    SIGNAL_COOLDOWN_EMA=8,
    SIGNAL_COOLDOWN_ADX=10,
    SIGNAL_COOLDOWN_ORB=5,
)

# ─────────────────────────────────────────────────────────────────────────────
# OPTIMIZED OVERRIDES  (written by optimizer.py — do not edit manually)
# Keys here override the base values above when loaded.
# ─────────────────────────────────────────────────────────────────────────────
OPTIMIZED = {
    "SCORE_REJECT": 80,
    "REQUIRE_H4_ALIGNED": true,
    "PER_SOURCE_THRESHOLDS": {
        "VWAP_Reclaim": 72,
        "SMC_OrderBlock": 52,
        "SMF_Retest": 91,
        "Confluence": 87,
        "SMF_Impulse": 70,
        "EMA_CrossSignal": 61,
        "ORB_Value_v2": 94,
        "ORB_Retest": 58,
        "OrderFlow_Abs": 88,
        "FailedAuction": 80,
        "ADX_TrendSignal": 83,
        "OrderFlow_Delta": 53,
        "SMC_BOS": 90,
        "SMC_IB_Retest": 51
    },
    "OPT_TIMESTAMP": "2026-04-20T01:21:18",
    "LAYER_A_H4": 19,
    "LAYER_A_H2": 13,
    "LAYER_A_H1": 4,
    "PENALTY_H4": 9,
    "PENALTY_H2": 2,
    "PENALTY_BOTH_EXTRA": 8,
    "PROXY_BAR_DELTA": 12,
    "PROXY_REGIME": 7,
    "LAYER_C_DIVERGENCE": 20,
    "LAYER_C_ABS_MAX": 11,
    "LAYER_D_FULL_STRUCT": 17,
    "LAYER_D_TREND_ONLY": 11
}
