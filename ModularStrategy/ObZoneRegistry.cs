#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Tracks Order Block zones per direction.
    /// Detection: triggered when BOS fires. Looks back up to LOOKBACK bars
    ///   for the most recent opposing candle (bearish before bull BOS, 
    ///   bullish before bear BOS). Stores its high/low as the zone.
    /// Invalidation: close beyond the zone (mirror of FVG rule).
    /// Publishes most recent unfilled zone to snapshot bus.
    /// </summary>
    public sealed class ObZoneRegistry
    {
        private const int MAX_ZONES = 4;
        private const int LOOKBACK  = 10;

        private readonly ObZone[] _bullZones = new ObZone[MAX_ZONES];
        private readonly ObZone[] _bearZones = new ObZone[MAX_ZONES];
        private int _bullWriteIdx = 0;
        private int _bearWriteIdx = 0;

        private struct ObZone
        {
            public double Low;
            public double High;
            public int CreatedBar;
            public bool IsActive;
        }

        /// <summary>
        /// Call every bar from HostStrategy.OnBarUpdate, after snapshot is 
        /// built AND after StructuralLabeler has run (so BOS flags are set).
        /// </summary>
        public void Update(ref MarketSnapshot snapshot, BarSnapshot price, int currentBar)
        {
            if (price.Highs == null || price.Highs.Length < LOOKBACK) return;
            if (price.Lows == null || price.Lows.Length < LOOKBACK) return;
            if (price.Opens == null || price.Opens.Length < LOOKBACK) return;
            if (price.Closes == null || price.Closes.Length < LOOKBACK) return;

            double close = price.Close;

            // Expire filled zones
            ExpireZones(_bullZones, close, isBull: true);
            ExpireZones(_bearZones, close, isBull: false);

            // Detect new bull OB on bull BOS: scan back for last bearish candle
            bool bosLong = snapshot.Get(SnapKeys.BOSFiredLong) > 0.5;
            if (bosLong)
            {
                for (int i = 1; i < LOOKBACK; i++)
                {
                    if (price.Closes[i] < price.Opens[i])
                    {
                        _bullZones[_bullWriteIdx % MAX_ZONES] = new ObZone
                        {
                            Low = price.Lows[i], High = price.Highs[i],
                            CreatedBar = currentBar - i, IsActive = true
                        };
                        _bullWriteIdx++;
                        break;
                    }
                }
            }

            // Detect new bear OB on bear BOS: scan back for last bullish candle
            bool bosShort = snapshot.Get(SnapKeys.BOSFiredShort) > 0.5;
            if (bosShort)
            {
                for (int i = 1; i < LOOKBACK; i++)
                {
                    if (price.Closes[i] > price.Opens[i])
                    {
                        _bearZones[_bearWriteIdx % MAX_ZONES] = new ObZone
                        {
                            Low = price.Lows[i], High = price.Highs[i],
                            CreatedBar = currentBar - i, IsActive = true
                        };
                        _bearWriteIdx++;
                        break;
                    }
                }
            }
        }

        public void PublishToSnap(ref MarketSnapshot snapshot)
        {
            bool foundBull = false;
            for (int i = _bullWriteIdx - 1; i >= 0 && i >= _bullWriteIdx - MAX_ZONES; i--)
            {
                ref ObZone z = ref _bullZones[i % MAX_ZONES];
                if (z.IsActive)
                {
                    snapshot.Set(SnapKeys.ObBullActive, 1.0);
                    snapshot.Set(SnapKeys.ObBullLow, z.Low);
                    snapshot.Set(SnapKeys.ObBullHigh, z.High);
                    foundBull = true;
                    break;
                }
            }
            if (!foundBull)
            {
                snapshot.Set(SnapKeys.ObBullActive, 0.0);
                snapshot.Set(SnapKeys.ObBullLow, 0.0);
                snapshot.Set(SnapKeys.ObBullHigh, 0.0);
            }

            bool foundBear = false;
            for (int i = _bearWriteIdx - 1; i >= 0 && i >= _bearWriteIdx - MAX_ZONES; i--)
            {
                ref ObZone z = ref _bearZones[i % MAX_ZONES];
                if (z.IsActive)
                {
                    snapshot.Set(SnapKeys.ObBearActive, 1.0);
                    snapshot.Set(SnapKeys.ObBearLow, z.Low);
                    snapshot.Set(SnapKeys.ObBearHigh, z.High);
                    foundBear = true;
                    break;
                }
            }
            if (!foundBear)
            {
                snapshot.Set(SnapKeys.ObBearActive, 0.0);
                snapshot.Set(SnapKeys.ObBearLow, 0.0);
                snapshot.Set(SnapKeys.ObBearHigh, 0.0);
            }
        }

        public void OnSessionOpen()
        {
            for (int i = 0; i < MAX_ZONES; i++)
            {
                _bullZones[i] = default;
                _bearZones[i] = default;
            }
            _bullWriteIdx = 0;
            _bearWriteIdx = 0;
        }

        private static void ExpireZones(ObZone[] zones, double close, bool isBull)
        {
            for (int i = 0; i < zones.Length; i++)
            {
                if (!zones[i].IsActive) continue;
                if (isBull && close < zones[i].Low)
                    zones[i].IsActive = false;
                else if (!isBull && close > zones[i].High)
                    zones[i].IsActive = false;
            }
        }
    }
}
