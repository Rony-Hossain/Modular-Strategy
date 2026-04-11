#region Using declarations
using System;
using System.Collections.Generic;
using MathLogic;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ========================================================================
    // VOLUME PROFILE PROCESSOR
    // ========================================================================
    //
    // A standalone, high-performance engine for building and analyzing
    // volume profiles (Price -> Volume distributions).
    //
    // Capabilities:
    //   - Accumulate tick data or entire footprint ladders.
    //   - Compute Point of Control (POC), Value Area High (VAH), and Value Area Low (VAL).
    //   - Support fixed-range profiling (e.g., Opening Range) with a Lock mechanism.
    //
    // Design:
    //   - Reuses a pre-allocated Dictionary to prevent GC pressure during RTH.
    //   - Delegates core math (POC/VA) to the static MathStructure library.
    //   - Can be instanced by any module (Host, SR Engine, ORB, etc.).
    // ========================================================================

    public sealed class VolumeProfileProcessor
    {
        // ── State ─────────────────────────────────────────────────────────
        private readonly Dictionary<double, double> _profile;
        private readonly double _tickSize;
        private readonly double _valueAreaCoverage;
        
        private bool _isLocked = false;
        private int  _barsAccumulated = 0;

        // Computed metrics (cached)
        private double _poc    = 0.0;
        private double _vaHigh = 0.0;
        private double _vaLow  = 0.0;
        private double _maxVol = 0.0;

        // ── Public Accessors ──────────────────────────────────────────────
        public double POC     => _poc;
        public double VAHigh  => _vaHigh;
        public double VALow   => _vaLow;
        public double MaxVol  => _maxVol;
        public bool   IsReady => _barsAccumulated > 0;
        public bool   IsLocked => _isLocked;

        // ====================================================================
        // CONSTRUCTION
        // ====================================================================
        public VolumeProfileProcessor(double tickSize, double valueAreaCoverage = 0.70, int initialCapacity = 512)
        {
            _tickSize = tickSize;
            _valueAreaCoverage = valueAreaCoverage;
            _profile = new Dictionary<double, double>(initialCapacity);
        }

        // ====================================================================
        // LIFECYCLE
        // ====================================================================
        public void Reset()
        {
            _profile.Clear();
            _barsAccumulated = 0;
            _isLocked        = false;
            _poc             = 0.0;
            _vaHigh          = 0.0;
            _vaLow           = 0.0;
            _maxVol          = 0.0;
        }

        public void Lock()
        {
            if (!_isLocked)
            {
                Calculate();
                _isLocked = true;
            }
        }

        // ====================================================================
        // ACCUMULATION
        // ====================================================================
        
        /// <summary>
        /// Add a single price/volume event to the profile.
        /// </summary>
        public void AddVolume(double price, double volume)
        {
            if (_isLocked || volume <= 0.0 || _tickSize <= 0.0) return;

            // Round to nearest tick to ensure stable dictionary keys
            double key = Math.Round(price / _tickSize) * _tickSize;

            if (_profile.TryGetValue(key, out double existing))
                _profile[key] = existing + volume;
            else
                _profile[key] = volume;
        }

        /// <summary>
        /// Mark a bar as completed for tracking readiness.
        /// </summary>
        public void FinalizeBar()
        {
            if (!_isLocked) _barsAccumulated++;
        }

        // ====================================================================
        // COMPUTATION
        // ====================================================================
        
        /// <summary>
        /// Compute POC and Value Area based on current accumulation.
        /// Automatically called by Lock(). Can be called continuously if tracking a rolling profile.
        /// </summary>
        public void Calculate()
        {
            if (_profile.Count == 0) return;

            _poc = MathStructure.POC_Find(_profile, out _maxVol);

            if (_poc > 0.0)
            {
                MathStructure.ValueArea_Calculate(
                    _profile,
                    _poc,
                    _valueAreaCoverage,
                    out _vaHigh,
                    out _vaLow);
            }
        }
    }
}