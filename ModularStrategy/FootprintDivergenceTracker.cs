#region Using declarations
using System;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// FootprintDivergenceTracker — stateful pivot-based delta divergence.
    ///
    /// Boundary: FootprintCore is stateless per-bar by design. This module
    /// is its stateful sibling. It consumes FootprintResult each closed bar,
    /// maintains a 7-bar rolling buffer (k=3 fractal), and on pivot
    /// confirmation compares the pivot's session-cumulative delta to the
    /// previous same-side pivot's cumulative delta.
    ///
    /// Divergence definitions:
    ///   Bear: swing-high(n) > swing-high(n-1) AND cumDelta < cumDelta(n-1)
    ///         Price made a higher high; sellers absorbed it.
    ///   Bull: swing-low(n)  < swing-low(n-1)  AND cumDelta > cumDelta(n-1)
    ///         Price made a lower low; buyers absorbed it.
    /// </summary>
    public sealed class FootprintDivergenceTracker
    {
        private const int    PIVOT_K              = StrategyConfig.Modules.FD_PIVOT_K;     // 7-bar fractal
        private const int    BUFFER_SIZE          = PIVOT_K * 2 + 1;
        private const int    FLAG_LIFETIME_BARS   = StrategyConfig.Modules.FD_FLAG_LIFETIME_BARS;
        private const double MIN_SWING_ATR_MULT   = StrategyConfig.Modules.FD_MIN_SWING_ATR_MULT;

        private struct BarSnap
        {
            public int    BarIndex;
            public double High;
            public double Low;
            public double CumDelta;
            public double Atr;
            public bool   Valid;
        }

        private readonly BarSnap[] _buffer = new BarSnap[BUFFER_SIZE];
        private int _bufferCount = 0;

        private double _prevHighPrice, _lastHighPrice;
        private double _prevHighCumDelta, _lastHighCumDelta;
        private bool   _hasPrevHigh, _hasLastHigh;

        private double _prevLowPrice, _lastLowPrice;
        private double _prevLowCumDelta, _lastLowCumDelta;
        private bool   _hasPrevLow, _hasLastLow;

        private int _bearDivConfirmedAtBar = -1;
        private int _bullDivConfirmedAtBar = -1;

        public void Reset()
        {
            _bufferCount = 0;
            _hasPrevHigh = _hasLastHigh = false;
            _hasPrevLow  = _hasLastLow  = false;
            _bearDivConfirmedAtBar = -1;
            _bullDivConfirmedAtBar = -1;
        }

        public void OnBar(FootprintResult fp, double atr, int barIndex)
        {
            if (!fp.IsValid) return;

            if (_bufferCount == BUFFER_SIZE)
            {
                for (int i = 0; i < BUFFER_SIZE - 1; i++)
                    _buffer[i] = _buffer[i + 1];
                _bufferCount = BUFFER_SIZE - 1;
            }

            _buffer[_bufferCount++] = new BarSnap
            {
                BarIndex = barIndex,
                High     = fp.High,
                Low      = fp.Low,
                CumDelta = fp.CumDelta,
                Atr      = atr > 0 ? atr : double.NaN,
                Valid    = true
            };

            if (_bufferCount < BUFFER_SIZE) return;
            EvaluateCentrePivot(barIndex);
        }

        private void EvaluateCentrePivot(int currentBarIndex)
        {
            ref readonly BarSnap centre = ref _buffer[PIVOT_K];
            double atr = centre.Atr;
            if (double.IsNaN(atr) || atr <= 0) return;

            bool isSwingHigh = true;
            for (int i = 0; i < BUFFER_SIZE; i++)
            {
                if (i == PIVOT_K) continue;
                if (_buffer[i].High >= centre.High) { isSwingHigh = false; break; }
            }
            if (isSwingHigh) ProcessSwingHigh(centre, atr, currentBarIndex);

            bool isSwingLow = true;
            for (int i = 0; i < BUFFER_SIZE; i++)
            {
                if (i == PIVOT_K) continue;
                if (_buffer[i].Low <= centre.Low) { isSwingLow = false; break; }
            }
            if (isSwingLow) ProcessSwingLow(centre, atr, currentBarIndex);
        }

        private void ProcessSwingHigh(in BarSnap pivot, double atr, int confirmBar)
        {
            if (_hasLastHigh)
            {
                double swingDist = Math.Abs(pivot.High - _lastHighPrice);
                if (swingDist < MIN_SWING_ATR_MULT * atr) return;
            }

            if (_hasLastHigh)
            {
                _prevHighPrice = _lastHighPrice;
                _prevHighCumDelta = _lastHighCumDelta;
                _hasPrevHigh = true;
            }
            _lastHighPrice = pivot.High;
            _lastHighCumDelta = pivot.CumDelta;
            _hasLastHigh = true;

            if (_hasPrevHigh && _hasLastHigh)
            {
                if (_lastHighPrice > _prevHighPrice && _lastHighCumDelta < _prevHighCumDelta)
                    _bearDivConfirmedAtBar = confirmBar;
            }
        }

        private void ProcessSwingLow(in BarSnap pivot, double atr, int confirmBar)
        {
            if (_hasLastLow)
            {
                double swingDist = Math.Abs(pivot.Low - _lastLowPrice);
                if (swingDist < MIN_SWING_ATR_MULT * atr) return;
            }

            if (_hasLastLow)
            {
                _prevLowPrice = _lastLowPrice;
                _prevLowCumDelta = _lastLowCumDelta;
                _hasPrevLow = true;
            }
            _lastLowPrice = pivot.Low;
            _lastLowCumDelta = pivot.CumDelta;
            _hasLastLow = true;

            if (_hasPrevLow && _hasLastLow)
            {
                if (_lastLowPrice < _prevLowPrice && _lastLowCumDelta > _prevLowCumDelta)
                    _bullDivConfirmedAtBar = confirmBar;
            }
        }

        public bool IsBearDivergenceActive(int currentBarIndex)
        {
            if (_bearDivConfirmedAtBar < 0) return false;
            return (currentBarIndex - _bearDivConfirmedAtBar) <= FLAG_LIFETIME_BARS;
        }

        public bool IsBullDivergenceActive(int currentBarIndex)
        {
            if (_bullDivConfirmedAtBar < 0) return false;
            return (currentBarIndex - _bullDivConfirmedAtBar) <= FLAG_LIFETIME_BARS;
        }
    }
}
