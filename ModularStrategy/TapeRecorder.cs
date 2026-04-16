using System;

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum Aggressor : byte { Unknown = 0, Buy = 1, Sell = 2 }

    public readonly struct Tick
    {
        public readonly long      SeqNo;
        public readonly long      TimeMs;
        public readonly double    Price;
        public readonly long      Volume;
        public readonly double    Bid;
        public readonly double    Ask;
        public readonly Aggressor Side;

        public Tick(long seqNo, long timeMs, double price, long volume, double bid, double ask, Aggressor side)
        {
            SeqNo  = seqNo;
            TimeMs = timeMs;
            Price  = price;
            Volume = volume;
            Bid    = bid;
            Ask    = ask;
            Side   = side;
        }
    }

    /// <summary>
    /// Phase 3.1/3.2 — Allocation-free tick ring buffer (~30s window)
    /// with Lee-Ready aggressor classification.
    /// Single-threaded: NT8 delivers OnMarketData on the instrument thread, no locking.
    /// </summary>
    public sealed class TapeRecorder
    {
        private const int  DEFAULT_CAPACITY  = 45000;
        private const long DEFAULT_WINDOW_MS = 30_000;

        private readonly Tick[] _ring;
        private readonly int    _capacity;
        private readonly long   _windowMs;

        private int  _head;          // next write slot
        private int  _oldest;        // index of oldest live tick
        private int  _count;         // live ticks
        private long _lastSeqNo;

        private DateTime _sessionOpenTime;
        private bool     _armed;

        // Phase 3.2 — pre-tick BBO + Lee-Ready state
        private double    _preBid;
        private double    _preAsk;
        private bool      _bboValid;
        private double    _lastTradePrice;
        private Aggressor _lastSide;

        private BigPrintDetector    _bigPrintDetector;
        private VelocityDetector    _velocityDetector;
        private SweepDetector       _sweepDetector;
        private TapeIcebergDetector _tapeIcebergDetector;

        public TapeRecorder(int capacity = DEFAULT_CAPACITY, long windowMs = DEFAULT_WINDOW_MS)
        {
            if (capacity <= 0)   throw new ArgumentOutOfRangeException("capacity");
            if (windowMs  <= 0)  throw new ArgumentOutOfRangeException("windowMs");
            _capacity = capacity;
            _windowMs = windowMs;
            _ring     = new Tick[capacity];
        }

        public void SetBigPrintDetector(BigPrintDetector detector)
        {
            _bigPrintDetector = detector;
        }

        public void SetVelocityDetector(VelocityDetector detector)
        {
            _velocityDetector = detector;
        }

        public void SetSweepDetector(SweepDetector detector)
        {
            _sweepDetector = detector;
        }

        public void SetTapeIcebergDetector(TapeIcebergDetector detector)
        {
            _tapeIcebergDetector = detector;
        }

        public int  Count     { get { return _count; } }
        public long WindowMs  { get { return _windowMs; } }
        public long LatestSeq { get { return _lastSeqNo; } }

        public void OnSessionOpen(DateTime sessionOpenTime)
        {
            _sessionOpenTime = sessionOpenTime;
            _armed           = true;
            _head            = 0;
            _oldest          = 0;
            _count           = 0;
            _lastSeqNo       = 0;
            _preBid          = 0;
            _preAsk          = 0;
            _bboValid        = false;
            _lastTradePrice  = 0;
            _lastSide        = Aggressor.Unknown;
        }

        /// <summary>Call on every Bid/Ask MarketData event to track pre-tick BBO.</summary>
        public void OnBbo(double bid, double ask)
        {
            _preBid   = bid;
            _preAsk   = ask;
            _bboValid = true;
        }

        public void OnTick(DateTime timeUtc, double price, long volume, double bid, double ask)
        {
            if (!_armed) return;

            long timeMs = (long)(timeUtc - _sessionOpenTime).TotalMilliseconds;

            // Lee-Ready classification (Phase 3.2)
            double useBid = _bboValid ? _preBid : bid;
            double useAsk = _bboValid ? _preAsk : ask;

            Aggressor side;
            if      (price >= useAsk) side = Aggressor.Buy;
            else if (price <= useBid) side = Aggressor.Sell;
            else if (price > _lastTradePrice && _lastTradePrice > 0) side = Aggressor.Buy;
            else if (price < _lastTradePrice && _lastTradePrice > 0) side = Aggressor.Sell;
            else if (_lastSide != Aggressor.Unknown)                 side = _lastSide;
            else                                                     side = Aggressor.Unknown;

            _lastTradePrice = price;
            _lastSide       = side;

            bid = useBid;
            ask = useAsk;

            long seq = ++_lastSeqNo;
            Tick tick = new Tick(seq, timeMs, price, volume, bid, ask, side);
            _ring[_head] = tick;
            _head = (_head + 1) % _capacity;

            if (_count < _capacity)
            {
                _count++;
            }
            else
            {
                // Ring full: overwrote oldest. Advance oldest pointer.
                _oldest = (_oldest + 1) % _capacity;
            }

            // Time-based eviction: drop slots older than the window.
            long cutoff = timeMs - _windowMs;
            while (_count > 1 && _ring[_oldest].TimeMs < cutoff)
            {
                _oldest = (_oldest + 1) % _capacity;
                _count--;
            }

            _bigPrintDetector?.OnTick(in tick);
            _velocityDetector?.OnTick(in tick);
            _sweepDetector?.OnTick(in tick);
            _tapeIcebergDetector?.OnTick(in tick);
        }

        /// <summary>0 = oldest, Count-1 = newest.</summary>
        public Tick At(int i)
        {
            if ((uint)i >= (uint)_count) throw new ArgumentOutOfRangeException("i");
            return _ring[(_oldest + i) % _capacity];
        }

        /// <summary>Copies live ticks oldest→newest into dst. Returns number copied.
        /// If dst is smaller than Count, copies only the most recent dst.Length ticks.</summary>
        public int CopyTo(Tick[] dst)
        {
            if (dst == null) return 0;
            int n     = dst.Length < _count ? dst.Length : _count;
            int start = (_oldest + (_count - n)) % _capacity;
            for (int i = 0; i < n; i++)
            {
                dst[i] = _ring[(start + i) % _capacity];
            }
            return n;
        }
    }
}
