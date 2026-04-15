using System;

namespace NinjaTrader.NinjaScript.Strategies
{
    public readonly struct LevelStat
    {
        public double Price    { get; }
        public double AskVol   { get; }
        public double BidVol   { get; }
        public double TotalVol { get; }  // Ask + Bid
        public double Delta    { get; }  // Ask - Bid

        public LevelStat(double price, double askVol, double bidVol)
        {
            Price    = price;
            AskVol   = askVol;
            BidVol   = bidVol;
            TotalVol = askVol + bidVol;
            Delta    = askVol - bidVol;
        }
    }

    public sealed class LevelHistoryTracker
    {
        private readonly int       _capacityBars;
        private readonly int       _maxLevelsPerBar;
        private readonly DateTime[] _barEndTimes;
        private readonly double[]   _barTickSizes;
        private readonly int[]      _barLevelCounts;
        private readonly LevelStat[,] _levels;

        private int _head = -1; // Index of the most recently CLOSED bar
        private int _count = 0; // Number of closed bars
        private int _writeIdx = 0; // Index of the bar currently being written

        public LevelHistoryTracker(int capacityBars, int maxLevelsPerBar)
        {
            if (capacityBars <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacityBars), "capacityBars must be > 0.");
            if (maxLevelsPerBar <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxLevelsPerBar), "maxLevelsPerBar must be > 0.");

            _capacityBars    = capacityBars;
            _maxLevelsPerBar = maxLevelsPerBar;

            _barEndTimes    = new DateTime[capacityBars];
            _barTickSizes   = new double[capacityBars];
            _barLevelCounts = new int[capacityBars];
            _levels         = new LevelStat[capacityBars, maxLevelsPerBar];
        }

        public int Count    => _count;
        public int Capacity => _capacityBars;

        /// Start ingesting a new primary bar. Overwrites the oldest slot in the ring.
        public void BeginBar(DateTime primaryBarEndTime, double tickSize)
        {
            // Determine where to write. If first bar, write to 0.
            // Otherwise write to (_head + 1) % _capacityBars.
            _writeIdx = (_head + 1) % _capacityBars;

            _barEndTimes[_writeIdx]    = primaryBarEndTime;
            _barTickSizes[_writeIdx]   = tickSize;
            _barLevelCounts[_writeIdx] = 0;
        }

        /// Append one price level for the current bar in-progress.
        public void AppendLevel(double price, double askVol, double bidVol)
        {
            int currentCount = _barLevelCounts[_writeIdx];
            if (currentCount >= _maxLevelsPerBar)
                return;

            _levels[_writeIdx, currentCount] = new LevelStat(price, askVol, bidVol);
            _barLevelCounts[_writeIdx] = currentCount + 1;
        }

        /// Close the current bar.
        public void EndBar()
        {
            _head = _writeIdx;
            if (_count < _capacityBars)
                _count++;
        }

        /// Look up a price across the last `lookbackBars` CLOSED bars.
        /// Matches within +/- 0.5 * storedTickSize.
        public int QueryPrice(double price, int lookbackBars, LevelStat[] outStats)
        {
            if (_count == 0 || outStats == null) return 0;

            int actualLookback = Math.Max(1, Math.Min(lookbackBars, _count));
            int written = 0;

            for (int i = 0; i < actualLookback; i++)
            {
                if (written >= outStats.Length)
                    break;

                int barIdx = (_head - i + _capacityBars) % _capacityBars;
                double tickSize = _barTickSizes[barIdx];
                double halfTick = 0.5 * tickSize;
                int levelCount = _barLevelCounts[barIdx];

                for (int j = 0; j < levelCount; j++)
                {
                    double levelPrice = _levels[barIdx, j].Price;
                    if (Math.Abs(levelPrice - price) <= halfTick + 1e-9) // Added small epsilon for floating point
                    {
                        outStats[written++] = _levels[barIdx, j];
                        break; // Only one match per price per bar expected
                    }
                }
            }

            return written;
        }
    }
}
