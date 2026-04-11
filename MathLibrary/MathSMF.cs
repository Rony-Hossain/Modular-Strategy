#region Using declarations
using System;
#endregion

namespace MathLogic
{
    // =========================================================================
    // MathSMF.cs — Smart Money Flow signal detection extension
    //
    // Extends MathFlow with the two signal-detection functions that are specific
    // to the SmartMoneyFlowCloudBOSWavesV3 indicator and are not in MathFlow:
    //
    //   SMF_Flip_Detect    One-shot latched regime flip detection (SwitchUp/SwitchDn).
    //                      The indicator compares current regime against prevLS2 (the
    //                      regime from the prior bar) with per-bar one-shot latching to
    //                      prevent double-firing. MathFlow.Regime_Update.DidFlip covers
    //                      the flip itself but not the prevLS2 semantic or the latch.
    //
    //   SMF_Retest_Detect  Cooldown-gated wick probe to basis.
    //                      MathFlow.Regime_RetestType detects the probe condition but
    //                      has no cooldown or one-shot latch. This adds both.
    //
    // ALL OTHER SMF math already exists in MathFlow — use it directly:
    //   CLV computation      → MathFlow.CLV_Calculate, CLV_RawFlow, CLV_RollingUpdate
    //   Flow smoothing        → MathFlow.Flow_Calculate (wraps EmaStep + Flow_Strength)
    //   Band multiplier       → MathFlow.Flow_BandMultiplier
    //   Basis EMA             → MathFlow.EmaStep (incremental, caller owns state)
    //   Basis ALMA            → MathFlow.ALMA_BuildWeights, ALMA_Calculate
    //   Adaptive bands        → MathFlow.Band_Calculate
    //   Band cross detection  → MathFlow.Regime_BandCrossConfirmed
    //   Regime state machine  → MathFlow.Regime_Update
    //   Retest type           → MathFlow.Regime_RetestType (no cooldown — use SMF_Retest_Detect)
    //   Gauge                 → MathFlow.Gauge_TanhInput, Gauge_Update
    //   Impulse check         → MathFlow.IsImpulse
    //   NonConf check         → MathFlow.IsNonConfirmation
    //
    // Design contract (same as MathFlow):
    //   - Pure stateless functions. Caller owns and maintains all state.
    //   - No LINQ, no allocations in hot paths.
    //   - sealed class result types with IsValid flag and static None sentinel.
    //   - Thread-safe: no shared mutable state.
    // =========================================================================


    // =========================================================================
    // RESULT TYPES
    // =========================================================================

    /// <summary>
    /// Result of SMF_Flip_Detect for one bar.
    /// Carries one-shot latched flip events and their downstream derivatives.
    /// </summary>
    public sealed class SMFFlipResult
    {
        /// <summary>Regime just flipped to Bullish this bar (one-shot, latched).</summary>
        public bool SwitchUp     { get; }
        /// <summary>Regime just flipped to Bearish this bar (one-shot, latched).</summary>
        public bool SwitchDn     { get; }
        /// <summary>SwitchUp AND flow strength >= impulseThreshold.</summary>
        public bool ImpulseUp    { get; }
        /// <summary>SwitchDn AND flow strength >= impulseThreshold.</summary>
        public bool ImpulseDn    { get; }
        /// <summary>SwitchUp but mfSmooth < 0 (price broke up, flow still negative).</summary>
        public bool NonConfLong  { get; }
        /// <summary>SwitchDn but mfSmooth > 0 (price broke down, flow still positive).</summary>
        public bool NonConfShort { get; }
        public bool IsValid      { get; }

        public SMFFlipResult(
            bool switchUp,    bool switchDn,
            bool impulseUp,   bool impulseDn,
            bool nonConfLong, bool nonConfShort,
            bool isValid)
        {
            SwitchUp     = switchUp;
            SwitchDn     = switchDn;
            ImpulseUp    = impulseUp;
            ImpulseDn    = impulseDn;
            NonConfLong  = nonConfLong;
            NonConfShort = nonConfShort;
            IsValid      = isValid;
        }

        public static readonly SMFFlipResult None =
            new SMFFlipResult(false, false, false, false, false, false, false);
    }

    /// <summary>
    /// Result of SMF_Retest_Detect for one bar.
    /// </summary>
    public sealed class SMFRetestResult
    {
        /// <summary>
        /// Bull retest fired: regime==Bullish AND low dipped below basis,
        /// cooldown elapsed, not a flip bar.
        /// </summary>
        public bool BullRetestOk { get; }

        /// <summary>
        /// Bear retest fired: regime==Bearish AND high spiked above basis,
        /// cooldown elapsed, not a flip bar.
        /// </summary>
        public bool BearRetestOk { get; }
        public bool IsValid      { get; }

        public SMFRetestResult(bool bullRetestOk, bool bearRetestOk, bool isValid)
        {
            BullRetestOk = bullRetestOk;
            BearRetestOk = bearRetestOk;
            IsValid      = isValid;
        }

        public static readonly SMFRetestResult None = new SMFRetestResult(false, false, false);
    }


    // =========================================================================
    // MathSMF
    // =========================================================================

    public static class MathSMF
    {
        // =====================================================================
        // SMF_Flip_Detect
        //
        // Detects one-shot latched regime flip events (SwitchUp / SwitchDn)
        // and their derivatives (Impulse, NonConf) matching the indicator's
        // SmartMoneyFlowCloudBOSWavesV3 logic exactly.
        //
        // The indicator compares current regime (lastSignal) against prevLS2.
        // Despite the name, prevLS2 is set to lastSignal every bar — making it
        // the prior bar's regime, not two bars ago. prevRegime parameter mirrors
        // this: caller passes the regime from the previous bar.
        //
        // Delegates to MathFlow.IsImpulse() and MathFlow.IsNonConfirmation() —
        // do not duplicate those checks in the caller.
        //
        // Parity: exact match to indicator lines 704-721, 774-800.
        // =====================================================================

        /// <summary>
        /// Detect regime flip events with one-shot latching.
        ///
        /// Caller maintains all six ref latch fields between bars.
        /// Initialise all to int.MinValue before the first bar.
        ///
        /// Parameters:
        ///   currentRegime    — regime this bar (from MathFlow.Regime_Update)
        ///   prevRegime       — regime last bar (caller saves at end of each bar)
        ///   mfSmooth         — smoothed money flow (MathFlow.Flow_Calculate.MFSmooth)
        ///   strength         — flow strength (MathFlow.Flow_Calculate.Strength)
        ///   impulseThreshold — minimum strength to qualify as impulse
        ///   currentBar       — current bar index (for latch comparison)
        /// </summary>
        public static SMFFlipResult SMF_Flip_Detect(
            RegimeState currentRegime,
            RegimeState prevRegime,
            double      mfSmooth,
            double      strength,
            double      impulseThreshold,
            int         currentBar,
            ref int     lastSwitchUpBar,
            ref int     lastSwitchDnBar,
            ref int     lastImpulseUpBar,
            ref int     lastImpulseDnBar,
            ref int     lastNonConfLongBar,
            ref int     lastNonConfShortBar)
        {
            // Raw flip: regime changed vs prior bar
            bool rawUp = currentRegime == RegimeState.Bullish
                      && prevRegime    == RegimeState.Bearish;
            bool rawDn = currentRegime == RegimeState.Bearish
                      && prevRegime    == RegimeState.Bullish;

            // One-shot latch — fire only once per bar; opposing flip blocks same bar
            bool switchUp = false;
            bool switchDn = false;

            if (rawUp && lastSwitchUpBar != currentBar && lastSwitchDnBar != currentBar)
            {
                switchUp        = true;
                lastSwitchUpBar = currentBar;
            }
            if (rawDn && lastSwitchDnBar != currentBar && lastSwitchUpBar != currentBar)
            {
                switchDn        = true;
                lastSwitchDnBar = currentBar;
            }

            // Impulse — delegate to MathFlow, apply per-bar latch
            bool impulseUp = false;
            bool impulseDn = false;

            if (switchUp && MathFlow.IsImpulse(true, strength, impulseThreshold)
                         && lastImpulseUpBar != currentBar)
            {
                impulseUp        = true;
                lastImpulseUpBar = currentBar;
            }
            if (switchDn && MathFlow.IsImpulse(true, strength, impulseThreshold)
                         && lastImpulseDnBar != currentBar)
            {
                impulseDn        = true;
                lastImpulseDnBar = currentBar;
            }

            // Non-confirmation — delegate to MathFlow, apply per-bar latch
            bool nonConfLong  = false;
            bool nonConfShort = false;

            if (switchUp
                && MathFlow.IsNonConfirmation(RegimeState.Bullish, true, mfSmooth)
                && lastNonConfLongBar != currentBar)
            {
                nonConfLong         = true;
                lastNonConfLongBar  = currentBar;
            }
            if (switchDn
                && MathFlow.IsNonConfirmation(RegimeState.Bearish, true, mfSmooth)
                && lastNonConfShortBar != currentBar)
            {
                nonConfShort         = true;
                lastNonConfShortBar  = currentBar;
            }

            return new SMFFlipResult(
                switchUp, switchDn,
                impulseUp, impulseDn,
                nonConfLong, nonConfShort,
                isValid: true);
        }

        // =====================================================================
        // SMF_Retest_Detect
        //
        // Applies cooldown gating and flip-bar guard to a wick probe detected
        // by MathFlow.Regime_RetestType(). The indicator enforces:
        //   - DotCooldown bars must have elapsed since last retest of same type
        //   - Retest cannot fire on the same bar as a SwitchUp or SwitchDn
        //     (AllowRetestOnFlipBar = false in our configuration)
        //
        // Call MathFlow.Regime_RetestType() first, pass its result here.
        //
        // Parity: exact match to indicator lines 726-764.
        // =====================================================================

        /// <summary>
        /// Gate a detected wick probe with cooldown and flip-bar exclusion.
        ///
        /// Caller maintains lastBullDotBar and lastBearDotBar between bars.
        /// Initialise both to int.MinValue before the first bar.
        ///
        /// Parameters:
        ///   retestType     — from MathFlow.Regime_RetestType(): +1=bull, -1=bear, 0=none
        ///   currentBar     — current bar index
        ///   dotCooldown    — minimum bars between retests (0 = no cooldown)
        ///   switchUpFired  — SwitchUp fired this bar (from SMF_Flip_Detect)
        ///   switchDnFired  — SwitchDn fired this bar (from SMF_Flip_Detect)
        /// </summary>
        public static SMFRetestResult SMF_Retest_Detect(
            int     retestType,
            int     currentBar,
            int     dotCooldown,
            bool    switchUpFired,
            bool    switchDnFired,
            ref int lastBullDotBar,
            ref int lastBearDotBar)
        {
            if (retestType == 0)              return SMFRetestResult.None;
            if (switchUpFired || switchDnFired) return SMFRetestResult.None;  // no retest on flip bar

            bool bullRetestOk = false;
            bool bearRetestOk = false;

            if (retestType == 1)   // bull retest probe detected
            {
                bool coolOk = dotCooldown == 0
                    || lastBullDotBar == int.MinValue
                    || (currentBar - lastBullDotBar) >= dotCooldown;

                if (coolOk)
                {
                    bullRetestOk   = true;
                    lastBullDotBar = currentBar;
                }
            }
            else if (retestType == -1)   // bear retest probe detected
            {
                bool coolOk = dotCooldown == 0
                    || lastBearDotBar == int.MinValue
                    || (currentBar - lastBearDotBar) >= dotCooldown;

                if (coolOk)
                {
                    bearRetestOk   = true;
                    lastBearDotBar = currentBar;
                }
            }

            return new SMFRetestResult(bullRetestOk, bearRetestOk, isValid: true);
        }
    }
}
