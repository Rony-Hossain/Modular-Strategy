#region Using declarations
using System;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ========================================================================
    // SUPPORT RESISTANCE RESULT
    // ========================================================================
    //
    // COMPLETE IMPLEMENTATION CHECKLIST
    //
    // A. Type contract
    //   [x] public readonly struct — immutable, value type, no reference-identity semantics
    //   [x] { get; } properties only — no setters; downstream consumers read, never write
    //   [x] All fields set in one constructor — no partial initialization paths
    //   [x] static readonly Empty sentinel — all zeros, IsValid = false
    //   [x] No business logic or behavioral methods — constructor and sentinel only
    //
    // B. Field groups
    //   [x] Validity          — IsValid
    //   [x] Nearest zones     — NearestSupport, NearestResistance
    //   [x] Strongest zones   — StrongestSupport, StrongestResistance
    //   [x] Positional flags  — AtSupport, AtResistance
    //   [x] Distance          — TicksToSupport, TicksToResistance, ATRsToSupport, ATRsToResistance
    //   [x] Zone counts       — ActiveSupportCount, ActiveResistanceCount
    //   [x] Volume profile    — POC, VAHigh, VALow, POCSkew, ProfileIsReady
    //   [x] Swing passthrough — SwingHighH1/Low, SwingHighH2/Low, SwingHighH4/Low
    //   [x] Pivot passthrough — PivotPP, PivotR1, PivotR2, PivotS1, PivotS2
    //
    // C. Passthrough field contract
    //   Swing and pivot passthrough fields exist so HostStrategy can publish the
    //   existing SnapKeys (H1SwingHigh, PivotR1, etc.) from one place without
    //   reaching into internal engine source module state.
    //   These are raw prices from SwingLevelSource and PivotLevelSource.
    //   0.0 when the level is not yet detected or has been invalidated.
    //   HostStrategy publishes:
    //     snapshot.Set(SnapKeys.H1SwingHigh, result.SwingHighH1)
    //     ... (same pattern for all 11 passthrough fields)
    //
    // D. Nearest vs Strongest distinction
    //   NearestSupport  = closest qualifying support zone on the correct side of price
    //                     (ZonePrice <= price + mergeRadius)
    //   StrongestSupport = active support zone with highest Strength score regardless
    //                     of distance
    //   These are often different zones. Nearest drives AtSupport / distance fields.
    //   Strongest drives ConfluenceEngine Layer B scoring.
    //
    // E. Distance field semantics
    //   TicksToSupport    = (price - NearestSupport.ZonePrice) / tickSize
    //                       0.0 when AtSupport = true
    //                       0.0 when NearestSupport.IsValid = false
    //   TicksToResistance = (NearestResistance.ZonePrice - price) / tickSize
    //                       same zero conditions
    //   ATRsToSupport     = TicksToSupport * tickSize / ATR
    //   ATRsToResistance  = TicksToResistance * tickSize / ATR
    //   All four are zero when the corresponding zone is invalid or price is at the zone.
    //
    // F. AtSupport / AtResistance flag semantics
    //   AtSupport    = NearestSupport.IsValid
    //                  AND Math.Abs(price - NearestSupport.ZonePrice) < ProximityATRMult × ATR
    //   AtResistance = NearestResistance.IsValid
    //                  AND Math.Abs(price - NearestResistance.ZonePrice) < ProximityATRMult × ATR
    //   These mirror ConfluenceEngine's proximity gate exactly.
    //
    // G. What does NOT belong here
    //   [x] HasFootprintConfirmation  — FootprintEntryAdvisor owns this
    //   [x] ZoneAcceptanceState       — trade management semantics, not structural truth
    //   [x] ZoneDefenseState          — same
    //   [x] Any derived scoring       — SupportResistanceEngine.BuildResult() computes;
    //                                   this struct carries the result unchanged
    //   [x] EnableSRLayerBForSMFNative — ConfluenceEngine integration flag, not here
    // ========================================================================

    /// <summary>
    /// Immutable output of SupportResistanceEngine.Update().
    /// The single public S/R boundary — downstream consumers read only this type.
    ///
    /// Produced every primary bar. Carried through SupportResistanceEngine.LastResult
    /// and passed directly to ConfluenceEngine, OrderManager, and HostStrategy.
    ///
    /// CONSUMERS:
    ///   HostStrategy        — publishes SnapKeys from passthrough fields
    ///   ConfluenceEngine    — reads NearestSupport/Resistance, AtSupport/Resistance,
    ///                         StrongestSupport/Resistance for Layer B scoring
    ///   OrderManager        — reads AtSupport/Resistance, TicksToSupport/Resistance
    ///                         for structural gate and zone trail
    ///
    /// Always check <see cref="IsValid"/> before reading any other field.
    /// Use <see cref="Empty"/> as the safe default on failure paths.
    /// </summary>
    public readonly struct SupportResistanceResult
    {
        // ====================================================================
        // VALIDITY
        // ====================================================================

        /// <summary>
        /// False when the engine has not yet produced a valid result —
        /// typically during warm-up before BARS_REQUIRED_TO_TRADE primary bars.
        /// All other fields are zero / SRZone.Empty when IsValid is false.
        /// </summary>
        public bool IsValid { get; }

        // ====================================================================
        // NEAREST ZONES
        // Closest active zone to current price, per direction.
        // Drives AtSupport / AtResistance flags and distance fields.
        // See checklist D for nearest vs strongest distinction.
        // ====================================================================

        /// <summary>
        /// Closest active support zone on the correct side of current price.
        /// Directionally strict: only zones where ZonePrice &lt;= price + mergeRadius
        /// qualify. Returns SRZone.Empty when no qualifying support zone exists.
        /// Check IsValid before reading.
        /// </summary>
        public SRZone NearestSupport { get; }

        /// <summary>
        /// Closest active resistance zone on the correct side of current price.
        /// Directionally strict: only zones where ZonePrice &gt;= price - mergeRadius
        /// qualify. Returns SRZone.Empty when no qualifying resistance zone exists.
        /// Check IsValid before reading.
        /// </summary>
        public SRZone NearestResistance { get; }

        // ====================================================================
        // STRONGEST ZONES
        // Highest-Strength active zone, per direction, regardless of distance.
        // Drives ConfluenceEngine Layer B scoring.
        // ====================================================================

        /// <summary>
        /// Active support zone with the highest Strength score.
        /// May be far from current price. Used by ConfluenceEngine Layer B
        /// to measure zone confluence quality, not proximity.
        /// Returns SRZone.Empty when no active support zone exists.
        /// </summary>
        public SRZone StrongestSupport { get; }

        /// <summary>
        /// Active resistance zone with the highest Strength score.
        /// Returns SRZone.Empty when no active resistance zone exists.
        /// </summary>
        public SRZone StrongestResistance { get; }

        // ====================================================================
        // POSITIONAL FLAGS
        // True when price is within ProximityATRMult × ATR of the nearest zone.
        // ====================================================================

        /// <summary>
        /// True when price is within ProximityATRMult × ATR of NearestSupport.ZonePrice
        /// and NearestSupport.IsValid.
        /// Mirrors the proximity gate used by ConfluenceEngine Layer B.
        /// Published to SnapKeys for OrderManager structural gate.
        /// </summary>
        public bool AtSupport { get; }

        /// <summary>
        /// True when price is within ProximityATRMult × ATR of NearestResistance.ZonePrice
        /// and NearestResistance.IsValid.
        /// </summary>
        public bool AtResistance { get; }

        // ====================================================================
        // DISTANCE
        // All four are 0.0 when the corresponding zone is invalid or AtZone is true.
        // ====================================================================

        /// <summary>
        /// Distance from current price to NearestSupport.ZonePrice in ticks.
        /// Formula: (price - NearestSupport.ZonePrice) / tickSize.
        /// Always &gt;= 0. Zero when AtSupport or NearestSupport.IsValid = false.
        /// </summary>
        public double TicksToSupport { get; }

        /// <summary>
        /// Distance from current price to NearestResistance.ZonePrice in ticks.
        /// Formula: (NearestResistance.ZonePrice - price) / tickSize.
        /// Always &gt;= 0. Zero when AtResistance or NearestResistance.IsValid = false.
        /// </summary>
        public double TicksToResistance { get; }

        /// <summary>
        /// TicksToSupport expressed as a multiple of ATR.
        /// Formula: TicksToSupport × tickSize / ATR.
        /// Zero when ATR is zero or TicksToSupport is zero.
        /// </summary>
        public double ATRsToSupport { get; }

        /// <summary>
        /// TicksToResistance expressed as a multiple of ATR.
        /// Formula: TicksToResistance × tickSize / ATR.
        /// Zero when ATR is zero or TicksToResistance is zero.
        /// </summary>
        public double ATRsToResistance { get; }

        // ====================================================================
        // ZONE COUNTS
        // ====================================================================

        /// <summary>
        /// Number of active (non-broken, non-expired) support zones
        /// currently in the engine's support buffer.
        /// </summary>
        public int ActiveSupportCount { get; }

        /// <summary>
        /// Number of active resistance zones currently in the engine's
        /// resistance buffer.
        /// </summary>
        public int ActiveResistanceCount { get; }

        // ====================================================================
        // VOLUME PROFILE
        // Sourced from VolumeProfileLevelSource. Session-scoped.
        // All four are 0.0 / false until ProfileIsReady is true.
        // ====================================================================

        /// <summary>
        /// Point of Control — price with highest cumulative session volume.
        /// 0.0 until ProfileIsReady. Published to SnapKeys.POC by HostStrategy.
        /// </summary>
        public double POC { get; }

        /// <summary>
        /// Value Area High — upper boundary of the session 70% volume zone.
        /// 0.0 until ProfileIsReady. Published to SnapKeys.VAHigh by HostStrategy.
        /// </summary>
        public double VAHigh { get; }

        /// <summary>
        /// Value Area Low — lower boundary of the session 70% volume zone.
        /// 0.0 until ProfileIsReady. Published to SnapKeys.VALow by HostStrategy.
        /// </summary>
        public double VALow { get; }

        /// <summary>
        /// POC skew: volume above POC / volume below POC.
        /// 1.0 (balanced) until ProfileIsReady.
        /// Published to SnapKeys.POCSkew by HostStrategy.
        /// </summary>
        public double POCSkew { get; }

        /// <summary>
        /// True when the session profile has accumulated at least
        /// MinProfileBars primary bars and POC / VAH / VAL are meaningful.
        /// False during the first ~100 minutes of each RTH session.
        /// </summary>
        public bool ProfileIsReady { get; }

        // ====================================================================
        // SWING PASSTHROUGH
        // Raw swing prices from SwingLevelSource.
        // Published by HostStrategy to preserve existing SnapKey compatibility.
        // 0.0 when not yet detected or invalidated by close-through.
        // ====================================================================

        /// <summary>Last confirmed 1H swing high. 0.0 if none active.</summary>
        public double SwingHighH1 { get; }

        /// <summary>Last confirmed 1H swing low. 0.0 if none active.</summary>
        public double SwingLowH1 { get; }

        /// <summary>Last confirmed 2H swing high. 0.0 if none active.</summary>
        public double SwingHighH2 { get; }

        /// <summary>Last confirmed 2H swing low. 0.0 if none active.</summary>
        public double SwingLowH2 { get; }

        /// <summary>Last confirmed 4H swing high. 0.0 if none active.</summary>
        public double SwingHighH4 { get; }

        /// <summary>Last confirmed 4H swing low. 0.0 if none active.</summary>
        public double SwingLowH4 { get; }

        // ====================================================================
        // PIVOT PASSTHROUGH
        // Daily floor trader pivot prices from PivotLevelSource.
        // Published by HostStrategy to preserve existing SnapKey compatibility.
        // 0.0 until the first PrevDay data is available.
        // ====================================================================

        /// <summary>Floor Trader Pivot Point. Acts as both support and resistance.</summary>
        public double PivotPP { get; }

        /// <summary>Pivot Resistance 1 — first resistance above PP.</summary>
        public double PivotR1 { get; }

        /// <summary>Pivot Resistance 2 — extended resistance.</summary>
        public double PivotR2 { get; }

        /// <summary>Pivot Support 1 — first support below PP.</summary>
        public double PivotS1 { get; }

        /// <summary>Pivot Support 2 — extended support.</summary>
        public double PivotS2 { get; }

        // ====================================================================
        // CONSTRUCTOR
        // All 29 fields set positionally. No partial initialization.
        // Follows FootprintResult convention exactly.
        // ====================================================================

        public SupportResistanceResult(
            bool    isValid,
            SRZone  nearestSupport,
            SRZone  nearestResistance,
            SRZone  strongestSupport,
            SRZone  strongestResistance,
            bool    atSupport,
            bool    atResistance,
            double  ticksToSupport,
            double  ticksToResistance,
            double  atrsToSupport,
            double  atrsToResistance,
            int     activeSupportCount,
            int     activeResistanceCount,
            double  poc,
            double  vaHigh,
            double  vaLow,
            double  pocSkew,
            bool    profileIsReady,
            double  swingHighH1,
            double  swingLowH1,
            double  swingHighH2,
            double  swingLowH2,
            double  swingHighH4,
            double  swingLowH4,
            double  pivotPP,
            double  pivotR1,
            double  pivotR2,
            double  pivotS1,
            double  pivotS2)
        {
            IsValid               = isValid;
            NearestSupport        = nearestSupport;
            NearestResistance     = nearestResistance;
            StrongestSupport      = strongestSupport;
            StrongestResistance   = strongestResistance;
            AtSupport             = atSupport;
            AtResistance          = atResistance;
            TicksToSupport        = ticksToSupport;
            TicksToResistance     = ticksToResistance;
            ATRsToSupport         = atrsToSupport;
            ATRsToResistance      = atrsToResistance;
            ActiveSupportCount    = activeSupportCount;
            ActiveResistanceCount = activeResistanceCount;
            POC                   = poc;
            VAHigh                = vaHigh;
            VALow                 = vaLow;
            POCSkew               = pocSkew;
            ProfileIsReady        = profileIsReady;
            SwingHighH1           = swingHighH1;
            SwingLowH1            = swingLowH1;
            SwingHighH2           = swingHighH2;
            SwingLowH2            = swingLowH2;
            SwingHighH4           = swingHighH4;
            SwingLowH4            = swingLowH4;
            PivotPP               = pivotPP;
            PivotR1               = pivotR1;
            PivotR2               = pivotR2;
            PivotS1               = pivotS1;
            PivotS2               = pivotS2;
        }

        // ====================================================================
        // SENTINEL
        // ====================================================================

        /// <summary>
        /// Safe default for failure paths and pre-initialization state.
        ///
        /// IsValid = false. All zone fields = SRZone.Empty. All numeric fields = 0.0.
        /// POCSkew = 0.0 (not 1.0 — zero signals "no data" unambiguously;
        /// consumers guard on ProfileIsReady before reading POCSkew).
        ///
        /// Matches FootprintResult.Zero convention — static readonly field,
        /// not a property.
        /// </summary>
        public static readonly SupportResistanceResult Empty = new SupportResistanceResult(
            isValid:               false,
            nearestSupport:        SRZone.Empty,
            nearestResistance:     SRZone.Empty,
            strongestSupport:      SRZone.Empty,
            strongestResistance:   SRZone.Empty,
            atSupport:             false,
            atResistance:          false,
            ticksToSupport:        0.0,
            ticksToResistance:     0.0,
            atrsToSupport:         0.0,
            atrsToResistance:      0.0,
            activeSupportCount:    0,
            activeResistanceCount: 0,
            poc:                   0.0,
            vaHigh:                0.0,
            vaLow:                 0.0,
            pocSkew:               0.0,
            profileIsReady:        false,
            swingHighH1:           0.0,
            swingLowH1:            0.0,
            swingHighH2:           0.0,
            swingLowH2:            0.0,
            swingHighH4:           0.0,
            swingLowH4:            0.0,
            pivotPP:               0.0,
            pivotR1:               0.0,
            pivotR2:               0.0,
            pivotS1:               0.0,
            pivotS2:               0.0);
    }
}
