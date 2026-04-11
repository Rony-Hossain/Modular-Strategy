#region Using declarations
using System;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ========================================================================
    // SR TYPES
    // ========================================================================
    //
    // COMPLETE IMPLEMENTATION CHECKLIST
    //
    // A. Contents
    //   [x] SRSourceType        — [Flags] enum, bitmask-capable, one bit per source
    //   [x] SRZoneRole          — Support / Resistance / Both
    //   [x] SRZoneState         — Fresh / Tested / Broken
    //   [x] SRLevelFact         — internal struct, per-bar source emission, scratch only
    //   [x] SRSourceTypeHelper  — internal static class, BitCount and IsStacked helpers
    //
    // B. Visibility rules
    //   [x] SRSourceType  — public  (used in SRZone.Sources, which is public)
    //   [x] SRZoneRole    — public  (used in SRZone.Role, which is public)
    //   [x] SRZoneState   — public  (used in SRZone.State, which is public)
    //   [x] SRLevelFact   — internal (never exposed outside SupportResistanceEngine)
    //
    // C. SRSourceType bitmask design
    //   [x] None = 0 sentinel
    //   [x] Each source occupies exactly one bit (1 << n)
    //   [x] v1 sources: SwingH1, SwingH2, SwingH4, Session, Pivot, VolumeProfile, RoundNumber
    //   [x] Stacked-zone check is (sources & (sources - 1)) != 0 — more than one bit set
    //   [x] Bit count = number of contributing sources — no LINQ, use BitCount helper
    //   [x] Values leave gaps for future sources (bits 7–30 available;
    //       bit 31 is the sign bit of the underlying int — do not use)
    //
    // D. SRLevelFact field rules
    //   [x] Instance fields only — no instance properties (scratch struct, accessed in tight loops)
    //   [x] IsSupport and IsResistance are independent booleans — a fact can be both
    //       (POC, PP, round numbers act as both support and resistance)
    //   [x] IsValid sentinel — invalid facts are skipped in merge pass without branching
    //   [x] One static readonly sentinel (None) — matches FootprintResult.Zero pattern
    //   [x] No allocation — struct, all primitive fields
    //
    // E. What does NOT belong here
    //   [x] SRZone — next type in the sequence, depends on these enums
    //   [x] SupportResistanceResult — depends on SRZone
    //   [x] SupportResistanceCoreConfig — already in its own file
    //   [x] Any logic — pure data contracts
    //       (SRSourceTypeHelper contains lightweight bitmask helpers only —
    //        no engine logic, no business rules, no state)
    // ========================================================================

    // ========================================================================
    // SRSourceType
    // ========================================================================

    /// <summary>
    /// Identifies which source module produced a level fact or contributed
    /// to a zone. Defined as a [Flags] enum so a single SRZone.Sources field
    /// can record multiple contributing sources as a bitmask.
    ///
    /// Usage:
    ///   // Check if a zone has contributions from more than one source:
    ///   bool isStacked = (zone.Sources &amp; (zone.Sources - 1)) != 0;
    ///
    ///   // Check if a specific source contributed:
    ///   bool hasSwing = (zone.Sources &amp; SRSourceType.SwingH4) != 0;
    ///
    /// v1 sources occupy bits 0–6. Bits 7–30 are reserved for future sources
    /// (daily swings, Fib, moving averages) without breaking existing values.
    /// Do not use bit 31 — it is the sign bit of the underlying int.
    /// </summary>
    [Flags]
    public enum SRSourceType
    {
        None          = 0,
        SwingH1       = 1 << 0,   //   1 — 1H confirmed swing high or low
        SwingH2       = 1 << 1,   //   2 — 2H confirmed swing high or low
        SwingH4       = 1 << 2,   //   4 — 4H confirmed swing high or low
        Session       = 1 << 3,   //   8 — PDH/PDL, London H/L, NY H/L, ORB H/L
        Pivot         = 1 << 4,   //  16 — daily floor trader pivots (PP, R1/R2, S1/S2)
        VolumeProfile = 1 << 5,   //  32 — session POC, VAH, VAL
        RoundNumber   = 1 << 6,   //  64 — instrument round-number intervals
    }

    // ========================================================================
    // SRZoneRole
    // ========================================================================

    /// <summary>
    /// Structural role of a zone: does it act as support, resistance, or both?
    ///
    /// Both is assigned when:
    ///   - The zone is created from a source that is inherently bidirectional
    ///     (POC, Pivot PP, round numbers).
    ///   - Two facts are merged where one is support-only and one is resistance-only
    ///     and their centroids fall within merge radius of each other.
    ///
    /// Both does NOT mean "flipped" or "role-reversal." A zone that was support
    /// and is now acting as resistance remains Role = Support with State = Broken
    /// or State = Tested. Role is initialized at creation and may be widened to
    /// Both if opposite-role facts merge into the same zone during a later bar.
    /// </summary>
    public enum SRZoneRole
    {
        Support    = 0,
        Resistance = 1,
        Both       = 2,
    }

    // ========================================================================
    // SRZoneState
    // ========================================================================

    /// <summary>
    /// Lifecycle state of a zone.
    ///
    /// Fresh   — zone has been created but price has not yet entered its proximity.
    ///           Initial state for all newly created zones.
    ///
    /// Tested  — price entered the zone's proximity (within ProximityATRMult × ATR)
    ///           without closing through the invalidation buffer.
    ///           A tested zone has demonstrated that market participants are
    ///           aware of the level. TouchCount increments on each test.
    ///
    /// Broken  — price closed beyond the zone boundary by the invalidation buffer:
    ///             support broken:    close &lt; ZoneLow  - (InvalidationTicks × TickSize)
    ///             resistance broken: close &gt; ZoneHigh + (InvalidationTicks × TickSize)
    ///           The zone is structurally dead.
    ///           Broken zones are removed from the active arrays during the
    ///           lifecycle compact pass and will not appear in SupportResistanceResult.
    ///
    /// Note: State transitions are one-directional:
    ///   Fresh → Tested → Broken
    ///   Fresh → Broken  (if price closes through without a proximity touch first)
    /// A Broken zone never reverts to Fresh or Tested.
    /// </summary>
    public enum SRZoneState
    {
        Fresh  = 0,
        Tested = 1,
        Broken = 2,
    }

    // ========================================================================
    // SRLevelFact
    // ========================================================================

    /// <summary>
    /// A single price level emitted by one source module on one bar.
    ///
    /// LIFECYCLE: Scratch only. Facts are written into a pre-allocated buffer
    /// inside SupportResistanceEngine.Update(), consumed by the merge pass,
    /// and then the buffer is overwritten on the next bar. Facts are never
    /// stored, queued, or passed outside the engine.
    ///
    /// MEMORY: value-type scratch record stored in preallocated buffers;
    /// no per-fact heap allocation. Written and read as plain fields
    /// in tight loops; properties would add unnecessary overhead here.
    ///
    /// IsSupport and IsResistance are independent:
    ///   SwingHigh:    IsSupport = false, IsResistance = true
    ///   SwingLow:     IsSupport = true,  IsResistance = false
    ///   POC / PP / Round: IsSupport = true,  IsResistance = true
    ///
    /// The merge pass uses both flags when routing a fact into the support
    /// and/or resistance zone arrays.
    /// </summary>
    internal struct SRLevelFact
    {
        /// <summary>
        /// Price of the level, rounded to the nearest tick by the source module.
        /// Zero is not a valid price — facts with Price == 0 must have IsValid = false.
        /// </summary>
        public double Price;

        /// <summary>
        /// Which source produced this fact. Single bit set — never a combination.
        /// The merge pass combines Sources across facts when building SRZone.Sources.
        /// </summary>
        public SRSourceType Source;

        /// <summary>
        /// Significance weight from SupportResistanceCoreConfig.
        /// Drives the weighted centroid calculation and zone Strength accumulation.
        /// Read from the config at source module construction time — not recomputed
        /// per fact.
        /// </summary>
        public int Weight;

        /// <summary>
        /// True when this level acts as support (price testing from above).
        /// SwingLows, PDL, LondonLow, VAL, S1/S2, ORBLow → true.
        /// SwingHighs, PDH, LondonHigh, VAH, R1/R2 → false.
        /// POC, PP, RoundNumbers → true (also IsResistance = true).
        /// </summary>
        public bool IsSupport;

        /// <summary>
        /// True when this level acts as resistance (price testing from below).
        /// See IsSupport for per-level assignments.
        /// </summary>
        public bool IsResistance;

        /// <summary>
        /// False for any fact that should be skipped in the merge pass.
        /// Source modules set this to false when a level is not yet available
        /// (e.g. no swing detected yet, profile not ready, ORB not complete).
        /// The merge pass checks IsValid before processing — no branching on Price == 0.
        /// </summary>
        public bool IsValid;

        /// <summary>
        /// Invalid sentinel. Source modules write this into the fact buffer
        /// when a level is not available this bar.
        /// Pattern matches FootprintResult.Zero — static readonly field,
        /// not a property, so the struct value is stored once and copied on read.
        /// </summary>
        public static readonly SRLevelFact None = new SRLevelFact { IsValid = false };
    }

    // ========================================================================
    // HELPER — SRSourceType bit count
    // ========================================================================

    /// <summary>
    /// Utility methods for SRSourceType bitmask operations.
    /// Used by ConfluenceEngine when building LevelHitResult.HitCount
    /// from a zone's Sources field.
    /// </summary>
    internal static class SRSourceTypeHelper
    {
        /// <summary>
        /// Count the number of set bits in a SRSourceType value.
        /// Equals the number of distinct source types that contributed to a zone.
        ///
        /// Uses Brian Kernighan's algorithm: O(k) where k = number of set bits.
        /// No allocation. Called at most once per bar per zone lookup.
        ///
        /// Example:
        ///   SwingH4 | Pivot = 0b00010100 → BitCount = 2
        /// </summary>
        public static int BitCount(SRSourceType sources)
        {
            int value = (int)sources;
            int count = 0;
            while (value != 0)
            {
                value &= (value - 1);   // clear lowest set bit
                count++;
            }
            return count;
        }

        /// <summary>
        /// True when more than one source type contributed to the zone.
        /// Equivalent to BitCount(sources) > 1 but faster — single operation.
        /// Used by the merge pass and ConfluenceEngine stacked-zone check.
        /// </summary>
        public static bool IsStacked(SRSourceType sources)
        {
            int value = (int)sources;
            return value != 0 && (value & (value - 1)) != 0;
        }
    }
}
