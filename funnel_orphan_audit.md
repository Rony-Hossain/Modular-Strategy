# Funnel & Orphan Audit Report

## Section 1: Funnel Invariants
                    Source  Evals  Rank_Weak  Rank_Passed  Rejected  Accepted  Ordered  Filled  Resolved
            SMC_OrderBlock   5816          0          296       283        13       13    13.0      13.0
              VWAP_Reclaim    921          0           95        54        41       41    40.0      40.0
                Confluence    912          0          175        12       163      163   163.0     163.0
               PriceAction      0          0            0         0         0        0     0.0       0.0
           VWAP_RTH_v1:REJ      0          0            0         0         0        0     0.0       0.0
             FailedAuction    731          0           39         0        39       39    39.0      39.0
           EMA_CrossSignal   1648          0           95        82        13       13    13.0      13.0
               SMF_Impulse   1817          0          148         0       148      148   148.0     148.0
                SMF_Retest   4209          0          101         0       101      101   101.0     101.0
           ADX_TrendSignal    530          0           54         0        54       54    54.0      54.0
            SMC_FVG_v1:REJ      0          0            0         0         0        0     0.0       0.0
                   SMC_BOS    213          0           11        11         0        0     0.0       0.0
            HybridScalp_v1      0          0            0         0         0        0     0.0       0.0
      SMF_Native_Retest_v1      0          0            0         0         0        0     0.0       0.0
     SMF_Native_Impulse_v1      0          0            0         0         0        0     0.0       0.0
          EMA_Cross_v1:REJ      0          0            0         0         0        0     0.0       0.0
                ORB_Retest    786          0          210         0       210      210   210.0     210.0
                 SMC_CHoCH     48          0            0         0         0        0     0.0       0.0
           SMF_BandReclaim     19          0            0         0         0        0     0.0       0.0
             OrderFlow_Abs    193          0           12         0        12       12    12.0      12.0
             SMC_IB_Retest    103          0            2         2         0        0     0.0       0.0
              EMA_Cross_v1      0          0            0         0         0        0     0.0       0.0
              ORB_Value_v2      0          0            0         0         0        0     0.0       0.0
           OrderFlow_Delta     22          0            1         0         1        1     1.0       1.0
      IcebergAbsorption_v1      0          0            0         0         0        0     0.0       0.0
               VWAP_RTH_v1      0          0            0         0         0        0     0.0       0.0
          FailedAuction_v1      0          0            0         0         0        0     0.0       0.0
OrderFlow_StackedImbalance     11          0            0         0         0        0     0.0       0.0
                SMC_FVG_v1      0          0            0         0         0        0     0.0       0.0
        HybridScalp_v1:REJ      0          0            0         0         0        0     0.0       0.0
              ADX_Trend_v1      0          0            0         0         0        0     0.0       0.0
        SMF_Full_Retest_v1      0          0            0         0         0        0     0.0       0.0
        DeltaDivergence_v1      0          0            0         0         0        0     0.0       0.0
       SMC_LiqSweep_v1:REJ      0          0            0         0         0        0     0.0       0.0
             SMC_IB_v1:REJ      0          0            0         0         0        0     0.0       0.0

All invariants passed.

## Section 2: Orphans
- EVAL_orphans: 16740
- SIGNAL_ACCEPTED_orphans: 795
- ORDER_LMT_orphans: 1
- STOP_MOVE_orphans: 0
- T_HIT_orphans: 0
- TA_DECISION_orphans: 0
- TRADE_RESET_orphans: 0

## Section 5: RANK_WEAK consistency
- Net mismatches: 559
BLOCKER: Weight optimization (Phase 13b) cannot proceed until Net calculation is fixed in StrategyLogger.cs.

## Logging gaps to close
- EVAL orphans detected. Check `StrategyEngine.cs` for silent filter stages.
- RANK_WEAK Net mismatch. Fix arithmetic in `StrategyLogger.cs`.
