#region Using declarations
using System;
#endregion

namespace MathLogic
{
    /// <summary>
    /// TradingMath: Shared ultra-lightweight math helpers intended to be reused
    /// across Indicators and Strategies without allocations.
    ///
    /// Design goals:
    /// - Deterministic / replay-safe (no LINQ, no random ordering)
    /// - Main-thread friendly (no locks, no I/O)
    /// - O(1) incremental updates for VWAP and SD engines
    /// </summary>
    public static class TradingMath
    {
        // ===================================================================
        // INCREMENTAL VWAP
        // ===================================================================

        /// <summary>
        /// Incremental VWAP update.
        /// Caller maintains pvSum and volSum and resets them on session start.
        /// Returns the updated VWAP (or previousVWAP if volume<=0).
        /// </summary>
        public static double VWAP_Update(ref double pvSum, ref double volSum, double typicalPrice, double volume, double previousVWAP, out bool isValid)
        {
            isValid = false;
            if (volume <= 0)
                return previousVWAP;

            pvSum += typicalPrice * volume;
            volSum += volume;

            if (volSum <= 0)
                return previousVWAP;

            isValid = true;
            return pvSum / volSum;
        }

        // ===================================================================
        // WELFORD ONLINE STANDARD DEVIATION
        // ===================================================================

        /// <summary>
        /// Welford incremental variance update.
        /// Updates (count, mean, m2) for the stream and returns sample SD.
        /// x is typically (price - vwap).
        /// </summary>
        public static double Welford_SD_Update(ref int count, ref double mean, ref double m2, double x)
        {
            count++;
            double delta = x - mean;
            mean += delta / count;
            double delta2 = x - mean;
            m2 += delta * delta2;

            if (count <= 1)
                return 0.0;

            double variance = m2 / (count - 1); // sample variance
            return Math.Sqrt(Math.Max(0.0, variance));
        }

        // ===================================================================
        // ROLLING STANDARD DEVIATION (RING BUFFER)
        // ===================================================================

        /// <summary>
        /// Rolling SD update using a ring buffer and running sums.
        /// buffer length defines the window.
        /// </summary>
        public static double Rolling_SD_Update(
            double[] buffer,
            ref int index,
            ref int count,
            ref double sum,
            ref double sumSq,
            double x)
        {
            if (buffer == null || buffer.Length < 2)
                return 0.0;

            int window = buffer.Length;

            // Remove old value if full
            if (count >= window)
            {
                double old = buffer[index];
                sum -= old;
                sumSq -= old * old;
            }
            else
            {
                count++;
            }

            // Add new
            buffer[index] = x;
            sum += x;
            sumSq += x * x;

            // Advance
            index++;
            if (index >= window)
                index = 0;

            if (count <= 1)
                return 0.0;

            double mean = sum / count;
            // sample variance with numerical guard
            double variance = (sumSq - (sum * sum / count)) / (count - 1);
            return Math.Sqrt(Math.Max(0.0, variance));
        }

        // ===================================================================
        // HYBRID SD
        // ===================================================================

        /// <summary>
        /// Session weighting schedule (same logic you use everywhere).
        /// Returns wSession in [0..1].
        /// </summary>
        public static double Hybrid_WSession(double sessionPct)
        {
            if (sessionPct < 0.15) return 0.30;
            if (sessionPct > 0.85) return 0.50;
            return 0.70;
        }

        /// <summary>
        /// Hybrid SD blend using RMS (recommended): sqrt(w*s^2 + (1-w)*r^2)
        /// Avoids under/over-sizing when one SD dominates.
        /// </summary>
        public static double SD_Hybrid_RMS(double sdSession, double sdRolling, double sessionPct)
        {
            double w = Hybrid_WSession(sessionPct);
            double s2 = sdSession * sdSession;
            double r2 = sdRolling * sdRolling;
            return Math.Sqrt(Math.Max(0.0, (w * s2) + ((1.0 - w) * r2)));
        }

        /// <summary>
        /// Tradeable SD check in ticks.
        /// </summary>
        public static bool SD_IsTradeableTicks(double sdValue, double tickSize, double minTicks, double maxTicks)
        {
            if (tickSize <= 0.0) return false;
            double sdTicks = sdValue / tickSize;
            return sdTicks >= minTicks && sdTicks <= maxTicks;
        }

        // ===================================================================
        // UTILS
        // ===================================================================

        /// <summary>
        /// Convert a DateTime trading day to an int key yyyymmdd.
        /// </summary>
        public static int TradingDayKey(DateTime tradingDay) => tradingDay.Year * 10000 + tradingDay.Month * 100 + tradingDay.Day;
    }
}
