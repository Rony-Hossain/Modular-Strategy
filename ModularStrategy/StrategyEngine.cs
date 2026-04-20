#region Using declarations
using System;
using System.Collections.Generic;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// STRATEGY ENGINE — the plug-and-play runner.
    ///
    /// Replaces StrategyLogic. Holds a list of IConditionSet implementations.
    /// On every bar it asks each set "do your conditions hold?"
    /// The first valid decision wins. All evaluations are logged.
    ///
    /// To add a new strategy:
    ///   1. Create a file implementing IConditionSet.
    ///   2. Pass it to StrategyEngine in HostStrategy — one line change.
    ///   3. Done. No other file changes needed.
    ///
    /// Every signal carries a unique SignalId in the format:
    ///   "{SetId}:{yyyyMMdd}:{barIndex}"
    ///   e.g. "ORB_Classic_v1:20260319:1023"
    ///
    /// This ID appears in:
    ///   - CSV log Label and Detail columns
    ///   - NT8 entry name on the chart
    ///   - TRADE_WIN / TRADE_LOSS rows for full audit trail
    ///
    /// Future: plug in RankingEngine between Evaluate and Return
    /// to choose best signal instead of first valid. Zero other changes needed.
    /// </summary>
    public class StrategyEngine : IStrategyLogic
    {
        // ===================================================================
        // FIELDS
        // ===================================================================

        private readonly IConditionSet[]  _sets;
        private StrategyLogger            _log;

        private double _tickSize;
        private double _tickValue;

        // Re-entry suppression — per-direction
        private int _lastFillBarLong  = -1;
        private int _lastFillBarShort = -1;
        private const int REENTRY_SUPPRESSION_BARS = StrategyConfig.Modules.SE_REENTRY_SUPPRESSION_BARS;

        private const int MAX_SETS = StrategyConfig.Modules.SE_MAX_SETS;
        private readonly RawDecision[] _candidates = new RawDecision[StrategyConfig.Modules.SE_MAX_SETS];

        // Candidate count written by Evaluate() — exposed to SignalRankingEngine
        // via CandidateCount property so the ranking engine can iterate the buffer
        // without requiring a separate allocation. Written to field (not local) so
        // the value survives Evaluate() returning. Reset to 0 at each Evaluate() call.
        private int _lastCandidateCount = 0;

        /// <summary>
        /// Pre-allocated candidate buffer filled by Evaluate() each bar.
        /// Valid entries are [0 .. CandidateCount-1].
        /// SignalRankingEngine reads this after calling Evaluate().
        /// </summary>
        public RawDecision[] CandidateBuffer => _candidates;

        /// <summary>
        /// Number of valid candidates written to CandidateBuffer on the last Evaluate() call.
        /// Zero when no condition sets fired. Always &lt;= MAX_SETS.
        /// </summary>
        public int CandidateCount => _lastCandidateCount;

        // Last known bar time — used in exception handler logging so we never
        // write DateTime.Now (which would be wall-clock time in backtest/replay).
        private DateTime _lastBarTime = DateTime.MinValue;

        // ===================================================================
        // CONSTRUCTION
        // ===================================================================

        /// <summary>
        /// Pass in any number of condition sets. They run in the order provided.
        /// First valid decision wins.
        /// </summary>
        public StrategyEngine(StrategyLogger log, params IConditionSet[] sets)
        {
            _log  = log;
            _sets = sets ?? new IConditionSet[0];
        }

        /// <summary>Logger-only constructor for existing code compatibility.</summary>
        public StrategyEngine(StrategyLogger log) : this(log, new IConditionSet[0]) { }

        public void SetLogger(StrategyLogger log) => _log = log;

    public IConditionSet GetSet(string setId)
    {
        foreach (var s in _sets)
            if (s != null && s.SetId == setId) return s;
        return null;
    }

        // ===================================================================
        // IStrategyLogic
        // ===================================================================

        public void Initialize(InstrumentKind instrument, double tickSize, double tickValue)
        {
            _tickSize  = tickSize;
            _tickValue = tickValue;

            foreach (var set in _sets)
            {
                try   { set.Initialise(tickSize, tickValue); }
                catch (Exception ex)
                {
                    // No bar time available at Initialize — use MinValue as sentinel.
                    // Logger will show 0001-01-01 which makes it obvious this is startup.
                    _log?.Warn(DateTime.MinValue, "StrategyEngine.Initialize: {0} threw {1}", set.SetId, ex.Message);
                }
            }
        }

        public void OnSessionOpen(MarketSnapshot snapshot)
        {
            _lastFillBarLong  = -1;
            _lastFillBarShort = -1;
            _lastBarTime      = snapshot.Primary.Time;

            foreach (var set in _sets)
            {
                try   { set.OnSessionOpen(snapshot); }
                catch (Exception ex)
                {
                    _log?.Warn(snapshot.Primary.Time, "StrategyEngine.OnSessionOpen: {0} threw {1}", set.SetId, ex.Message);
                }
            }
        }

        public void OnFill(SignalObject signal, double fillPrice)
        {
            if (signal.Direction == SignalDirection.Long)  _lastFillBarLong  = signal.BarIndex;
            if (signal.Direction == SignalDirection.Short) _lastFillBarShort = signal.BarIndex;

            foreach (var set in _sets)
            {
                try   { set.OnFill(signal, fillPrice); }
                catch { }
            }
        }

        public void OnClose(SignalObject signal, double exitPrice, double pnl)
        {
            foreach (var set in _sets)
            {
                try   { set.OnClose(signal, exitPrice, pnl); }
                catch { }
            }
        }

        // ===================================================================
        // MAIN EVALUATION — called every bar
        // ===================================================================

        public RawDecision Evaluate(MarketSnapshot snapshot)
        {
            if (!snapshot.IsValid)
            {
                _log?.Warn(_lastBarTime, "StrategyEngine.Evaluate: Snapshot invalid (warm-up or feed gap). Signal detection skipped.");
                return RawDecision.None;
            }
            if (_sets.Length == 0)            return RawDecision.None;

            var p = snapshot.Primary;

            _lastCandidateCount = 0;
            _lastBarTime = p.Time;

            foreach (var set in _sets)
            {
                RawDecision d;

                try
                {
                    d = set.Evaluate(snapshot);
                }
                catch (Exception ex)
                {
                    _log?.Warn(p.Time, "StrategyEngine.Evaluate: {0} threw {1}", set.SetId, ex.Message);
                    continue;
                }

                if (!d.IsValid)
                {
                    _log?.EvalNoSignal(p.CurrentBar, p.Time, $"[{set.SetId}] no match");
                    continue;
                }

                // FIX (#N9): Direction-aware re-entry suppression
                if (d.Direction == SignalDirection.Long && _lastFillBarLong >= 0 && p.CurrentBar - _lastFillBarLong < REENTRY_SUPPRESSION_BARS)
                    continue;
                if (d.Direction == SignalDirection.Short && _lastFillBarShort >= 0 && p.CurrentBar - _lastFillBarShort < REENTRY_SUPPRESSION_BARS)
                    continue;

                // Stamp origin IDs onto the decision
                d.ConditionSetId = set.SetId;
                d.BarIndex = p.CurrentBar;
                d.SignalId = BuildSignalId(set.SetId, p.Time, p.CurrentBar);

                // Log every valid candidate — not just the winner
                _log?.EvalDecision(p.Time, d, snapshot);

                // Buffer the candidate (guard against overflow — should never fire)
                if (_lastCandidateCount < MAX_SETS)
                    _candidates[_lastCandidateCount++] = d;
            }

            // ── Select winner: highest RawScore ────────────────────────
            // Tiebreak: lower index (earlier in _sets) wins — preserves
            // the CreateLogic() ordering as explicit priority, not accident.
            if (_lastCandidateCount == 0)
                return RawDecision.None;

            RawDecision winner = _candidates[0];
            for (int i = 1; i < _lastCandidateCount; i++)
            {
                if (_candidates[i].RawScore > winner.RawScore)
                    winner = _candidates[i];
            }

            return winner;
        }

        // ===================================================================
        // HELPERS
        // ===================================================================

        /// <summary>
        /// Builds the globally unique signal ID.
        /// Format: "{SetId}:{yyyyMMdd}:{barIndex}"
        /// This string is used as the NT8 order name so it appears on the chart
        /// and in the trades CSV, linking every fill back to its condition set.
        /// </summary>
        private static string BuildSignalId(string setId, DateTime barTime, int barIndex)
        {
            // Keep it short enough for NT8 order name field (max ~64 chars)
            return $"{setId}:{barTime:yyyyMMdd}:{barIndex}";
        }

        /// <summary>
        /// Returns a summary of all registered condition sets for logging.
        /// </summary>
        public string GetSetSummary()
        {
            if (_sets.Length == 0) return "(none)";
            return string.Join(", ", System.Array.ConvertAll(_sets, s => s.SetId));
        }
    }
}
