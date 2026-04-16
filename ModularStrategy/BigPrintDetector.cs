using System;

namespace NinjaTrader.NinjaScript.Strategies
{
    public sealed class BigPrintDetector
    {
        private const int    VOL_RING_SIZE       = 2000;  // ticks for p95 calculation
        private const int    RECOMPUTE_INTERVAL  = 200;   // ticks between p95 recalc
        private const double PERCENTILE          = 0.95;
        private const int    BP_RING_SIZE        = 500;   // max BigPrints in rolling window
        private const long   WINDOW_MS           = 30_000; // 30s rolling window for BigPrints

        // Volume distribution tracking
        private readonly long[] _volRing;       // ring of recent trade volumes
        private readonly long[] _sortBuf;       // scratch buffer for partial sort
        private int  _volHead;
        private int  _volCount;
        private int  _ticksSinceRecompute;
        private long _p95Threshold;

        // BigPrint ring (for rolling aggregation)
        private readonly BPEntry[] _bpRing;
        private int _bpHead;
        private int _bpCount;
        private int _bpOldest;

        // Accumulated state
        public long      BigPrintBuyVolume  { get; private set; }
        public long      BigPrintSellVolume { get; private set; }
        public long      BigPrintDelta      { get; private set; }  // Buy - Sell
        public int       BigPrintCount      { get; private set; }
        public Aggressor LastBigPrintSide   { get; private set; }
        public double    LastBigPrintPrice  { get; private set; }
        public long      LastBigPrintAgeMs  { get; private set; }

        public long      P95Threshold       => _p95Threshold;
        public bool      IsWarmedUp         { get; private set; }

        public BigPrintDetector()
        {
            _volRing = new long[VOL_RING_SIZE];
            _sortBuf = new long[VOL_RING_SIZE];
            _bpRing  = new BPEntry[BP_RING_SIZE];
            
            OnSessionOpen();
        }

        public void OnSessionOpen()
        {
            _volHead             = 0;
            _volCount            = 0;
            _ticksSinceRecompute = 0;
            _p95Threshold        = 0;
            IsWarmedUp           = false;

            _bpHead   = 0;
            _bpCount  = 0;
            _bpOldest = 0;

            BigPrintBuyVolume  = 0;
            BigPrintSellVolume = 0;
            BigPrintDelta      = 0;
            BigPrintCount      = 0;
            LastBigPrintSide   = Aggressor.Unknown;
            LastBigPrintPrice  = 0.0;
            LastBigPrintAgeMs  = long.MaxValue;
        }

        public void OnTick(in Tick tick)
        {
            // 1. Update volume ring for p95 calculation
            _volRing[_volHead] = tick.Volume;
            _volHead = (_volHead + 1) % VOL_RING_SIZE;
            if (_volCount < VOL_RING_SIZE)
            {
                _volCount++;
                if (_volCount == VOL_RING_SIZE) IsWarmedUp = true;
            }

            // 2. Periodic p95 recompute
            _ticksSinceRecompute++;
            if (_ticksSinceRecompute >= RECOMPUTE_INTERVAL && IsWarmedUp)
            {
                RecomputeP95();
                _ticksSinceRecompute = 0;
            }

            // 3. Time-based eviction for BigPrint window
            EvictExpiredBigPrints(tick.TimeMs);

            // 4. Detection
            if (IsWarmedUp && _p95Threshold > 0 && tick.Volume >= _p95Threshold)
            {
                AddBigPrint(tick);
            }

            // 5. Update Age
            if (_bpCount > 0)
            {
                int lastIdx = (_bpHead - 1 + BP_RING_SIZE) % BP_RING_SIZE;
                LastBigPrintAgeMs = tick.TimeMs - _bpRing[lastIdx].TimeMs;
            }
            else
            {
                LastBigPrintAgeMs = long.MaxValue;
            }
        }

        private void RecomputeP95()
        {
            Array.Copy(_volRing, _sortBuf, _volCount);
            Array.Sort(_sortBuf, 0, _volCount);
            int idx = (int)(_volCount * PERCENTILE);
            if (idx >= _volCount) idx = _volCount - 1;
            _p95Threshold = _sortBuf[idx];
        }

        private void EvictExpiredBigPrints(long currentTimeMs)
        {
            long cutoff = currentTimeMs - WINDOW_MS;
            while (_bpCount > 0 && _bpRing[_bpOldest].TimeMs < cutoff)
            {
                BPEntry evicted = _bpRing[_bpOldest];
                
                if      (evicted.Side == Aggressor.Buy)  BigPrintBuyVolume  -= evicted.Volume;
                else if (evicted.Side == Aggressor.Sell) BigPrintSellVolume -= evicted.Volume;
                
                BigPrintCount--;
                BigPrintDelta = BigPrintBuyVolume - BigPrintSellVolume;
                
                _bpOldest = (_bpOldest + 1) % BP_RING_SIZE;
                _bpCount--;
            }
        }

        private void AddBigPrint(in Tick tick)
        {
            // Overwrite oldest if ring is full (unlikely with BP_RING_SIZE=500)
            if (_bpCount == BP_RING_SIZE)
            {
                BPEntry evicted = _bpRing[_bpOldest];
                if      (evicted.Side == Aggressor.Buy)  BigPrintBuyVolume  -= evicted.Volume;
                else if (evicted.Side == Aggressor.Sell) BigPrintSellVolume -= evicted.Volume;
                
                _bpOldest = (_bpOldest + 1) % BP_RING_SIZE;
                _bpCount--;
            }

            _bpRing[_bpHead] = new BPEntry
            {
                TimeMs = tick.TimeMs,
                Volume = tick.Volume,
                Side   = tick.Side
            };

            if      (tick.Side == Aggressor.Buy)  BigPrintBuyVolume  += tick.Volume;
            else if (tick.Side == Aggressor.Sell) BigPrintSellVolume += tick.Volume;

            BigPrintCount++;
            BigPrintDelta     = BigPrintBuyVolume - BigPrintSellVolume;
            LastBigPrintSide  = tick.Side;
            LastBigPrintPrice = tick.Price;

            _bpHead = (_bpHead + 1) % BP_RING_SIZE;
            _bpCount++;
        }
    }

    internal struct BPEntry
    {
        public long      TimeMs;
        public long      Volume;
        public Aggressor Side;
    }
}
