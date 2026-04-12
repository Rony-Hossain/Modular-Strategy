#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Tracks Fair Value Gap zones per direction. Mirrors ImbalanceZoneRegistry.
    /// Detection: 3-bar gap pattern. Expiry: close beyond the gap.
    /// Publishes most recent unfilled zone to snapshot bus.
    /// </summary>
    public sealed class FvgZoneRegistry
    {
        private const int MAX_ZONES = 4;
        private const double MIN_GAP_ATR = 0.15; // minimum gap size as fraction of ATR

        private readonly FvgZone[] _bullZones = new FvgZone[MAX_ZONES];
        private readonly FvgZone[] _bearZones = new FvgZone[MAX_ZONES];
        private int _bullWriteIdx = 0;
        private int _bearWriteIdx = 0;
        private int _debugUpdateCount = 0;

        private struct FvgZone
        {
            public double Low;
            public double High;
            public int CreatedBar;
            public bool IsActive;
        }

        /// <summary>
        /// Call every bar from HostStrategy.OnBarUpdate, after snapshot is built.
        /// Detects new FVGs, expires filled ones, tracks state.
        /// </summary>
        public void Update(BarSnapshot price, double atr, int currentBar)
        {
            if (price.Highs == null || price.Highs.Length < 3) return;
            if (price.Lows == null || price.Lows.Length < 3) return;
            if (atr <= 0) return;

            double close = price.Close;
            double tickSize = price.TickSize;
            double minGap = atr * MIN_GAP_ATR;

            // Temporary diagnostic — first 20 bars with valid data
            if (_debugUpdateCount < 20)
            {
                _debugUpdateCount++;
                double bullGapSize = price.Lows[0] - price.Highs[2];
                double bearGapSize = price.Lows[2] - price.Highs[0];
                int activeBulls = 0, activeBears = 0;
                for (int i = 0; i < MAX_ZONES; i++)
                {
                    if (_bullZones[i].IsActive) activeBulls++;
                    if (_bearZones[i].IsActive) activeBears++;
                }
                NinjaTrader.Code.Output.Process(
                    string.Format(
                        "[FVG_DBG #{0}] bar={1} atr={2:F2} minGap={3:F2} | " +
                        "h0={4:F2} l0={5:F2} h2={6:F2} l2={7:F2} | " +
                        "bullGap={8:F2} bearGap={9:F2} | " +
                        "activeBulls={10} activeBears={11} | " +
                        "writeIdxB={12} writeIdxS={13}",
                        _debugUpdateCount, currentBar, atr, minGap,
                        price.Highs[0], price.Lows[0], price.Highs[2], price.Lows[2],
                        bullGapSize, bearGapSize,
                        activeBulls, activeBears,
                        _bullWriteIdx, _bearWriteIdx),
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
            }

            // Expire filled zones (close beyond the gap)
            ExpireZones(_bullZones, close, isBull: true);
            ExpireZones(_bearZones, close, isBull: false);

            // Detect new bullish FVG: bar[2].High < bar[0].Low (gap up)
            // Index 0 = current bar, 2 = two bars ago
            double gapLow = price.Highs[2];  // top of bar 2 ago
            double gapHigh = price.Lows[0];   // bottom of current bar
            if (gapHigh > gapLow && (gapHigh - gapLow) >= minGap)
            {
                _bullZones[_bullWriteIdx % MAX_ZONES] = new FvgZone
                {
                    Low = gapLow, High = gapHigh,
                    CreatedBar = currentBar, IsActive = true
                };
                _bullWriteIdx++;
            }

            // Detect new bearish FVG: bar[2].Low > bar[0].High (gap down)
            gapLow = price.Highs[0];   // top of current bar
            gapHigh = price.Lows[2];    // bottom of bar 2 ago
            if (gapHigh > gapLow && (gapHigh - gapLow) >= minGap)
            {
                _bearZones[_bearWriteIdx % MAX_ZONES] = new FvgZone
                {
                    Low = gapLow, High = gapHigh,
                    CreatedBar = currentBar, IsActive = true
                };
                _bearWriteIdx++;
            }
        }

        /// <summary>
        /// Publish the most recent unfilled zone per direction to snapshot.
        /// Call from OnPopulateIndicatorBag (ref snapshot context).
        /// </summary>
        public void PublishToSnap(ref MarketSnapshot snapshot)
        {
            // Bull: find most recent active zone
            bool foundBull = false;
            for (int i = _bullWriteIdx - 1; i >= 0 && i >= _bullWriteIdx - MAX_ZONES; i--)
            {
                ref FvgZone z = ref _bullZones[i % MAX_ZONES];
                if (z.IsActive)
                {
                    snapshot.Set(SnapKeys.FvgBullActive, 1.0);
                    snapshot.Set(SnapKeys.FvgBullLow, z.Low);
                    snapshot.Set(SnapKeys.FvgBullHigh, z.High);
                    foundBull = true;
                    break;
                }
            }
            if (!foundBull)
            {
                snapshot.Set(SnapKeys.FvgBullActive, 0.0);
                snapshot.Set(SnapKeys.FvgBullLow, 0.0);
                snapshot.Set(SnapKeys.FvgBullHigh, 0.0);
            }

            // Bear: find most recent active zone
            bool foundBear = false;
            for (int i = _bearWriteIdx - 1; i >= 0 && i >= _bearWriteIdx - MAX_ZONES; i--)
            {
                ref FvgZone z = ref _bearZones[i % MAX_ZONES];
                if (z.IsActive)
                {
                    snapshot.Set(SnapKeys.FvgBearActive, 1.0);
                    snapshot.Set(SnapKeys.FvgBearLow, z.Low);
                    snapshot.Set(SnapKeys.FvgBearHigh, z.High);
                    foundBear = true;
                    break;
                }
            }
            if (!foundBear)
            {
                snapshot.Set(SnapKeys.FvgBearActive, 0.0);
                snapshot.Set(SnapKeys.FvgBearLow, 0.0);
                snapshot.Set(SnapKeys.FvgBearHigh, 0.0);
            }
        }

        /// <summary>Reset all zones (call on session open).</summary>
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

        private static void ExpireZones(FvgZone[] zones, double close, bool isBull)
        {
            for (int i = 0; i < zones.Length; i++)
            {
                if (!zones[i].IsActive) continue;
                // Bull FVG filled when close drops below zone low
                // Bear FVG filled when close rises above zone high
                if (isBull && close < zones[i].Low)
                    zones[i].IsActive = false;
                else if (!isBull && close > zones[i].High)
                    zones[i].IsActive = false;
            }
        }
    }
}
