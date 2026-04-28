# Replay Baseline Validation

**Verdict:** FAIL (Max pct diff: 777.9%)

## Overall Metrics Comparison
                metric      actual       replay     abs_diff   pct_diff
             total_pnl 1029.500000  9038.280000  8008.780000 777.929092
              n_trades  794.000000   525.000000   269.000000  33.879093
              win_rate    0.508816     0.462857     0.045959   9.032532
          max_drawdown 2130.500000 14287.850000 12157.350000 570.633654
max_consecutive_losses    9.000000     5.000000     4.000000  44.444444

## Per-Source Comparison
         source  actual_n  actual_pnl  replay_n  replay_pnl
ADX_TrendSignal        54      -625.5      27.0    -4778.46
     Confluence       163       766.0      90.0    -2096.39
EMA_CrossSignal        13      -152.0       5.0       26.34
  FailedAuction        39      1023.0      26.0     2527.30
     ORB_Retest       210       811.5     193.0    18299.14
  OrderFlow_Abs        12       577.0       5.0     1287.22
OrderFlow_Delta         1       -30.0       0.0        0.00
 SMC_OrderBlock        13       301.5      10.0     -243.96
    SMF_Impulse       148     -2037.5      88.0    -4908.70
     SMF_Retest       101       564.0      63.0      746.76
   VWAP_Reclaim        40      -168.5      18.0    -1820.97

## Top 20 Mismatches (Total: 479)
                          signal_id          source  score  traded_actual  traded_replay      reject_reason
          ORB_Value_v2:20260106:673      ORB_Retest   85.0           True          False halted_daily_limit
          ORB_Value_v2:20260106:673      ORB_Retest   85.0           True          False halted_daily_limit
 SMF_Native_Impulse_v1:20260106:690     SMF_Impulse   65.0           True          False halted_daily_limit
        HybridScalp_v1:20260106:790      Confluence   69.0           True          False halted_daily_limit
  IcebergAbsorption_v1:20260106:807   OrderFlow_Abs   71.0           True          False halted_daily_limit
           VWAP_RTH_v1:20260106:828    VWAP_Reclaim   65.0           True          False halted_daily_limit
 SMF_Native_Impulse_v1:20260106:884     SMF_Impulse   65.0           True          False halted_daily_limit
          VWAP_RTH_v1:20260108:1380    VWAP_Reclaim   65.0           True          False halted_daily_limit
       HybridScalp_v1:20260108:1407      Confluence   64.0           True          False halted_daily_limit
       HybridScalp_v1:20260108:1448      Confluence   70.0           True          False halted_daily_limit
SMF_Native_Impulse_v1:20260109:1545     SMF_Impulse   65.0           True          False halted_daily_limit
 SMF_Native_Retest_v1:20260109:1554      SMF_Retest   63.0           True          False halted_daily_limit
         ORB_Value_v2:20260109:1564      ORB_Retest   85.0           True          False halted_daily_limit
         ORB_Value_v2:20260109:1564      ORB_Retest   85.0           True          False halted_daily_limit
         ADX_Trend_v1:20260109:1573 ADX_TrendSignal   72.0           True          False halted_daily_limit
 IcebergAbsorption_v1:20260109:1612   OrderFlow_Abs   71.0           True          False halted_daily_limit
SMF_Native_Impulse_v1:20260113:2060     SMF_Impulse   65.0           True          False halted_daily_limit
         ORB_Value_v2:20260113:2069      ORB_Retest   85.0           True          False halted_daily_limit
         ORB_Value_v2:20260113:2069      ORB_Retest   85.0           True          False halted_daily_limit
         ORB_Value_v2:20260113:2080      ORB_Retest   85.0           True          False halted_daily_limit
