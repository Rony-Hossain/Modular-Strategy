#region Using declarations
using System;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ========================================================================
    // SR ZONE
    // ========================================================================
    //
    // COMPLETE IMPLEMENTATION CHECKLIST
    //
    // A. Type contract
    //   [x] public struct — value type, lives in pre-allocated SRZone[] arrays
    //       inside SupportResistanceEngine; no per-zone object allocation for the
    //       struct itself — only SourceLabel (string) allocates when built or rebuilt
    //   [x] Mutable — engine updates zones via copy-modify-write-back pattern:
    //           SRZone z = zones[i];
    //           z.State = SRZoneState.Broken;
    //           zones[i] = z;
    //   [x] { get; set; } properties — matches LevelHitResult convention for
    //       public structs; readable by downstream consumers
    //   [x] static readonly Empty sentinel — matches FootprintResult.Zero pattern
    //
    // B. Field inventory
    //   [x] IsValid        bool          — false on Empty sentinel and after compact pass removes the zone
    //   [x] ZonePrice      double        — weighted centroid of contributing level facts
    //   [x] ZoneLow        double        — ZonePrice - (mergeRadius / 2)
    //   [x] ZoneHigh       double        — ZonePrice + (mergeRadius / 2)
    //   [x] Strength       int           — sum of contributing weights, capped at MaxZoneStrength
    //   [x] Role           SRZoneRole    — Support / Resistance / Both; widened on merge
    //   [x] Sources        SRSourceType  — bitmask of contributing source modules
    //   [x] State          SRZoneState   — Fresh / Tested / Broken
    //   [x] TouchCount     int           — proximity tests without close-through
    //   [x] CreatedBar     int           — primary bar index at zone creation
    //   [x] LastTestedBar  int           — primary bar index of last proximity touch
    //   [x] SourceLabel    string        — diagnostic label rebuilt whenever Sources widens
    //
    // C. Age expiry lookup — NO extra field needed
    //   The lifecycle pass determines age expiry from Sources:
    //     Sources has SwingH4 bit → use config.SwingExpiryBarsH4  (longest)
    //     Sources has SwingH2 bit → use config.SwingExpiryBarsH2
    //     Sources has SwingH1 bit → use config.SwingExpiryBarsH1
    //     Sources has no swing bit → no age expiry (session/pivot/profile are self-managed)
    //   Priority: H4 > H2 > H1. A zone with both H1 and H4 uses H4 expiry.
    //   A zone with SwingH1 + Session uses H1 expiry (2000 bars), not infinite,
    //   because the swing component is what requires age tracking.
    //
    // D. Methods
    //   [x] ContainsPrice(double price) — true when price is within [ZoneLow, ZoneHigh]
    //   [x] Overlaps(SRZone other)      — true when zones share > 50% of this zone's width
    //       Used by merge pass to skip duplicate zones and by ImbalanceZoneRegistry migration
    //
    // E. SourceLabel rebuild rule
    //   SourceLabel reflects the current Sources bitmask at all times.
    //   The engine rebuilds it whenever Sources widens during a merge.
    //   If a merge does not add a new source bit, SourceLabel is unchanged.
    //   The Empty sentinel sets it to string.Empty.
    //   Default struct initialization (outside the engine) leaves it null —
    //   callers must treat null as string.Empty.
    //
    // F. String field — one reference type on this struct
    //   SourceLabel is the only reference-type field. Struct copies share the
    //   same string reference — safe because strings are immutable in C#.
    //   No additional heap allocation occurs when the struct is copied.
    //
    // G. What does NOT belong here
    //   [x] Distance-to-price calculations — depend on current price, computed
    //       in SupportResistanceEngine.BuildResult(), not on the zone itself
    //   [x] Age expiry values — read from SupportResistanceCoreConfig in the
    //       lifecycle pass; not stored on the zone
    //   [x] Footprint confirmation state — belongs in FootprintEntryAdvisor
    //   [x] Trade management semantics — no position awareness here
    // ========================================================================

    /// <summary>
    /// A persistent clustered support or resistance zone maintained by
    /// SupportResistanceEngine.
    ///
    /// LIFECYCLE:
    ///   Created  — when a source module emits a fact with no nearby active zone.
    ///   Updated  — when subsequent facts merge into an existing zone (centroid
    ///              and strength updated, Sources bitmask widened).
    ///   Tested   — when price enters proximity without closing through.
    ///   Broken   — when price closes through the invalidation buffer.
    ///   Expired  — age exceeded per-TF limit; lifecycle pass marks State = Broken.
    ///   Evicted  — zone buffer full; lifecycle pass clears IsValid directly.
    ///
    /// MEMORY:
    ///   Value type stored in pre-allocated SRZone[] arrays inside
    ///   SupportResistanceEngine. No per-zone object allocation for the struct
    ///   itself; only SourceLabel may allocate when built or rebuilt.
    ///   Engine mutates via copy-modify-write-back. Downstream consumers
    ///   receive copies through SupportResistanceResult.
    ///
    /// MUTATION MODEL:
    /// <code>
    ///   SRZone z = _supportZones[i];   // copy out
    ///   z.State      = SRZoneState.Broken;
    ///   z.TouchCount++;
    ///   _supportZones[i] = z;          // write back
    /// </code>
    /// </summary>
    public struct SRZone
    {
        // ====================================================================
        // VALIDITY
        // ====================================================================

        /// <summary>
        /// False on the Empty sentinel and on zones after the lifecycle compact pass
        /// has removed them from the active buffer.
        ///
        /// A zone with State = Broken still has IsValid = true until the compact
        /// pass runs — age-expired zones are first marked State = Broken by the
        /// lifecycle pass, then removed in the same compact sweep.
        /// Evicted zones have IsValid cleared directly without a state transition.
        /// Downstream consumers must check IsValid before reading any other field —
        /// do not rely on State alone.
        /// </summary>
        public bool IsValid { get; set; }

        // ====================================================================
        // GEOMETRY
        // ====================================================================

        /// <summary>
        /// Weighted centroid of all contributing level facts, in price units.
        /// Updated on every merge until Strength reaches MaxZoneStrength.
        ///
        /// Formula (while Strength &lt; MaxZoneStrength):
        ///   effectiveWeight = Math.Min(newWeight, MaxZoneStrength - oldStrength)
        ///   newCentroid = (oldCentroid × oldStrength + newPrice × effectiveWeight)
        ///                 / (oldStrength + effectiveWeight)
        ///
        /// Using effectiveWeight instead of newWeight at the saturation boundary
        /// ensures that ZonePrice and Strength always describe the same zone.
        /// If oldStrength == MaxZoneStrength the centroid is frozen — skip the
        /// update entirely. The engine must enforce both rules.
        /// </summary>
        public double ZonePrice { get; set; }

        /// <summary>
        /// Lower boundary of the zone band.
        /// ZoneLow = ZonePrice - (MergeRadiusATRMult × ATR / 2)
        ///
        /// Recalculated whenever ZonePrice updates. Used for:
        ///   - support invalidation check: close &lt; ZoneLow - buffer
        ///   - NearBullZoneLow SnapKey (zone trail)
        ///   - ContainsPrice()
        /// </summary>
        public double ZoneLow { get; set; }

        /// <summary>
        /// Upper boundary of the zone band.
        /// ZoneHigh = ZonePrice + (MergeRadiusATRMult × ATR / 2)
        ///
        /// Recalculated whenever ZonePrice updates. Used for:
        ///   - resistance invalidation check: close &gt; ZoneHigh + buffer
        ///   - NearBearZoneHigh SnapKey (zone trail)
        ///   - ContainsPrice()
        /// </summary>
        public double ZoneHigh { get; set; }

        // ====================================================================
        // STRENGTH AND COMPOSITION
        // ====================================================================

        /// <summary>
        /// Accumulated weight of all source facts that contributed to this zone,
        /// capped at SupportResistanceCoreConfig.MaxZoneStrength (default: 60).
        ///
        /// Starts at the weight of the first contributing fact.
        /// Increments by each subsequent merged fact's weight.
        /// Used as the primary sorting key: higher Strength = more confluent zone.
        ///
        /// Also drives ConfluenceEngine Layer B scoring when SREngine replaces
        /// LevelRegistry as the Layer B input.
        /// </summary>
        public int Strength { get; set; }

        /// <summary>
        /// Structural role of this zone.
        ///
        /// Initialized at creation from the first contributing fact's IsSupport /
        /// IsResistance flags. May be widened to Both if a subsequent fact with
        /// the opposite role merges into the zone.
        ///
        /// Both does NOT indicate role-reversal — use State = Broken for that.
        /// See SRZoneRole for full semantics.
        /// </summary>
        public SRZoneRole Role { get; set; }

        /// <summary>
        /// Bitmask of all source types that have contributed facts to this zone.
        /// A single bit per source — use SRSourceTypeHelper.IsStacked() to check
        /// whether more than one source type is present.
        ///
        /// Used by:
        ///   - ConfluenceEngine: IsStacked → stacked-zone bonus
        ///   - Lifecycle pass:   swing bits → age expiry lookup
        ///   - Diagnostics:      SourceLabel is derived from this field and rebuilt when Sources widens
        /// </summary>
        public SRSourceType Sources { get; set; }

        // ====================================================================
        // LIFECYCLE STATE
        // ====================================================================

        /// <summary>
        /// Current lifecycle state of the zone.
        /// Transitions: Fresh → Tested → Broken  or  Fresh → Broken.
        /// One-directional — never reverts.
        /// See SRZoneState for exact conditions.
        /// </summary>
        public SRZoneState State { get; set; }

        /// <summary>
        /// Number of times price has entered this zone's proximity
        /// (within ProximityATRMult × ATR of ZonePrice) without closing through.
        ///
        /// Increments on every proximity contact, not just the first.
        /// A higher TouchCount indicates a level that has been repeatedly
        /// respected — useful for future touch-extension logic (v2).
        /// </summary>
        public int TouchCount { get; set; }

        /// <summary>
        /// Primary bar index at which this zone was first created.
        /// Used by the lifecycle pass for age expiry:
        ///   expired when (currentBar - CreatedBar) > swingExpiryBars[TF]
        /// Session and profile zones do not expire by age — they are
        /// governed by source self-management via OnSessionOpen().
        /// </summary>
        public int CreatedBar { get; set; }

        /// <summary>
        /// Primary bar index of the most recent proximity touch.
        /// Zero until the zone has been tested at least once.
        /// Preserved across session opens — HTF zones are persistent.
        /// </summary>
        public int LastTestedBar { get; set; }

        // ====================================================================
        // DIAGNOSTICS
        // ====================================================================

        /// <summary>
        /// Compact diagnostic label reflecting the current Sources bitmask.
        /// Examples: "4Hsw+R1", "PDH+London(stk)", "POC", "Rnd".
        ///
        /// Rebuilt by the engine every time Sources widens — when a new fact
        /// merges into the zone and adds a source bit not previously present.
        /// If Sources does not change on a merge, SourceLabel is left as-is.
        ///
        /// Never null when created through the engine — Empty sentinel uses
        /// string.Empty. Default struct initialization (outside the engine)
        /// may leave this null; callers should treat null as string.Empty.
        ///
        /// Used by StrategyLogger and ConfluenceEngine LayerB hit-name string.
        /// </summary>
        public string SourceLabel { get; set; }

        // ====================================================================
        // METHODS
        // ====================================================================

        /// <summary>
        /// True when <paramref name="price"/> falls within the zone band
        /// [ZoneLow, ZoneHigh] inclusive.
        ///
        /// Used by the merge pass to check whether an incoming fact overlaps
        /// an existing zone's band before comparing centroid distance.
        /// </summary>
        public bool ContainsPrice(double price)
        {
            return price >= ZoneLow && price <= ZoneHigh;
        }

        /// <summary>
        /// True when this zone and <paramref name="other"/> overlap by more than
        /// 50% of THIS zone's width.
        ///
        /// ASYMMETRIC: <c>a.Overlaps(b)</c> and <c>b.Overlaps(a)</c> can return
        /// different values when the zones have different widths. This is intentional.
        ///
        /// Calling convention in the engine's duplicate-suppression check:
        ///   <c>existingZone.Overlaps(newCandidate)</c>
        /// The existing zone is always the receiver — its width is the reference.
        /// A new candidate is suppressed when it overlaps an existing zone by more
        /// than 50% of that existing zone's band. This protects established zones
        /// from being split by closely-spaced incoming facts.
        ///
        /// Edge case: if ZoneHigh == ZoneLow (zero-width zone), equality of
        /// centroids is used instead of overlap ratio. Only reachable if ATR
        /// was zero at zone creation time, which upstream guards prevent.
        /// </summary>
        public bool Overlaps(SRZone other)
        {
            double width = ZoneHigh - ZoneLow;
            if (width <= 0.0)
                return Math.Abs(ZonePrice - other.ZonePrice) < double.Epsilon;

            double overlapLow  = Math.Max(ZoneLow,  other.ZoneLow);
            double overlapHigh = Math.Min(ZoneHigh, other.ZoneHigh);
            double overlap     = overlapHigh - overlapLow;
            return overlap > 0.5 * width;
        }

        // ====================================================================
        // SENTINEL
        // ====================================================================

        /// <summary>
        /// Safe default for failure paths and uninitialized zone slots.
        ///
        /// IsValid = false. All numeric fields at zero. SourceLabel = string.Empty.
        ///
        /// Matches FootprintResult.Zero convention — static readonly field,
        /// not a property, so the value is stored once and copied on each read.
        ///
        /// Downstream consumers must check IsValid before reading any other field.
        /// </summary>
        public static readonly SRZone Empty = new SRZone
        {
            IsValid       = false,
            ZonePrice     = 0.0,
            ZoneLow       = 0.0,
            ZoneHigh      = 0.0,
            Strength      = 0,
            Role          = SRZoneRole.Support,
            State         = SRZoneState.Fresh,
            Sources       = SRSourceType.None,
            TouchCount    = 0,
            CreatedBar    = 0,
            LastTestedBar = 0,
            SourceLabel   = string.Empty,
        };
    }
}
