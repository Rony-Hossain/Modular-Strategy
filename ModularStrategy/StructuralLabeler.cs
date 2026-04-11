#region Using declarations
using System;
using System.Collections.Generic;
using MathLogic;
using MathLogic.Strategy;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.ConditionSets
{
    /// <summary>
    /// Pure producer module for fractal swing detection and structural labeling.
    /// 
    /// Numeric Conventions (from ConfluenceEngine.cs):
    ///   - LastHighLabel: HH=1, LH=3
    ///   - LastLowLabel:  HL=2, LL=4
    ///   - SwingTrend:    Bullish=1, Bearish=-1, Undefined=0
    /// </summary>
    public sealed class StructuralLabeler
    {
        private const int FRACTAL_N = 2; // N=2 requires 2 bars left, 2 bars right
        private const int MAX_SWINGS = 8;

        private readonly List<SwingPoint> _highs = new List<SwingPoint>(MAX_SWINGS);
        private readonly List<SwingPoint> _lows  = new List<SwingPoint>(MAX_SWINGS);

        private int _totalConfirmedSwings = 0;
        private SwingLabel _lastHighLabel = SwingLabel.None;
        private SwingLabel _lastLowLabel  = SwingLabel.None;

        // ── CHoCH one-shot tracking ──
        private int _chochLongRefBar  = -1; // BarIndex of the swing high that triggered the last bullish CHoCH
        private int _chochShortRefBar = -1; // BarIndex of the swing low  that triggered the last bearish CHoCH

        private struct SwingPoint
        {
            public double Price;
            public int BarIndex;
            public SwingLabel Label;
        }

        public void Update(ref MarketSnapshot snapshot, BarSnapshot price, int currentBar)
        {
            // 1. Reset bar-specific flags
            snapshot.Set(SnapKeys.CHoCHFiredLong, 0.0);
            snapshot.Set(SnapKeys.CHoCHFiredShort, 0.0);

            // 2. Detect Swings (Confirmed at i + 2)
            int evalIdx = currentBar - FRACTAL_N;
            if (evalIdx < FRACTAL_N) return;

            if (IsSwingHigh(price.Highs))
            {
                ProcessNewHigh(price.Highs[FRACTAL_N], evalIdx);
            }

            if (IsSwingLow(price.Lows))
            {
                ProcessNewLow(price.Lows[FRACTAL_N], evalIdx);
            }

            // 3. Publish Persistent State
            snapshot.Set(SnapKeys.ConfirmedSwings, (double)_totalConfirmedSwings);
            snapshot.Set(SnapKeys.LastHighLabel, (double)_lastHighLabel);
            snapshot.Set(SnapKeys.LastLowLabel, (double)_lastLowLabel);
            
            double trend = DeriveTrend();
            snapshot.Set(SnapKeys.SwingTrend, trend);

            // 4. CHoCH Detection (One-shot per swing break)
            if (trend == -1.0) // Bearish trend (LH + LL)
            {
                if (_highs.Count > 0)
                {
                    var refHigh = _highs[_highs.Count - 1];
                    if (price.Close > refHigh.Price && refHigh.BarIndex != _chochLongRefBar)
                    {
                        snapshot.Set(SnapKeys.CHoCHFiredLong, 1.0);
                        _chochLongRefBar = refHigh.BarIndex;
                    }
                }
            }
            else if (trend == 1.0) // Bullish trend (HH + HL)
            {
                if (_lows.Count > 0)
                {
                    var refLow = _lows[_lows.Count - 1];
                    if (price.Close < refLow.Price && refLow.BarIndex != _chochShortRefBar)
                    {
                        snapshot.Set(SnapKeys.CHoCHFiredShort, 1.0);
                        _chochShortRefBar = refLow.BarIndex;
                    }
                }
            }
        }

        private bool IsSwingHigh(double[] highs)
        {
            if (highs == null || highs.Length < 5) return false;
            double mid = highs[2];
            return mid > highs[0] && mid > highs[1] && mid > highs[3] && mid > highs[4];
        }

        private bool IsSwingLow(double[] lows)
        {
            if (lows == null || lows.Length < 5) return false;
            double mid = lows[2];
            return mid < lows[0] && mid < lows[1] && mid < lows[3] && mid < lows[4];
        }

        private void ProcessNewHigh(double price, int barIdx)
        {
            SwingLabel label = (_highs.Count > 0) 
                ? (price > _highs[_highs.Count - 1].Price ? SwingLabel.HH : SwingLabel.LH)
                : SwingLabel.HH;

            AddSwing(_highs, price, barIdx, label);
            _lastHighLabel = label;
            _totalConfirmedSwings++;
        }

        private void ProcessNewLow(double price, int barIdx)
        {
            SwingLabel label = (_lows.Count > 0)
                ? (price < _lows[_lows.Count - 1].Price ? SwingLabel.LL : SwingLabel.HL)
                : SwingLabel.LL;

            AddSwing(_lows, price, barIdx, label);
            _lastLowLabel = label;
            _totalConfirmedSwings++;
        }

        private void AddSwing(List<SwingPoint> list, double price, int barIdx, SwingLabel label)
        {
            if (list.Count >= MAX_SWINGS) list.RemoveAt(0);
            list.Add(new SwingPoint { Price = price, BarIndex = barIdx, Label = label });
        }

        private double DeriveTrend()
        {
            if (_lastHighLabel == SwingLabel.HH && _lastLowLabel == SwingLabel.HL) return 1.0;
            if (_lastHighLabel == SwingLabel.LH && _lastLowLabel == SwingLabel.LL) return -1.0;
            return 0.0;
        }
    }
}
