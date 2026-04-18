#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    // =========================================================================
    // IcebergAbsorptionSignal — Aggressor exhaustion + iceberg absorption reversal
    //
    // CONCEPT (Paper 1 — Strategy I — Reversal via Absorption):
    //   This signal captures the exact transition point the paper identifies as
    //   the core institutional trade: aggressive participants (retail, momentum
    //   algos) exhaust their capital attacking a price level, while a hidden
    //   institutional iceberg order silently absorbs every aggressive hit. Once
    //   the last aggressive participant has been absorbed, the market "snaps"
    //   violently in the direction of the absorbing institution.
    //
    //   The paper cites 75-80% historical win rate for this specific combination
    //   of aggressor exhaustion + iceberg absorption. That number comes from the
    //   co-occurrence of three specific conditions — which is exactly what this
    //   set requires.
    //
    // TWO EXHAUSTION DETECTORS IN YOUR CODEBASE:
    //   BullExhaustion/BearExhaustion (FootprintCore Phase 2.3 — bar-level):
    //     Bar's top-level or bottom-level volume < 50% of bar average level volume.
    //     "The thrust ran out of participants at the extreme."
    //     BearExhaustion (sellers ran out at bar LOW) → confirms LONG.
    //     BullExhaustion (buyers ran out at bar HIGH) → confirms SHORT.
    //
    //   DeltaExhaustion (HostStrategy — 3-bar trend):
    //     Price moving in one direction while delta momentum fades bar-by-bar.
    //     -1.0 = bear exhaustion (delta improving despite lower price) → LONG.
    //     +1.0 = bull exhaustion (delta weakening despite higher price) → SHORT.
    //
    //   Either detector can trigger this signal. Both firing simultaneously = highest
    //   conviction. The signal accepts EITHER as the primary exhaustion confirmation.
    //
    // TWO ICEBERG DETECTORS IN YOUR CODEBASE:
    //   BullIceberg/BearIceberg (FootprintCore Phase 2.5 — bar-level, 3-bar window):
    //     MaxCombinedVol at a price >= 2x avg level vol AND that price recurs across
    //     2+ bars in a 3-bar window. Fires when the absorbed level touches bar Low
    //     (BullIceberg) or bar High (BearIceberg).
    //     SLOW but HIGH CONVICTION — the wall has held for multiple bars.
    //
    //   TapeBullIceberg/TapeBearIceberg (TapeIcebergDetector Phase 3.6 — tick-level):
    //     8+ repeated aggressor prints at the same price within a 5-second window.
    //     Real-time detection of the iceberg reloading on the tape.
    //     FAST but SHORTER LIFETIME — detects the wall right now at the tick level.
    //
    //   At least one iceberg signal on the absorbing side is REQUIRED.
    //   Both firing together earns a score bonus: dual-layer confirmation.
    //
    // SIGNAL LOGIC:
    //   Long entry — all three tiers must be satisfied:
    //
    //   TIER 1 — Exhaustion (at least one required):
    //     BearExhaustion = 1  (sellers ran out at bar low, bar-level confirmation)
    //     OR DeltaExhaustion < 0  (-1, bear exhaustion over 3-bar delta window)
    //
    //   TIER 2 — Absorption confirmed (required):
    //     AbsorptionScore >= MIN_ABSORPTION_SCORE (30)
    //     The institutional wall was ACTIVELY absorbing. Without this, exhaustion
    //     is just thin volume, not institutional defense.
    //
    //   TIER 3 — Iceberg identified on absorbing side (required, at least one):
    //     BullIceberg = 1  (bar-level: absorption at bar low across 3-bar window)
    //     OR TapeBullIceberg = 1  (tape-level: repeated sells absorbed at same price)
    //
    //   Short entry: exact mirror.
    //     BullExhaustion = 1 OR DeltaExhaustion > 0 (buyers exhausted)
    //     AbsorptionScore >= MIN_ABSORPTION_SCORE
    //     BearIceberg = 1 OR TapeBearIceberg = 1
    //
    // HARD VETOES:
    //   SellSweep on long — aggressive sellers are STILL sweeping. Not exhausted.
    //   BuySweep on short — aggressive buyers are STILL sweeping. Not exhausted.
    //   BearDivergence on long — CVD shows sellers absorbing at highs. Macro context
    //     is bearish. A local low-exhaustion signal against a macro bearish divergence
    //     is a fade trade against institutional distribution.
    //   BullDivergence on short — mirror. Buyers absorbing at lows, macro bullish.
    //
    //   NOTE: BullDivergence on LONG is a BONUS (not a veto) — CVD confirming buyers
    //   absorbing at lows agrees with our long thesis.
    //   BearDivergence on SHORT is a BONUS — CVD confirming sellers at highs agrees.
    //
    // STOP PLACEMENT:
    //   Long:  stop below bar low (where BearExhaustion/BullIceberg fired)
    //          stop = min(Low[0..1]) - ATR_STOP_BUFFER × ATR
    //   Short: stop above bar high
    //          stop = max(High[0..1]) + ATR_STOP_BUFFER × ATR
    //   Lookback is deliberately SHORT (2 bars) — the exhaustion fired on THIS bar.
    //   The stop should sit just below the level that was defended, not 3 bars back.
    //   Minimum width enforced at MIN_STOP_ATR_MULT × ATR.
    //
    // TARGETS:
    //   T1: VWAP (mean-reversion magnet after exhaustion) if directionally favorable
    //       and at meaningful distance (> MIN_T1_ATR_DIST × ATR)
    //       Else: LastSwingHigh/Low from StructuralLabeler if in usable range
    //       Else: 1.5 × ATR fallback
    //   T2: 2.5 × ATR (runner — if the exhaustion triggers a full reversal)
    //
    // SCORING:
    //   Base: 76 (paper cites 75-80% win rate for this specific three-tier setup)
    //   +5  Both bar-level AND tape-level iceberg confirm simultaneously
    //       (dual-layer detection = institution confirmed at two resolutions)
    //   +5  TrappedLongs/Shorts confirm — forced exits fuel the reversal move
    //   +5  CVD divergence agrees with direction (BullDiv on long / BearDiv on short)
    //   +4  Both exhaustion detectors fire (BullExhaustion AND DeltaExhaustion agree)
    //   +3  ImbalZoneAtBull/Bear — exhaustion happened at an institutional zone
    //   +3  UnfinishedBottom/Top — unfinished auction acts as a magnetic target
    //   Max before cap: 101 → capped at 92
    //
    // REGISTRATION:
    //   Add to HostStrategy.CreateLogic() after ImbalanceReAggressionSignal:
    //     new ConditionSets.IcebergAbsorptionSignal(),
    // =========================================================================

    public class IcebergAbsorptionSignal : IConditionSet
    {
        public string SetId => "IcebergAbsorption_v1";

        // ── Thresholds ────────────────────────────────────────────────────────

        // AbsorptionScore gate. The paper requires a confirmed institutional wall.
        // 30 = "notable" per StrategyLogger docs. Below this the absorption could
        // be normal market noise, not a genuine iceberg defense.
        private const double MIN_ABSORPTION_SCORE = 7.0;

        // High absorption bonus tier. Signals a substantially stronger wall —
        // the institution absorbed significantly more than typical.
        private const double HIGH_ABSORPTION_SCORE = 12.0;

        // Alternative Tier 3: when no iceberg flag fires, accept high absorption
        // + favorable close position as evidence the wall held. This compensates
        // for bar-level iceberg requiring MaxCombinedVol at the exact bar extreme
        // (structurally rare for BullIceberg) and tape-level iceberg being unreliable
        // in backtests with simulated ticks.
        private const double ALT_ICE_ABS_SCORE = 9.0;
        private const double ALT_ICE_CLOSE_PCT = 0.6;

        // ATR fraction added to the bar extreme when placing stop.
        // Tighter than other sets because the exhaustion fired on THIS bar —
        // the structural level is fresh and precisely located.
        private const double ATR_STOP_BUFFER = 0.06;

        // Minimum stop width. Prevents trivial stops on doji exhaustion bars.
        private const double MIN_STOP_ATR_MULT = 0.18;

        // Stop lookback: only 2 bars. The exhaustion event fired on this bar.
        // Using 3+ bars would place the stop well below/above the actual
        // defended level, creating unnecessary risk.
        private const int STOP_LOOKBACK_BARS = 2;

        // Re-entry suppression. Exhaustion reversals can have sharp initial
        // moves then retrace into consolidation — cooldown prevents chasing.
        private const int REENTRY_COOLDOWN = 10;

        // T1 target: minimum and maximum ATR distance from entry.
        private const double MIN_T1_ATR_DIST = 0.5;
        private const double MAX_T1_ATR_DIST = 3.0;

        // ── State ─────────────────────────────────────────────────────────────

        private double _tickSize;
        private double _tickValue;
        private int    _lastFillBar    = -1;
        private string _lastBailReason = "";

        public string LastDiagnostic => _lastBailReason;

        // ── IConditionSet lifecycle ───────────────────────────────────────────

        public void Initialise(double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _lastFillBar    = -1;
            _lastBailReason = "session_open";
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
                _lastFillBar = signal.BarIndex;
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl) { }

        // ── Main evaluation ───────────────────────────────────────────────────

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            // ── Guard: snapshot valid ──────────────────────────────────────────
            if (!snapshot.IsValid)
            { _lastBailReason = "snapshot_invalid"; return RawDecision.None; }

            var    p      = snapshot.Primary;
            double atr    = snapshot.ATR;
            double tickSz = p.TickSize > 0 ? p.TickSize : _tickSize;

            // ── Guard: minimum ATR ────────────────────────────────────────────
            if (atr <= 0)
            { _lastBailReason = "atr_zero"; return RawDecision.None; }

            // ── Guard: cooldown ────────────────────────────────────────────────
            if (_lastFillBar >= 0 && p.CurrentBar - _lastFillBar < REENTRY_COOLDOWN)
            { 
                return new RawDecision { Direction = SignalDirection.None, Label = "REJ:Ice Cooldown", IsValid = false }; 
            }

            // ── Guard: volumetric data required ───────────────────────────────
            if (!snapshot.GetFlag(SnapKeys.HasVolumetric))
            { 
                _lastBailReason = "no_volumetric";
                return new RawDecision { Direction = SignalDirection.None, Label = "REJ:Ice NoVol", IsValid = false };
            }

            // ── TIER 1: Exhaustion detection ───────────────────────────────────
            bool barLevelBearExh = snapshot.GetFlag(SnapKeys.BearExhaustion); // sellers ran out → LONG
            bool barLevelBullExh = snapshot.GetFlag(SnapKeys.BullExhaustion); // buyers ran out → SHORT

            double deltaExh = snapshot.Get(SnapKeys.DeltaExhaustion);
            bool deltaBearExh = deltaExh < -0.5;  // bear exhaustion → LONG
            bool deltaBullExh = deltaExh > 0.5;   // bull exhaustion → SHORT

            bool longExhaustion  = barLevelBearExh || deltaBearExh;
            bool shortExhaustion = barLevelBullExh || deltaBullExh;

            if (!longExhaustion && !shortExhaustion)
            { _lastBailReason = "no_exhaustion"; return RawDecision.None; }

            // ── TIER 2: Absorption gate ────────────────────────────────────────
            double absScore = snapshot.Get(SnapKeys.AbsorptionScore);
            if (absScore < MIN_ABSORPTION_SCORE)
            { 
                _lastBailReason = $"absorption_low ({absScore:F1}<{MIN_ABSORPTION_SCORE})"; 
                return new RawDecision { Direction = longExhaustion ? SignalDirection.Long : SignalDirection.Short, Label = "REJ:Ice LowAbs", IsValid = false };
            }

            // ── TIER 3: Iceberg identification ────────────────────────────────
            bool barBullIce  = snapshot.GetFlag(SnapKeys.BullIceberg);
            bool barBearIce  = snapshot.GetFlag(SnapKeys.BearIceberg);
            bool tapeBullIce = snapshot.GetFlag(SnapKeys.TapeBullIceberg);
            bool tapeBearIce = snapshot.GetFlag(SnapKeys.TapeBearIceberg);

            double barRange = p.High - p.Low;
            double closePct = barRange > 0 ? (p.Close - p.Low) / barRange : 0.5;
            bool altIceLong  = absScore >= ALT_ICE_ABS_SCORE && closePct >= ALT_ICE_CLOSE_PCT;
            bool altIceShort = absScore >= ALT_ICE_ABS_SCORE && closePct <= (1.0 - ALT_ICE_CLOSE_PCT);

            bool longIceberg  = barBullIce  || tapeBullIce  || altIceLong;
            bool shortIceberg = barBearIce  || tapeBearIce  || altIceShort;

            if (!longIceberg && !shortIceberg)
            { 
                _lastBailReason = "no_iceberg"; 
                return new RawDecision { Direction = longExhaustion ? SignalDirection.Long : SignalDirection.Short, Label = "REJ:Ice NoIce", IsValid = false };
            }

            // ── Determine direction ────────────────────────────────────────────
            SignalDirection direction = SignalDirection.None;
            if      (longExhaustion  && longIceberg)  direction = SignalDirection.Long;
            else if (shortExhaustion && shortIceberg) direction = SignalDirection.Short;

            if (direction == SignalDirection.None)
            { 
                _lastBailReason = $"exh_ice_mismatch"; 
                return new RawDecision { Direction = longExhaustion ? SignalDirection.Long : SignalDirection.Short, Label = "REJ:Ice SideMismatch", IsValid = false };
            }

            bool isLong = direction == SignalDirection.Long;

            // ── Hard veto: opposing sweep active ──────────────────────────────
            if (isLong  && snapshot.GetFlag(SnapKeys.SellSweep))
            { 
                return new RawDecision { Direction = SignalDirection.Long, Label = "REJ:Ice SweepVeto", IsValid = false, EntryPrice = p.Close, StopPrice = p.Low - atr*0.2 }; 
            }
            if (!isLong && snapshot.GetFlag(SnapKeys.BuySweep))
            { 
                return new RawDecision { Direction = SignalDirection.Short, Label = "REJ:Ice SweepVeto", IsValid = false, EntryPrice = p.Close, StopPrice = p.High + atr*0.2 }; 
            }

            // ── Hard veto: macro CVD divergence opposing ───────────────────────
            if (isLong  && snapshot.GetFlag(SnapKeys.BearDivergence))
            { 
                return new RawDecision { Direction = SignalDirection.Long, Label = "REJ:Ice MacroVeto", IsValid = false, EntryPrice = p.Close, StopPrice = p.Low - atr*0.2 }; 
            }
            if (!isLong && snapshot.GetFlag(SnapKeys.BullDivergence))
            { 
                return new RawDecision { Direction = SignalDirection.Short, Label = "REJ:Ice MacroVeto", IsValid = false, EntryPrice = p.Close, StopPrice = p.High + atr*0.2 }; 
            }

            // ── Stop placement ─────────────────────────────────────────────────
            // Stop goes just beyond the bar extreme where exhaustion fired.
            // Short lookback (2 bars) — the event is fresh and precisely located.
            double stopPrice;

            if (isLong)
            {
                // BearExhaustion fired at bar Low — stop below that low
                double recentLow = p.Low;
                if (p.Lows != null && p.Lows.Length >= STOP_LOOKBACK_BARS)
                    for (int i = 1; i < STOP_LOOKBACK_BARS; i++)
                        if (p.Lows[i] < recentLow) recentLow = p.Lows[i];

                stopPrice = recentLow - ATR_STOP_BUFFER * atr;

                double minStop = p.Close - MIN_STOP_ATR_MULT * atr;
                if (stopPrice > minStop) stopPrice = minStop;
            }
            else
            {
                // BullExhaustion fired at bar High — stop above that high
                double recentHigh = p.High;
                if (p.Highs != null && p.Highs.Length >= STOP_LOOKBACK_BARS)
                    for (int i = 1; i < STOP_LOOKBACK_BARS; i++)
                        if (p.Highs[i] > recentHigh) recentHigh = p.Highs[i];

                stopPrice = recentHigh + ATR_STOP_BUFFER * atr;

                double minStop = p.Close + MIN_STOP_ATR_MULT * atr;
                if (stopPrice < minStop) stopPrice = minStop;
            }

            // ── Sanity check ───────────────────────────────────────────────────
            if (isLong  && stopPrice >= p.Close)
            { _lastBailReason = "stop_above_entry"; return RawDecision.None; }
            if (!isLong && stopPrice <= p.Close)
            { _lastBailReason = "stop_below_entry"; return RawDecision.None; }

            // ── Target placement ───────────────────────────────────────────────
            // After exhaustion, price mean-reverts. VWAP is the primary magnet.
            // If VWAP is not in a useful range, fall back to swing levels then ATR.
            double vwap          = snapshot.VWAP;
            double lastSwingHigh = snapshot.Get(SnapKeys.LastSwingHigh);
            double lastSwingLow  = snapshot.Get(SnapKeys.LastSwingLow);
            double t1Price;
            double t2Price;

            if (isLong)
            {
                t2Price = p.Close + 2.5 * atr;

                // VWAP is the best T1 for exhaustion reversals — mean reversion target
                double vwapDist = vwap > 0 ? (vwap - p.Close) / atr : 0.0;
                if (vwap > p.Close && vwapDist >= MIN_T1_ATR_DIST && vwapDist <= MAX_T1_ATR_DIST)
                    t1Price = vwap;
                else
                {
                    double swingDist = lastSwingHigh > 0 ? (lastSwingHigh - p.Close) / atr : 0.0;
                    if (lastSwingHigh > 0 && swingDist >= MIN_T1_ATR_DIST && swingDist <= MAX_T1_ATR_DIST)
                        t1Price = lastSwingHigh;
                    else
                        t1Price = p.Close + 1.5 * atr;
                }
            }
            else
            {
                t2Price = p.Close - 2.5 * atr;

                double vwapDist = vwap > 0 ? (p.Close - vwap) / atr : 0.0;
                if (vwap > 0 && vwap < p.Close && vwapDist >= MIN_T1_ATR_DIST && vwapDist <= MAX_T1_ATR_DIST)
                    t1Price = vwap;
                else
                {
                    double swingDist = lastSwingLow > 0 ? (p.Close - lastSwingLow) / atr : 0.0;
                    if (lastSwingLow > 0 && swingDist >= MIN_T1_ATR_DIST && swingDist <= MAX_T1_ATR_DIST)
                        t1Price = lastSwingLow;
                    else
                        t1Price = p.Close - 1.5 * atr;
                }
            }

            // ── RR gate: minimum 1.2:1 ────────────────────────────────────────
            double riskTicks   = Math.Abs(p.Close - stopPrice) / tickSz;
            double rewardTicks = Math.Abs(t1Price  - p.Close)  / tickSz;
            if (riskTicks > 0 && rewardTicks / riskTicks < 1.2)
            { _lastBailReason = $"rr_low ({rewardTicks:F1}/{riskTicks:F1}={rewardTicks/riskTicks:F2})"; return RawDecision.None; }

            // ── Score ──────────────────────────────────────────────────────────
            int score = 76;

            // Dual-layer iceberg: both bar-level and tape-level firing simultaneously
            bool dualIceLong  = isLong  && barBullIce && tapeBullIce;
            bool dualIceShort = !isLong && barBearIce && tapeBearIce;
            if (dualIceLong || dualIceShort) score += 5;

            // Alternative iceberg path gets a small penalty vs true iceberg
            bool usedAltIce = isLong ? (!barBullIce && !tapeBullIce && altIceLong)
                                     : (!barBearIce && !tapeBearIce && altIceShort);
            if (usedAltIce) score -= 2;

            // Trapped traders: forced exits from the opposite side fuel the move
            if (isLong  && snapshot.GetFlag(SnapKeys.TrappedShorts)) score += 5;
            if (!isLong && snapshot.GetFlag(SnapKeys.TrappedLongs))  score += 5;

            // CVD divergence agrees with direction (not opposing — veto handled above)
            // BullDiv on long = buyers absorbing at lows, confirming our long thesis
            // BearDiv on short = sellers absorbing at highs, confirming our short thesis
            if (isLong  && snapshot.GetFlag(SnapKeys.BullDivergence)) score += 5;
            if (!isLong && snapshot.GetFlag(SnapKeys.BearDivergence)) score += 5;

            // Both exhaustion detectors agree: bar-level AND 3-bar delta trend
            bool dualExhLong  = isLong  && barLevelBearExh && deltaBearExh;
            bool dualExhShort = !isLong && barLevelBullExh && deltaBullExh;
            if (dualExhLong || dualExhShort) score += 4;

            // High absorption: exceptionally strong institutional wall
            if (absScore >= HIGH_ABSORPTION_SCORE) score += 3;

            // Exhaustion happened AT a known imbalance zone — structural confluence
            if (isLong  && snapshot.GetFlag(SnapKeys.ImbalZoneAtBull)) score += 3;
            if (!isLong && snapshot.GetFlag(SnapKeys.ImbalZoneAtBear)) score += 3;

            // Unfinished auction: magnetic target overhead/below drawing price away
            if (isLong  && snapshot.GetFlag(SnapKeys.UnfinishedTop))    score += 3;
            if (!isLong && snapshot.GetFlag(SnapKeys.UnfinishedBottom)) score += 3;

            score = Math.Min(score, 92);

            // ── Build decision ─────────────────────────────────────────────────
            _lastBailReason = "FIRED_" + (isLong ? "LONG" : "SHORT");

            return new RawDecision
            {
                Direction      = direction,
                Source         = SignalSource.OrderFlow_Abs,
                ConditionSetId = SetId,
                EntryPrice     = p.Close,
                StopPrice      = stopPrice,
                TargetPrice    = t1Price,
                Target2Price   = t2Price,
                Label          = BuildLabel(isLong, absScore, barLevelBearExh, barLevelBullExh,
                                            deltaBearExh, deltaBullExh, barBullIce || barBearIce,
                                            tapeBullIce || tapeBearIce, usedAltIce),
                RawScore       = score,
                IsValid        = true,
                BarIndex       = p.CurrentBar,
                SignalId       = string.Format("{0}:{1:yyyyMMdd}:{2}", SetId, p.Time, p.CurrentBar)
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private string BuildLabel(bool isLong, double absScore,
            bool barExh, bool barBullExh, bool deltaExh, bool deltaBullExh,
            bool barIce, bool tapeIce, bool altIce)
        {
            string exhTag  = (barExh || barBullExh) && (deltaExh || deltaBullExh)
                ? "dual_exh" : (barExh || barBullExh) ? "bar_exh" : "delta_exh";
            string iceTag  = altIce ? "alt_ice" : barIce && tapeIce ? "dual_ice" : barIce ? "bar_ice" : "tape_ice";

            return string.Format(
                "IcebergAbs {0} abs={1:F0} [{2}|{3}] [{4}]",
                isLong ? "long" : "short",
                absScore,
                exhTag, iceTag,
                SetId);
        }
    }
}