#region Using declarations
using System;
using System.Collections.Generic;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ========================================================================
    // SUPPORT RESISTANCE ENGINE — STAGE 3
    // ========================================================================
    //
    // COMPLETE IMPLEMENTATION CHECKLIST
    //
    // A. Architecture
    //   [x] SupportResistanceEngine is a sealed instance class
    //   [x] Single public S/R boundary — no other module computes S/R truth
    //   [x] Returns one SupportResistanceResult per Update() call
    //   [x] Does not publish to MarketSnapshot directly
    //   [x] Owns all source modules as internal sealed classes in the same file.
    //       Source modules are NOT nested inside SupportResistanceEngine — they are
    //       top-level internal classes in the same .cs file. This is the chosen
    //       approach: same file keeps related code together without requiring
    //       SupportResistanceEngine to become an unreadably long outer class.
    //
    // B. NON-GOALS
    //   [x] No signal scoring
    //   [x] No entry / exit decisions
    //   [x] No stop or target logic
    //   [x] No trade management
    //   [x] No snapshot publishing
    //   [x] No footprint / order-flow discovery
    //
    // C. Source modules — build status
    //   [x] VolumeProfileLevelSource  — COMPLETE (Stage 1)
    //   [x] SwingLevelSource           — COMPLETE (Stage 2 — swing detection + bootstrap scan)
    //   [x] PivotLevelSource           — COMPLETE (Stage 2 — daily floor trader pivots)
    //   [x] SessionLevelSource         — COMPLETE (Stage 3)
    //   [x] RoundNumberLevelSource     — COMPLETE (Stage 3)
    //
    // D. Engine passes — build status
    //   [x] Source update pass        — COMPLETE (all five v1 sources)
    //   [x] Fact collection pass      — COMPLETE (all five sources)
    //   [x] Merge pass                — COMPLETE (two-pass recompute)
    //   [x] Lifecycle pass            — COMPLETE (orphan + age + invalidation + touch)
    //   [x] Result builder            — COMPLETE (zone + passthrough fields, all sources)
    //
    // E. Parallel-run migration gate (Stage 1 requirement)
    //   VolumeProfileLevelSource must produce POC / VAH / VAL / POCSkew
    //   matching the current HostStrategy block (lines 1181-1218) within tolerance
    //   for 5 consecutive sessions before removing the old HostStrategy POC block.
    //   Pass criteria (see VolumeProfileLevelSource checklist E for full detail):
    //     POC / VAHigh / VALow: within 1 tick on every non-degenerate bar
    //     POCSkew: within 1e-9 on every non-degenerate bar
    //     Degenerate bar: snap.Get(SnapKeys.POC) == 0.0 (old path published nothing)
    //       → this source retains last valid value instead → known divergence, not failure
    //   HostStrategy publishes both paths simultaneously during parallel run.
    //   Log key: "PROFILE_COMPARE" — written every bar once profile is ready.
    //
    // F. Memory contract
    //   [x] Zone arrays pre-allocated at construction, populated by merge pass
    //   [x] Fact buffer pre-allocated at construction
    //   [x] Profile dictionary pre-allocated — same instance reused each session
    //   [x] No allocation in Update() hot path except:
    //       - MathStructure.ValueArea_Calculate (one sorted double[] per bar when profile ready)
    //       - BuildSourceLabel (string allocation, only when zone Sources bitmask changes;
    //         on the common path — same swing/pivot facts as last bar — no allocation occurs)
    //
    // G. HostStrategy wiring (Stage 3)
    //   Add to HostStrategy.DataLoaded():
    //     _srEngine = new SupportResistanceEngine(
    //         SupportResistanceCoreConfig.ForInstrument(ResolveInstrument()), _log);
    //     // Bootstrap MUST be called after construction — seeds swings, pivots,
    //     // session levels, and round numbers so NearestSupport / NearestResistance
    //     // are meaningful on bar 1.
    //     var bootSnap = _feed.GetSnapshot();
    //     _srEngine.Bootstrap(ref bootSnap);
    //
    //   Add to HostStrategy.OnSessionOpen():
    //     _srEngine?.OnSessionOpen();
    //
    //   Add to HostStrategy.OnPopulateIndicatorBag() after existing POC block:
    //     var srResult = _srEngine?.Update(ref snapshot) ?? SupportResistanceResult.Empty;
    //     // Parallel-run logging (Stage 1 only — remove after migration gate passes):
    //     LogProfileCompare(snapshot, srResult);
    //     // After migration gate passes, replace the existing POC block with:
    //     snapshot.Set(SnapKeys.POC,     srResult.POC);
    //     snapshot.Set(SnapKeys.VAHigh,  srResult.VAHigh);
    //     snapshot.Set(SnapKeys.VALow,   srResult.VALow);
    //     snapshot.Set(SnapKeys.POCSkew, srResult.POCSkew);
    // ========================================================================

    /// <summary>
    /// SupportResistanceEngine — the single public S/R boundary.
    ///
    /// Owns all source modules. Produces one <see cref="SupportResistanceResult"/>
    /// per primary bar via <see cref="Update"/>.
    ///
    /// Downstream consumers read <see cref="SupportResistanceResult"/> only.
    /// No other module computes S/R truth independently.
    /// </summary>
    public sealed class SupportResistanceEngine
    {
        // ====================================================================
        // CONFIG AND LOGGING
        // ====================================================================

        private readonly SupportResistanceCoreConfig _config;
        // _log used for swing invalidation, zone lifecycle, and bootstrap diagnostics
        private readonly StrategyLogger              _log;

        // ====================================================================
        // SOURCE MODULES
        // ====================================================================

        private readonly VolumeProfileLevelSource _profile;
        private readonly SwingLevelSource         _swings;
        private readonly PivotLevelSource         _pivots;
        private readonly SessionLevelSource       _session;
        private readonly RoundNumberLevelSource   _rounds;

        // ====================================================================
        // ZONE STORAGE — pre-allocated, populated by merge pass
        // ====================================================================

        private readonly SRZone[] _supportZones;
        private readonly SRZone[] _resistanceZones;
        private int _supportCount    = 0;
        private int _resistanceCount = 0;

        // ====================================================================
        // SCRATCH BUFFERS — pre-allocated for merge and recompute passes
        // ====================================================================

        private readonly SRLevelFact[] _factBuf;

        // Weighted price accumulators for RecomputeZoneStrengths — one slot per
        // zone buffer slot. Indexed identically to _supportZones / _resistanceZones.
        // Pre-allocated to avoid per-bar heap allocation.
        private readonly double[]       _scratchSupPriceSum;
        private readonly double[]       _scratchResPriceSum;
        // Prior-bar Sources bitmask — used to skip SourceLabel rebuild when Sources
        // has not changed, avoiding a string allocation per zone per bar.
        private readonly SRSourceType[] _scratchSupPriorSrc;
        private readonly SRSourceType[] _scratchResPriorSrc;

        // ====================================================================
        // LAST RESULT
        // ====================================================================

        private SupportResistanceResult _lastResult = SupportResistanceResult.Empty;

        /// <summary>
        /// The result produced by the most recent <see cref="Update"/> call.
        /// <see cref="SupportResistanceResult.Empty"/> before the first valid bar.
        /// </summary>
        public SupportResistanceResult LastResult => _lastResult;

        // ====================================================================
        // CONSTRUCTION
        // ====================================================================

        public SupportResistanceEngine(SupportResistanceCoreConfig config, StrategyLogger log)
        {
            _config = config;
            _log    = log;

            // Source modules
            _profile = new VolumeProfileLevelSource(config, log);
            _swings  = new SwingLevelSource(config, log);
            _pivots  = new PivotLevelSource(config, log);
            _session = new SessionLevelSource(config, log);
            _rounds  = new RoundNumberLevelSource(config, log);

            // Zone buffers — pre-allocated; merge pass populates them from source facts
            _supportZones    = new SRZone[config.MaxActiveZonesPerSide];
            _resistanceZones = new SRZone[config.MaxActiveZonesPerSide];

            // Fact buffer: max facts across all v1 sources:
            // 18 (swings) + 5 (pivots) + 8 (session) + 3 (profile) + 11 (rounds) = 45
            // 64 slots provides comfortable headroom
            _factBuf            = new SRLevelFact[64];
            _scratchSupPriceSum = new double[config.MaxActiveZonesPerSide];
            _scratchResPriceSum = new double[config.MaxActiveZonesPerSide];
            _scratchSupPriorSrc = new SRSourceType[config.MaxActiveZonesPerSide];
            _scratchResPriorSrc = new SRSourceType[config.MaxActiveZonesPerSide];
        }

        // ====================================================================
        // LIFECYCLE
        // ====================================================================

        /// <summary>
        /// Call from HostStrategy.OnSessionOpen().
        /// Resets session-scoped source state: volume profile and ORB levels.
        /// HTF swing zones and pivot levels survive session boundaries.
        /// </summary>
        public void OnSessionOpen()
        {
            _profile.OnSessionOpen();
            // Swing zones persist across session boundaries — no reset needed.
            // Pivot levels recalculate daily via PrevDay change detection — no reset needed.
            _session.OnSessionOpen();
        }

        /// <summary>
        /// One-time historical scan. Call from HostStrategy.DataLoaded() after
        /// construction, once DataFeed has populated its rolling arrays.
        ///
        /// Populates swing zones from the full available HTF rolling history so
        /// that NearestSupport / NearestResistance are meaningful on bar 1.
        /// Seeds swings from rolling HTF history, pivots from PrevDay data,
        /// session levels from the current snapshot, and round numbers from
        /// current price before the initial merge runs.
        ///
        /// Without this call the engine starts with empty zone arrays and
        /// produces no structural S/R until new swings form organically.
        ///
        /// HostStrategy wiring (DataLoaded, after engine construction):
        ///   var snap = _feed.GetSnapshot();
        ///   if (snap.IsValid) _srEngine.Bootstrap(ref snap);
        ///
        /// The snapshot does not need to be fully warm (ATR can be 0 — the
        /// bootstrap uses tick size only, which is available immediately).
        /// </summary>
        public void Bootstrap(ref MarketSnapshot snapshot)
        {
            if (snapshot.Primary.TickSize <= 0.0) return;

            // Seed historical swings from full rolling array depth
            _swings.Bootstrap(ref snapshot);

            // Seed pivots — PrevDay data is already available by DataLoaded
            _pivots.Update(ref snapshot);

            // Seed session levels — PDH/PDL and any available session H/L
            _session.Update(ref snapshot);

            // Run initial merge so zones are populated before first Update()
            double tickSize   = snapshot.Primary.TickSize;
            double atr        = snapshot.ATR > 0 ? snapshot.ATR : tickSize * 10;
            int    currentBar = snapshot.Primary.CurrentBar;

            int factCount = 0;
            _swings.EmitFacts(_factBuf, ref factCount);
            _pivots.EmitFacts(_factBuf, ref factCount);
            _session.EmitFacts(_factBuf, ref factCount);
            _rounds.EmitFacts(_factBuf, ref factCount,
                snapshot.Primary.Close, tickSize);
            // Profile is not seeded at bootstrap — session profile resets each day
            // and has no meaningful history to scan.

            MergeFacts(factCount, atr, currentBar);

            _log?.Warn(snapshot.Primary.Time,
                "SR Bootstrap: {0} support zones, {1} resistance zones seeded",
                _supportCount, _resistanceCount);
        }

        // ====================================================================
        // UPDATE — called every primary bar
        // ====================================================================

        /// <summary>
        /// Process the current bar. Updates all source modules, runs the merge
        /// and lifecycle passes, and returns one immutable result.
        ///
        /// Must be called after DataFeed.OnBarUpdate() and after ATR is valid
        /// in the snapshot — same slot currently occupied by HTFLevelEngine.Update().
        /// </summary>
        public SupportResistanceResult Update(ref MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid)
            {
                _lastResult = SupportResistanceResult.Empty;
                return _lastResult;
            }

            double tickSize    = snapshot.Primary.TickSize;
            double close       = snapshot.Primary.Close;
            double atr         = snapshot.ATR > 0 ? snapshot.ATR : tickSize * 10;
            int    currentBar  = snapshot.Primary.CurrentBar;

            // ── Step 1: Update all source modules ─────────────────────────
            _profile.Update(ref snapshot);
            _swings.Update(ref snapshot);
            _pivots.Update(ref snapshot);
            _session.Update(ref snapshot);
            _rounds.Update(ref snapshot);

            // ── Step 2: Collect all facts into scratch buffer ──────────────
            int factCount = 0;
            _profile.EmitFacts(_factBuf, ref factCount);
            _swings.EmitFacts(_factBuf, ref factCount);
            _pivots.EmitFacts(_factBuf, ref factCount);
            _session.EmitFacts(_factBuf, ref factCount);
            _rounds.EmitFacts(_factBuf, ref factCount, close, tickSize);

            // ── Step 3: Merge facts into zone arrays ───────────────────────
            MergeFacts(factCount, atr, currentBar);

            // ── Step 4: Lifecycle — age expiry, invalidation, compact ──────
            RunLifecycle(close, tickSize, atr, currentBar);

            // ── Step 5: Build result ───────────────────────────────────────
            _lastResult = BuildResult(close, tickSize, atr);
            return _lastResult;
        }

        public int GetZoneDTOs(ZoneDTO[] buffer)
        {
            if (buffer == null) return 0;
            int count = 0;
            // Resistance zones (Bearish)
            for (int i = 0; i < _resistanceCount && count < buffer.Length; i++)
            {
                var z = _resistanceZones[i];
                if (!z.IsValid || z.State == SRZoneState.Broken) continue;
                buffer[count++] = new ZoneDTO { High = z.ZoneHigh, Low = z.ZoneLow, Label = z.SourceLabel, IsBullish = false, Strength = z.Strength, IsValid = true };
            }
            // Support zones (Bullish)
            for (int i = 0; i < _supportCount && count < buffer.Length; i++)
            {
                var z = _supportZones[i];
                if (!z.IsValid || z.State == SRZoneState.Broken) continue;
                buffer[count++] = new ZoneDTO { High = z.ZoneHigh, Low = z.ZoneLow, Label = z.SourceLabel, IsBullish = true, Strength = z.Strength, IsValid = true };
            }
            return count;
        }

        // ====================================================================
        // MERGE PASS
        // ====================================================================

        private void MergeFacts(int factCount, double atr, int currentBar)
        {
            double mergeRadius = _config.MergeRadiusATRMult * atr;

            // First recompute: gives existing zones current-bar strength so that
            // eviction decisions inside MergeFactIntoArray use current strength,
            // not stale prior-bar strength.
            RecomputeZoneStrengths(_factBuf, factCount, mergeRadius);

            for (int f = 0; f < factCount; f++)
            {
                SRLevelFact fact = _factBuf[f];
                if (!fact.IsValid) continue;

                if (fact.IsSupport)
                    MergeFactIntoArray(fact, _supportZones, ref _supportCount,
                        mergeRadius, currentBar, isSupport: true);

                if (fact.IsResistance)
                    MergeFactIntoArray(fact, _resistanceZones, ref _resistanceCount,
                        mergeRadius, currentBar, isSupport: false);
            }

            // Second recompute: picks up all zones created during the merge loop
            // this bar, including multi-fact clusters that formed from scratch.
            // Without this, a new zone's Strength/Sources/centroid would reflect
            // only its first contributing fact until the next bar's first recompute.
            RecomputeZoneStrengths(_factBuf, factCount, mergeRadius);
        }

        private void MergeFactIntoArray(
            SRLevelFact fact,
            SRZone[]    zones,
            ref int     count,
            double      mergeRadius,
            int         currentBar,
            bool        isSupport)
        {
            // Find the closest active zone within mergeRadius of this fact
            int    bestIdx  = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (!zones[i].IsValid || zones[i].State == SRZoneState.Broken) continue;
                double dist = Math.Abs(zones[i].ZonePrice - fact.Price);
                if (dist < mergeRadius && dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx  = i;
                }
            }

            if (bestIdx >= 0)
            {
                // Widen role only — centroid/strength recomputed by RecomputeZoneStrengths
                SRZone z = zones[bestIdx];
                UpdateZoneWithFact(ref z, fact);
                zones[bestIdx] = z;
            }
            else
            {
                // No nearby zone — create new one or evict weakest if full
                if (count >= zones.Length)
                {
                    int weakestIdx = FindWeakestZone(zones, count);
                    if (weakestIdx < 0 || zones[weakestIdx].Strength >= fact.Weight)
                        return;   // all existing zones stronger — discard incoming fact

                    zones[weakestIdx] = CreateZone(fact, mergeRadius, currentBar, isSupport);
                }
                else
                {
                    zones[count++] = CreateZone(fact, mergeRadius, currentBar, isSupport);
                }
            }
        }

        private static void UpdateZoneWithFact(ref SRZone z, SRLevelFact fact)
        {
            // Centroid, Strength, Sources, and SourceLabel are all managed by
            // RecomputeZoneStrengths(). This method only widens the zone's role,
            // which must happen at merge time when the fact's direction is known.
            if (fact.IsSupport && fact.IsResistance)
                z.Role = SRZoneRole.Both;
            else if (fact.IsSupport    && z.Role == SRZoneRole.Resistance)
                z.Role = SRZoneRole.Both;
            else if (fact.IsResistance && z.Role == SRZoneRole.Support)
                z.Role = SRZoneRole.Both;
        }

        /// <summary>
        /// Recompute Strength, Sources, SourceLabel, and ZonePrice centroid for
        /// every active zone from scratch using the current bar's fact set.
        ///
        /// CALLED TWICE per bar by MergeFacts:
        ///   1. Before the merge loop — existing zones get current-bar strength
        ///      so eviction decisions use current strength, not stale prior-bar values.
        ///   2. After the merge loop — newly created zones and same-bar clusters
        ///      get correct strength/sources/centroid on the bar they first form.
        ///
        /// CENTROID RULE (matches frozen SRZone.ZonePrice contract):
        ///   centroid = sum(fact.Price × effectiveWeight) / sum(effectiveWeight)
        ///   capped at MaxZoneStrength total weight.
        ///   Deterministic — same facts in any order produce the same result.
        ///   ZonePrice only updates when contributing facts are present;
        ///   a zone with no matching facts this bar retains its prior centroid.
        ///
        /// CLOSEST-ZONE RULE (matches MergeFactIntoArray selection):
        ///   Each fact contributes to only the single closest zone within
        ///   mergeRadius. Prevents one fact from strengthening two neighboring zones.
        ///
        /// ALLOCATION RULE:
        ///   SourceLabel is only rebuilt when Sources changes. On the common path
        ///   (same swing/pivot/profile facts as the prior bar) no string allocation
        ///   occurs. The only per-bar allocation is MathStructure.ValueArea_Calculate.
        /// </summary>
        private void RecomputeZoneStrengths(SRLevelFact[] facts, int factCount, double mergeRadius)
        {
            int maxStrength = _config.MaxZoneStrength;

            // Reset Strength / Sources / price accumulators on all active zones.
            // Capture prior Sources before clearing — FinaliseZoneStrengths uses this
            // to skip SourceLabel rebuild when Sources is unchanged this bar, keeping
            // the existing label without clearing it first.
            // ZonePrice and SourceLabel are NOT reset here.
            for (int i = 0; i < _supportCount; i++)
            {
                if (!_supportZones[i].IsValid) continue;
                SRZone z = _supportZones[i];
                _scratchSupPriorSrc[i] = z.Sources;
                z.Strength             = 0;
                z.Sources              = SRSourceType.None;
                _supportZones[i]       = z;
                _scratchSupPriceSum[i] = 0.0;
            }
            for (int i = 0; i < _resistanceCount; i++)
            {
                if (!_resistanceZones[i].IsValid) continue;
                SRZone z = _resistanceZones[i];
                _scratchResPriorSrc[i] = z.Sources;
                z.Strength             = 0;
                z.Sources              = SRSourceType.None;
                _resistanceZones[i]    = z;
                _scratchResPriceSum[i] = 0.0;
            }

            // Accumulate each fact into its single closest zone
            for (int f = 0; f < factCount; f++)
            {
                SRLevelFact fact = facts[f];
                if (!fact.IsValid) continue;

                if (fact.IsSupport)
                    AccumulateFactStrength(fact, _supportZones,    _supportCount,
                        _scratchSupPriceSum, mergeRadius, maxStrength);
                if (fact.IsResistance)
                    AccumulateFactStrength(fact, _resistanceZones, _resistanceCount,
                        _scratchResPriceSum, mergeRadius, maxStrength);
            }

            // Finalise: compute centroid and rebuild SourceLabel only when Sources changed
            FinaliseZoneStrengths(_supportZones,    _supportCount,
                _scratchSupPriceSum, _scratchSupPriorSrc, mergeRadius);
            FinaliseZoneStrengths(_resistanceZones, _resistanceCount,
                _scratchResPriceSum, _scratchResPriorSrc, mergeRadius);
        }

        /// <summary>
        /// Accumulate a single fact into the closest active zone within mergeRadius.
        /// Updates that zone's Strength, Sources, and weighted price accumulator.
        /// Does not affect any other zone — one fact, one zone.
        /// </summary>
        private static void AccumulateFactStrength(
            SRLevelFact  fact,
            SRZone[]     zones,
            int          count,
            double[]     priceSum,
            double       mergeRadius,
            int          maxStrength)
        {
            // Find closest zone within mergeRadius — identical selection to MergeFactIntoArray
            int    bestIdx  = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (!zones[i].IsValid) continue;
                double dist = Math.Abs(zones[i].ZonePrice - fact.Price);
                if (dist < mergeRadius && dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx  = i;
                }
            }
            if (bestIdx < 0) return;

            SRZone z = zones[bestIdx];
            if (z.Strength < maxStrength)
            {
                int effectiveWeight = Math.Min(fact.Weight, maxStrength - z.Strength);
                z.Strength          = Math.Min(z.Strength + effectiveWeight, maxStrength);
                priceSum[bestIdx]  += fact.Price * effectiveWeight;
            }
            z.Sources     = z.Sources | fact.Source;
            zones[bestIdx] = z;
        }

        /// <summary>
        /// After all facts are accumulated, compute the deterministic weighted
        /// centroid for each zone and rebuild SourceLabel only when Sources changed.
        ///
        /// ZonePrice = accumulated weighted price sum / Strength.
        /// Zones with Strength == 0 (no contributing facts this bar) retain
        /// their prior centroid — they are orphaned and will be removed by the
        /// lifecycle pass.
        ///
        /// SourceLabel is a string allocation. It is only rebuilt when the zone's
        /// Sources bitmask has changed from the prior-bar value captured in
        /// <paramref name="priorSrc"/>. On bars where Sources is stable (the common
        /// case for persistent swing/pivot zones) no string allocation occurs.
        /// </summary>
        private static void FinaliseZoneStrengths(
            SRZone[]       zones,
            int            count,
            double[]       priceSum,
            SRSourceType[] priorSrc,
            double         mergeRadius)
        {
            for (int i = 0; i < count; i++)
            {
                if (!zones[i].IsValid) continue;
                SRZone z = zones[i];

                if (z.Strength > 0)
                {
                    z.ZonePrice = priceSum[i] / z.Strength;
                    z.ZoneLow   = z.ZonePrice - mergeRadius * 0.5;
                    z.ZoneHigh  = z.ZonePrice + mergeRadius * 0.5;
                }

                // Only rebuild SourceLabel when Sources actually changed.
                // On the common path (same swing/pivot facts as last bar),
                // Sources is identical to priorSrc[i] and no allocation occurs.
                if (z.Sources != SRSourceType.None && z.Sources != priorSrc[i])
                    z.SourceLabel = BuildSourceLabel(z.Sources);

                zones[i] = z;
            }
        }

        private SRZone CreateZone(SRLevelFact fact, double mergeRadius, int currentBar, bool isSupport)
        {
            SRZoneRole role;
            if (fact.IsSupport && fact.IsResistance) role = SRZoneRole.Both;
            else if (isSupport)                      role = SRZoneRole.Support;
            else                                     role = SRZoneRole.Resistance;

            int strength = Math.Min(fact.Weight, _config.MaxZoneStrength);

            return new SRZone
            {
                IsValid       = true,
                ZonePrice     = fact.Price,
                ZoneLow       = fact.Price - mergeRadius * 0.5,
                ZoneHigh      = fact.Price + mergeRadius * 0.5,
                Strength      = strength,
                Role          = role,
                State         = SRZoneState.Fresh,
                Sources       = fact.Source,
                TouchCount    = 0,
                CreatedBar    = currentBar,
                LastTestedBar = 0,
                SourceLabel   = BuildSourceLabel(fact.Source),
            };
        }

        private static int FindWeakestZone(SRZone[] zones, int count)
        {
            int weakestIdx = -1;
            int weakestStr = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (!zones[i].IsValid) continue;
                if (zones[i].Strength < weakestStr)
                {
                    weakestStr = zones[i].Strength;
                    weakestIdx = i;
                }
            }
            return weakestIdx;
        }

        private static string BuildSourceLabel(SRSourceType sources)
        {
            // Called by FinaliseZoneStrengths only when the zone's Sources bitmask
            // has changed, and by CreateZone at zone creation. One allocation per call.
            var sb = new System.Text.StringBuilder(32);
            if ((sources & SRSourceType.SwingH4)       != 0) { if (sb.Length > 0) sb.Append('+'); sb.Append("4Hsw"); }
            if ((sources & SRSourceType.SwingH2)       != 0) { if (sb.Length > 0) sb.Append('+'); sb.Append("2Hsw"); }
            if ((sources & SRSourceType.SwingH1)       != 0) { if (sb.Length > 0) sb.Append('+'); sb.Append("H1sw"); }
            if ((sources & SRSourceType.Session)       != 0) { if (sb.Length > 0) sb.Append('+'); sb.Append("Sess"); }
            if ((sources & SRSourceType.Pivot)         != 0) { if (sb.Length > 0) sb.Append('+'); sb.Append("Pvt");  }
            if ((sources & SRSourceType.VolumeProfile) != 0) { if (sb.Length > 0) sb.Append('+'); sb.Append("VP");   }
            if ((sources & SRSourceType.RoundNumber)   != 0) { if (sb.Length > 0) sb.Append('+'); sb.Append("Rnd");  }
            return sb.ToString();
        }

        // ====================================================================
        // LIFECYCLE PASS — age expiry, invalidation, touch detection, compact
        // ====================================================================

        private void RunLifecycle(double close, double tickSize, double atr, int currentBar)
        {
            double buffer   = _config.InvalidationTicks * tickSize;
            double proxDist = _config.ProximityATRMult  * atr;

            RunLifecycleOnArray(_supportZones,    ref _supportCount,
                close, tickSize, atr, buffer, proxDist, currentBar, isSupport: true);
            RunLifecycleOnArray(_resistanceZones, ref _resistanceCount,
                close, tickSize, atr, buffer, proxDist, currentBar, isSupport: false);
        }

        private void RunLifecycleOnArray(
            SRZone[] zones, ref int count,
            double close, double tickSize, double atr,
            double buffer, double proxDist,
            int currentBar, bool isSupport)
        {
            for (int i = 0; i < count; i++)
            {
                if (!zones[i].IsValid) continue;
                SRZone z = zones[i];

                // ── Orphan check ──────────────────────────────────────────
                // RecomputeZoneStrengths resets Strength to 0 each bar and only
                // re-fills it from currently-emitted facts. A zone with Strength==0
                // after recompute has no active supporting facts — its swing was
                // invalidated, its pivot recalculated away, or its profile level
                // disappeared. Remove it rather than letting it ghost in the buffer.
                if (z.Strength == 0)
                {
                    z.State = SRZoneState.Broken;
                    _log?.Warn(default(DateTime),
                        "SR zone orphaned (no active facts) at {0:F2} [{1}]",
                        z.ZonePrice, z.SourceLabel ?? "?");
                    zones[i] = z;
                    continue;
                }

                // ── Age expiry ────────────────────────────────────────────
                int ageLimit = GetAgeLimit(z.Sources);
                if (ageLimit > 0 && (currentBar - z.CreatedBar) > ageLimit)
                {
                    z.State = SRZoneState.Broken;
                    _log?.Warn(default(DateTime),
                        "SR zone expired by age at {0:F2} [{1}] age={2}",
                        z.ZonePrice, z.SourceLabel ?? "?", currentBar - z.CreatedBar);
                    zones[i] = z;
                    continue;
                }

                // ── Price invalidation (close through) ────────────────────
                bool broken = isSupport
                    ? close < z.ZoneLow  - buffer
                    : close > z.ZoneHigh + buffer;

                if (broken)
                {
                    z.State = SRZoneState.Broken;
                    _log?.Warn(default(DateTime),
                        "SR zone broken at {0:F2} [{1}] close={2:F2}",
                        z.ZonePrice, z.SourceLabel ?? "?", close);
                    zones[i] = z;
                    continue;
                }

                // ── Touch detection ───────────────────────────────────────
                bool inProx = Math.Abs(close - z.ZonePrice) < proxDist;
                if (inProx)
                {
                    if (z.State == SRZoneState.Fresh)
                        z.State = SRZoneState.Tested;
                    z.TouchCount++;
                    z.LastTestedBar = currentBar;
                }

                zones[i] = z;
            }

            // ── Compact — remove Broken zones ────────────────────────────
            int write = 0;
            for (int read = 0; read < count; read++)
            {
                if (zones[read].IsValid && zones[read].State != SRZoneState.Broken)
                    zones[write++] = zones[read];
            }
            for (int k = write; k < count; k++)
                zones[k] = SRZone.Empty;
            count = write;
        }

        private int GetAgeLimit(SRSourceType sources)
        {
            // Priority: H4 > H2 > H1. A zone with multiple swing sources
            // gets the longest-lived TF's expiry.
            // Non-swing sources (session, pivot, profile) have no age expiry —
            // they are governed by source self-management and invalidation only.
            if ((sources & SRSourceType.SwingH4) != 0) return _config.SwingExpiryBarsH4;
            if ((sources & SRSourceType.SwingH2) != 0) return _config.SwingExpiryBarsH2;
            if ((sources & SRSourceType.SwingH1) != 0) return _config.SwingExpiryBarsH1;
            return 0;  // no age expiry for this zone
        }

        // ====================================================================
        // RESULT BUILDER — zone fields + passthrough fields (all sources)
        // ====================================================================

        private SupportResistanceResult BuildResult(double price, double tickSize, double atr)
        {
            double proxDist    = _config.ProximityATRMult   * atr;
            double mergeRadius = _config.MergeRadiusATRMult * atr;

            SRZone nearestSup  = FindNearest(price, _supportZones,    _supportCount,    isSupport: true,  mergeRadius);
            SRZone nearestRes  = FindNearest(price, _resistanceZones, _resistanceCount, isSupport: false, mergeRadius);
            SRZone strongSup   = FindStrongest(_supportZones,    _supportCount);
            SRZone strongRes   = FindStrongest(_resistanceZones, _resistanceCount);

            bool atSup = nearestSup.IsValid
                && Math.Abs(price - nearestSup.ZonePrice)  < proxDist;
            bool atRes = nearestRes.IsValid
                && Math.Abs(price - nearestRes.ZonePrice) < proxDist;

            // Distance fields are 0.0 when At* is true — per SupportResistanceResult contract.
            // A non-zero distance while AtSupport is true would violate the stated guarantee.
            double ticksToSup = (!atSup && nearestSup.IsValid && tickSize > 0)
                ? Math.Max(0.0, (price - nearestSup.ZonePrice)  / tickSize) : 0.0;
            double ticksToRes = (!atRes && nearestRes.IsValid && tickSize > 0)
                ? Math.Max(0.0, (nearestRes.ZonePrice - price)  / tickSize) : 0.0;

            double atrsToSup = atr > 0 ? ticksToSup * tickSize / atr : 0.0;
            double atrsToRes = atr > 0 ? ticksToRes * tickSize / atr : 0.0;

            return new SupportResistanceResult(
                isValid:               true,
                nearestSupport:        nearestSup,
                nearestResistance:     nearestRes,
                strongestSupport:      strongSup,
                strongestResistance:   strongRes,
                atSupport:             atSup,
                atResistance:          atRes,
                ticksToSupport:        ticksToSup,
                ticksToResistance:     ticksToRes,
                atrsToSupport:         atrsToSup,
                atrsToResistance:      atrsToRes,
                activeSupportCount:    _supportCount,
                activeResistanceCount: _resistanceCount,
                poc:                   _profile.POC,
                vaHigh:                _profile.VAHigh,
                vaLow:                 _profile.VALow,
                pocSkew:               _profile.POCSkew,
                profileIsReady:        _profile.IsReady,
                swingHighH1:           _swings.LastHighH1,
                swingLowH1:            _swings.LastLowH1,
                swingHighH2:           _swings.LastHighH2,
                swingLowH2:            _swings.LastLowH2,
                swingHighH4:           _swings.LastHighH4,
                swingLowH4:            _swings.LastLowH4,
                pivotPP:               _pivots.PP,
                pivotR1:               _pivots.R1,
                pivotR2:               _pivots.R2,
                pivotS1:               _pivots.S1,
                pivotS2:               _pivots.S2);
        }

        private static SRZone FindNearest(double price, SRZone[] zones, int count, bool isSupport, double mergeRadius)
        {
            // Directionally strict with mergeRadius tolerance — matches the
            // frozen SupportResistanceResult contract exactly:
            //
            // Support:    ZonePrice <= price + mergeRadius
            //             Allows a zone whose centroid is slightly above price
            //             but whose band still overlaps the current price level.
            //
            // Resistance: ZonePrice >= price - mergeRadius
            //             Allows a zone whose centroid is slightly below price
            //             but whose band still overlaps the current price level.
            //
            // Zones with Role == Both are eligible for both directions.
            // If no directional zone qualifies, returns SRZone.Empty.
            // Caller must check IsValid before reading any zone field.
            SRZone best     = SRZone.Empty;
            double bestDist = double.MaxValue;

            for (int i = 0; i < count; i++)
            {
                if (!zones[i].IsValid) continue;

                double zp = zones[i].ZonePrice;

                // Directional filter with mergeRadius tolerance
                bool eligible = isSupport
                    ? zp <= price + mergeRadius
                    : zp >= price - mergeRadius;

                if (!eligible) continue;

                double dist = Math.Abs(price - zp);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best     = zones[i];
                }
            }
            return best;
        }

        private static SRZone FindStrongest(SRZone[] zones, int count)
        {
            SRZone best = SRZone.Empty;
            for (int i = 0; i < count; i++)
            {
                if (!zones[i].IsValid) continue;
                if (!best.IsValid || zones[i].Strength > best.Strength)
                    best = zones[i];
            }
            return best;
        }
    }

    // ========================================================================
    // VOLUME PROFILE LEVEL SOURCE
    // ========================================================================
    //
    // IMPLEMENTATION CHECKLIST
    //
    // A. Role
    //   [x] Owns the session price→volume accumulator (absorbs DataFeed._sessionVolumeProfile)
    //   [x] Computes POC / VAH / VAL / POCSkew each bar (absorbs HostStrategy POC block)
    //   [x] Resets on OnSessionOpen()
    //   [x] Exposes computed values as read-only properties
    //   [x] EmitFacts() writes up to 3 SRLevelFact entries into the caller's buffer,
    //       with per-fact buffer guards — POC is emitted even if no room for VAH/VAL
    //
    // B. Accumulation logic (verbatim from DataFeed.UpdateVolumeProfile)
    //   [x] Key = Math.Round(close / tickSize) * tickSize — tick-aligned, no float noise
    //   [x] Volume > 0 guard before accumulating
    //   [x] TickSize > 0 guard before accumulating
    //   [x] Dictionary reused each session (Clear on OnSessionOpen) — no per-session alloc
    //
    // C. Computation logic (verbatim from HostStrategy POC block)
    //   [x] MinProfileBars guard — POC meaningless below threshold
    //   [x] MathStructure.POC_Find — returns 0.0 on empty profile
    //   [x] poc > 0 guard before ValueArea_Calculate and Profile_POCSkew
    //   [x] MathStructure.ValueArea_Calculate — uses config.ValueAreaCoverage
    //   [x] MathStructure.Profile_POCSkew
    //   [x] On poc == 0 despite bar count: prior computed values are preserved,
    //       not zeroed. A single bad bar should not erase a valid prior POC.
    //       This means _poc/_vaHigh/_vaLow/_pocSkew hold the last good computation
    //       until a valid poc is produced again. EmitFacts guards on _poc <= 0.0
    //       so stale values from a prior session cannot be emitted after OnSessionOpen()
    //       resets the fields to 0.0.
    //
    // D. EmitFacts output
    //   POC  → IsSupport = true, IsResistance = true (bidirectional)
    //   VAH  → IsSupport = false, IsResistance = true
    //   VAL  → IsSupport = true,  IsResistance = false
    //   All three only emitted when IsReady and POC > 0
    //
    // E. Parallel-run contract
    //   During Stage 1 migration, HostStrategy runs both this source and the
    //   existing POC block simultaneously. Pass criteria (5 consecutive sessions):
    //     Price fields (POC, VAHigh, VALow):
    //       Math.Abs(srResult.POC - snap.Get(SnapKeys.POC)) <= tickSize
    //       Same for VAHigh, VALow.
    //       Rationale: both paths use tick-rounded dictionary keys, so identical
    //       prices are expected. 1-tick tolerance covers floating-point edge cases.
    //     Ratio field (POCSkew — not a price):
    //       Math.Abs(srResult.POCSkew - snap.Get(SnapKeys.POCSkew)) <= 1e-9
    //       Rationale: both paths accumulate from the same snapshot.Primary.Close
    //       and Volume values using identical tick-rounding math. They are separate
    //       dictionary instances with identical contents, so the same float ops
    //       produce bit-identical results. Any difference > 1e-9 indicates a
    //       logic divergence, not a rounding artefact.
    //   Known intentional divergence (not a failure):
    //     If POC_Find() returns 0.0 on a ready profile (degenerate bar), the old
    //     HostStrategy path publishes 0.0 for that bar. This source preserves the
    //     last valid computed values instead. Both paths converge on the next bar
    //     where POC_Find() returns a valid price. The parallel-run gate ignores
    //     bars where snap.Get(SnapKeys.POC) == 0.0 — those are old-path degenerate
    //     bars and do not count as failures.
    //   Log key: "PROFILE_COMPARE" — written every bar once profile is ready.
    //   Any failure on a non-degenerate bar immediately pauses migration.
    //
    // F. Memory
    //   Dictionary<double, double> allocated once at construction with capacity 512.
    //   Same instance reused each session via Clear(). No per-bar allocation except
    //   MathStructure.ValueArea_Calculate (one Array.Sort per bar when IsReady).
    // ========================================================================

    /// <summary>
    /// Internal source module — session volume profile.
    /// Absorbs DataFeed._sessionVolumeProfile accumulator and
    /// HostStrategy POC / VAH / VAL / POCSkew computation.
    /// </summary>
    internal sealed class VolumeProfileLevelSource
    {
        // ── Config ────────────────────────────────────────────────────────────
        private readonly SupportResistanceCoreConfig _config;
        // _log used for parallel-run compare logging (PROFILE_COMPARE rows)
        private readonly StrategyLogger              _log;

        // ── Profile state ─────────────────────────────────────────────────────
        // Dictionary reused each session. Same pattern as DataFeed._sessionVolumeProfile.
        // Capacity 512: typical RTH session has ~78 bars × average range of a few ticks.
        // 512 slots cover any realistic session without rehashing.
        private readonly Dictionary<double, double> _profile
            = new Dictionary<double, double>(512);

        private int _profileBars = 0;

        // ── Computed values — updated each bar once IsReady ───────────────────
        private double _poc     = 0.0;
        private double _vaHigh  = 0.0;
        private double _vaLow   = 0.0;
        private double _pocSkew = 0.0;

        // ── Public read-only surface ──────────────────────────────────────────

        /// <summary>Point of Control price. 0.0 until IsReady.</summary>
        public double POC     => _poc;

        /// <summary>Value Area High. 0.0 until IsReady.</summary>
        public double VAHigh  => _vaHigh;

        /// <summary>Value Area Low. 0.0 until IsReady.</summary>
        public double VALow   => _vaLow;

        /// <summary>
        /// POC skew: volume above POC / volume below POC.
        /// 0.0 until IsReady (not 1.0 — zero unambiguously signals no data).
        /// </summary>
        public double POCSkew => _pocSkew;

        /// <summary>
        /// True when profile has accumulated at least MinProfileBars primary bars
        /// and POC / VAH / VAL are statistically meaningful.
        /// </summary>
        public bool IsReady => _profileBars >= _config.MinProfileBars;

        // ====================================================================
        // CONSTRUCTION
        // ====================================================================

        public VolumeProfileLevelSource(SupportResistanceCoreConfig config, StrategyLogger log)
        {
            _config = config;
            _log    = log;
        }

        // ====================================================================
        // LIFECYCLE
        // ====================================================================

        /// <summary>
        /// Reset session state. Call from SupportResistanceEngine.OnSessionOpen().
        /// Clears the profile dictionary (reuses the instance — no allocation).
        /// Resets all computed values to 0.0.
        /// </summary>
        public void OnSessionOpen()
        {
            _profile.Clear();
            _profileBars = 0;
            _poc         = 0.0;
            _vaHigh      = 0.0;
            _vaLow       = 0.0;
            _pocSkew     = 0.0;
        }

        // ====================================================================
        // UPDATE — called every primary bar from SupportResistanceEngine.Update()
        // ====================================================================

        /// <summary>
        /// Accumulate current bar into the session profile and recompute
        /// POC / VAH / VAL / POCSkew if the profile is ready.
        ///
        /// Logic is verbatim from:
        ///   DataFeed.UpdateVolumeProfile()       — accumulation
        ///   HostStrategy.OnPopulateIndicatorBag() — POC computation (lines 1181–1218)
        /// </summary>
        public void Update(ref MarketSnapshot snapshot)
        {
            double tickSize = snapshot.Primary.TickSize;
            if (tickSize <= 0.0) return;

            double vol   = snapshot.Primary.Volume;
            double close = snapshot.Primary.Close;

            // ── Accumulation (verbatim from DataFeed.UpdateVolumeProfile) ───
            if (vol > 0.0)
            {
                // Round to nearest tick — prevents float-key mismatches in Dictionary.
                // Uses bar Close, not OHLC average: we want the accepted price,
                // the one the bar closed at. Deterministic, no look-ahead.
                double key = Math.Round(close / tickSize) * tickSize;

                double existing;
                if (_profile.TryGetValue(key, out existing))
                    _profile[key] = existing + vol;
                else
                    _profile[key] = vol;

                _profileBars++;
            }

            // ── Computation (verbatim from HostStrategy POC block) ───────────
            // Guard: profile must have enough bars to be statistically meaningful.
            // Below MinProfileBars (default 20), one dominant bar skews POC.
            if (!IsReady) return;

            double maxVol;
            double poc = MathStructure.POC_Find(_profile, out maxVol);

            // Defensive: POC_Find returns 0.0 on an empty dictionary.
            // Profile can be non-empty but still return 0.0 if all volumes are 0.
            if (poc <= 0.0)
            {
                // Leave existing computed values unchanged — do not zero them out.
                // A single bad bar should not erase a valid prior computation.
                return;
            }

            double vaHigh, vaLow;
            MathStructure.ValueArea_Calculate(
                _profile,
                poc,
                _config.ValueAreaCoverage,
                out vaHigh,
                out vaLow);

            double pocSkew = MathStructure.Profile_POCSkew(_profile, poc);

            _poc     = poc;
            _vaHigh  = vaHigh;
            _vaLow   = vaLow;
            _pocSkew = pocSkew;
        }

        // ====================================================================
        // EMIT FACTS — called by merge pass each bar
        // ====================================================================

        /// <summary>
        /// Write up to 3 <see cref="SRLevelFact"/> entries into the caller's
        /// pre-allocated buffer. Facts are only emitted when IsReady and POC > 0.
        ///
        /// Emitted facts:
        ///   POC — bidirectional (IsSupport = true, IsResistance = true)
        ///   VAH — resistance only (emitted when VAHigh > 0 and buffer has space)
        ///   VAL — support only   (emitted when VALow  > 0 and buffer has space)
        ///
        /// Each fact is guarded individually — if the buffer is almost full,
        /// POC is still emitted even if there is no room for VAH or VAL.
        /// Caller is responsible for allocating a buffer large enough for all sources.
        /// </summary>
        public void EmitFacts(SRLevelFact[] buf, ref int count)
        {
            if (!IsReady || _poc <= 0.0) return;
            if (buf == null) return;

            // POC — bidirectional: acts as both support and resistance
            if (count < buf.Length)
            {
                buf[count++] = new SRLevelFact
                {
                    Price        = _poc,
                    Source       = SRSourceType.VolumeProfile,
                    Weight       = _config.WeightPOC,
                    IsSupport    = true,
                    IsResistance = true,
                    IsValid      = true,
                };
            }

            // VAH — resistance: price testing from below
            if (_vaHigh > 0.0 && count < buf.Length)
            {
                buf[count++] = new SRLevelFact
                {
                    Price        = _vaHigh,
                    Source       = SRSourceType.VolumeProfile,
                    Weight       = _config.WeightVA,
                    IsSupport    = false,
                    IsResistance = true,
                    IsValid      = true,
                };
            }

            // VAL — support: price testing from above
            if (_vaLow > 0.0 && count < buf.Length)
            {
                buf[count++] = new SRLevelFact
                {
                    Price        = _vaLow,
                    Source       = SRSourceType.VolumeProfile,
                    Weight       = _config.WeightVA,
                    IsSupport    = true,
                    IsResistance = false,
                    IsValid      = true,
                };
            }
        }
    }

    // ========================================================================
    // SWING LEVEL SOURCE
    // ========================================================================
    //
    // IMPLEMENTATION CHECKLIST
    //
    // A. Role
    //   [x] Absorbs HTFLevelEngine swing detection logic verbatim
    //   [x] Extends storage from 1 level per side (HTFLevelEngine) to MaxSwingsPerTF
    //   [x] H1 / H2 / H4 only — no 15m, no daily, no weekly (v1 scope)
    //   [x] Exposes Last* properties for SupportResistanceResult passthrough fields
    //   [x] EmitFacts() writes up to (MaxSwingsPerTF × 6) SRLevelFact entries
    //
    // B. Swing detection (verbatim from HTFLevelEngine.UpdateHTFSwings)
    //   [x] HTF_SWING_STRENGTH = 2 (frozen spec — 5-bar confirmation structure)
    //   [x] MIN_ARRAY_LEN = 5 (2 × strength + 1)
    //   [x] Invalidation BEFORE detection on each bar (prevents race condition)
    //   [x] Invalidation: close > swingHigh + buffer → clear that slot
    //                     close < swingLow  - buffer → clear that slot
    //   [x] Duplicate guard: new candidate must differ from all stored levels by > tickSize
    //   [x] Levels persist across session boundaries — no OnSessionOpen() reset
    //   [x] Log swing additions and invalidations
    //
    // C. Multi-level storage (improvement over HTFLevelEngine single-level)
    //   [x] _h1Highs[], _h1Lows[], _h2Highs[], _h2Lows[], _h4Highs[], _h4Lows[]
    //       each of length MaxSwingsPerTF (default 3)
    //   [x] Count fields track occupied slots per array
    //   [x] Insertion: shift existing entries back, insert new at index 0 (most recent first)
    //   [x] When full: oldest entry (highest index) is evicted — most recent 3 are kept
    //   [x] Compact after invalidation: shift valid entries forward, zero trailing slots
    //
    // D. Last* passthrough properties
    //   [x] LastHighH1 = _h1Highs[0] when count > 0, else 0.0
    //       Matches HTFLevelEngine single-value behavior for SnapKey passthrough
    //
    // E. EmitFacts output
    //   Per stored swing high: IsSupport = false, IsResistance = true
    //   Per stored swing low:  IsSupport = true,  IsResistance = false
    //   Source and Weight from config (WeightSwingH1/H2/H4)
    //   Per-slot buffer guard — partial emission if buffer is nearly full
    // ========================================================================

    internal sealed class SwingLevelSource
    {
        private readonly SupportResistanceCoreConfig _config;
        private readonly StrategyLogger              _log;

        private const int STRENGTH = 2;
        private const int MIN_LEN  = 2 * STRENGTH + 1;   // 5 — matches HTFLevelEngine MIN_ARRAY_LEN

        // ── Per-TF swing arrays — most recent at index 0 ──────────────────
        private readonly double[] _h1Highs, _h1Lows;
        private readonly double[] _h2Highs, _h2Lows;
        private readonly double[] _h4Highs, _h4Lows;
        private int _h1HighCount, _h1LowCount;
        private int _h2HighCount, _h2LowCount;
        private int _h4HighCount, _h4LowCount;

        // ── Passthrough — most recent confirmed swing per TF ──────────────
        /// <summary>Last confirmed 1H swing high. 0.0 if none active.</summary>
        public double LastHighH1 => _h1HighCount > 0 ? _h1Highs[0] : 0.0;
        /// <summary>Last confirmed 1H swing low. 0.0 if none active.</summary>
        public double LastLowH1  => _h1LowCount  > 0 ? _h1Lows[0]  : 0.0;
        /// <summary>Last confirmed 2H swing high. 0.0 if none active.</summary>
        public double LastHighH2 => _h2HighCount > 0 ? _h2Highs[0] : 0.0;
        /// <summary>Last confirmed 2H swing low. 0.0 if none active.</summary>
        public double LastLowH2  => _h2LowCount  > 0 ? _h2Lows[0]  : 0.0;
        /// <summary>Last confirmed 4H swing high. 0.0 if none active.</summary>
        public double LastHighH4 => _h4HighCount > 0 ? _h4Highs[0] : 0.0;
        /// <summary>Last confirmed 4H swing low. 0.0 if none active.</summary>
        public double LastLowH4  => _h4LowCount  > 0 ? _h4Lows[0]  : 0.0;

        public SwingLevelSource(SupportResistanceCoreConfig config, StrategyLogger log)
        {
            _config = config;
            _log    = log;

            int n    = config.MaxSwingsPerTF;
            _h1Highs = new double[n]; _h1Lows = new double[n];
            _h2Highs = new double[n]; _h2Lows = new double[n];
            _h4Highs = new double[n]; _h4Lows = new double[n];
        }

        // ── Swing zones persist across session boundaries — no OnSessionOpen needed

        /// <summary>
        /// One-time historical scan. Call from SupportResistanceEngine.Bootstrap()
        /// immediately after construction, before the first Update().
        ///
        /// Walks the full available rolling arrays in each HTF bar snapshot and
        /// inserts every confirmed swing found in the historical window. This
        /// populates the engine with historically-grounded levels on bar 1 rather
        /// than waiting for new swings to form during the live session.
        ///
        /// Scan range: every candidate index from STRENGTH to array.Length - STRENGTH - 1.
        /// This is the same 5-bar confirmation structure used in Update(), applied
        /// at every valid offset in the rolling history.
        ///
        /// Insertion order: oldest first (highest index first), so that when the
        /// arrays reach capacity the most recent swings survive — same recency
        /// preference as the live Update() path.
        ///
        /// Safe to call even if arrays are not yet populated — guards on null
        /// and minimum length degrade gracefully.
        /// </summary>
        public void Bootstrap(ref MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return;

            double tickSize = snapshot.Primary.TickSize;
            if (tickSize <= 0.0) return;

            // Use bar 0 time as the timestamp for all bootstrap insertions.
            // These are historical levels, not live events — the exact timestamp
            // is for log readability only.
            DateTime time = snapshot.Primary.Time;

            BootstrapArray(snapshot.Higher2.Highs, _h1Highs, ref _h1HighCount,
                tickSize, time, "H1 SwingHigh", isHigh: true);
            BootstrapArray(snapshot.Higher2.Lows,  _h1Lows,  ref _h1LowCount,
                tickSize, time, "H1 SwingLow",  isHigh: false);

            BootstrapArray(snapshot.Higher3.Highs, _h2Highs, ref _h2HighCount,
                tickSize, time, "H2 SwingHigh", isHigh: true);
            BootstrapArray(snapshot.Higher3.Lows,  _h2Lows,  ref _h2LowCount,
                tickSize, time, "H2 SwingLow",  isHigh: false);

            BootstrapArray(snapshot.Higher4.Highs, _h4Highs, ref _h4HighCount,
                tickSize, time, "H4 SwingHigh", isHigh: true);
            BootstrapArray(snapshot.Higher4.Lows,  _h4Lows,  ref _h4LowCount,
                tickSize, time, "H4 SwingLow",  isHigh: false);

            _log?.Warn(time,
                "SR Bootstrap complete: H1 {0}h/{1}l  H2 {2}h/{3}l  H4 {4}h/{5}l",
                _h1HighCount, _h1LowCount,
                _h2HighCount, _h2LowCount,
                _h4HighCount, _h4LowCount);
        }

        private void BootstrapArray(
            double[] srcArr, double[] dstArr, ref int dstCount,
            double tickSize, DateTime time, string label, bool isHigh)
        {
            if (srcArr == null || srcArr.Length < MIN_LEN) return;

            // Walk from oldest valid pivot position to most recent.
            // Oldest = highest index (rolling arrays: index 0 = most recent).
            // Insert oldest first so that when the destination fills, the most
            // recent swings (lowest indices, inserted last) survive.
            int maxIdx = srcArr.Length - STRENGTH - 1;
            for (int pivot = maxIdx; pivot >= STRENGTH; pivot--)
            {
                // Build a synthetic window of 2*STRENGTH+1 bars centred on pivot.
                // IsSwingHigh/Low expects: arr[0] = most recent, arr[STRENGTH] = pivot.
                // We need a temporary window slice in the correct orientation.
                bool confirmed = isHigh
                    ? IsSwingHighAt(srcArr, pivot, STRENGTH)
                    : IsSwingLowAt(srcArr,  pivot, STRENGTH);

                if (confirmed)
                    TryInsert(dstArr, ref dstCount, srcArr[pivot], tickSize, time, label);
            }
        }

        /// <summary>
        /// IsSwingHigh check at an arbitrary index position in a rolling array.
        /// Rolling arrays: index 0 = most recent bar, higher index = older bar.
        /// A swing high at <paramref name="pivot"/> requires:
        ///   - all bars MORE RECENT than pivot (lower indices) have lower highs
        ///   - all bars OLDER    than pivot (higher indices) have lower highs
        /// </summary>
        private static bool IsSwingHighAt(double[] arr, int pivot, int strength)
        {
            // Right side (more recent bars): indices pivot-strength .. pivot-1
            for (int i = pivot - strength; i < pivot; i++)
                if (i < 0 || arr[i] >= arr[pivot]) return false;

            // Left side (older bars): indices pivot+1 .. pivot+strength
            for (int i = pivot + 1; i <= pivot + strength; i++)
                if (i >= arr.Length || arr[i] >= arr[pivot]) return false;

            return true;
        }

        /// <summary>
        /// IsSwingLow check at an arbitrary index position in a rolling array.
        /// </summary>
        private static bool IsSwingLowAt(double[] arr, int pivot, int strength)
        {
            // Right side (more recent bars): indices pivot-strength .. pivot-1
            for (int i = pivot - strength; i < pivot; i++)
                if (i < 0 || arr[i] <= arr[pivot]) return false;

            // Left side (older bars): indices pivot+1 .. pivot+strength
            for (int i = pivot + 1; i <= pivot + strength; i++)
                if (i >= arr.Length || arr[i] <= arr[pivot]) return false;

            return true;
        }

        public void Update(ref MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid) return;

            double tickSize = snapshot.Primary.TickSize;
            double close    = snapshot.Primary.Close;
            double buffer   = _config.InvalidationTicks * tickSize;
            DateTime time   = snapshot.Primary.Time;

            // ── Step 1: Invalidate BEFORE detecting (verbatim ordering from HTFLevelEngine) ──
            InvalidateArray(_h1Highs, ref _h1HighCount, close, buffer, above: true,  time, "H1 SwingHigh");
            InvalidateArray(_h1Lows,  ref _h1LowCount,  close, buffer, above: false, time, "H1 SwingLow");
            InvalidateArray(_h2Highs, ref _h2HighCount, close, buffer, above: true,  time, "H2 SwingHigh");
            InvalidateArray(_h2Lows,  ref _h2LowCount,  close, buffer, above: false, time, "H2 SwingLow");
            InvalidateArray(_h4Highs, ref _h4HighCount, close, buffer, above: true,  time, "H4 SwingHigh");
            InvalidateArray(_h4Lows,  ref _h4LowCount,  close, buffer, above: false, time, "H4 SwingLow");

            // ── Step 2: Detect new swings (verbatim from HTFLevelEngine) ─────────────────
            // 1H → Higher2 (matches snapshot.Higher2 mapping in HTFLevelEngine)
            var h1 = snapshot.Higher2;
            if (h1.Highs != null && h1.Highs.Length >= MIN_LEN)
            {
                if (MathSMC.IsSwingHigh(h1.Highs, STRENGTH))
                    TryInsert(_h1Highs, ref _h1HighCount, h1.Highs[STRENGTH], tickSize, time, "H1 SwingHigh");
                if (MathSMC.IsSwingLow(h1.Lows, STRENGTH))
                    TryInsert(_h1Lows,  ref _h1LowCount,  h1.Lows[STRENGTH],  tickSize, time, "H1 SwingLow");
            }

            // 2H → Higher3
            var h2 = snapshot.Higher3;
            if (h2.Highs != null && h2.Highs.Length >= MIN_LEN)
            {
                if (MathSMC.IsSwingHigh(h2.Highs, STRENGTH))
                    TryInsert(_h2Highs, ref _h2HighCount, h2.Highs[STRENGTH], tickSize, time, "H2 SwingHigh");
                if (MathSMC.IsSwingLow(h2.Lows, STRENGTH))
                    TryInsert(_h2Lows,  ref _h2LowCount,  h2.Lows[STRENGTH],  tickSize, time, "H2 SwingLow");
            }

            // 4H → Higher4
            var h4 = snapshot.Higher4;
            if (h4.Highs != null && h4.Highs.Length >= MIN_LEN)
            {
                if (MathSMC.IsSwingHigh(h4.Highs, STRENGTH))
                    TryInsert(_h4Highs, ref _h4HighCount, h4.Highs[STRENGTH], tickSize, time, "H4 SwingHigh");
                if (MathSMC.IsSwingLow(h4.Lows, STRENGTH))
                    TryInsert(_h4Lows,  ref _h4LowCount,  h4.Lows[STRENGTH],  tickSize, time, "H4 SwingLow");
            }
        }

        public void EmitFacts(SRLevelFact[] buf, ref int count)
        {
            if (buf == null) return;
            EmitArray(_h1Highs, _h1HighCount, SRSourceType.SwingH1, _config.WeightSwingH1, false, true,  buf, ref count);
            EmitArray(_h1Lows,  _h1LowCount,  SRSourceType.SwingH1, _config.WeightSwingH1, true,  false, buf, ref count);
            EmitArray(_h2Highs, _h2HighCount, SRSourceType.SwingH2, _config.WeightSwingH2, false, true,  buf, ref count);
            EmitArray(_h2Lows,  _h2LowCount,  SRSourceType.SwingH2, _config.WeightSwingH2, true,  false, buf, ref count);
            EmitArray(_h4Highs, _h4HighCount, SRSourceType.SwingH4, _config.WeightSwingH4, false, true,  buf, ref count);
            EmitArray(_h4Lows,  _h4LowCount,  SRSourceType.SwingH4, _config.WeightSwingH4, true,  false, buf, ref count);
        }

        // ── Private helpers ────────────────────────────────────────────────────

        private void InvalidateArray(
            double[] arr, ref int count,
            double close, double buffer, bool above,
            DateTime time, string label)
        {
            for (int i = 0; i < count; i++)
            {
                if (arr[i] <= 0.0) continue;
                bool broken = above
                    ? close > arr[i] + buffer
                    : close < arr[i] - buffer;
                if (broken)
                {
                    _log?.Warn(time, "SR {0} invalidated: {1:F2} close={2:F2}", label, arr[i], close);
                    arr[i] = 0.0;
                }
            }
            // Compact: shift non-zero values forward, zero trailing slots
            int write = 0;
            for (int read = 0; read < count; read++)
                if (arr[read] > 0.0) arr[write++] = arr[read];
            for (int k = write; k < count; k++) arr[k] = 0.0;
            count = write;
        }

        private void TryInsert(
            double[] arr, ref int count,
            double candidate, double tickSize,
            DateTime time, string label)
        {
            if (candidate <= 0.0) return;

            // Duplicate guard: reject if within tickSize of any stored level
            for (int i = 0; i < count; i++)
                if (Math.Abs(arr[i] - candidate) <= tickSize) return;

            _log?.Warn(time, "SR {0} added: {1:F2}", label, candidate);

            int n = arr.Length;
            if (count < n)
            {
                // Shift existing entries back one slot to make room at index 0
                for (int i = Math.Min(count, n - 1); i > 0; i--)
                    arr[i] = arr[i - 1];
                arr[0] = candidate;
                count++;
            }
            else
            {
                // Buffer full — evict oldest (index n-1), shift remaining back, insert at 0
                for (int i = n - 1; i > 0; i--)
                    arr[i] = arr[i - 1];
                arr[0] = candidate;
                // count stays at n
            }
        }

        private static void EmitArray(
            double[] arr, int count,
            SRSourceType source, int weight,
            bool isSupport, bool isResistance,
            SRLevelFact[] buf, ref int bufCount)
        {
            for (int i = 0; i < count && bufCount < buf.Length; i++)
            {
                if (arr[i] <= 0.0) continue;
                buf[bufCount++] = new SRLevelFact
                {
                    Price        = arr[i],
                    Source       = source,
                    Weight       = weight,
                    IsSupport    = isSupport,
                    IsResistance = isResistance,
                    IsValid      = true,
                };
            }
        }
    }

    // ========================================================================
    // PIVOT LEVEL SOURCE
    // ========================================================================
    //
    // IMPLEMENTATION CHECKLIST
    //
    // A. Role
    //   [x] Absorbs HTFLevelEngine.UpdatePivots() — formulas unchanged
    //   [x] Day-change detection via Date comparison (improvement over HTFLevelEngine
    //       which used PDH delta — fails if PDH is identical on two consecutive days)
    //   [x] Exposes PP/R1/R2/S1/S2 as read-only properties for result passthrough
    //   [x] EmitFacts() writes up to 5 SRLevelFact entries with per-fact guards
    //   [x] Persists across session boundaries — no OnSessionOpen() reset
    //
    // B. Computation (formulas verbatim from HTFLevelEngine)
    //   [x] PP = (PDH + PDL + PDC) / 3
    //   [x] R1 = (2 × PP) - PDL
    //   [x] R2 = PP + (PDH - PDL)
    //   [x] S1 = (2 × PP) - PDH
    //   [x] S2 = PP - (PDH - PDL)
    //   [x] Guard: PDH / PDL / PDC must all be > 0
    //   [x] Degrade to 0.0 until first PrevDay data available
    //
    // C. EmitFacts role assignments
    //   PP  → IsSupport = true,  IsResistance = true  (bidirectional)
    //   R1  → IsSupport = false, IsResistance = true
    //   R2  → IsSupport = false, IsResistance = true
    //   S1  → IsSupport = true,  IsResistance = false
    //   S2  → IsSupport = true,  IsResistance = false
    // ========================================================================

    internal sealed class PivotLevelSource
    {
        private readonly SupportResistanceCoreConfig _config;
        private readonly StrategyLogger              _log;

        private double   _pp, _r1, _r2, _s1, _s2;
        private DateTime _lastPivotDate = DateTime.MinValue;

        /// <summary>Pivot Point. 0.0 until first PrevDay data.</summary>
        public double PP => _pp;
        /// <summary>Resistance 1.</summary>
        public double R1 => _r1;
        /// <summary>Resistance 2.</summary>
        public double R2 => _r2;
        /// <summary>Support 1.</summary>
        public double S1 => _s1;
        /// <summary>Support 2.</summary>
        public double S2 => _s2;

        public PivotLevelSource(SupportResistanceCoreConfig config, StrategyLogger log)
        {
            _config = config;
            _log    = log;
        }

        // ── Pivot levels persist across session boundaries — no OnSessionOpen needed

        public void Update(ref MarketSnapshot snapshot)
        {
            double pdh = snapshot.PrevDayHigh;
            double pdl = snapshot.PrevDayLow;
            double pdc = snapshot.PrevDayClose;

            // Degrade gracefully — same as HTFLevelEngine
            if (pdh <= 0 || pdl <= 0 || pdc <= 0) return;

            // Day-change detection via calendar Date — more robust than PDH delta.
            // HTFLevelEngine used: Math.Abs(pdh - _pivotPrevDayHigh) < tickSize
            // Edge case that fails: two consecutive days with the same PDH (e.g. flat
            // overnight session) would not trigger recalculation in the old path.
            DateTime today = snapshot.Primary.Time.Date;
            if (today == _lastPivotDate) return;

            _lastPivotDate = today;

            // Standard Floor Trader Pivot formulas — verbatim from HTFLevelEngine
            _pp = (pdh + pdl + pdc) / 3.0;
            _r1 = (2.0 * _pp) - pdl;
            _r2 = _pp + (pdh - pdl);
            _s1 = (2.0 * _pp) - pdh;
            _s2 = _pp - (pdh - pdl);

            _log?.Warn(snapshot.Primary.Time,
                "SR Pivots: PP={0:F2} R1={1:F2} R2={2:F2} S1={3:F2} S2={4:F2}",
                _pp, _r1, _r2, _s1, _s2);
        }

        public void EmitFacts(SRLevelFact[] buf, ref int count)
        {
            if (_pp <= 0.0 || buf == null) return;

            // PP — bidirectional
            if (count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _pp, Source = SRSourceType.Pivot,
                    Weight = _config.WeightPivotPP,
                    IsSupport = true, IsResistance = true, IsValid = true,
                };

            // R1 — resistance
            if (_r1 > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _r1, Source = SRSourceType.Pivot,
                    Weight = _config.WeightPivotR1S1,
                    IsSupport = false, IsResistance = true, IsValid = true,
                };

            // R2 — resistance
            if (_r2 > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _r2, Source = SRSourceType.Pivot,
                    Weight = _config.WeightPivotR2S2,
                    IsSupport = false, IsResistance = true, IsValid = true,
                };

            // S1 — support
            if (_s1 > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _s1, Source = SRSourceType.Pivot,
                    Weight = _config.WeightPivotR1S1,
                    IsSupport = true, IsResistance = false, IsValid = true,
                };

            // S2 — support
            if (_s2 > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _s2, Source = SRSourceType.Pivot,
                    Weight = _config.WeightPivotR2S2,
                    IsSupport = true, IsResistance = false, IsValid = true,
                };
        }
    }

    // ========================================================================
    // SESSION LEVEL SOURCE
    // ========================================================================
    //
    // IMPLEMENTATION CHECKLIST
    //
    // A. Role
    //   [x] Emits structural session reference levels as SRLevelFacts
    //   [x] Sources: PDH/PDL, London H/L, NY H/L, ORB H/L
    //   [x] OnSessionOpen() resets intraday levels (ORB); PDH/PDL persist
    //   [x] All facts guarded: only emitted when price > 0
    //
    // B. Level sources (all from MarketSnapshot — no NT8 API calls)
    //   [x] PDH  = snapshot.PrevDayHigh   → resistance
    //   [x] PDL  = snapshot.PrevDayLow    → support
    //   [x] London High = snapshot.London.High  → resistance (when IsValid)
    //   [x] London Low  = snapshot.London.Low   → support   (when IsValid)
    //   [x] NY High     = snapshot.NewYork.High → resistance (when IsValid)
    //   [x] NY Low      = snapshot.NewYork.Low  → support   (when IsValid)
    //   [x] ORB High    = snapshot.ORBHigh      → resistance (when ORBComplete)
    //   [x] ORB Low     = snapshot.ORBLow       → support   (when ORBComplete)
    //
    // C. OnSessionOpen behavior
    //   ORB levels are intraday — cleared on session open.
    //   PDH/PDL and session highs/lows (London, NY) are persistent —
    //   they remain valid until the next day's data overwrites them.
    //   No full reset: only _orbComplete flag is cleared.
    //
    // D. EmitFacts — up to 8 facts, per-fact buffer guard
    //   PDH → resistance,  PDL → support
    //   London H → resistance, London L → support
    //   NY H → resistance,  NY L → support
    //   ORB H → resistance, ORB L → support (only when ORBComplete)
    // ========================================================================

    internal sealed class SessionLevelSource
    {
        private readonly SupportResistanceCoreConfig _config;
        private readonly StrategyLogger              _log;

        // Cached values — updated each bar from snapshot
        private double _pdh, _pdl;
        private double _londonHigh, _londonLow;
        private double _nyHigh,     _nyLow;
        private double _orbHigh,    _orbLow;
        private bool   _orbComplete;

        public SessionLevelSource(SupportResistanceCoreConfig config, StrategyLogger log)
        {
            _config = config;
            _log    = log;
        }

        /// <summary>
        /// Reset intraday ORB levels at session open.
        /// PDH/PDL and session highs/lows persist across session boundaries.
        /// </summary>
        public void OnSessionOpen()
        {
            _orbHigh     = 0.0;
            _orbLow      = 0.0;
            _orbComplete = false;
        }

        public void Update(ref MarketSnapshot snapshot)
        {
            // PDH / PDL — from daily level tracking
            _pdh = snapshot.PrevDayHigh;
            _pdl = snapshot.PrevDayLow;

            // London session high / low — assigned unconditionally every bar.
            // When IsValid is false (not yet seen this instance), zero is stored,
            // which suppresses emission in EmitFacts. This prevents stale values
            // from a prior day's session persisting into a new day.
            _londonHigh = snapshot.London.IsValid ? snapshot.London.High : 0.0;
            _londonLow  = snapshot.London.IsValid ? snapshot.London.Low  : 0.0;

            // NY session high / low — same unconditional pattern
            _nyHigh = snapshot.NewYork.IsValid ? snapshot.NewYork.High : 0.0;
            _nyLow  = snapshot.NewYork.IsValid ? snapshot.NewYork.Low  : 0.0;

            // ORB — only use when complete; avoid premature breakout levels
            if (snapshot.ORBComplete)
            {
                _orbComplete = true;
                _orbHigh     = snapshot.ORBHigh;
                _orbLow      = snapshot.ORBLow;
            }
        }

        public void EmitFacts(SRLevelFact[] buf, ref int count)
        {
            if (buf == null) return;

            // PDH — resistance
            if (_pdh > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _pdh, Source = SRSourceType.Session,
                    Weight = _config.WeightPDH_PDL,
                    IsSupport = false, IsResistance = true, IsValid = true,
                };

            // PDL — support
            if (_pdl > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _pdl, Source = SRSourceType.Session,
                    Weight = _config.WeightPDH_PDL,
                    IsSupport = true, IsResistance = false, IsValid = true,
                };

            // London High — resistance
            if (_londonHigh > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _londonHigh, Source = SRSourceType.Session,
                    Weight = _config.WeightLondon,
                    IsSupport = false, IsResistance = true, IsValid = true,
                };

            // London Low — support
            if (_londonLow > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _londonLow, Source = SRSourceType.Session,
                    Weight = _config.WeightLondon,
                    IsSupport = true, IsResistance = false, IsValid = true,
                };

            // NY High — resistance
            if (_nyHigh > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _nyHigh, Source = SRSourceType.Session,
                    Weight = _config.WeightNY,
                    IsSupport = false, IsResistance = true, IsValid = true,
                };

            // NY Low — support
            if (_nyLow > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _nyLow, Source = SRSourceType.Session,
                    Weight = _config.WeightNY,
                    IsSupport = true, IsResistance = false, IsValid = true,
                };

            // ORB High — resistance (only when range is complete)
            if (_orbComplete && _orbHigh > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _orbHigh, Source = SRSourceType.Session,
                    Weight = _config.WeightORB,
                    IsSupport = false, IsResistance = true, IsValid = true,
                };

            // ORB Low — support (only when range is complete)
            if (_orbComplete && _orbLow > 0.0 && count < buf.Length)
                buf[count++] = new SRLevelFact
                {
                    Price = _orbLow, Source = SRSourceType.Session,
                    Weight = _config.WeightORB,
                    IsSupport = true, IsResistance = false, IsValid = true,
                };
        }
    }

    // ========================================================================
    // ROUND NUMBER LEVEL SOURCE
    // ========================================================================
    //
    // IMPLEMENTATION CHECKLIST
    //
    // A. Role
    //   [x] Generates round-number reference levels near current price each bar
    //   [x] No state — fully recomputed from current price and RoundNumberInterval
    //   [x] No OnSessionOpen() needed — stateless
    //   [x] EmitFacts() writes up to 11 levels centred on current price (HALF_RANGE=5, i=-5..+5)
    //
    // B. Generation rule
    //   Nearest round number below price:
    //     base = Math.Floor(price / interval) * interval
    //   Generate N levels below and N levels above:
    //     for i in [-HALF_RANGE .. +HALF_RANGE]: base + i * interval
    //   HALF_RANGE = 5 → up to 11 levels total (5 below, at, 5 above)
    //   Only levels > 0 are emitted.
    //
    // C. Role assignment
    //   Levels BELOW current price → support (IsSupport = true)
    //   Levels ABOVE current price → resistance (IsResistance = true)
    //   Level exactly at current price → both (IsSupport = true, IsResistance = true)
    //   Tolerance: within 1 tick = "at price" → both
    //
    // D. Why stateless is safe
    //   Round numbers do not change between bars — only their relationship to price
    //   changes as price moves. Recomputing each bar from scratch is cheaper than
    //   maintaining a cache that must be invalidated on every price move.
    //
    // E. EmitFacts — up to 11 facts, per-fact buffer guard
    //   Weight = config.WeightRoundNumber (lowest weight tier)
    // ========================================================================

    internal sealed class RoundNumberLevelSource
    {
        private readonly SupportResistanceCoreConfig _config;

        private const int HALF_RANGE = 5;   // levels on each side of price

        public RoundNumberLevelSource(SupportResistanceCoreConfig config, StrategyLogger log)
        {
            _config = config;
        }

        // Stateless — no OnSessionOpen needed
        // No Update() state to maintain — EmitFacts computes directly from snapshot

        public void Update(ref MarketSnapshot snapshot)
        {
            // No state to update — EmitFacts reads price directly
        }

        public void EmitFacts(SRLevelFact[] buf, ref int count)
        {
            // This parameterless overload exists only to satisfy the EmitFacts
            // convention used by all other source modules. It must not be called
            // directly — the engine always calls the price-aware overload below.
            // Round numbers require current price, which is not available here.
        }

        /// <summary>
        /// Price-aware overload called by SupportResistanceEngine.
        /// Generates round number levels centred on <paramref name="price"/>.
        /// </summary>
        public void EmitFacts(SRLevelFact[] buf, ref int count, double price, double tickSize)
        {
            if (buf == null) return;
            double interval = _config.RoundNumberInterval;
            if (interval <= 0.0 || price <= 0.0 || tickSize <= 0.0) return;

            // Nearest round number at or below current price
            double baseLevel = Math.Floor(price / interval) * interval;

            for (int i = -HALF_RANGE; i <= HALF_RANGE; i++)
            {
                if (count >= buf.Length) break;

                double level = baseLevel + i * interval;
                if (level <= 0.0) continue;

                // Classify by position relative to price
                double diff = level - price;
                bool isAt   = Math.Abs(diff) <= tickSize;
                bool isSup  = isAt || diff < 0.0;  // at or below
                bool isRes  = isAt || diff > 0.0;  // at or above

                buf[count++] = new SRLevelFact
                {
                    Price        = level,
                    Source       = SRSourceType.RoundNumber,
                    Weight       = _config.WeightRoundNumber,
                    IsSupport    = isSup,
                    IsResistance = isRes,
                    IsValid      = true,
                };
            }
        }
    }
}
