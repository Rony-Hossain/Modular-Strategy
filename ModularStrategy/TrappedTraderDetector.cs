using System;

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Phase 2.6 — Trapped Traders Detector
    /// Detects high-volume clusters at extremes that fail to follow through and are rejected.
    /// Emits on a 2-bar delay (evaluates bar B using bar B+1 and current bar C).
    /// </summary>
    public sealed class TrappedTraderDetector
    {
        private struct BarSnap
        {
            public double High;
            public double Low;
            public double Open;
            public double Close;
            public double TopLevelTotalVol;
            public double BottomLevelTotalVol;
            public int    LevelCount;
            public double TotalBuyVol;
            public double TotalSellVol;
            public double TickSize;
        }

        private readonly double _clusterRatio;
        private readonly double _rejectionFrac;

        private BarSnap _barMinus1;
        private BarSnap _barMinus2;
        private int     _barsSeen = 0;

        public TrappedTraderDetector(double clusterRatio = 2.0, double rejectionFrac = 0.5)
        {
            _clusterRatio  = clusterRatio;
            _rejectionFrac = rejectionFrac;
        }

        public int BarsSeen => _barsSeen;

        /// <summary>
        /// Single call per bar: emits flags based on prior 2-bar state, then advances
        /// the internal ring (snapshot of current bar) for the next call.
        /// </summary>
        public void Evaluate(
            in FootprintResult current,
            out bool   trappedLongs,
            out bool   trappedShorts,
            out double trapLevel)
        {
            trappedLongs  = false;
            trappedShorts = false;
            trapLevel     = 0.0;

            // Warmup: need at least 2 prior bars to evaluate the one from 2 bars ago (bar B).
            if (_barsSeen >= 2)
            {
                // barB  = _barMinus2
                // barB1 = _barMinus1
                // C     = current

                double avgB = _barMinus2.LevelCount > 0 
                    ? (_barMinus2.TotalBuyVol + _barMinus2.TotalSellVol) / _barMinus2.LevelCount 
                    : 0.0;
                
                double rangeB = _barMinus2.High - _barMinus2.Low;

                if (_barMinus2.LevelCount > 0 && rangeB > 0)
                {
                    // 1. Bull Trap (Longs trapped at High)
                    // - High volume at the top of bar B
                    // - Bar B rejected from its high (closed in bottom half)
                    // - Bar B+1 failed to take out bar B's high
                    bool clusterHi = _barMinus2.TopLevelTotalVol >= _clusterRatio * avgB;
                    bool rejectHi  = (_barMinus2.High - _barMinus2.Close) >= _rejectionFrac * rangeB;
                    bool noFT_Hi   = _barMinus1.High <= _barMinus2.High + 0.5 * _barMinus2.TickSize + 1e-9;

                    if (clusterHi && rejectHi && noFT_Hi)
                    {
                        trappedLongs = true;
                        trapLevel    = _barMinus2.High;
                    }

                    // 2. Bear Trap (Shorts trapped at Low)
                    // - High volume at the bottom of bar B
                    // - Bar B rejected from its low (closed in top half)
                    // - Bar B+1 failed to take out bar B's low
                    bool clusterLo = _barMinus2.BottomLevelTotalVol >= _clusterRatio * avgB;
                    bool rejectLo  = (_barMinus2.Close - _barMinus2.Low) >= _rejectionFrac * rangeB;
                    bool noFT_Lo   = _barMinus1.Low >= _barMinus2.Low - 0.5 * _barMinus2.TickSize - 1e-9;

                    if (clusterLo && rejectLo && noFT_Lo)
                    {
                        trappedShorts = true;
                        // Preference for trappedLongs level if both fire (rare doji-with-dual-clusters)
                        if (!trappedLongs) trapLevel = _barMinus2.Low;
                    }
                }
            }

            // Advance the 2-bar ring buffer
            _barMinus2 = _barMinus1;
            _barMinus1 = new BarSnap
            {
                High                = current.High,
                Low                 = current.Low,
                Open                = current.Open,
                Close               = current.Close,
                TopLevelTotalVol    = current.TopLevelTotalVol,
                BottomLevelTotalVol = current.BottomLevelTotalVol,
                LevelCount          = current.LevelCount,
                TotalBuyVol         = current.TotalBuyVol,
                TotalSellVol        = current.TotalSellVol,
                TickSize            = current.TickSize
            };
            _barsSeen++;
        }
    }
}
