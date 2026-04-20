using System;

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Phase 3.4 — Velocity Detector (EMA + Spike)
    /// Detects sudden bursts of tape activity (1s volume > 3x EMA).
    /// Zero-allocation incremental maintenance.
    /// </summary>
    public sealed class VelocityDetector
    {
        private const long   WINDOW_MS         = StrategyConfig.Modules.VD_WINDOW_MS;   // rolling velocity window
        private const long   SAMPLE_INTERVAL_MS = StrategyConfig.Modules.VD_SAMPLE_INTERVAL_MS;   // EMA sample rate
        private const double EMA_ALPHA         = StrategyConfig.Modules.VD_EMA_ALPHA;   // smoothing (~2s effective window)
        private const double SPIKE_MULTIPLIER  = StrategyConfig.Modules.VD_SPIKE_MULTIPLIER;
        private const double EMA_MIN           = StrategyConfig.Modules.VD_EMA_MIN;
        private const int    RING_CAPACITY     = StrategyConfig.Modules.VD_RING_CAPACITY;   // max ticks in 1s window (headroom)

        // Rolling window of (timeMs, volume, side) for the last WINDOW_MS
        private readonly long[]      _timeRing;
        private readonly long[]      _volRing;
        private readonly Aggressor[] _sideRing;
        private int  _head;
        private int  _oldest;
        private int  _count;

        // Running sums (incremental maintenance, no rescan)
        private long _currentBuyVol;
        private long _currentSellVol;

        // EMA state
        private double _emaBuyVel;
        private double _emaSellVel;
        private long   _lastSampleTimeMs;
        private bool   _armed;

        // Outputs (read by Phase 3.7 scoring)
        public long   CurrentBuyVel   { get { return _currentBuyVol;  } }
        public long   CurrentSellVel  { get { return _currentSellVol; } }
        public double EmaBuyVel       { get { return _emaBuyVel;      } }
        public double EmaSellVel      { get { return _emaSellVel;     } }
        public bool   BuySpike        { get; private set; }
        public bool   SellSpike       { get; private set; }
        public long   LastSpikeTimeMs { get; private set; }    // most recent of either side
        public long   LastSpikeAgeMs  { get; private set; }    // relative to last OnTick

        public VelocityDetector()
        {
            _timeRing = new long[StrategyConfig.Modules.VD_RING_CAPACITY];
            _volRing  = new long[StrategyConfig.Modules.VD_RING_CAPACITY];
            _sideRing = new Aggressor[StrategyConfig.Modules.VD_RING_CAPACITY];
            LastSpikeAgeMs = long.MaxValue;
        }

        public void OnSessionOpen()
        {
            _head             = 0;
            _oldest           = 0;
            _count            = 0;
            _currentBuyVol    = 0;
            _currentSellVol   = 0;
            _emaBuyVel        = 0;
            _emaSellVel       = 0;
            _lastSampleTimeMs = 0;
            _armed            = true;
            BuySpike          = false;
            SellSpike         = false;
            LastSpikeTimeMs   = 0;
            LastSpikeAgeMs    = long.MaxValue;
        }

        public void OnTick(in Tick tick)
        {
            if (!_armed) return;

            // 1. Append to ring
            _timeRing[_head] = tick.TimeMs;
            _volRing[_head]  = tick.Volume;
            _sideRing[_head] = tick.Side;

            if (tick.Side == Aggressor.Buy)       _currentBuyVol  += tick.Volume;
            else if (tick.Side == Aggressor.Sell) _currentSellVol += tick.Volume;

            _head = (_head + 1) % RING_CAPACITY;
            if (_count < RING_CAPACITY)
            {
                _count++;
            }
            else
            {
                // Overflow (should not happen with 2000 slots). 
                // Evict oldest manually to keep sums accurate.
                EvictIndex(_oldest);
                _oldest = (_oldest + 1) % RING_CAPACITY;
            }

            // 2. Evict expired entries
            long cutoff = tick.TimeMs - WINDOW_MS;
            while (_count > 0 && _timeRing[_oldest] < cutoff)
            {
                EvictIndex(_oldest);
                _oldest = (_oldest + 1) % RING_CAPACITY;
                _count--;
            }

            // 3. EMA sample (if interval elapsed)
            // First tick of session forces sample because tick.TimeMs >= 0 and _lastSampleTimeMs=0
            if (tick.TimeMs - _lastSampleTimeMs >= SAMPLE_INTERVAL_MS)
            {
                _emaBuyVel  = (EMA_ALPHA * _currentBuyVol)  + ((1.0 - EMA_ALPHA) * _emaBuyVel);
                _emaSellVel = (EMA_ALPHA * _currentSellVol) + ((1.0 - EMA_ALPHA) * _emaSellVel);
                _lastSampleTimeMs = tick.TimeMs;
            }

            // 4. Spike detection (every tick)
            BuySpike  = (_currentBuyVol  > SPIKE_MULTIPLIER * _emaBuyVel)  && (_emaBuyVel  > EMA_MIN);
            SellSpike = (_currentSellVol > SPIKE_MULTIPLIER * _emaSellVel) && (_emaSellVel > EMA_MIN);

            if (BuySpike || SellSpike)
            {
                LastSpikeTimeMs = tick.TimeMs;
            }

            LastSpikeAgeMs = (LastSpikeTimeMs > 0) ? (tick.TimeMs - LastSpikeTimeMs) : long.MaxValue;
        }

        private void EvictIndex(int index)
        {
            if (_sideRing[index] == Aggressor.Buy)       _currentBuyVol  -= _volRing[index];
            else if (_sideRing[index] == Aggressor.Sell) _currentSellVol -= _volRing[index];
        }
    }
}
