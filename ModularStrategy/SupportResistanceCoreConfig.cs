#region Using declarations
using System;
using MathLogic;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ========================================================================
    // SUPPORT RESISTANCE CORE CONFIG
    // ========================================================================
    //
    // COMPLETE IMPLEMENTATION CHECKLIST
    //
    // A. Type contract
    //   [x] readonly struct — value type, immutable, no reference-identity semantics
    //   [x] All parameters validated in constructor — no silent misconfiguration
    //   [x] ForInstrument() is the canonical factory — reads RoundNumberInterval
    //       from InstrumentSpecs, the single source of truth for instrument metadata
    //   [x] Default property retained for tests and fallback contexts only —
    //       production code must call ForInstrument(ResolveInstrument())
    //   [x] No logic — pure data contract
    //
    // B. Parameter groups
    //   [x] Clustering      — MergeRadiusATRMult, ProximityATRMult
    //   [x] Lifecycle       — InvalidationTicks, SwingExpiryBarsH1/H2/H4
    //   [x] Volume profile  — MinProfileBars, ValueAreaCoverage
    //   [x] Storage         — MaxActiveZonesPerSide, MaxSwingsPerTF, MaxZoneStrength
    //   [x] Weights         — one field per source type, matching LevelRegistry hierarchy
    //   [x] Round numbers   — RoundNumberInterval sourced from InstrumentSpecs
    //
    // C. Frozen v1 values (from design review — do not change without a documented decision)
    //   MergeRadiusATRMult    = 0.50   (matches STACK_ATR_MULT in LevelRegistry)
    //   ProximityATRMult      = 0.35   (matches PROXIMITY_ATR_MULT in LevelRegistry)
    //   InvalidationTicks     = 3.0    (matches INVALIDATION_TICKS in HTFLevelEngine)
    //   SwingExpiryBarsH1     = 2000   (~7 trading days at 5-min primary)
    //   SwingExpiryBarsH2     = 4000   (~14 trading days)
    //   SwingExpiryBarsH4     = 16000  (~56 trading days — H4 structure is long-lived)
    //   MinProfileBars        = 20     (matches MIN_PROFILE_BARS guard in HostStrategy)
    //   ValueAreaCoverage     = 0.70   (standard 70% value area)
    //   MaxActiveZonesPerSide = 20
    //   MaxSwingsPerTF        = 3
    //   MaxZoneStrength       = 60     (cap on summed source weights; must be >= 20)
    //   WeightSwingH4         = 20     (highest — institutional timeframe)
    //   WeightSwingH2         = 16
    //   WeightPDH_PDL         = 15
    //   WeightLondon          = 15
    //   WeightSwingH1         = 12
    //   WeightPOC             =  8
    //   WeightPivotR1S1       = 10
    //   WeightNY              = 10
    //   WeightORB             =  6
    //   WeightPivotR2S2       =  6
    //   WeightVA              =  5
    //   WeightPivotPP         =  4
    //   WeightRoundNumber     =  3     (lowest — contextual only)
    //   RoundNumberInterval: sourced from InstrumentSpecs.Get(instrument).RoundNumberInterval
    //     ES  / MES  = 25.0  (from InstrumentSpecs)
    //     NQ  / MNQ  = 100.0 (from InstrumentSpecs)
    //
    // D. Zero-weight sources — explicit design choice
    //   Setting any weight to zero disables that source entirely.
    //   The constructor permits zero weights to support research scenarios
    //   where a source must be surgically disabled without code changes.
    //   In production v1, all weights are positive.
    //   This is intentional permissiveness, not an oversight.
    //
    // E. What does NOT belong here
    //   [ ] EnableSRLayerBForSMFNative — ConfluenceEngine integration flag, not
    //       engine-truth config. SREngine produces identical structural output
    //       regardless of how downstream consumers use the result.
    //   [ ] Footprint parameters — SREngine does not consume FootprintResult
    //   [ ] Signal scoring parameters — SREngine has no signal awareness
    //   [ ] Trade management parameters — SREngine has no position awareness
    //   [ ] RoundNumberInterval switch — moved to InstrumentSpecs (single source of truth)
    // ========================================================================

    /// <summary>
    /// Immutable configuration for SupportResistanceEngine.
    ///
    /// Controls all structural truth parameters:
    ///   - how level facts are clustered into zones (merge radius)
    ///   - how far price must be to "be at" a zone (proximity)
    ///   - when a zone is structurally broken (invalidation)
    ///   - how long swing zones survive without invalidation (age expiry)
    ///   - volume profile readiness threshold
    ///   - zone storage limits
    ///   - source weights (matching the hierarchy in LevelRegistry)
    ///   - round-number interval (sourced from InstrumentSpecs)
    ///
    /// Production usage:
    /// <code>
    ///   var config = SupportResistanceCoreConfig.ForInstrument(ResolveInstrument());
    /// </code>
    ///
    /// The only instrument-varying parameter is <see cref="RoundNumberInterval"/>,
    /// which is read from <see cref="InstrumentSpecs"/> — the single source of truth
    /// for all instrument-specific constants.
    /// </summary>
    public readonly struct SupportResistanceCoreConfig
    {
        // ====================================================================
        // CLUSTERING
        // ====================================================================

        /// <summary>
        /// Radius within which two level facts are merged into one zone,
        /// expressed as a multiple of ATR.
        ///
        /// Default: 0.50 — matches STACK_ATR_MULT in LevelRegistry.
        /// A 4H swing high at 5300.00 and a Pivot R1 at 5301.25 (within 0.50×ATR
        /// at typical ES ATR) become one resistance zone rather than two independent
        /// point levels.
        /// </summary>
        public double MergeRadiusATRMult { get; }

        /// <summary>
        /// Distance within which price is considered "at" a zone,
        /// expressed as a multiple of ATR.
        ///
        /// Default: 0.35 — matches PROXIMITY_ATR_MULT in LevelRegistry.
        /// Controls AtSupport / AtResistance flags and ConfluenceEngine Layer B
        /// proximity gate.
        /// </summary>
        public double ProximityATRMult { get; }

        // ====================================================================
        // LIFECYCLE
        // ====================================================================

        /// <summary>
        /// Ticks beyond a zone's boundary that price must close through
        /// to mark the zone as Broken.
        ///
        /// Default: 3.0 — matches INVALIDATION_TICKS in HTFLevelEngine.
        /// A wick through the zone without a close-through does NOT break it.
        /// Support broken when: close &lt; ZoneLow  - (InvalidationTicks × TickSize).
        /// Resistance broken when: close &gt; ZoneHigh + (InvalidationTicks × TickSize).
        /// </summary>
        public double InvalidationTicks { get; }

        /// <summary>
        /// Maximum age in primary bars before an H1 swing zone is expired,
        /// regardless of whether it has been invalidated by price.
        ///
        /// Default: 2000 — approximately 7 trading days at a 5-min primary.
        /// Age expiry is a backstop for orphaned zones that price never revisited.
        /// The close-through invalidation rule is the primary cleanup mechanism.
        /// </summary>
        public int SwingExpiryBarsH1 { get; }

        /// <summary>
        /// Maximum age in primary bars before an H2 swing zone is expired.
        ///
        /// Default: 4000 — approximately 14 trading days at a 5-min primary.
        /// </summary>
        public int SwingExpiryBarsH2 { get; }

        /// <summary>
        /// Maximum age in primary bars before an H4 swing zone is expired.
        ///
        /// Default: 16000 — approximately 56 trading days at a 5-min primary.
        /// H4 structure is long-lived. Institutional levels at 4H swing points
        /// remain relevant for weeks. Tightening this prematurely removes valid
        /// reference levels before they are structurally broken.
        /// </summary>
        public int SwingExpiryBarsH4 { get; }

        // ====================================================================
        // VOLUME PROFILE
        // ====================================================================

        /// <summary>
        /// Minimum number of primary bars accumulated in the session profile
        /// before POC / VAH / VAL are considered statistically meaningful.
        ///
        /// Default: 20 — matches MIN_PROFILE_BARS guard in HostStrategy.
        /// At 5-min bars: 20 bars = 100 minutes ≈ first hour RTH + buffer.
        /// Below this threshold, one large-volume bar dominates the profile
        /// and POC is not a real acceptance level.
        /// </summary>
        public int MinProfileBars { get; }

        /// <summary>
        /// Fraction of total session volume that defines the Value Area.
        ///
        /// Default: 0.70 — standard 70% value area.
        /// The Value Area expands bidirectionally from POC until this fraction
        /// of total volume is enclosed. VAH and VAL are the upper and lower
        /// boundaries of that region.
        /// </summary>
        public double ValueAreaCoverage { get; }

        // ====================================================================
        // STORAGE
        // ====================================================================

        /// <summary>
        /// Maximum number of simultaneously active zones per direction.
        /// Support and resistance each maintain separate arrays of this size.
        ///
        /// Default: 20.
        /// When the buffer is full and a new zone must be created, the weakest
        /// existing zone (lowest Strength) is evicted if the new zone is stronger.
        /// If all existing zones are stronger than the incoming fact, it is discarded.
        /// </summary>
        public int MaxActiveZonesPerSide { get; }

        /// <summary>
        /// Maximum number of swing highs (and separately, swing lows) stored
        /// per timeframe in SwingLevelSource.
        ///
        /// Default: 3.
        /// HTFLevelEngine stored only 1 per side. Storing 3 captures multiple
        /// active structural levels at different price distances — the normal
        /// state of any trending or ranging market. All stored swings enter the
        /// merge pass and contribute to zone strength.
        /// </summary>
        public int MaxSwingsPerTF { get; }

        /// <summary>
        /// Upper bound on the Strength field of any single SRZone.
        ///
        /// Default: 60.
        /// Prevents a zone receiving contributions from every source type from
        /// overwhelming ConfluenceEngine Layer B. Must be &gt;= the highest
        /// individual source weight (WeightSwingH4 = 20 by default). The
        /// constructor enforces this invariant at construction time.
        /// </summary>
        public int MaxZoneStrength { get; }

        // ====================================================================
        // SOURCE WEIGHTS
        // Mirror the LevelRegistry weight hierarchy exactly.
        // When SREngine replaces LevelRegistry as the Layer B input,
        // these values preserve scoring behavior for equivalent configurations.
        //
        // Zero weight = source disabled. Intentional — permits surgical
        // source disabling for research without code changes. In production
        // v1 all weights are positive.
        // ====================================================================

        /// <summary>4H swing high/low — highest weight. Institutional timeframe.</summary>
        public int WeightSwingH4   { get; }

        /// <summary>2H swing high/low.</summary>
        public int WeightSwingH2   { get; }

        /// <summary>Previous day high / low — session auction boundary.</summary>
        public int WeightPDH_PDL   { get; }

        /// <summary>London session high / low — smart money session level.</summary>
        public int WeightLondon    { get; }

        /// <summary>1H swing high/low — execution timeframe structure.</summary>
        public int WeightSwingH1   { get; }

        /// <summary>Point of Control — volume acceptance level.</summary>
        public int WeightPOC       { get; }

        /// <summary>Pivot R1 / S1 — primary floor trader reference.</summary>
        public int WeightPivotR1S1 { get; }

        /// <summary>New York session high / low — active session boundary.</summary>
        public int WeightNY        { get; }

        /// <summary>Opening Range high / low — early session reference.</summary>
        public int WeightORB       { get; }

        /// <summary>Pivot R2 / S2 — extended pivot levels.</summary>
        public int WeightPivotR2S2 { get; }

        /// <summary>Value Area High / Low — 70% volume distribution boundary.</summary>
        public int WeightVA        { get; }

        /// <summary>Pivot PP — central pivot. Acts as both support and resistance.</summary>
        public int WeightPivotPP   { get; }

        /// <summary>
        /// Round number levels — lowest weight. Contextual reference only.
        /// Provides tiebreaking and clustering context, not standalone significance.
        /// </summary>
        public int WeightRoundNumber { get; }

        // ====================================================================
        // ROUND NUMBERS
        // ====================================================================

        /// <summary>
        /// Price interval for generating round-number reference levels.
        /// Sourced from <see cref="InstrumentSpecs"/> — the single source of truth
        /// for instrument-specific constants. Not duplicated here.
        ///
        /// Values (via InstrumentSpecs):
        ///   ES  / MES  = 25.0 points
        ///   NQ  / MNQ  = 100.0 points
        /// </summary>
        public double RoundNumberInterval { get; }

        // ====================================================================
        // CONSTRUCTOR
        // ====================================================================

        public SupportResistanceCoreConfig(
            double mergeRadiusATRMult,
            double proximityATRMult,
            double invalidationTicks,
            int    swingExpiryBarsH1,
            int    swingExpiryBarsH2,
            int    swingExpiryBarsH4,
            int    minProfileBars,
            double valueAreaCoverage,
            int    maxActiveZonesPerSide,
            int    maxSwingsPerTF,
            int    maxZoneStrength,
            int    weightSwingH4,
            int    weightSwingH2,
            int    weightPDH_PDL,
            int    weightLondon,
            int    weightSwingH1,
            int    weightPOC,
            int    weightPivotR1S1,
            int    weightNY,
            int    weightORB,
            int    weightPivotR2S2,
            int    weightVA,
            int    weightPivotPP,
            int    weightRoundNumber,
            double roundNumberInterval)
        {
            // ── Clustering ────────────────────────────────────────────────
            if (mergeRadiusATRMult <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(mergeRadiusATRMult), "mergeRadiusATRMult must be > 0.");
            if (proximityATRMult <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(proximityATRMult), "proximityATRMult must be > 0.");

            // ── Lifecycle ─────────────────────────────────────────────────
            if (invalidationTicks <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(invalidationTicks), "invalidationTicks must be > 0.");
            if (swingExpiryBarsH1 <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(swingExpiryBarsH1), "swingExpiryBarsH1 must be > 0.");
            if (swingExpiryBarsH2 <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(swingExpiryBarsH2), "swingExpiryBarsH2 must be > 0.");
            if (swingExpiryBarsH4 <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(swingExpiryBarsH4), "swingExpiryBarsH4 must be > 0.");
            if (swingExpiryBarsH1 > swingExpiryBarsH2)
                throw new ArgumentException(string.Format(
                    "swingExpiryBarsH1 ({0}) must be <= swingExpiryBarsH2 ({1}). " +
                    "H1 swing zones must not outlast H2 swing zones — " +
                    "higher-timeframe structure is longer-lived.",
                    swingExpiryBarsH1, swingExpiryBarsH2));
            if (swingExpiryBarsH2 > swingExpiryBarsH4)
                throw new ArgumentException(string.Format(
                    "swingExpiryBarsH2 ({0}) must be <= swingExpiryBarsH4 ({1}). " +
                    "H2 swing zones must not outlast H4 swing zones — " +
                    "higher-timeframe structure is longer-lived.",
                    swingExpiryBarsH2, swingExpiryBarsH4));

            // ── Volume profile ────────────────────────────────────────────
            if (minProfileBars <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(minProfileBars), "minProfileBars must be > 0.");
            if (valueAreaCoverage <= 0.0 || valueAreaCoverage >= 1.0)
                throw new ArgumentOutOfRangeException(
                    nameof(valueAreaCoverage), "valueAreaCoverage must be in range (0, 1).");

            // ── Storage ───────────────────────────────────────────────────
            if (maxActiveZonesPerSide <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxActiveZonesPerSide), "maxActiveZonesPerSide must be > 0.");
            if (maxSwingsPerTF <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxSwingsPerTF), "maxSwingsPerTF must be > 0.");
            if (maxZoneStrength <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxZoneStrength), "maxZoneStrength must be > 0.");

            // ── Weights — non-negative; zero = source disabled (intentional) ─
            if (weightSwingH4     < 0) throw new ArgumentOutOfRangeException(nameof(weightSwingH4),     "Weight must be >= 0.");
            if (weightSwingH2     < 0) throw new ArgumentOutOfRangeException(nameof(weightSwingH2),     "Weight must be >= 0.");
            if (weightSwingH1     < 0) throw new ArgumentOutOfRangeException(nameof(weightSwingH1),     "Weight must be >= 0.");
            if (weightPDH_PDL     < 0) throw new ArgumentOutOfRangeException(nameof(weightPDH_PDL),     "Weight must be >= 0.");
            if (weightLondon      < 0) throw new ArgumentOutOfRangeException(nameof(weightLondon),      "Weight must be >= 0.");
            if (weightNY          < 0) throw new ArgumentOutOfRangeException(nameof(weightNY),          "Weight must be >= 0.");
            if (weightPivotR1S1   < 0) throw new ArgumentOutOfRangeException(nameof(weightPivotR1S1),   "Weight must be >= 0.");
            if (weightPivotR2S2   < 0) throw new ArgumentOutOfRangeException(nameof(weightPivotR2S2),   "Weight must be >= 0.");
            if (weightPivotPP     < 0) throw new ArgumentOutOfRangeException(nameof(weightPivotPP),     "Weight must be >= 0.");
            if (weightPOC         < 0) throw new ArgumentOutOfRangeException(nameof(weightPOC),         "Weight must be >= 0.");
            if (weightVA          < 0) throw new ArgumentOutOfRangeException(nameof(weightVA),          "Weight must be >= 0.");
            if (weightORB         < 0) throw new ArgumentOutOfRangeException(nameof(weightORB),         "Weight must be >= 0.");
            if (weightRoundNumber < 0) throw new ArgumentOutOfRangeException(nameof(weightRoundNumber), "Weight must be >= 0.");

            // ── MaxZoneStrength >= highest individual source weight ────────
            // A zone can receive at most one contribution per source per bar.
            // If MaxZoneStrength < the highest single-source weight, every
            // zone of that type is clipped immediately — the cap is meaningless.
            // Compute max without LINQ — no heap allocation.
            int maxW = weightSwingH4;
            if (weightSwingH2    > maxW) maxW = weightSwingH2;
            if (weightSwingH1    > maxW) maxW = weightSwingH1;
            if (weightPDH_PDL    > maxW) maxW = weightPDH_PDL;
            if (weightLondon     > maxW) maxW = weightLondon;
            if (weightNY         > maxW) maxW = weightNY;
            if (weightPivotR1S1  > maxW) maxW = weightPivotR1S1;
            if (weightPivotR2S2  > maxW) maxW = weightPivotR2S2;
            if (weightPivotPP    > maxW) maxW = weightPivotPP;
            if (weightPOC        > maxW) maxW = weightPOC;
            if (weightVA         > maxW) maxW = weightVA;
            if (weightORB        > maxW) maxW = weightORB;
            if (weightRoundNumber> maxW) maxW = weightRoundNumber;

            if (maxZoneStrength < maxW)
                throw new ArgumentException(string.Format(
                    "maxZoneStrength ({0}) must be >= the highest individual source weight ({1}). " +
                    "A cap below the highest weight clips every zone of that source type immediately. " +
                    "Raise maxZoneStrength or lower the offending weight.",
                    maxZoneStrength, maxW));

            // ── Round numbers ─────────────────────────────────────────────
            if (roundNumberInterval <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(roundNumberInterval), "roundNumberInterval must be > 0.");

            // ── Assign ────────────────────────────────────────────────────
            MergeRadiusATRMult    = mergeRadiusATRMult;
            ProximityATRMult      = proximityATRMult;
            InvalidationTicks     = invalidationTicks;
            SwingExpiryBarsH1     = swingExpiryBarsH1;
            SwingExpiryBarsH2     = swingExpiryBarsH2;
            SwingExpiryBarsH4     = swingExpiryBarsH4;
            MinProfileBars        = minProfileBars;
            ValueAreaCoverage     = valueAreaCoverage;
            MaxActiveZonesPerSide = maxActiveZonesPerSide;
            MaxSwingsPerTF        = maxSwingsPerTF;
            MaxZoneStrength       = maxZoneStrength;
            WeightSwingH4         = weightSwingH4;
            WeightSwingH2         = weightSwingH2;
            WeightPDH_PDL         = weightPDH_PDL;
            WeightLondon          = weightLondon;
            WeightSwingH1         = weightSwingH1;
            WeightPOC             = weightPOC;
            WeightPivotR1S1       = weightPivotR1S1;
            WeightNY              = weightNY;
            WeightORB             = weightORB;
            WeightPivotR2S2       = weightPivotR2S2;
            WeightVA              = weightVA;
            WeightPivotPP         = weightPivotPP;
            WeightRoundNumber     = weightRoundNumber;
            RoundNumberInterval   = roundNumberInterval;
        }

        // ====================================================================
        // FACTORIES
        // ====================================================================

        /// <summary>
        /// Returns the frozen v1 configuration for the given instrument.
        ///
        /// <see cref="RoundNumberInterval"/> is read from <see cref="InstrumentSpecs"/> —
        /// the single source of truth. No instrument-specific knowledge is duplicated here.
        ///
        /// Call from HostStrategy.DataLoaded():
        /// <code>
        ///   _srEngine = new SupportResistanceEngine(
        ///       SupportResistanceCoreConfig.ForInstrument(ResolveInstrument()), _log);
        /// </code>
        /// </summary>
        public static SupportResistanceCoreConfig ForInstrument(InstrumentKind instrument)
        {
            double roundInterval = InstrumentSpecs.Get(instrument).RoundNumberInterval;

            return new SupportResistanceCoreConfig(
                mergeRadiusATRMult:    0.50,
                proximityATRMult:      0.35,
                invalidationTicks:     5.0,
                swingExpiryBarsH1:     2000,
                swingExpiryBarsH2:     4000,
                swingExpiryBarsH4:     16000,
                minProfileBars:        20,
                valueAreaCoverage:     0.70,
                maxActiveZonesPerSide: 20,
                maxSwingsPerTF:        3,
                maxZoneStrength:       60,
                weightSwingH4:         20,
                weightSwingH2:         16,
                weightPDH_PDL:         15,
                weightLondon:          15,
                weightSwingH1:         12,
                weightPOC:             8,
                weightPivotR1S1:       10,
                weightNY:              10,
                weightORB:             6,
                weightPivotR2S2:       6,
                weightVA:              5,
                weightPivotPP:         4,
                weightRoundNumber:     3,
                roundNumberInterval:   roundInterval);
        }

        /// <summary>
        /// Frozen v1 defaults using ES round-number spacing (25.0 points).
        ///
        /// FOR TESTS AND FALLBACK CONTEXTS ONLY.
        /// Production code must call ForInstrument(ResolveInstrument()) so the
        /// correct instrument-specific round-number interval is applied.
        /// Calling Default on an NQ or MNQ chart silently uses ES spacing.
        /// </summary>
        public static SupportResistanceCoreConfig Default
            => ForInstrument(InstrumentKind.ES);
    }
}
