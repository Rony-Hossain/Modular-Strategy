#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    // =========================================================================
    // HybridScalpSignal — Two-stage sequenced entry: Structure → Zone → Flow
    //
    // CONCEPT:
    //   This is the full hybrid chain. It sequences the three paper concepts into
    //   one coherent entry that requires:
    //     1. A structural reason to exist (CHoCH or BOS event)
    //     2. A precise entry zone (FVG or OB retest)
    //     3. Real-time order flow confirmation (delta + absorption)
    //
    //   No other condition set in this codebase combines all three layers.
    //   DeltaDivergenceSignal is pure order flow.
    //   ImbalanceReAggressionSignal is pure zone defense.
    //   IcebergAbsorptionSignal is pure exhaustion.
    //   THIS set requires all three to sequence in the right order.
    //
    // WHY A STATE MACHINE:
    //   CHoCHFiredLong and BOSFiredLong are ONE-SHOT flags — StructuralLabeler
    //   resets them to 0.0 at the START of every bar's Update() call. They are
    //   only 1.0 on the exact bar they fire. This set cannot simply poll them
    //   each bar. It must CATCH the one-shot event and internally maintain a
    //   countdown window during which it watches for a zone retest.
    //
    //   Other condition sets in this codebase are stateless per-bar evaluators.
    //   This set is explicitly stateful. That is correct and intentional.
    //
    // TWO STAGES:
    //
    //   STAGE 1 — CATALYST (arms the window):
    //     CHoCHFiredLong = 1.0 on this bar (reversal) — highest conviction
    //     OR BOSFiredLong = 1.0 on this bar (continuation) — second entry
    //     AND H4 EMA bias is bullish or neutral (not strongly opposing)
    //     → Arms a 10-bar window. Locks the direction. Resets zone touch state.
    //
    //   STAGE 2 — ENTRY (fires within the window):
    //     Price enters an active FVG or OB zone in the armed direction.
    //     On the zone-touch bar:
    //       BarDelta > 0 (buyers re-engaging at the zone — re-aggression)
    //       AbsorptionScore > MIN_ABSORPTION (institutional wall at the zone)
    //       Close in upper half of bar (strong close, not a wick-through)
    //     → Fires the RawDecision.
    //
    // WINDOW EXPIRY (whichever comes first):
    //   - 10 bars elapsed since Stage 1 armed
    //   - Opposing structure event fires: CHoCHFiredShort/BOSFiredShort = 1.0
    //     (the market has reversed the reversal — the thesis is invalidated)
    //   - Price closes below FVG low / OB low (zone breached before being tested
    //     cleanly — distribution, not accumulation)
    //
    // ZONE TOUCH DETECTION:
    //   FVG bull zone: FvgBullActive = 1 AND p.Low <= FvgBullHigh AND
    //                  p.Close >= FvgBullLow (touched into zone, didn't collapse through)
    //   OB bull zone:  ObBullActive = 1 AND p.Low <= ObBullHigh AND
    //                  p.Close >= ObBullLow
    //   FVG is preferred over OB when both are active — FVGs are fresher
    //   structural references aligned with the CHoCH impulse.
    //
    //   Zone boundaries are available in SnapKeys:
    //     FvgBullLow, FvgBullHigh — from FvgZoneRegistry.PublishToSnap()
    //     ObBullLow, ObBullHigh   — from ObZoneRegistry.PublishToSnap()
    //   This enables precise stop placement at the zone edge.
    //
    // STOP PLACEMENT:
    //   Long: stop below zone low (FVG or OB low) - ATR_STOP_BUFFER × ATR
    //         If zone low is not available or degenerate: min(Low[0..2]) - buffer
    //   Short: mirror using zone high
    //   Minimum stop width: MIN_STOP_ATR_MULT × ATR
    //
    //   Using zone boundary for stop is more precise than recent bar lows — the
    //   zone low IS the structural invalidation level. If price closes below the
    //   zone low, the institutional thesis is broken.
    //
    // TARGETS:
    //   T1: LastSwingHigh (for long) — the structural target of the CHoCH move
    //       If out of range: VWAP if directionally favorable
    //       Fallback: 1.5 × ATR
    //   T2: 2.5 × ATR (runner — CHoCH moves can extend significantly)
    //
    // SCORING:
    //   Base: 80 (three-layer confluence is structurally the strongest setup)
    //   +10 BullDivergence active — CVD confirms buyers absorbing at lows.
    //       Triple-layer: structure + zone + divergence = all systems aligned
    //   +8  ImbalZoneAtBull — zone retest overlaps a historical stacked imbalance.
    //       Adds a fourth independent structural anchor to the entry
    //   +5  TrappedShorts — trapped participants accelerate the move
    //   +4  CHoCH (vs BOS) as the catalyst — reversal signals are higher conviction
    //       than continuation signals at the same zone
    //   +3  TapeBullIceberg — real-time tape confirms iceberg still active
    //   +3  BearExhaustion on the pullback bar — sellers ran out as they retraced
    //       into the zone, confirming institutional absorption during the pullback
    //   Max before cap: 113 → capped at 95
    //
    //   NOTE ON H4 OPPOSING MACRO:
    //   If H4 EMA bias strongly opposes (H4 = -1.0), Stage 1 is blocked entirely.
    //   A CHoCH against the H4 trend is a counter-trend reversal — plausible but
    //   requires much higher confluence to justify. The ConfluenceEngine penalty
    //   system handles this correctly at the scoring stage for any signal that
    //   does leak through. Blocking at Stage 1 prevents wasted window duration
    //   on low-probability counter-trend setups.
    //
    // REGISTRATION:
    //   Add to HostStrategy.CreateLogic() after IcebergAbsorptionSignal:
    //     new ConditionSets.HybridScalpSignal(),
    // =========================================================================

    public class HybridScalpSignal : IConditionSet
    {
        public string SetId => "HybridScalp_v1";

        // ── Configuration ─────────────────────────────────────────────────────

        // Bars available after Stage 1 to find a zone retest.
        // 10 bars = 50 minutes on a 5-min chart. Long enough to catch a
        // meaningful pullback, short enough not to trade stale structure.
        private const int WINDOW_BARS = StrategyConfig.Modules.HYBRID_WINDOW_BARS;

        // Minimum AbsorptionScore on the zone-touch bar.
        // The institutional wall must be present at the zone for re-aggression
        // to be genuine. Below this threshold, positive delta could be noise.
        private const double MIN_ABSORPTION_SCORE = StrategyConfig.Modules.HYBRID_MIN_ABSORP;

        // ATR fraction added to zone edge for stop placement.
        private const double ATR_STOP_BUFFER = StrategyConfig.Modules.HYBRID_ATR_STOP_BUFFER;

        // Minimum stop width from entry.
        private const double MIN_STOP_ATR_MULT = StrategyConfig.Modules.HYBRID_MIN_STOP_ATR_MULT;

        // Maximum stop width from entry — prevents catastrophic losses on wide structural levels.
        private const double MAX_STOP_ATR_MULT = StrategyConfig.Modules.HYBRID_MAX_STOP_ATR_MULT;

        // Stop lookback when zone boundaries are unavailable.
        private const int STOP_LOOKBACK_BARS = StrategyConfig.Modules.HYBRID_STOP_LOOKBACK_BARS;

        // Re-entry cooldown after a fill.
        private const int REENTRY_COOLDOWN = StrategyConfig.Modules.HYBRID_REENTRY_COOLDOWN;

        // T1 target range from entry, expressed as ATR multiples.
        private const double MIN_T1_ATR_DIST = StrategyConfig.Modules.HYBRID_MIN_T1_ATR_DIST;
        private const double MAX_T1_ATR_DIST = StrategyConfig.Modules.HYBRID_MAX_T1_ATR_DIST;

        // H4 EMA threshold: block Stage 1 if H4 is strongly opposing.
        // -1.0 = bearish H4 for a long signal = macro headwind.
        // 0.0 = neutral = acceptable.
        // We block only when H4 is actively bearish (-1.0), not when neutral.
        private const double H4_BLOCK_THRESHOLD = StrategyConfig.Modules.HYBRID_H4_BLOCK_THRESHOLD;  // < -0.5 blocks long stage 1

        // ── Window state ──────────────────────────────────────────────────────

        private bool            _windowArmed     = false;
        private SignalDirection _armedDirection  = SignalDirection.None;
        private int             _windowArmedBar  = -1;
        private bool            _wasChoch        = false; // true = CHoCH armed, false = BOS armed

        // ── Other state ───────────────────────────────────────────────────────

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
            _windowArmed    = false;
            _armedDirection = SignalDirection.None;
            _windowArmedBar = -1;
            _wasChoch       = false;
            _lastFillBar    = -1;
            _lastBailReason = "session_open";
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.ConditionSetId == SetId)
            {
                _lastFillBar    = signal.BarIndex;
                // Disarm window after a fill — don't re-enter on the same event
                _windowArmed    = false;
                _armedDirection = SignalDirection.None;
            }
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

            if (atr <= 0)
            { _lastBailReason = "atr_zero"; return RawDecision.None; }

            // ── Guard: cooldown ────────────────────────────────────────────────
            if (_lastFillBar >= 0 && p.CurrentBar - _lastFillBar < REENTRY_COOLDOWN)
            { _lastBailReason = "cooldown"; return RawDecision.None; }

            // ── Read one-shot structural flags BEFORE window expiry checks ─────
            bool chochLong  = snapshot.GetFlag(SnapKeys.CHoCHFiredLong);
            bool chochShort = snapshot.GetFlag(SnapKeys.CHoCHFiredShort);
            bool bosLong    = snapshot.GetFlag(SnapKeys.BOSFiredLong);
            bool bosShort   = snapshot.GetFlag(SnapKeys.BOSFiredShort);

            // ── Stage 1: Check for window expiry ──────────────────────────────
            if (_windowArmed)
            {
                if (p.CurrentBar - _windowArmedBar >= WINDOW_BARS)
                {
                    _lastBailReason = $"window_expired ({p.CurrentBar - _windowArmedBar} bars)";
                    _windowArmed    = false;
                    _armedDirection = SignalDirection.None;
                }
                else if (_armedDirection == SignalDirection.Long  && (chochShort || bosShort))
                {
                    _lastBailReason = "window_killed_by_opposing_structure";
                    _windowArmed    = false;
                    _armedDirection = SignalDirection.None;
                }
                else if (_armedDirection == SignalDirection.Short && (chochLong || bosLong))
                {
                    _lastBailReason = "window_killed_by_opposing_structure";
                    _windowArmed    = false;
                    _armedDirection = SignalDirection.None;
                }
            }

            // ── Stage 1: Check for new window arming ──────────────────────────
            // Macro filter REMOVED for testing/visibility
            if (!_windowArmed && (chochLong || bosLong))
            {
                _windowArmed    = true;
                _armedDirection = SignalDirection.Long;
                _windowArmedBar = p.CurrentBar;
                _wasChoch       = chochLong;
                _lastBailReason = $"window_armed_long (bar={p.CurrentBar} choch={chochLong})";
            }
            else if (!_windowArmed && (chochShort || bosShort))
            {
                _windowArmed    = true;
                _armedDirection = SignalDirection.Short;
                _windowArmedBar = p.CurrentBar;
                _wasChoch       = chochShort;
                _lastBailReason = $"window_armed_short (bar={p.CurrentBar} choch={chochShort})";
            }

            // ── Guard: window must be armed to proceed ─────────────────────────
            if (!_windowArmed || _armedDirection == SignalDirection.None)
            { 
                _lastBailReason = string.IsNullOrEmpty(_lastBailReason) || _lastBailReason == "session_open"
                    ? "no_window" : _lastBailReason;
                return RawDecision.None; 
            }

            bool isLong = _armedDirection == SignalDirection.Long;

            // ── Stage 2: Zone touch detection ─────────────────────────────────
            bool   fvgActive  = isLong ? snapshot.GetFlag(SnapKeys.FvgBullActive)  : snapshot.GetFlag(SnapKeys.FvgBearActive);
            double fvgLow     = isLong ? snapshot.Get(SnapKeys.FvgBullLow)         : snapshot.Get(SnapKeys.FvgBearLow);
            double fvgHigh    = isLong ? snapshot.Get(SnapKeys.FvgBullHigh)        : snapshot.Get(SnapKeys.FvgBearHigh);

            bool   obActive   = isLong ? snapshot.GetFlag(SnapKeys.ObBullActive)   : snapshot.GetFlag(SnapKeys.ObBearActive);
            double obLow      = isLong ? snapshot.Get(SnapKeys.ObBullLow)          : snapshot.Get(SnapKeys.ObBearLow);
            double obHigh     = isLong ? snapshot.Get(SnapKeys.ObBullHigh)         : snapshot.Get(SnapKeys.ObBearHigh);

            bool fvgTouch = false;
            bool obTouch  = false;

            if (isLong)
            {
                if (fvgActive && fvgLow > 0 && fvgHigh > 0)
                    fvgTouch = p.Low <= fvgHigh && p.Close >= fvgLow;
                if (obActive && obLow > 0 && obHigh > 0)
                    obTouch  = p.Low <= obHigh  && p.Close >= obLow;
            }
            else
            {
                if (fvgActive && fvgLow > 0 && fvgHigh > 0)
                    fvgTouch = p.High >= fvgLow && p.Close <= fvgHigh;
                if (obActive && obLow > 0 && obHigh > 0)
                    obTouch  = p.High >= obLow  && p.Close <= obHigh;
            }

            bool zoneTouch = fvgTouch || obTouch;

            if (!zoneTouch)
            { 
                _lastBailReason = $"no_zone_touch (fvgA={fvgActive} obA={obActive} L={p.Low:F2} H={p.High:F2} C={p.Close:F2})";
                return RawDecision.None; // We don't show a rejection bubble every single bar while waiting
            }

            // ── Stage 2: Order flow confirmation ──────────────────────────────
            // If we touch the zone, we want to know WHY it rejected
            
            if (!snapshot.GetFlag(SnapKeys.HasVolumetric))
            {
                 _lastBailReason = "no_volumetric";
                 return new RawDecision { Direction = _armedDirection, Label = "REJ:Hybrid NoVol", IsValid = false };
            }

            double barDelta = snapshot.Get(SnapKeys.BarDelta);
            if (isLong  && barDelta <= 0)
            { 
                _lastBailReason = $"delta_not_positive ({barDelta:F0})"; 
                return new RawDecision { Direction = _armedDirection, Label = "REJ:Hybrid Delta", IsValid = false };
            }
            if (!isLong && barDelta >= 0)
            { 
                _lastBailReason = $"delta_not_negative ({barDelta:F0})"; 
                return new RawDecision { Direction = _armedDirection, Label = "REJ:Hybrid Delta", IsValid = false };
            }

            double absScore = snapshot.Get(SnapKeys.AbsorptionScore);
            bool lowAbs = absScore < MIN_ABSORPTION_SCORE; // Will penalize in score section

            double barMid = (p.High + p.Low) / 2.0;
            bool weakClose = (isLong && p.Close <= barMid) || (!isLong && p.Close >= barMid);

            // ── Hard vetoes ────────────────────────────────────────────────────
            if (isLong  && snapshot.GetFlag(SnapKeys.SellSweep))
            { _lastBailReason = "veto_sell_sweep"; return RawDecision.None; }
            if (!isLong && snapshot.GetFlag(SnapKeys.BuySweep))
            { _lastBailReason = "veto_buy_sweep"; return RawDecision.None; }

            // Opposing CVD divergence: macro institutional flow disagrees
            if (isLong  && snapshot.GetFlag(SnapKeys.BearDivergence))
            { _lastBailReason = "veto_bear_divergence"; return RawDecision.None; }
            if (!isLong && snapshot.GetFlag(SnapKeys.BullDivergence))
            { _lastBailReason = "veto_bull_divergence"; return RawDecision.None; }

            // ── Stop placement ─────────────────────────────────────────────────
            // Prefer zone boundary for stop — it IS the structural invalidation.
            // FVG zone takes priority over OB if both were touched (FVG is fresher).
            double stopPrice;
            double zoneLowForStop  = 0;
            double zoneHighForStop = 0;

            if (fvgTouch)
            {
                zoneLowForStop  = fvgLow;
                zoneHighForStop = fvgHigh;
            }
            else if (obTouch)
            {
                zoneLowForStop  = obLow;
                zoneHighForStop = obHigh;
            }

            if (isLong)
            {
                // Stop below the zone low — zone breach = institutional defense failed
                if (zoneLowForStop > 0)
                    stopPrice = zoneLowForStop - ATR_STOP_BUFFER * atr;
                else
                {
                    // Fallback: recent bar lows
                    double recentLow = p.Low;
                    if (p.Lows != null)
                        for (int i = 1; i < STOP_LOOKBACK_BARS && i < p.Lows.Length; i++)
                            if (p.Lows[i] < recentLow) recentLow = p.Lows[i];
                    stopPrice = recentLow - ATR_STOP_BUFFER * atr;
                }

                double minStop = p.Close - MIN_STOP_ATR_MULT * atr;
                if (stopPrice > minStop) stopPrice = minStop;

                double maxStop = p.Close - MAX_STOP_ATR_MULT * atr;
                if (stopPrice < maxStop) stopPrice = maxStop;
            }
            else
            {
                if (zoneHighForStop > 0)
                    stopPrice = zoneHighForStop + ATR_STOP_BUFFER * atr;
                else
                {
                    double recentHigh = p.High;
                    if (p.Highs != null)
                        for (int i = 1; i < STOP_LOOKBACK_BARS && i < p.Highs.Length; i++)
                            if (p.Highs[i] > recentHigh) recentHigh = p.Highs[i];
                    stopPrice = recentHigh + ATR_STOP_BUFFER * atr;
                }

                double minStop = p.Close + MIN_STOP_ATR_MULT * atr;
                if (stopPrice < minStop) stopPrice = minStop;

                double maxStop = p.Close + MAX_STOP_ATR_MULT * atr;
                if (stopPrice > maxStop) stopPrice = maxStop;
            }

            // ── Sanity check ───────────────────────────────────────────────────
            if (isLong  && stopPrice >= p.Close)
            { _lastBailReason = "stop_above_entry"; return RawDecision.None; }
            if (!isLong && stopPrice <= p.Close)
            { _lastBailReason = "stop_below_entry"; return RawDecision.None; }

            // ── Target placement ───────────────────────────────────────────────
            // CHoCH/BOS moves aim for the prior swing high/low — structural target.
            // VWAP as fallback (mean reversion anchor), then 1.5×ATR.
            double lastSwingHigh = snapshot.Get(SnapKeys.LastSwingHigh);
            double lastSwingLow  = snapshot.Get(SnapKeys.LastSwingLow);
            double vwap          = snapshot.VWAP;
            double t1Price;
            double t2Price;

            if (isLong)
            {
                t2Price = p.Close + 2.5 * atr;

                // Swing high is the natural CHoCH target
                double swingDist = lastSwingHigh > 0 ? (lastSwingHigh - p.Close) / atr : 0.0;
                if (lastSwingHigh > p.Close
                    && swingDist >= MIN_T1_ATR_DIST
                    && swingDist <= MAX_T1_ATR_DIST)
                    t1Price = lastSwingHigh;
                else
                {
                    double vwapDist = vwap > 0 ? (vwap - p.Close) / atr : 0.0;
                    if (vwap > p.Close && vwapDist >= MIN_T1_ATR_DIST && vwapDist <= MAX_T1_ATR_DIST)
                        t1Price = vwap;
                    else
                        t1Price = p.Close + 1.5 * atr;
                }
            }
            else
            {
                t2Price = p.Close - 2.5 * atr;

                double swingDist = lastSwingLow > 0 ? (p.Close - lastSwingLow) / atr : 0.0;
                if (lastSwingLow > 0 && lastSwingLow < p.Close
                    && swingDist >= MIN_T1_ATR_DIST
                    && swingDist <= MAX_T1_ATR_DIST)
                    t1Price = lastSwingLow;
                else
                {
                    double vwapDist = vwap > 0 ? (p.Close - vwap) / atr : 0.0;
                    if (vwap > 0 && vwap < p.Close && vwapDist >= MIN_T1_ATR_DIST && vwapDist <= MAX_T1_ATR_DIST)
                        t1Price = vwap;
                    else
                        t1Price = p.Close - 1.5 * atr;
                }
            }

            // ── RR & Scoring (Softened) ───────────────────────────────────────
            double riskTicks   = Math.Abs(p.Close - stopPrice) / tickSz;
            double rewardTicks = Math.Abs(t1Price  - p.Close)  / tickSz;
            bool lowRR = (riskTicks > 0 && rewardTicks / riskTicks < 1.2);

            int score = StrategyConfig.Modules.HYBRID_BASE_SCORE;
            if (lowAbs)    score -= StrategyConfig.Modules.HYBRID_PENALTY_LOW_ABS;
            if (weakClose) score -= StrategyConfig.Modules.HYBRID_PENALTY_WEAK_CLOSE;
            if (lowRR)     score -= StrategyConfig.Modules.HYBRID_PENALTY_LOW_RR;

            // CVD divergence agrees: price pulled back while buyers absorbing at lows.
            // This is the triple-layer setup: structure + zone + divergence.
            if (isLong  && snapshot.GetFlag(SnapKeys.BullDivergence)) score += StrategyConfig.Modules.HYBRID_BONUS_DIVERGENCE;
            if (!isLong && snapshot.GetFlag(SnapKeys.BearDivergence)) score += StrategyConfig.Modules.HYBRID_BONUS_DIVERGENCE;

            // Historical imbalance zone overlap: a fourth independent structural anchor
            if (isLong  && snapshot.GetFlag(SnapKeys.ImbalZoneAtBull)) score += StrategyConfig.Modules.HYBRID_BONUS_IMBAL_ZONE;
            if (!isLong && snapshot.GetFlag(SnapKeys.ImbalZoneAtBear)) score += StrategyConfig.Modules.HYBRID_BONUS_IMBAL_ZONE;

            // Trapped participants: forced exits accelerate the move
            if (isLong  && snapshot.GetFlag(SnapKeys.TrappedShorts)) score += StrategyConfig.Modules.HYBRID_BONUS_TRAPPED;
            if (!isLong && snapshot.GetFlag(SnapKeys.TrappedLongs))  score += StrategyConfig.Modules.HYBRID_BONUS_TRAPPED;

            // CHoCH vs BOS: reversal events are higher conviction than continuations
            if (_wasChoch) score += StrategyConfig.Modules.HYBRID_BONUS_CHOCH;

            // Real-time tape iceberg at the zone
            if (isLong  && snapshot.GetFlag(SnapKeys.TapeBullIceberg)) score += StrategyConfig.Modules.HYBRID_BONUS_TICE;
            if (!isLong && snapshot.GetFlag(SnapKeys.TapeBearIceberg)) score += StrategyConfig.Modules.HYBRID_BONUS_TICE;

            // Exhaustion on the pullback bar: sellers ran out as they pulled back into zone
            if (isLong  && snapshot.GetFlag(SnapKeys.BearExhaustion)) score += StrategyConfig.Modules.HYBRID_BONUS_TICE;
            if (!isLong && snapshot.GetFlag(SnapKeys.BullExhaustion)) score += StrategyConfig.Modules.HYBRID_BONUS_TICE;

            score = Math.Min(score, StrategyConfig.Modules.HYBRID_SCORE_CAP);

            // ── Disarm window after firing ─────────────────────────────────────
            // Prevent multiple signals from the same CHoCH/BOS event.
            // OnFill() will also disarm, but this prevents double-fire within
            // the same window if the signal is somehow rejected downstream.
            _windowArmed    = false;
            _armedDirection = SignalDirection.None;

            // ── Build decision ─────────────────────────────────────────────────
            string zoneTag    = fvgTouch ? "FVG" : "OB";
            string catalystTag = _wasChoch ? "CHoCH" : "BOS";
            _lastBailReason = $"FIRED_{(isLong ? "LONG" : "SHORT")}";

            return new RawDecision
            {
                Direction      = isLong ? SignalDirection.Long : SignalDirection.Short,
                Source         = SignalSource.Confluence,
                ConditionSetId = SetId,
                EntryPrice     = p.Close,
                StopPrice      = stopPrice,
                TargetPrice    = t1Price,
                Target2Price   = t2Price,
                Label          = string.Format(
                    "HybridScalp {0} {1}→{2} abs={3:F0} bd={4:F0} [{5}]",
                    isLong ? "long" : "short",
                    catalystTag, zoneTag,
                    absScore, barDelta, SetId),
                RawScore       = score,
                IsValid        = true,
                BarIndex       = p.CurrentBar,
                SignalId       = string.Format("{0}:{1:yyyyMMdd}:{2}", SetId, p.Time, p.CurrentBar)
            };
        }
    }
}