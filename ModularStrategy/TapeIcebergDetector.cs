using System;

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Phase 3.6 — Tape-Level Iceberg Refresh
    /// Detects hidden liquidity (icebergs) by monitoring repeated hits at the same price 
    /// without price breaking through. Runs on the tick stream.
    /// </summary>
    public sealed class TapeIcebergDetector
    {
        private const long WINDOW_MS       = 5_000;   // 5-second refresh window
        private const int  ICEBERG_MIN_HITS = 8;      // same-side prints to fire
        private const int  SLOT_CAPACITY   = 16;      // concurrent levels under observation

        private readonly double _tickSize;
        private readonly LevelSlot[] _slots;

        private bool _armed;

        // Outputs
        public bool   BullIcebergActive  { get; private set; }
        public bool   BearIcebergActive  { get; private set; }
        public double BullIcebergPrice   { get; private set; }
        public double BearIcebergPrice   { get; private set; }
        public long   BullIcebergVolume  { get; private set; }
        public long   BearIcebergVolume  { get; private set; }
        public long   LastIcebergTimeMs  { get; private set; }
        public long   LastIcebergAgeMs   { get; private set; }

        public TapeIcebergDetector(double tickSize)
        {
            _tickSize = tickSize > 0 ? tickSize : 0.25;
            _slots    = new LevelSlot[SLOT_CAPACITY];
            LastIcebergAgeMs = long.MaxValue;
        }

        public void OnSessionOpen()
        {
            for (int i = 0; i < SLOT_CAPACITY; i++)
            {
                _slots[i].InUse  = false;
                _slots[i].Active = false;
            }

            BullIcebergActive = false;
            BearIcebergActive = false;
            BullIcebergPrice  = 0;
            BearIcebergPrice  = 0;
            BullIcebergVolume = 0;
            BearIcebergVolume = 0;
            LastIcebergTimeMs = 0;
            LastIcebergAgeMs  = long.MaxValue;
            _armed            = true;
        }

        public void OnTick(in Tick tick)
        {
            if (!_armed) return;
            if (tick.Side == Aggressor.Unknown) return;

            // 1. Expire / Break pass
            for (int i = 0; i < SLOT_CAPACITY; i++)
            {
                if (!_slots[i].InUse) continue;

                // Time expiration
                if (tick.TimeMs - _slots[i].LastTimeMs > WINDOW_MS)
                {
                    _slots[i].InUse = _slots[i].Active = false;
                    continue;
                }

                // Price break: if same-side aggressor walked past the level
                if (_slots[i].Side == Aggressor.Buy) // Bear iceberg candidate (lifting ask)
                {
                    if (tick.Side == Aggressor.Buy && tick.Price > _slots[i].Price + _tickSize * 0.5)
                    {
                        _slots[i].InUse = _slots[i].Active = false;
                        continue;
                    }
                }
                else if (_slots[i].Side == Aggressor.Sell) // Bull iceberg candidate (hitting bid)
                {
                    if (tick.Side == Aggressor.Sell && tick.Price < _slots[i].Price - _tickSize * 0.5)
                    {
                        _slots[i].InUse = _slots[i].Active = false;
                        continue;
                    }
                }
            }

            // 2. Match or Create
            int slotIdx = -1;
            for (int i = 0; i < SLOT_CAPACITY; i++)
            {
                if (_slots[i].InUse && _slots[i].Side == tick.Side && Math.Abs(_slots[i].Price - tick.Price) < _tickSize * 0.5)
                {
                    slotIdx = i;
                    break;
                }
            }

            if (slotIdx != -1)
            {
                // Increment match
                _slots[slotIdx].HitCount++;
                _slots[slotIdx].Volume += tick.Volume;
                _slots[slotIdx].LastTimeMs = tick.TimeMs;
                if (_slots[slotIdx].HitCount >= ICEBERG_MIN_HITS)
                {
                    _slots[slotIdx].Active = true;
                }
            }
            else
            {
                // Find empty or oldest slot to recycle
                int bestToRecycle = -1;
                long oldestTime = long.MaxValue;

                for (int i = 0; i < SLOT_CAPACITY; i++)
                {
                    if (!_slots[i].InUse)
                    {
                        bestToRecycle = i;
                        break;
                    }
                    if (_slots[i].LastTimeMs < oldestTime)
                    {
                        oldestTime = _slots[i].LastTimeMs;
                        bestToRecycle = i;
                    }
                }

                if (bestToRecycle != -1)
                {
                    _slots[bestToRecycle].Price       = tick.Price;
                    _slots[bestToRecycle].Side        = tick.Side;
                    _slots[bestToRecycle].FirstTimeMs = tick.TimeMs;
                    _slots[bestToRecycle].LastTimeMs  = tick.TimeMs;
                    _slots[bestToRecycle].HitCount    = 1;
                    _slots[bestToRecycle].Volume      = tick.Volume;
                    _slots[bestToRecycle].Active      = false;
                    _slots[bestToRecycle].InUse       = true;
                }
            }

            // 3. Aggregate results
            bool bullActive = false;
            bool bearActive = false;
            double bullPrice = 0, bearPrice = 0;
            long bullVol = 0, bearVol = 0;
            long maxLastTime = 0;
            long latestBullTime = 0, latestBearTime = 0;

            for (int i = 0; i < SLOT_CAPACITY; i++)
            {
                if (!_slots[i].InUse || !_slots[i].Active) continue;

                if (_slots[i].Side == Aggressor.Sell) // Bull Iceberg
                {
                    bullActive = true;
                    if (_slots[i].LastTimeMs > latestBullTime)
                    {
                        latestBullTime = _slots[i].LastTimeMs;
                        bullPrice = _slots[i].Price;
                        bullVol = _slots[i].Volume;
                    }
                }
                else if (_slots[i].Side == Aggressor.Buy) // Bear Iceberg
                {
                    bearActive = true;
                    if (_slots[i].LastTimeMs > latestBearTime)
                    {
                        latestBearTime = _slots[i].LastTimeMs;
                        bearPrice = _slots[i].Price;
                        bearVol = _slots[i].Volume;
                    }
                }

                if (_slots[i].LastTimeMs > maxLastTime) maxLastTime = _slots[i].LastTimeMs;
            }

            BullIcebergActive = bullActive;
            BearIcebergActive = bearActive;
            BullIcebergPrice  = bullPrice;
            BearIcebergPrice  = bearPrice;
            BullIcebergVolume = bullVol;
            BearIcebergVolume = bearVol;
            LastIcebergTimeMs = maxLastTime;
            LastIcebergAgeMs  = (LastIcebergTimeMs > 0) ? (tick.TimeMs - LastIcebergTimeMs) : long.MaxValue;
        }

        private struct LevelSlot
        {
            public double    Price;
            public long      FirstTimeMs;
            public long      LastTimeMs;
            public int       HitCount;
            public long      Volume;
            public Aggressor Side;
            public bool      Active;
            public bool      InUse;
        }
    }
}
