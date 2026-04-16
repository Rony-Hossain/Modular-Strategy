using System;

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Phase 3.5 — Sweep Detector
    /// Detects directional aggressors walking >= 3 price levels within 200ms.
    /// Zero-allocation ring buffers for high-performance tape scanning.
    /// </summary>
    public sealed class SweepDetector
    {
        private const long WINDOW_MS    = 200;
        private const int  SWEEP_LEVELS = 3;
        private const int  RING_CAPACITY = 512;

        private readonly double _tickSize;

        private readonly long[]   _buyTime;
        private readonly double[] _buyPrice;
        private int _buyHead, _buyOldest, _buyCount;

        private readonly long[]   _sellTime;
        private readonly double[] _sellPrice;
        private int _sellHead, _sellOldest, _sellCount;

        private bool _armed;

        public bool      BuySweepActive    { get; private set; }
        public bool      SellSweepActive   { get; private set; }
        public int       LastSweepLevels   { get; private set; }
        public Aggressor LastSweepSide     { get; private set; }
        public double    LastSweepMinPrice { get; private set; }
        public double    LastSweepMaxPrice { get; private set; }
        public long      LastSweepTimeMs   { get; private set; }
        public long      LastSweepAgeMs    { get; private set; }

        public SweepDetector(double tickSize)
        {
            _tickSize = tickSize > 0 ? tickSize : 0.25;
            
            _buyTime   = new long[RING_CAPACITY];
            _buyPrice  = new double[RING_CAPACITY];
            
            _sellTime  = new long[RING_CAPACITY];
            _sellPrice = new double[RING_CAPACITY];
            
            LastSweepAgeMs = long.MaxValue;
        }

        public void OnSessionOpen()
        {
            _buyHead = _buyOldest = _buyCount = 0;
            _sellHead = _sellOldest = _sellCount = 0;
            
            BuySweepActive    = false;
            SellSweepActive   = false;
            LastSweepLevels   = 0;
            LastSweepSide     = Aggressor.Unknown;
            LastSweepMinPrice = 0;
            LastSweepMaxPrice = 0;
            LastSweepTimeMs   = 0;
            LastSweepAgeMs    = long.MaxValue;
            _armed            = true;
        }

        public void OnTick(in Tick tick)
        {
            if (!_armed) return;

            if (tick.Side == Aggressor.Buy)
            {
                // 1. Append
                _buyTime[_buyHead]  = tick.TimeMs;
                _buyPrice[_buyHead] = tick.Price;
                _buyHead = (_buyHead + 1) % RING_CAPACITY;
                if (_buyCount < RING_CAPACITY) _buyCount++;
                else _buyOldest = (_buyOldest + 1) % RING_CAPACITY;

                // 2. Evict
                long cutoff = tick.TimeMs - WINDOW_MS;
                while (_buyCount > 0 && _buyTime[_buyOldest] < cutoff)
                {
                    _buyOldest = (_buyOldest + 1) % RING_CAPACITY;
                    _buyCount--;
                }

                // 3. Scan
                if (_buyCount >= 2)
                {
                    double min = double.MaxValue;
                    double max = double.MinValue;
                    for (int k = 0; k < _buyCount; k++)
                    {
                        double p = _buyPrice[(_buyOldest + k) % RING_CAPACITY];
                        if (p < min) min = p;
                        if (p > max) max = p;
                    }

                    int levels = (int)Math.Round((max - min) / _tickSize) + 1;
                    if (levels >= SWEEP_LEVELS)
                    {
                        BuySweepActive = true;
                        UpdateLastSweep(Aggressor.Buy, levels, min, max, tick.TimeMs);
                    }
                    else
                    {
                        BuySweepActive = false;
                    }
                }
                else
                {
                    BuySweepActive = false;
                }
            }
            else if (tick.Side == Aggressor.Sell)
            {
                // 1. Append
                _sellTime[_sellHead]  = tick.TimeMs;
                _sellPrice[_sellHead] = tick.Price;
                _sellHead = (_sellHead + 1) % RING_CAPACITY;
                if (_sellCount < RING_CAPACITY) _sellCount++;
                else _sellOldest = (_sellOldest + 1) % RING_CAPACITY;

                // 2. Evict
                long cutoff = tick.TimeMs - WINDOW_MS;
                while (_sellCount > 0 && _sellTime[_sellOldest] < cutoff)
                {
                    _sellOldest = (_sellOldest + 1) % RING_CAPACITY;
                    _sellCount--;
                }

                // 3. Scan
                if (_sellCount >= 2)
                {
                    double min = double.MaxValue;
                    double max = double.MinValue;
                    for (int k = 0; k < _sellCount; k++)
                    {
                        double p = _sellPrice[(_sellOldest + k) % RING_CAPACITY];
                        if (p < min) min = p;
                        if (p > max) max = p;
                    }

                    int levels = (int)Math.Round((max - min) / _tickSize) + 1;
                    if (levels >= SWEEP_LEVELS)
                    {
                        SellSweepActive = true;
                        UpdateLastSweep(Aggressor.Sell, levels, min, max, tick.TimeMs);
                    }
                    else
                    {
                        SellSweepActive = false;
                    }
                }
                else
                {
                    SellSweepActive = false;
                }
            }
            // else Unknown: ignored

            LastSweepAgeMs = (LastSweepTimeMs > 0) ? (tick.TimeMs - LastSweepTimeMs) : long.MaxValue;
        }

        private void UpdateLastSweep(Aggressor side, int levels, double min, double max, long timeMs)
        {
            LastSweepSide     = side;
            LastSweepLevels   = levels;
            LastSweepMinPrice = min;
            LastSweepMaxPrice = max;
            LastSweepTimeMs   = timeMs;
        }
    }
}
