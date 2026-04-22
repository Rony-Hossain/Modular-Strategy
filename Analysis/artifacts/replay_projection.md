# Proposed Config Replay Projection

## Scenario Definitions
- **Baseline**: Current backtest results
- **S1_SrcFilter**: Disable high-drag sources (SMF_Impulse, ADX_TrendSignal, VWAP_Reclaim, EMA_CrossSignal)
- **S2_SlipCap**: S1 + Skip trades with >5t entry slippage
- **S3_RTH_Only**: S2 + Limit entries to 09:30-15:30 ET

## Comparison Table

| Metric | Baseline | S1_SrcFilter | S2_SlipCap | S3_RTH_Only | S3_Delta |
| --- | --- | --- | --- | --- | --- |
| Trades | 794 | 539 | 539 | 68 | -726 |
| Win % | 50.9% | 52.9% | 52.9% | 60.3% | 9.4% |
| PF | 1.04 | 1.21 | 1.21 | 1.68 | 0.64 |
| Gross PnL $ | 1,029.50 | 4,013.00 | 4,013.00 | 1,095.50 | 66.00 |
| Slippage $ | 4,060.77 | 2,637.10 | 2,637.10 | 359.59 | -3,701.18 |
| Comm $ | 2,382.00 | 1,617.00 | 1,617.00 | 204.00 | -2,178.00 |
| Net PnL $ | -5,413.27 | -241.10 | -241.10 | 531.91 | 5,945.18 |