#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using NinjaTrader.NinjaScript.BarsTypes;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // =========================================================================
    // ImbalanceZoneRegistry — historical footprint imbalance zone tracker
    //
    // CONCEPT (from footprint trading):
    //   When 3+ consecutive price levels in a bar are dominated 3:1 by one side
    //   (bid > ask × 3 for bull, ask > bid × 3 for bear), that cluster of levels
    //   represents institutional activity. After price leaves, these zones often
    //   act as support (bull zones) or resistance (bear zones) when revisited.
    //
    // WHAT THIS CLASS DOES:
    //   - Each bar, scans the current Volumetric bar's footprint for stacked
    //     imbalance zones using GetAskVolumeForPrice / GetBidVolumeForPrice.
    //   - Stores any qualifying zone (3+ consecutive levels, 3:1 ratio) with
    //     its price range and bar index.
    //   - On each subsequent bar, checks if current price is near any stored zone.
    //   - Expires zones after MAX_ZONE_AGE_BARS or when price closes through them
    //     (mitigation — the level has been consumed).
    //   - Publishes SnapKeys.ImbalZoneAtBull and ImbalZoneAtBear to the snapshot bag.
    //
    // CAPACITY:
    //   MAX_BULL_ZONES = 5, MAX_BEAR_ZONES = 5.
    //   Fixed-size arrays — no heap allocation per bar.
    //   When full, oldest zone is overwritten (circular buffer).
    //
    // DESIGN CONTRACT:
    //   - Call Update() once per primary bar from HostStrategy.OnBarUpdate,
    //     AFTER Volumetric data has been injected (SetVolumetricData called).
    //   - Call PublishToSnap() once per bar from OnPopulateIndicatorBag.
    //   - Call OnSessionOpen() at session start to clear intraday zones.
    //     Zones from prior sessions are stale — overnight gaps invalidate them.
    //   - No NT8 API calls except through the passed-in VolumetricBarsType ref.
    // =========================================================================

    public class ImbalanceZoneRegistry
    {
        // ── Configuration ─────────────────────────────────────────────────
        private const int    MAX_BULL_ZONES      = StrategyConfig.Modules.IZ_MAX_BULL_ZONES;
        private const int    MAX_BEAR_ZONES      = StrategyConfig.Modules.IZ_MAX_BEAR_ZONES;
        private const int    MAX_ZONE_AGE_BARS   = StrategyConfig.Modules.IZ_MAX_ZONE_AGE_BARS;  // ~500 min at 5-min bars
        private const double IMBAL_RATIO         = StrategyConfig.Modules.IZ_IMBAL_RATIO;  // 3:1 minimum bid/ask dominance
        private const int    MIN_STACKED_LEVELS  = StrategyConfig.Modules.IZ_MIN_STACKED_LEVELS;    // minimum consecutive levels
        private const double PROXIMITY_TICKS     = StrategyConfig.Modules.IZ_PROXIMITY_TICKS;  // within 4 ticks = "at zone"

        // ── Zone storage — fixed arrays, no allocation ─────────────────────
        private readonly ImbalZone[] _bullZones = new ImbalZone[StrategyConfig.Modules.IZ_MAX_BULL_ZONES];
        private readonly ImbalZone[] _bearZones = new ImbalZone[StrategyConfig.Modules.IZ_MAX_BEAR_ZONES];
        private int _bullWriteIdx = 0;  // circular write pointer
        private int _bearWriteIdx = 0;

        // ── Published state (read by PublishToSnap each bar) ───────────────
        public bool IsPriceAtBullZone { get; private set; }
        public bool IsPriceAtBearZone { get; private set; }

        // ── Logger reference (optional) ───────────────────────────────────
        private readonly StrategyLogger _log;

        public ImbalanceZoneRegistry(StrategyLogger log = null)
        {
            _log = log;
        }

        // ── Session open: reset state without destroying overnight zones ──
        public void OnSessionOpen()
        {
            IsPriceAtBullZone = false;
            IsPriceAtBearZone = false;
        }

        // ── Main update — called once per primary bar ──────────────────────
        /// <summary>
        /// Canonical update path — consumes FootprintCore's assembled result
        /// instead of rescanning raw VolumetricBarsType independently.
        /// </summary>
        public void Update(BarSnapshot primary, in FootprintResult fp)
        {
            IsPriceAtBullZone = false;
            IsPriceAtBearZone = false;

            if (primary.TickSize <= 0.0) return;

            double tickSize   = primary.TickSize;
            double close      = primary.Close;
            int    currentBar = primary.CurrentBar;

            ExpireZones(_bullZones, currentBar, close, tickSize, isBull: true);
            ExpireZones(_bearZones, currentBar, close, tickSize, isBull: false);

            if (fp.IsValid)
            {
                if (fp.HasBullStack)
                {
                    TryAddZone(_bullZones, ref _bullWriteIdx,
                        new ImbalZone(fp.BullStackLow, fp.BullStackHigh, currentBar, isBull: true));
                }

                if (fp.HasBearStack)
                {
                    TryAddZone(_bearZones, ref _bearWriteIdx,
                        new ImbalZone(fp.BearStackLow, fp.BearStackHigh, currentBar, isBull: false));
                }
            }

            CheckProximity(close, tickSize, currentBar);
        }

        // ── Publish to snapshot bag ────────────────────────────────────────
        public void PublishToSnap(ref MarketSnapshot snapshot)
        {
            snapshot.Set(SnapKeys.ImbalZoneAtBull, IsPriceAtBullZone ? 1.0 : 0.0);
            snapshot.Set(SnapKeys.ImbalZoneAtBear, IsPriceAtBearZone ? 1.0 : 0.0);
        }

        // ── Private: expire zones ──────────────────────────────────────────
        private void ExpireZones(
            ImbalZone[] zones, int currentBar, double close, double tickSize, bool isBull)
        {
            for (int i = 0; i < zones.Length; i++)
            {
                if (!zones[i].IsValid) continue;

                // Age expiry
                if (currentBar - zones[i].CreatedBar > MAX_ZONE_AGE_BARS)
                {
                    zones[i] = ImbalZone.Empty;
                    continue;
                }

                // Mitigation: price closes through the zone in the opposite direction
                if (isBull  && close < zones[i].ZoneLow  - tickSize * 2)
                { 
                    _log?.ImbalZoneMitigated(DateTime.MinValue, isBull, zones[i].ZoneLow, zones[i].ZoneHigh, close);
                    zones[i] = ImbalZone.Empty; 
                }
                else if (!isBull && close > zones[i].ZoneHigh + tickSize * 2)
                { 
                    _log?.ImbalZoneMitigated(DateTime.MinValue, isBull, zones[i].ZoneLow, zones[i].ZoneHigh, close);
                    zones[i] = ImbalZone.Empty; 
                }
            }
        }

        // ── Private: add zone with duplicate check ─────────────────────────
        private void TryAddZone(
            ImbalZone[] zones, ref int writeIdx, ImbalZone newZone)
        {
            // Check for near-duplicate (zone within 4 ticks of an existing one)
            for (int i = 0; i < zones.Length; i++)
            {
                if (!zones[i].IsValid) continue;
                double overlap = Math.Min(newZone.ZoneHigh, zones[i].ZoneHigh)
                               - Math.Max(newZone.ZoneLow,  zones[i].ZoneLow);
                double newSize = newZone.ZoneHigh - newZone.ZoneLow;
                // If overlap is more than 50% of the new zone, it's a duplicate
                if (newSize > 0 && overlap / newSize > 0.5) return;
            }

            // Write to circular slot
            zones[writeIdx] = newZone;
            _log?.ImbalZoneCreated(DateTime.MinValue, newZone.IsBull, newZone.ZoneLow, newZone.ZoneHigh, newZone.CreatedBar);
            writeIdx = (writeIdx + 1) % zones.Length;
        }

        // ── Private: check proximity ──────────────────────────────────────
        private void CheckProximity(double close, double tickSize, int currentBar)
        {
            for (int i = 0; i < _bullZones.Length; i++)
            {
                if (!_bullZones[i].IsValid) continue;
                if (MathOrderFlow.ImbalanceZone_IsNearPrice(
                    close, _bullZones[i].ZoneLow, _bullZones[i].ZoneHigh,
                    tickSize, PROXIMITY_TICKS))
                {
                    IsPriceAtBullZone = true;
                    break;
                }
            }
            for (int i = 0; i < _bearZones.Length; i++)
            {
                if (!_bearZones[i].IsValid) continue;
                if (MathOrderFlow.ImbalanceZone_IsNearPrice(
                    close, _bearZones[i].ZoneLow, _bearZones[i].ZoneHigh,
                    tickSize, PROXIMITY_TICKS))
                {
                    IsPriceAtBearZone = true;
                    break;
                }
            }
        }
    }

    // =========================================================================
    // ImbalZone — value type, no allocation
    // =========================================================================

    /// <summary>
    /// A single historical imbalance zone. Stored in fixed arrays — value type
    /// to avoid GC pressure. IsValid = false = empty slot.
    /// </summary>
    internal struct ImbalZone
    {
        public double ZoneLow   { get; }
        public double ZoneHigh  { get; }
        public int    CreatedBar { get; }
        public bool   IsBull    { get; }
        public bool   IsValid   { get; }

        public ImbalZone(double low, double high, int bar, bool isBull)
        {
            ZoneLow    = low;
            ZoneHigh   = high;
            CreatedBar = bar;
            IsBull     = isBull;
            IsValid    = true;
        }

        // Private constructor for the Empty sentinel — IsValid stays false (default).
        // Do NOT call the public constructor for Empty: it unconditionally sets IsValid=true.
        private ImbalZone(bool isValid)
        {
            ZoneLow    = 0.0;
            ZoneHigh   = 0.0;
            CreatedBar = 0;
            IsBull     = false;
            IsValid    = isValid;
        }

        public static readonly ImbalZone Empty = new ImbalZone(isValid: false);
    }
}
