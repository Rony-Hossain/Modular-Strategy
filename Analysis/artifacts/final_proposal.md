# Final Proposal

### 1. Recommended OPTIMIZED block
```json
{
  "SCORE_REJECT": 82,
  "SCORE_GRADE_B": 69,
  "SCORE_GRADE_A": 75,
  "SCORE_GRADE_A_PLUS": 86,
  "CUMDELTA_EXHAUSTED": 3500.0,
  "WEAK_STACK_COUNT": 3.0,
  "BRICK_WALL_ATR": 0.24000000000000002,
  "MIN_NET_VOLUMETRIC": 57,
  "MIN_NET_STRUCTURE": 40,
  "BOS_FLOOR_VOLUMETRIC": 32,
  "LONG_H4_BEARISH_FLOOR": 45,
  "MIN_RR_RATIO": 1.3,
  "MIN_STOP_TICKS": 9,
  "MAX_CONSECUTIVE_LOSS": 4,
  "REQUIRE_H4_ALIGNED": false,
  "PER_SOURCE_THRESHOLDS": {
    "VWAP_Reclaim": 84,
    "SMC_OrderBlock": 70,
    "Confluence": 44,
    "SMF_Retest": 84,
    "SMF_Impulse": 43,
    "EMA_CrossSignal": 76,
    "ORB_Retest": 86,
    "OrderFlow_Abs": 46,
    "FailedAuction": 59,
    "ADX_TrendSignal": 76,
    "OrderFlow_Delta": 73,
    "SMC_BOS": 84,
    "SMC_IB_Retest": 87
  },
  "OPT_STATUS": "PROPOSED",
  "BE_ARM_RETEST": 0.25,
  "BE_ARM_BOS": 0.3,
  "BE_ARM_IMPULSE": 0.35,
  "T1_PARTIAL_PCT": 0.5,
  "OPT_TIMESTAMP": "2026-04-27T19:17:16.666511"
}
```

### 2. Rollout checklist
- [ ] Run proposal on last 30 days of paper-trading data
- [ ] Verify replay match > 95%
- [ ] Hand off to dev for StrategyConfig.cs copy
