#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// CONFLUENCE ENGINE — structured, layered edge scoring.
    ///
    /// Consumes the MarketSnapshot bag and returns a ConfluenceResult
    /// that SignalRankingEngine uses to re-weight each RawDecision score.
    ///
    /// Design contract:
    ///   - Stateless. Caller passes snapshot, gets result. No side effects.
    ///   - Layers are independent. A missing data source degrades that layer
    ///     to zero — it never blocks the entire score.
    ///   - Veto is binary. If the orderflow opposes direction, result.IsVetoed
    ///     = true regardless of score. SignalRankingEngine must honour this.
    ///   - ConfluenceResult is a VALUE TYPE (struct). Zero heap allocation
    ///     per bar. Safe to call inside the NT8 bar update loop.
    ///
    /// Layer budget (max 100 before penalty):
    ///   A — MTFA macro bias       : 0–30
    ///   B — Level Registry        : 0–40  (HTF swings + session + pivots + stacked bonus)
    ///   C — OrderFlow conviction  : 0–30
    ///       With Volumetric: CVD div, DeltaSl/Sh, Absorption, StackedImbalance, BarDelta
    ///       Without Volumetric: BarDelta proxy, SMF Regime, VWAP side, H1 direction
    ///   D — Price action trigger  : 0–15
    ///       CHoCH fired this bar → full reversal credit (bypasses stale label check)
    ///       Continuation → HH+HL / LH+LL label scoring
    ///   E — Macro penalty         : −18 max
    /// </summary>
    public static class ConfluenceEngine
    {
        // ── Layer A weights (MTFA) ────────────────────────────────────────
        private const int LAYER_A_H4 = StrategyConfig.Confluence.LAYER_A_H4;   // 4H — dominant macro anchor
        private const int LAYER_A_H2 = StrategyConfig.Confluence.LAYER_A_H2;   // 2H — intermediate filter
        private const int LAYER_A_H1 = StrategyConfig.Confluence.LAYER_A_H1;   // 1H — execution TF bias

        // Layer B weights are owned by LevelRegistry — see LevelRegistry.cs.
        // ConfluenceEngine reads the pre-computed FinalScore via LevelRegistry.Score().

        // ── Layer C weights (OrderFlow) ───────────────────────────────────
        private const int LAYER_C_DIVERGENCE = StrategyConfig.Confluence.LAYER_C_DIVERGENCE;  // CVD divergence confirms
        private const int LAYER_C_DELTA_SL = StrategyConfig.Confluence.LAYER_C_DELTA_SL;  // buying at bar low (DeltaSl)
        private const int LAYER_C_ABS_MAX = StrategyConfig.Confluence.LAYER_C_ABS_MAX;  // absorption — graduated below
        private const int LAYER_C_DELTA_EXHST = StrategyConfig.Confluence.LAYER_C_DELTA_EXHST;  // delta exhaustion confirms signal direction
        private const int LAYER_C_IMBAL_ZONE = StrategyConfig.Confluence.LAYER_C_IMBAL_ZONE;  // price at historical imbalance zone
        private const int LAYER_C_TRAPPED_AGREE = StrategyConfig.Confluence.LAYER_C_TRAPPED_AGREE;   // trapped flag on opposite side confirms direction
        private const int LAYER_C_ICEBERG_AGREE = StrategyConfig.Confluence.LAYER_C_ICEBERG_AGREE;   // iceberg flag on SAME side confirms direction
        private const int LAYER_C_EXHAUSTION_AGREE = StrategyConfig.Confluence.LAYER_C_EXHAUSTION_AGREE;   // opposite-side exhaustion confirms direction
        private const int LAYER_C_UNFINISHED_AGREE = StrategyConfig.Confluence.LAYER_C_UNFINISHED_AGREE;   // target-side magnet — lighter, no veto
        // Phase 3.7: same-side bonuses removed after autopsy showed they elevated low-quality longs.
        // Sweep-opposition veto retained below. Other tape flags read but not scored.

        // ── Layer D weights (Price action trigger) ────────────────────────
        private const int LAYER_D_FULL_STRUCT = StrategyConfig.Confluence.LAYER_D_FULL_STRUCT;  // HH+HL or LH+LL confirmed
        private const int LAYER_D_TREND_ONLY = StrategyConfig.Confluence.LAYER_D_TREND_ONLY;  // swing trend agrees only

        // ── Penalty weights ───────────────────────────────────────────────
        private const int PENALTY_H4 = StrategyConfig.Penalties.PENALTY_H4;
        private const int PENALTY_H2 = StrategyConfig.Penalties.PENALTY_H2;
        private const int PENALTY_BOTH_EXTRA = StrategyConfig.Penalties.PENALTY_BOTH_EXTRA;    // stacks when both oppose

        // Phase 3.9 (2026-04-16) REVERTED — sweep-chase penalty on same-side
        // sweep regressed net $2.4K via ranker cascade effects. The direct filter
        // removed 2 swp+ long losers as intended, but NetScore shifts on surviving
        // signals re-ordered competing candidates and pulled in worse trades.
        // Lesson: small-sample (25 trades) tape-based penalties can't be shipped
        // without cross-window validation. See phase3_10 research notes.

        // =================================================================
        public static ConfluenceResult Evaluate(bool isLong, MarketSnapshot snap,
            in SupportResistanceResult sr,
            string conditionSetId = "")
        {
            if (!snap.IsValid)
                return ConfluenceResult.Invalid();

            var    p        = snap.Primary;

            int layerA = 0, layerB = 0, layerC = 0, layerD = 0, penalty = 0;
            bool isVetoed = false;

            // BOSWave signals (SMF_Native_*) are flow-based, not structure-based.
            // They trade pullbacks and retests — by design they fire counter to
            // the HTF EMA trend. Applying the macro penalty kills valid signals.
            // For BOSWave: Layer A and macro penalty are both disabled.
            // Layer B (level proximity) is also disabled — BOSWave entries happen
            // at the SMF basis (a 34-bar EMA), not at HTF structural levels.
            // Layer C and D remain fully active — order flow and swing structure
            // are still valid quality filters for flow-based signals.
            bool isBOSWave = !string.IsNullOrEmpty(conditionSetId)
                && conditionSetId.StartsWith("SMF_Native_");

            // =============================================================
            // LAYER A — MTFA macro bias (0–30)
            // Disabled for BOSWave — flow signals are direction-agnostic to HTF EMA.
            // =============================================================
            double h4b = snap.Get(SnapKeys.H4HrEmaBias);
            double h2b = snap.Get(SnapKeys.H2HrEmaBias);
            double h1b = snap.Get(SnapKeys.H1EmaBias);

            if (!isBOSWave)
            {
                if (h4b != 0 && ((isLong && h4b > 0) || (!isLong && h4b < 0))) layerA += LAYER_A_H4;
                if (h2b != 0 && ((isLong && h2b > 0) || (!isLong && h2b < 0))) layerA += LAYER_A_H2;
                if (h1b != 0 && ((isLong && h1b > 0) || (!isLong && h1b < 0))) layerA += LAYER_A_H1;
            }

            // Macro opposition penalty — disabled for BOSWave, active for all others.
            bool h4Opposes = h4b != 0 && ((isLong && h4b < 0) || (!isLong && h4b > 0));
            bool h2Opposes = h2b != 0 && ((isLong && h2b < 0) || (!isLong && h2b > 0));
            if (!isBOSWave)
            {
                if (h4Opposes) penalty += PENALTY_H4;
                if (h2Opposes) penalty += PENALTY_H2;
                if (h4Opposes && h2Opposes) penalty += PENALTY_BOTH_EXTRA;
            }

            // =============================================================
            // LAYER B — SupportResistanceEngine (0–40)
            // =============================================================
            // Disabled for BOSWave — BOSWave entries happen at the SMF basis
            // (34-bar EMA), not at HTF structural levels. Requiring level
            // proximity would kill valid flow-based signals that have no reason
            // to be near a 4H swing or PDH/PDL.
            if (!isBOSWave)
            {
                layerB = EvaluateSRLayerB(isLong, in sr);
                if (layerB == -1) 
                {
                    isVetoed = true;
                    layerB = 0;
                }
            }

            // =============================================================
            // LAYER C — OrderFlow conviction (0–22 cap)
            // =============================================================
            // 
            // PREVIOUS BUG (FIX #28 was incorrect):
            // FIX #28 emptied the volumetric path under the assumption that 
            // FootprintEntryAdvisor would take over Layer C scoring. In fact:
            //   1. Advisor's SupportScore output is discarded by HostStrategy
            //      (only its Multiplier is used)
            //   2. Advisor is bypassed entirely for ORB candidates
            // Result: ORB signals running with volumetric data had Layer C = 0.
            // 
            // CONSERVATIVE FIX:
            // Use the existing fallback proxies for ALL candidates regardless 
            // of hasVol. Max possible: ~37 pts (22 proxy + 15 divergence).
            // LAYER_C_DIVERGENCE activated; remaining constants (DELTA_SL,
            // ABS_MAX, DELTA_EXHST, IMBAL_ZONE) still unwired pending validation.

            // BarDelta from DataFeed (always populated)
            double bd = snap.Get(SnapKeys.BarDelta);
            if (isLong  && bd > 0) layerC += StrategyConfig.Confluence.PROXY_BAR_DELTA;
            if (!isLong && bd < 0) layerC += StrategyConfig.Confluence.PROXY_BAR_DELTA;

            // SMF Regime agreement
            int regime = (int)snap.Get(SnapKeys.Regime);
            if ((isLong && regime > 0) || (!isLong && regime < 0)) layerC += StrategyConfig.Confluence.PROXY_REGIME;

            // VWAP side
            if (snap.VWAP > 0)
            {
                bool aboveVwap = p.Close > snap.VWAP;
                if ((isLong && aboveVwap) || (!isLong && !aboveVwap)) layerC += StrategyConfig.Confluence.PROXY_VWAP_SIDE;
            }

            // Higher1 (15-min) bar direction
            if (snap.Higher1.Closes != null && snap.Higher1.Closes.Length >= 2)
            {
                bool h1Rising = snap.Higher1.Close > snap.Higher1.Closes[1];
                if ((isLong && h1Rising) || (!isLong && !h1Rising)) layerC += StrategyConfig.Confluence.PROXY_H1_BAR_DIR;
            }

            // ── CVD Divergence agreement ────────────────────────────────────
            // BullDivergence (price lower low, CVD higher low = hidden buying) confirms LONG.
            // BearDivergence (price higher high, CVD lower high = hidden selling) confirms SHORT.
            // Opposition side is already a veto (Phase 1.1). This adds the confirmation bonus.
            if ( isLong && snap.GetFlag(SnapKeys.BullDivergence)) layerC += LAYER_C_DIVERGENCE;
            if (!isLong && snap.GetFlag(SnapKeys.BearDivergence)) layerC += LAYER_C_DIVERGENCE;

            // ── Trapped Traders agreement (Phase 2.7) ───────────────────────
            // Long is confirmed when SHORTS were trapped at a low (their exit flow
            // extends upward). Short is confirmed when LONGS were trapped at a high.
            // The flag is already 2-bar-deferred by the detector, so no re-timing here.
            bool trapLongsFlag  = snap.GetFlag(SnapKeys.TrappedLongs);
            bool trapShortsFlag = snap.GetFlag(SnapKeys.TrappedShorts);

            if ( isLong && trapShortsFlag) layerC += LAYER_C_TRAPPED_AGREE;
            if (!isLong && trapLongsFlag ) layerC += LAYER_C_TRAPPED_AGREE;

            // ── Iceberg agreement (Phase 2.8) ───────────────────────────────
            // BullIceberg (cluster at Low) confirms LONG. BearIceberg (cluster at High)
            // confirms SHORT. Same-side mapping — opposite of Trapped Traders.
            bool bullIcebergFlag = snap.GetFlag(SnapKeys.BullIceberg);
            bool bearIcebergFlag = snap.GetFlag(SnapKeys.BearIceberg);

            if ( isLong && bullIcebergFlag) layerC += LAYER_C_ICEBERG_AGREE;
            if (!isLong && bearIcebergFlag) layerC += LAYER_C_ICEBERG_AGREE;

            // ── Exhaustion agreement (Phase 2.9) ────────────────────────────
            // BullExhaustion (top exhausted) confirms SHORT. BearExhaustion (bottom
            // exhausted) confirms LONG. Opposite-side mapping — mirrors Trapped.
            bool bullExhFlag = snap.GetFlag(SnapKeys.BullExhaustion);
            bool bearExhFlag = snap.GetFlag(SnapKeys.BearExhaustion);

            if ( isLong && bearExhFlag) layerC += LAYER_C_EXHAUSTION_AGREE;
            if (!isLong && bullExhFlag) layerC += LAYER_C_EXHAUSTION_AGREE;

            // ── Unfinished Auction target-side (Phase 2.9) ──────────────────
            // UnfinishedTop (high = magnet) gives longs a target overhead.
            // UnfinishedBottom (low = magnet) gives shorts a target below.
            bool unfinTopFlag    = snap.GetFlag(SnapKeys.UnfinishedTop);
            bool unfinBottomFlag = snap.GetFlag(SnapKeys.UnfinishedBottom);

            if ( isLong && unfinTopFlag   ) layerC += LAYER_C_UNFINISHED_AGREE;
            if (!isLong && unfinBottomFlag) layerC += LAYER_C_UNFINISHED_AGREE;

            // Phase 3.7 — tape flags read here for veto use below and autopsy tagging.
            // Same-side scoring bonuses were removed after autopsy showed they elevated
            // low-quality longs. Flags retained for Sweep-opposition veto and Phase 3.9
            // diagnostic tags (bp±, vel±, swp±, tice±) — no scoring effect, data only.
            bool buySweepFlag    = snap.GetFlag(SnapKeys.BuySweep);
            bool sellSweepFlag   = snap.GetFlag(SnapKeys.SellSweep);
            double bpDelta       = snap.Get(SnapKeys.BigPrintDelta);
            bool velBuyFlag      = snap.GetFlag(SnapKeys.VelocityBuySpike);
            bool velSellFlag     = snap.GetFlag(SnapKeys.VelocitySellSpike);
            bool tapeBullIceFlag = snap.GetFlag(SnapKeys.TapeBullIceberg);
            bool tapeBearIceFlag = snap.GetFlag(SnapKeys.TapeBearIceberg);

            // SMF NonConfirmation veto
            if (isLong  && snap.GetFlag(SnapKeys.NonConfLong))  isVetoed = true;
            if (!isLong && snap.GetFlag(SnapKeys.NonConfShort)) isVetoed = true;

            // =============================================================
            // PHASE 1.1 — ORDER-FLOW DIRECTIONAL VETOS
            // =============================================================
            // Blocks entries that match the losing-trade signatures from the
            // 6-week backtest (see specs/phase1_1_long_side_vetos.md).
            // Each rule is symmetric: long-side rule vetos longs, short-side
            // rule vetos shorts.
            //
            // Thresholds chosen conservatively from the 15-trade long sample:
            //   CUMDELTA_EXHAUSTED = 2500  (losers avg 2889, winners avg 841)
            //   WEAK_STACK_COUNT   = 3     (below full MinStackedLevels=3)
            //
            // CvdAccel (Phase 1.2) will replace the static CUMDELTA_EXHAUSTED
            // threshold with a derivative-based exhaustion check.

            const double CUMDELTA_EXHAUSTED = StrategyConfig.Vetoes.CUMDELTA_EXHAUSTED;
            const double WEAK_STACK_COUNT = StrategyConfig.Vetoes.WEAK_STACK_COUNT;

            // Rule 1 — Divergence opposite the trade direction.
            // Long with bearish divergence = buying into weakness the tape already sees.
            if (isLong  && snap.GetFlag(SnapKeys.BearDivergence)) { isVetoed = true; }
            if (!isLong && snap.GetFlag(SnapKeys.BullDivergence)) { isVetoed = true; }

            // Rule 2 — Opposing stacked-imbalance zone at price (from ImbalanceZoneRegistry).
            // Long into a live bearish zone = buying directly into a seller wall.
            if (isLong  && snap.GetFlag(SnapKeys.ImbalZoneAtBear)) { isVetoed = true; }
            if (!isLong && snap.GetFlag(SnapKeys.ImbalZoneAtBull)) { isVetoed = true; }

            // Rule 3 — Exhausted cumulative delta without fresh same-side stack support.
            // Long when CD is already far above session mean AND no bull stack this bar
            // = chasing a move that's already stretched.
            double cd        = snap.Get(SnapKeys.CumDelta);
            double sbullCnt  = snap.Get(SnapKeys.StackedImbalanceBull);
            double sbearCnt  = snap.Get(SnapKeys.StackedImbalanceBear);

            if (isLong  && cd >  CUMDELTA_EXHAUSTED && sbullCnt < WEAK_STACK_COUNT) { isVetoed = true; }
            if (!isLong && cd < -CUMDELTA_EXHAUSTED && sbearCnt < WEAK_STACK_COUNT) { isVetoed = true; }

            // Phase 2.7 — Trapped Traders opposition veto
            // Long signal when LONGS are trapped at a high → trapped-long exit flow
            // will push price DOWN. The signal is trading into forced-seller flow.
            // Symmetric for shorts.
            if (isLong  && trapLongsFlag ) { isVetoed = true; }
            if (!isLong && trapShortsFlag) { isVetoed = true; }

            // Phase 2.8 — Iceberg opposition veto
            // Long signal while a BEAR iceberg (overhead wall) is active → trading
            // into absorbed resistance. Short signal while BULL iceberg is active →
            // trading into absorbed support.
            if ( isLong && bearIcebergFlag) { isVetoed = true; }
            if (!isLong && bullIcebergFlag) { isVetoed = true; }

            // Phase 3.7 — Sweep opposition veto
            // Buy sweep during a short signal = live buying pressure walking up the book.
            // Sell sweep during a long signal = live selling pressure walking down.
            // Sweep is rare (≥3 levels in 200ms) — binary veto is appropriate.
            if ( isLong && sellSweepFlag) { isVetoed = true; }
            if (!isLong && buySweepFlag ) { isVetoed = true; }

            // Phase 2.9 Exhaustion opposition veto was REMOVED (2026-04-15).
            // The binary veto on a 5–15% fire-rate signal killed valid short-
            // continuation setups where price briefly thinned at the low mid-move.
            // Test A measured: removing this veto alone recovered +$21.8K net
            // profit ($1.7K → $23.5K, beating pre-scoring baseline by +$4.5K).
            // Scoring bonus (+8 on opposite-side agreement) stays — it works.
            // See specs/phase2_9_exhaustion_unfinished_scoring.md Revision block.

            layerC = Math.Min(layerC, 30);  // hard cap retained for safety,
                                            // though new max is ~22

            // =============================================================
            // LAYER D — Price action structure on execution TF (0–15)
            // =============================================================
            // TWO PATHS:
            //   1. CHoCH fired this bar — reversal entry. Score positively
            //      regardless of stale swing labels (they still show old trend).
            //   2. No CHoCH — score based on confirmed swing structure labels.
            //
            // Read swing state from snap bag. Written by SMCBase.UpdateSwings()
            // on every bar that an SMC condition set evaluates.
            // If no SMC sets registered, all keys are 0 — layer degrades to 0.
            double confirmedSwings = snap.Get(SnapKeys.ConfirmedSwings);

            // CHoCH reversal path — takes priority over label-based scoring
            bool chochFiredLong  = snap.GetFlag(SnapKeys.CHoCHFiredLong);
            bool chochFiredShort = snap.GetFlag(SnapKeys.CHoCHFiredShort);

            if (isLong  && chochFiredLong)
            {
                // Bullish CHoCH fired this bar. Structure labels are stale (still show
                // LH+LL from prior trend). Give full reversal structure credit.
                layerD += LAYER_D_FULL_STRUCT;
            }
            else if (!isLong && chochFiredShort)
            {
                // Bearish CHoCH fired this bar — same logic, mirror side.
                layerD += LAYER_D_FULL_STRUCT;
            }
            else if (confirmedSwings >= 4)
            {
                // Normal continuation path — score based on confirmed labels
                double swingTrend    = snap.Get(SnapKeys.SwingTrend);
                double lastHighLabel = snap.Get(SnapKeys.LastHighLabel);
                double lastLowLabel  = snap.Get(SnapKeys.LastLowLabel);

                bool fullBull = (isLong
                    && lastHighLabel == (double)SwingLabel.HH
                    && lastLowLabel  == (double)SwingLabel.HL);
                bool fullBear = (!isLong
                    && lastHighLabel == (double)SwingLabel.LH
                    && lastLowLabel  == (double)SwingLabel.LL);

                if (fullBull || fullBear)
                    layerD += LAYER_D_FULL_STRUCT;
                else if ((isLong && swingTrend > 0) || (!isLong && swingTrend < 0))
                    layerD += LAYER_D_TREND_ONLY;
            }

            // =============================================================
            // TOTAL
            // =============================================================
            int bonus = 0;
            int rawTotal = layerA + layerB + layerC + layerD;

            //bool isORB = p.Source == SignalSource.ORB_Breakout || p.Source == SignalSource.ORB_Retest;

            // ── PERFORMANCE TUNING: Maturity Gate ───────────────────────
            // FIX: Prevent trades in structural "voids" before levels are mapped.
            // If less than 3 swings confirmed, apply a -20 penalty.
            // EXEMPTION: ORB signals happen early and are exempt.
            //if (confirmedSwings < 3 && !isORB)
            //{
            //    penalty += 20;
            //}

            // ── PERFORMANCE TUNING: Value Area Constraints (Longs) ──────
            // FIX: Stop buying "high" during range expansions.
            // EXEMPTION: ORB is a breakout strategy; buying high is the point.
			//            if (isLong && !isORB)

			            if (isLong)

            {
                double poc = snap.Get(SnapKeys.POC);
                double val = snap.Get(SnapKeys.VALow);
                if (poc > 0 && p.Close > poc) penalty += StrategyConfig.Confluence.PENALTY_ABOVE_FAIR; // Penalty for buying above fair value
                if (val > 0 && p.Close < val) { bonus += StrategyConfig.Confluence.BONUS_DEEP_DISCOUNT; rawTotal += bonus; } // Bonus for buying at deep discount
            }

            int netScore = Math.Max(0, rawTotal - penalty);

            double multiplier = isVetoed
                ? 0.0
                : Math.Max(0.5, Math.Min(1.5, 0.5 + netScore / 100.0));

            // ── Build per-layer reason string for log diagnostics ─────────
            // Format: "A:reasons B:reasons C:reasons D:reasons [Pen:reasons]"
            // Each reason tag is 2-4 chars so the full string stays compact.
            var sb = new System.Text.StringBuilder(64);

            // LayerA reasons
            sb.Append("A:");
            if (layerA == 0)
            {
                sb.Append(h4b == 0 && h2b == 0 && h1b == 0 ? "cold" : "opp");
            }
            else
            {
                if (h4b != 0 && ((isLong && h4b > 0) || (!isLong && h4b < 0))) sb.Append("h4+");
                if (h2b != 0 && ((isLong && h2b > 0) || (!isLong && h2b < 0))) sb.Append("h2+");
                if (h1b != 0 && ((isLong && h1b > 0) || (!isLong && h1b < 0))) sb.Append("h1+");
            }

            // LayerB reasons — summarize SR location/strength context
            sb.Append(" B:");
            sb.Append(DescribeSRLayerB(isLong, in sr, layerB));

            // LayerC reasons — proxy signals (same path for all candidates 
            // after FIX #28 reversal)
            sb.Append(" C:");
            if (layerC == 0) { sb.Append("none"); }
            else
            {
                double bd2 = snap.Get(SnapKeys.BarDelta);
                if ((isLong && bd2 > 0) || (!isLong && bd2 < 0)) sb.Append("bd+");
                int reg = (int)snap.Get(SnapKeys.Regime);
                if ((isLong && reg > 0) || (!isLong && reg < 0)) sb.Append("reg+");
                if (snap.VWAP > 0)
                {
                    bool above = p.Close > snap.VWAP;
                    if ((isLong && above) || (!isLong && !above)) sb.Append("vwap+");
                }
                if ((isLong && snap.GetFlag(SnapKeys.BullDivergence)) || (!isLong && snap.GetFlag(SnapKeys.BearDivergence))) sb.Append("div+");
                if ((isLong && trapShortsFlag) || (!isLong && trapLongsFlag)) sb.Append("trap+");
                if ((isLong && bullIcebergFlag) || (!isLong && bearIcebergFlag)) sb.Append("ice+");
                if ((isLong && bearExhFlag)    || (!isLong && bullExhFlag))    sb.Append("exh+");
                if ((isLong && unfinTopFlag)   || (!isLong && unfinBottomFlag)) sb.Append("unf+");

                // Phase 3.9 tape diagnostic tags — data only, no scoring effect.
                // Same-side (+) = tape flow aligned with trade direction.
                // Opposite-side (-) = tape flow opposing trade direction.
                // swp- is redundant with vSWP (same event vetoes the trade) but kept for symmetry.
                if ((isLong && bpDelta > 0)      || (!isLong && bpDelta < 0))       sb.Append("bp+");
                if ((isLong && bpDelta < 0)      || (!isLong && bpDelta > 0))       sb.Append("bp-");
                if ((isLong && velBuyFlag)       || (!isLong && velSellFlag))       sb.Append("vel+");
                if ((isLong && velSellFlag)      || (!isLong && velBuyFlag))        sb.Append("vel-");
                if ((isLong && buySweepFlag)     || (!isLong && sellSweepFlag))     sb.Append("swp+");
                if ((isLong && sellSweepFlag)    || (!isLong && buySweepFlag))      sb.Append("swp-");
                if ((isLong && tapeBullIceFlag)  || (!isLong && tapeBearIceFlag))   sb.Append("tice+");
                if ((isLong && tapeBearIceFlag)  || (!isLong && tapeBullIceFlag))   sb.Append("tice-");

                if (snap.GetFlag(SnapKeys.NonConfLong) || snap.GetFlag(SnapKeys.NonConfShort)) sb.Append("ncVETO");
            }

            // Phase 1.1 veto reasons — only appended when a rule fired this evaluation.
            if (isLong)
            {
                if (snap.GetFlag(SnapKeys.BearDivergence))   sb.Append("vBDIV");
                if (snap.GetFlag(SnapKeys.ImbalZoneAtBear))  sb.Append("vZB");
                if (cd > CUMDELTA_EXHAUSTED && sbullCnt < WEAK_STACK_COUNT) sb.Append("vEXHL");
                if (trapLongsFlag) sb.Append("vTRAP");
                if (bearIcebergFlag) sb.Append("vICE");
                if (sellSweepFlag) sb.Append("vSWP");
            }
            else
            {
                if (snap.GetFlag(SnapKeys.BullDivergence))   sb.Append("vBULLDIV");
                if (snap.GetFlag(SnapKeys.ImbalZoneAtBull))  sb.Append("vZU");
                if (cd < -CUMDELTA_EXHAUSTED && sbearCnt < WEAK_STACK_COUNT) sb.Append("vEXHS");
                if (trapShortsFlag) sb.Append("vTRAP");
                if (bullIcebergFlag) sb.Append("vICE");
                if (buySweepFlag) sb.Append("vSWP");
            }

            // LayerD reasons
            sb.Append(" D:");
            bool cfLong  = snap.GetFlag(SnapKeys.CHoCHFiredLong);
            bool cfShort = snap.GetFlag(SnapKeys.CHoCHFiredShort);
            if ((isLong && cfLong) || (!isLong && cfShort))
            {
                sb.Append("choch");
            }
            else if (layerD > 0)
            {
                double lastHighLabel = snap.Get(SnapKeys.LastHighLabel);
                double lastLowLabel  = snap.Get(SnapKeys.LastLowLabel);
                bool fullBull = isLong
                    && lastHighLabel == (double)SwingLabel.HH
                    && lastLowLabel  == (double)SwingLabel.HL;
                bool fullBear = !isLong
                    && lastHighLabel == (double)SwingLabel.LH
                    && lastLowLabel  == (double)SwingLabel.LL;
                if (fullBull)      sb.Append("HH+HL");
                else if (fullBear) sb.Append("LH+LL");
                else               sb.Append("trend");
            }
            else
            {
                sb.Append(confirmedSwings < 4
                    ? string.Format("sw={0}", (int)confirmedSwings)
                    : "none");
            }

            // Penalty reasons
            if (penalty > 0)
            {
                sb.Append(" Pen:");
                if (h4Opposes) sb.Append("h4-");
                if (h2Opposes) sb.Append("h2-");
            }

            return new ConfluenceResult
            {
                LayerA     = layerA,
                LayerB     = layerB,
                LayerC     = layerC,
                LayerD     = layerD,
                Bonus      = bonus,
                Penalty    = penalty,
                NetScore   = netScore,
                IsVetoed   = isVetoed,
                IsValid    = true,
                Multiplier = multiplier,
                Detail     = sb.ToString()
            };
        }

        private static int EvaluateSRLayerB(bool isLong, in SupportResistanceResult sr)
        {
            if (!sr.IsValid)
                return 0;

            SRZone nearFavorable   = isLong ? sr.NearestSupport    : sr.NearestResistance;
            SRZone nearAdverse     = isLong ? sr.NearestResistance : sr.NearestSupport;
            SRZone strongFavorable = isLong ? sr.StrongestSupport  : sr.StrongestResistance;

            bool   atFavorable  = isLong ? sr.AtSupport    : sr.AtResistance;
            bool   atAdverse    = isLong ? sr.AtResistance : sr.AtSupport;
            double favorableAtr = isLong ? sr.ATRsToSupport    : sr.ATRsToResistance;
            double adverseAtr   = isLong ? sr.ATRsToResistance : sr.ATRsToSupport;

            int score = 0;

            if (atAdverse)
                return 0;

            // ── PERFORMANCE TUNING: Brick Wall Veto ────────────────────
            // FIX (#idea2): Veto trades entered too close to adverse structural levels.
            // If within 0.20 ATR of a major level (Support for Shorts, Resistance for Longs),
            // return -1 to signal a VETO to the ranking engine.
            if (nearAdverse.IsValid && adverseAtr > 0.0 && adverseAtr <= StrategyConfig.Vetoes.BRICK_WALL_ATR)
            {
                return -1; // Special sentinel for VETO
            }

            if (atFavorable) score += StrategyConfig.Confluence.SR_AT_FAVORABLE;
            else if (nearFavorable.IsValid)
            {
                if      (favorableAtr > 0.0 && favorableAtr <= 0.35) score += StrategyConfig.Confluence.SR_NEAR_FAV_CLOSE;
                else if (favorableAtr > 0.0 && favorableAtr <= 0.75) score += StrategyConfig.Confluence.SR_NEAR_FAV_MID;
                else if (favorableAtr > 0.0 && favorableAtr <= 1.25) score += StrategyConfig.Confluence.SR_NEAR_FAV_FAR;
            }

            bool favorableContext = atFavorable
                || (nearFavorable.IsValid && favorableAtr > 0.0 && favorableAtr <= 1.50);

            if (strongFavorable.IsValid && favorableContext)
            {
                if      (strongFavorable.Strength >= 30) score += StrategyConfig.Confluence.SR_STRONG_FAV_S3;
                else if (strongFavorable.Strength >= 20) score += StrategyConfig.Confluence.SR_STRONG_FAV_S2;
                else if (strongFavorable.Strength >= 12) score += StrategyConfig.Confluence.SR_STRONG_FAV_S1;

                if (SRSourceTypeHelper.IsStacked(strongFavorable.Sources))
                    score += StrategyConfig.Confluence.SR_STACKED_BONUS;
            }

            if (nearAdverse.IsValid)
            {
                if      (adverseAtr > 0.0 && adverseAtr <= 0.35) score -= StrategyConfig.Confluence.SR_NEAR_ADV_CLOSE;
                else if (adverseAtr > 0.0 && adverseAtr <= 0.75) score -= StrategyConfig.Confluence.SR_NEAR_ADV_MID;
                else if (adverseAtr > 0.0 && adverseAtr <= 1.25) score -= StrategyConfig.Confluence.SR_NEAR_ADV_FAR;
            }

            if (score < 0)  score = 0;
            if (score > StrategyConfig.Confluence.LAYER_B_MAX_CAP) score = StrategyConfig.Confluence.LAYER_B_MAX_CAP;
            return score;
        }

        private static string DescribeSRLayerB(bool isLong, in SupportResistanceResult sr, int layerB)
        {
            if (!sr.IsValid || layerB <= 0)
                return "none";

            bool atFavorable = isLong ? sr.AtSupport : sr.AtResistance;
            bool atAdverse   = isLong ? sr.AtResistance : sr.AtSupport;
            SRZone near      = isLong ? sr.NearestSupport : sr.NearestResistance;
            SRZone strong    = isLong ? sr.StrongestSupport : sr.StrongestResistance;

            if (atAdverse)
                return "opp";

            string loc = atFavorable ? "touch"
                       : near.IsValid ? "near"
                       : "ctx";

            string strength = strong.IsValid ? ("s" + strong.Strength.ToString()) : "s0";
            string stacked  = (strong.IsValid && SRSourceTypeHelper.IsStacked(strong.Sources))
                ? "+stk"
                : string.Empty;

            return loc + "+" + strength + stacked;
        }
    }

    // =========================================================================
    // RESULT — struct, not class. Zero heap allocation. Stack-only lifetime.
    // =========================================================================

    /// <summary>
    /// Output of ConfluenceEngine.Evaluate(). VALUE TYPE — no GC pressure.
    ///
    /// IsVetoed=true means SignalRankingEngine must discard the candidate
    /// regardless of FinalScore.
    ///
    /// Multiplier is applied to RawScore:
    ///   FinalScore = RawDecision.RawScore × ConfluenceResult.Multiplier
    /// </summary>
    public struct ConfluenceResult
    {
        public int    LayerA     { get; set; }
        public int    LayerB     { get; set; }
        public int    LayerC     { get; set; }
        public int    LayerD     { get; set; }
        public int    Bonus      { get; set; }
        public int    Penalty    { get; set; }
        public int    NetScore   { get; set; }
        public bool   IsVetoed   { get; set; }
        public bool   IsValid    { get; set; }
        public double Multiplier { get; set; }
        /// <summary>
        /// Human-readable explanation of what contributed to each layer.
        /// e.g. "A:h4+h2+h1 B:4Hsw+London C:BullDiv+DeltaSl D:CHoCH"
        /// Written by ConfluenceEngine.Evaluate(). Used in log detail column.
        /// </summary>
        public string Detail     { get; set; }

        public static ConfluenceResult Invalid()
            => new ConfluenceResult { IsValid = false, Multiplier = 1.0 };

        public override string ToString()
            => string.Format("A={0} B={1} C={2} D={3} Bon={4} Pen={5} Net={6} Mult={7:F2}{8}",
                LayerA, LayerB, LayerC, LayerD, Bonus, Penalty, NetScore,
                Multiplier, IsVetoed ? " VETOED" : "");

        /// <summary>Full debug string including per-layer reasons.</summary>
        public string ToDetailString()
            => string.Format("A={0} B={1} C={2} D={3} Bon={4} Pen={5} Net={6} Mult={7:F2}{8} | {9}",
                LayerA, LayerB, LayerC, LayerD, Bonus, Penalty, NetScore,
                Multiplier, IsVetoed ? " VETOED" : "",
                string.IsNullOrEmpty(Detail) ? "?" : Detail);
    }
}
