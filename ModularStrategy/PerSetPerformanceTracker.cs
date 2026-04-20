#region Using declarations
using System;
using System.Collections.Generic;
using MathLogic;
using MathLogic.Strategy;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// PER-SET PERFORMANCE TRACKER
    ///
    /// Tracks wins, losses, P&amp;L, and R-multiples per condition set ID,
    /// broken down by session phase. Maintains both session and lifetime buckets.
    ///
    /// Called from HostStrategy:
    ///   OnSessionOpen   → call EmitSessionSummary() then ResetSession()
    ///   OnExecutionUpdate (position flat) → call RecordTrade()
    ///
    /// Output: ranked summary rows written via StrategyLogger.Warn() at
    /// session boundary. Each row is also a CSV WARN entry for offline analysis.
    ///
    /// Design:
    ///   - Hot path (RecordTrade) is called once per trade — not per bar.
    ///     Dictionary lookup per trade is acceptable.
    ///   - Session summary is called once per session open — allocation in
    ///     the summary path is fine (it is not latency-critical).
    ///   - PerSetRecord is a class (not struct) because Dictionary<string, T>
    ///     with struct values requires a full copy on every update — classes
    ///     give us direct mutation without re-boxing.
    ///   - No LINQ anywhere. Sorting uses a pre-allocated List and List.Sort().
    ///
    /// R-multiple formula:
    ///   R = pnl / (signal.StopTicks × tickValue × signal.Contracts)
    ///   Positive R = winner. Negative R = loser.
    ///   Requires tickValue from caller (not stored in SignalObject).
    /// </summary>
    public class PerSetPerformanceTracker
    {
        // ── Storage ───────────────────────────────────────────────────────
        private readonly Dictionary<string, PerSetRecord> _session  = new Dictionary<string, PerSetRecord>(32);
        private readonly Dictionary<string, PerSetRecord> _lifetime = new Dictionary<string, PerSetRecord>(32);

        // Pre-allocated sort buffer — reused each summary call, no per-summary alloc
        private readonly List<PerSetRecord> _sortBuf = new List<PerSetRecord>(32);

        // ── Session lifecycle ─────────────────────────────────────────────

        /// <summary>
        /// Call at session open — AFTER EmitSessionSummary() for the closing session.
        /// Clears the session bucket only; lifetime is never reset.
        /// </summary>
        public void ResetSession()
        {
            _session.Clear();
        }

        // ── Trade recording ───────────────────────────────────────────────

        /// <summary>
        /// Call when a position closes flat.
        ///
        /// signal:    the active SignalObject (has ConditionSetId, StopTicks, Contracts)
        /// pnl:       actual P&amp;L in dollars for this position (sum of all partial exits)
        /// tickValue: instrument tick value (from InstrumentSpecs or NT8 TickValue)
        /// session:   session phase at trade entry (from signal's bar snapshot at entry time)
        /// </summary>
        public void RecordTrade(
            SignalObject signal,
            double       pnl,
            double       tickValue,
            SessionPhase session = SessionPhase.MidSession)
        {
            if (signal == null) return;

            string setId = signal.ConditionSetId;
            if (string.IsNullOrEmpty(setId)) setId = "Unknown";

            // R-multiple: pnl normalised by 1R (dollar risk for this trade).
            // Requires StopTicks > 0 and Contracts > 0 from the signal object.
            double rMultiple = 0.0;
            if (signal.StopTicks > 0 && signal.Contracts > 0 && tickValue > 0)
            {
                double oneR = signal.StopTicks * tickValue * signal.Contracts;
                rMultiple   = pnl / oneR;
            }

            bool isWin = pnl >= 0;

            // ── Session bucket ────────────────────────────────────────────
            PerSetRecord sessRec;
            if (!_session.TryGetValue(setId, out sessRec))
            {
                sessRec = new PerSetRecord(setId);
                _session[setId] = sessRec;
            }
            sessRec.Add(isWin, pnl, rMultiple, session);

            // ── Lifetime bucket ───────────────────────────────────────────
            PerSetRecord lifeRec;
            if (!_lifetime.TryGetValue(setId, out lifeRec))
            {
                lifeRec = new PerSetRecord(setId);
                _lifetime[setId] = lifeRec;
            }
            lifeRec.Add(isWin, pnl, rMultiple, session);
        }

        // ── Session summary ───────────────────────────────────────────────

        /// <summary>
        /// Emit a ranked session summary via StrategyLogger.
        /// Call this BEFORE ResetSession() at each session open.
        ///
        /// Writes one WARN row per condition set that traded this session,
        /// ranked by total session P&amp;L descending. Also writes a lifetime
        /// aggregate footer.
        ///
        /// These rows appear in the CSV under Tag=WARN and Detail contains
        /// the per-set stats — use Excel filter on Tag=WARN + Detail contains
        /// "PERF_SET" to extract the performance table.
        /// </summary>
        public void EmitSessionSummary(DateTime sessionCloseTime, StrategyLogger log)
        {
            if (_session.Count == 0) return;

            // ── Build sorted list ─────────────────────────────────────────
            _sortBuf.Clear();
            foreach (var kvp in _session)
                _sortBuf.Add(kvp.Value);

            // Sort by TotalPnL descending — best performing set first
            _sortBuf.Sort((a, b) => b.TotalPnL.CompareTo(a.TotalPnL));

            // ── Session header ────────────────────────────────────────────
            log?.Warn(sessionCloseTime,
                "PERF_SET SESSION_SUMMARY {0} sets traded ─────────────────",
                _sortBuf.Count);

            // ── Per-set rows ──────────────────────────────────────────────
            for (int i = 0; i < _sortBuf.Count; i++)
            {
                var r = _sortBuf[i];
                log?.Warn(sessionCloseTime,
                    "PERF_SET [{0,-22}] T={1,2} W={2,2} L={3,2} WR={4,5:P0} " +
                    "PnL={5,8:C2} AvgR={6,5:F2} TotR={7,5:F2}",
                    r.SetId,
                    r.Trades, r.Wins, r.Losses,
                    r.WinRate,
                    r.TotalPnL, r.AvgR, r.TotalR);
            }

            // ── Lifetime footer ───────────────────────────────────────────
            // Only emit for sets that have enough trades to be statistically
            // meaningful (>= 10). Below 10 trades any win rate is noise.
            const int MIN_LIFETIME_TRADES = StrategyConfig.Policy.PERF_MIN_LIFETIME_TRADES;
            _sortBuf.Clear();
            foreach (var kvp in _lifetime)
                if (kvp.Value.Trades >= MIN_LIFETIME_TRADES)
                    _sortBuf.Add(kvp.Value);

            if (_sortBuf.Count > 0)
            {
                _sortBuf.Sort((a, b) => b.TotalPnL.CompareTo(a.TotalPnL));
                log?.Warn(sessionCloseTime,
                    "PERF_SET LIFETIME (sets with ≥{0} trades) ─────────────",
                    MIN_LIFETIME_TRADES);

                for (int i = 0; i < _sortBuf.Count; i++)
                {
                    var r = _sortBuf[i];
                    log?.Warn(sessionCloseTime,
                        "PERF_SET_LT [{0,-22}] T={1,3} W={2,3} L={3,3} WR={4,5:P0} " +
                        "PnL={5,9:C2} AvgR={6,5:F2}",
                        r.SetId,
                        r.Trades, r.Wins, r.Losses,
                        r.WinRate,
                        r.TotalPnL, r.AvgR);
                }
            }
        }

        /// <summary>
        /// Returns a snapshot of lifetime records for external inspection
        /// (e.g. UI rendering, adaptive weight engine).
        /// Returns null if the set has no lifetime record.
        /// </summary>
        public PerSetRecord GetLifetime(string setId)
        {
            PerSetRecord rec;
            return _lifetime.TryGetValue(setId, out rec) ? rec : null;
        }

        /// <summary>
        /// Returns all lifetime records, sorted by total P&amp;L descending.
        /// Allocates — for offline use only (UI, file export). Not for hot path.
        /// </summary>
        public List<PerSetRecord> GetAllLifetimeSorted()
        {
            var list = new List<PerSetRecord>(_lifetime.Count);
            foreach (var kvp in _lifetime) list.Add(kvp.Value);
            list.Sort((a, b) => b.TotalPnL.CompareTo(a.TotalPnL));
            return list;
        }
    }

    // =========================================================================
    // PER-SET RECORD — class for direct Dictionary mutation without boxing
    // =========================================================================

    /// <summary>
    /// Performance record for one condition set.
    /// Also tracks the best and worst single-trade R-multiples for
    /// outlier detection (a strategy with AvgR=0.8 but MaxR=8.0 is
    /// probably riding one lucky outlier).
    /// </summary>
    public sealed class PerSetRecord
    {
        public string SetId    { get; }
        public int    Wins     { get; private set; }
        public int    Losses   { get; private set; }
        public double TotalPnL { get; private set; }
        public double TotalR   { get; private set; }
        public double MaxR     { get; private set; } = double.MinValue;
        public double MinR     { get; private set; } = double.MaxValue;

        // Per-session-phase breakdown (EarlySession / MidSession / LateSession)
        public int WinsEarly  { get; private set; }
        public int LossEarly  { get; private set; }
        public int WinsMid    { get; private set; }
        public int LossMid    { get; private set; }
        public int WinsLate   { get; private set; }
        public int LossLate   { get; private set; }

        // Computed
        public int    Trades  => Wins + Losses;
        public double WinRate => Trades > 0 ? (double)Wins / Trades : 0.0;
        public double AvgR    => Trades > 0 ? TotalR / Trades : 0.0;
        public double AvgPnL  => Trades > 0 ? TotalPnL / Trades : 0.0;

        public PerSetRecord(string setId)
        {
            SetId = setId;
        }

        internal void Add(bool isWin, double pnl, double rMultiple, SessionPhase session)
        {
            if (isWin) Wins++;
            else       Losses++;

            TotalPnL += pnl;
            TotalR   += rMultiple;

            if (rMultiple > MaxR) MaxR = rMultiple;
            if (rMultiple < MinR) MinR = rMultiple;

            // Session phase breakdown
            switch (session)
            {
                case SessionPhase.OpeningRange:
                case SessionPhase.EarlySession:
                    if (isWin) WinsEarly++; else LossEarly++; break;
                case SessionPhase.MidSession:
                    if (isWin) WinsMid++;   else LossMid++;   break;
                case SessionPhase.LateSession:
                    if (isWin) WinsLate++;  else LossLate++;  break;
                // PreMarket / AfterHours — counted in totals only, no phase bucket
            }
        }
    }
}
