# ML Weight Optimization Report

**Date:** 2026-04-28 13:40
**CV ROC-AUC:** 0.5024 ± 0.0771

## Feature Importance (LightGBM)

| Rank | Feature | Importance % |
| --- | --- | --- |
| 1 | cd | 15.0% |
| 2 | skew | 14.8% |
| 3 | bd | 14.4% |
| 4 | vah | 8.9% |
| 5 | val | 7.2% |
| 6 | layer_c | 6.7% |
| 7 | poc | 5.4% |
| 8 | dsh | 5.3% |
| 9 | layer_b | 5.3% |
| 10 | dsl | 5.2% |
| 11 | layer_a | 4.7% |
| 12 | source_enc | 3.2% |
| 13 | layer_d | 2.2% |
| 14 | penalty | 1.6% |
| 15 | bonus | 0.0% |
| 16 | ctx_h4_enc | 0.0% |
| 17 | ctx_h2_enc | 0.0% |
| 18 | direction_enc | 0.0% |
| 19 | regime | 0.0% |
| 20 | ctx_sw | 0.0% |
| 21 | ctx_smf_enc | 0.0% |
| 22 | ctx_h1_enc | 0.0% |
| 23 | ctx_str | 0.0% |
| 24 | pp | 0.0% |

## SHAP Values (mean |SHAP| per feature)

| Feature | mean |SHAP| | mean SHAP (direction) |
| --- | --- | --- |
| skew | 0.3264 | -0.0040 |
| bd | 0.2942 | +0.0079 |
| cd | 0.2696 | +0.0024 |
| layer_a | 0.2276 | -0.0011 |
| layer_c | 0.2164 | -0.0050 |
| val | 0.2162 | +0.0007 |
| layer_b | 0.1915 | -0.0021 |
| poc | 0.1673 | -0.0047 |
| vah | 0.1600 | +0.0007 |
| dsh | 0.1430 | -0.0010 |
| layer_d | 0.1042 | +0.0014 |
| source_enc | 0.1019 | +0.0071 |
| dsl | 0.1002 | -0.0022 |
| penalty | 0.0436 | -0.0007 |
| bonus | 0.0011 | +0.0003 |
| ctx_h4_enc | 0.0000 | 0.0000 |
| ctx_h2_enc | 0.0000 | 0.0000 |
| direction_enc | 0.0000 | 0.0000 |
| regime | 0.0000 | 0.0000 |
| ctx_sw | 0.0000 | 0.0000 |
| ctx_smf_enc | 0.0000 | 0.0000 |
| ctx_h1_enc | 0.0000 | 0.0000 |
| ctx_str | 0.0000 | 0.0000 |
| pp | 0.0000 | 0.0000 |

## Weight Proposals

| Config Key | Current | Proposed | Importance | SHAP | Reason |
| --- | --- | --- | --- | --- | --- |
| LAYER_A_H4 | 14 | 14 | 25.0% | -0.0011 | importance OK (25.0%), keeping |
| LAYER_A_H2 | 10 | 10 | 25.0% | -0.0011 | importance OK (25.0%), keeping |
| LAYER_A_H1 | 6 | 6 | 25.0% | -0.0011 | importance OK (25.0%), keeping |
| LAYER_B_MAX_CAP | 40 | 40 | 27.8% | -0.0021 | importance OK (27.8%), keeping |
| PROXY_BAR_DELTA | 7 | 9 | 35.5% | -0.0050 | high importance (35.5%), increasing **CHANGED** |
| PROXY_REGIME | 6 | 7 | 35.5% | -0.0050 | high importance (35.5%), increasing **CHANGED** |
| PROXY_VWAP_SIDE | 5 | 6 | 35.5% | -0.0050 | high importance (35.5%), increasing **CHANGED** |
| PROXY_H1_BAR_DIR | 4 | 5 | 35.5% | -0.0050 | high importance (35.5%), increasing **CHANGED** |
| LAYER_C_DIVERGENCE | 15 | 19 | 35.5% | -0.0050 | high importance (35.5%), increasing **CHANGED** |
| LAYER_D_FULL_STRUCT | 12 | 12 | 11.8% | 0.0014 | importance OK (11.8%), keeping |
| LAYER_D_TREND_ONLY | 8 | 8 | 11.8% | 0.0014 | importance OK (11.8%), keeping |

## How to Apply

1. Review proposed weights above
2. Update values in `ModularStrategy/StrategyConfig.cs` → `Confluence` class
3. Re-run backtest
4. Re-run analysis pipeline (scripts 23-28)
5. Compare grade calibration and source performance vs baseline

If CV AUC < 0.52, the model has weak predictive power — weights may not
improve results significantly. Focus on feature engineering or more data first.