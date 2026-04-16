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
    /// Phase 3.1 — Allocation-free tick ring buffer (~30s window).
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

        public TapeRecorder(int capacity = DEFAULT_CAPACITY, long windowMs = DEFAULT_WINDOW_MS)
        {
            if (capacity <= 0)   throw new ArgumentOutOfRangeException("capacity");
            if (windowMs  <= 0)  throw new ArgumentOutOfRangeException("windowMs");
            _capacity = capacity;
            _windowMs = windowMs;
            _ring     = new Tick[capacity];
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
        }

        public void OnTick(DateTime timeUtc, double price, long volume, double bid, double ask)
        {
            if (!_armed) return;

            long timeMs = (long)(timeUtc - _sessionOpenTime).TotalMilliseconds;

            Aggressor side;
            if      (price >= ask) side = Aggressor.Buy;
            else if (price <= bid) side = Aggressor.Sell;
            else                   side = Aggressor.Unknown;

            long seq = ++_lastSeqNo;
            _ring[_head] = new Tick(seq, timeMs, price, volume, bid, ask, side);
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
