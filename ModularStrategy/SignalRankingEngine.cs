#region Using declarations
using System;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// SIGNAL RANKING ENGINE — selects the best signal from all candidates.
    ///
    /// Sits between StrategyEngine and OrderManager. Receives every valid
    /// RawDecision the StrategyEngine buffered this bar, runs each one through
    /// ConfluenceEngine, and returns the single highest-quality winner.
    ///
    /// Replaces the StrategyEngine's raw best-by-RawScore selection with:
    ///   FinalScore = RawDecision.RawScore × ConfluenceResult.Multiplier
    ///
    /// This means a Breaker at RawScore=92 with the 4H EMA opposing will lose
    /// to a BOS at RawScore=75 with all three TFs aligned — as it should.
    ///
    /// Veto path (ConfluenceResult.IsVetoed=true): candidate is discarded
    /// entirely regardless of RawScore.
    ///
    /// MinConfluenceNetScore floor: candidates with too little contextual
    /// support are discarded before multiplier calculation. Threshold scales
    /// with whether Volumetric data is present (LayerC degrades to ~0 without it).
    ///
    /// THREAD SAFETY: Not thread-safe. Called once per primary bar from the
    /// NT8 OnBarUpdate event loop — single-threaded by design.
    /// </summary>
    public class SignalRankingEngine
    {
        // ── Pre-allocated ranking scratch buffer — no heap allocation in hot path
        private const int MAX_CANDIDATES = 32;
        private readonly RankedCandidate[] _ranked = new RankedCandidate[MAX_CANDIDATES];

        // ── Confluence floor — candidates below this are discarded
        // Scaled by SetVolumetricMode() each bar
        private int _minNetScore = 25;

        /// <summary>
        /// Confluence Detail string of the last winning candidate (from the winner's
        /// ConfluenceResult). HostStrategy reads this after Rank() and passes it to
        /// SignalGenerator.Process so the tags land in the SIGNAL_ACCEPTED CSV row.
        /// Empty when Rank returned RawDecision.None.
        /// </summary>
        public string LastWinnerDetail { get; private set; } = "";

        /// <summary>
        /// Captures all candidates that were discarded during Rank().
        /// Re-populated every bar. HostStrategy reads this to show gray "REJ" bubbles.
        /// </summary>
        public System.Collections.Generic.List<RawDecision> Rejections { get; } = new System.Collections.Generic.List<RawDecision>(16);

        private StrategyLogger _log;

        public SignalRankingEngine(StrategyLogger log)
        {
            _log = log;
        }

        public void SetLogger(StrategyLogger log) => _log = log;

        /// <summary>
        /// Call once per bar before Rank(). Sets the confluence floor:
        ///   hasVolumetric=true  → minNetScore=40 (order flow data present — use it)
        ///   hasVolumetric=false → minNetScore=25 (structure + MTFA only — lower bar)
        /// </summary>
        public void SetVolumetricMode(bool hasVolumetric)
        {
            _minNetScore = hasVolumetric ? 40 : 25;
        }

        /// <summary>
        /// Re-rank all candidates from StrategyEngine and return the winner.
        ///
        /// candidateBuffer: StrategyEngine.CandidateBuffer (pre-allocated array)
        /// count:           StrategyEngine.CandidateCount  (valid entries this bar)
        /// snap:            current MarketSnapshot (same one StrategyEngine used)
        /// </summary>
        public RawDecision Rank(
            RawDecision[]  candidateBuffer,
            int            count,
            MarketSnapshot snap,
            in SupportResistanceResult sr)
        {
            LastWinnerDetail = "";
            Rejections.Clear();
            if (count == 0 || candidateBuffer == null) return RawDecision.None;

            int rankedCount = 0;
            DateTime barTime = snap.Primary.Time;

            // ── Directional Conflict Detection ──────────────────────────
            // If the modules disagree (Long and Short candidates on the same bar),
            // it indicates a range-bound or indecisive market. Apply a penalty.
            bool hasLong  = false;
            bool hasShort = false;
            for (int i = 0; i < count; i++)
            {
                if (!candidateBuffer[i].IsValid) continue;
                if (candidateBuffer[i].Direction == SignalDirection.Long)  hasLong  = true;
                if (candidateBuffer[i].Direction == SignalDirection.Short) hasShort = true;
            }
            bool marketConflict = hasLong && hasShort;

            for (int i = 0; i < count; i++)
            {
                RawDecision candidate = candidateBuffer[i];
                if (!candidate.IsValid) continue;

                bool isLong = candidate.Direction == SignalDirection.Long;

                ConfluenceResult conf = ConfluenceEngine.Evaluate(isLong, snap,
                    in sr,
                    candidate.ConditionSetId ?? "");

                // Apply conflict penalty if modules disagree
                if (marketConflict)
                {
                    conf.NetScore -= 15; // 15 point penalty for directional disagreement
                    _log?.Warn(barTime, "RANK_CONFLICT [{0}] -15 penalty applied", candidate.ConditionSetId);
                }

                // ── Confluence floor per signal type ──────────────────────
                // BOSWave (SMF_Native_*): floor = 0. These signals have their own
                // internal strength/regime gates. Layer A and B are disabled for
                // SMF_Native_* so max possible score is ~42. Floor = 0 avoids
                // killing everything while still allowing IsVetoed to block bad entries.
                //
                // BOS (SMC_BOS_v1): NOW gets a real floor.
                // Was floor = 0 on the assumption BOS has its own delta framework.
                // Problem: that framework only checks bar-level delta — it cannot
                // see H4/H2/H1 EMA bias or HTF level proximity (Layer A and B).
                // With floor = 0, a BOS with h4=- h2=- h1=- (all bearish) passes
                // straight through with zero HTF penalty.
                // New floor: 20 with Volumetric, 10 without.
                // This means a BOS with full HTF opposition (-14 -10 = -24 points
                // on Layer A alone) will fall below floor and be rejected.
                // Data: Long BOS with all 3 TFs bullish still lost -$4,080. The
                // architectural fix is wiring HTF context — this floor does that.
                bool isBOSWave = (candidate.ConditionSetId ?? "").StartsWith("SMF_Native_");
                bool isBOS     = (candidate.ConditionSetId ?? "") == "SMC_BOS_v1";

                int bosFloor       = _minNetScore > 30 ? 20 : 10;  // scales with volumetric mode
                int effectiveFloor = isBOSWave ? 0
                                   : isBOS     ? bosFloor
                                   : _minNetScore;

                // ── PERFORMANCE TUNING: Macro Regime Gate ──────────────────
                // FIX (#Layer4): Align Longs with the H4 macro anchor.
                // If Long signal fires while H4 bias is Bearish, increase floor to 60.
                // REFINEMENT (Commit 7): If H4 bias is Undefined (0.0), increase floor to 70.
                bool isORB = (candidate.ConditionSetId ?? "").StartsWith("ORB_");
                if (isLong && !isORB)
                {
                    double h4b = snap.Get(SnapKeys.H4HrEmaBias);
                    if (h4b < 0) effectiveFloor = Math.Max(effectiveFloor, 60);
                    else if (h4b == 0) effectiveFloor = Math.Max(effectiveFloor, 70);
                }

                // ── VETO: discard immediately ──────────────────────────────
                if (conf.IsVetoed)
                {
                    candidate.RawScore = (int)(candidate.RawScore * conf.Multiplier);
                    candidate.Label = "VETO";
                    Rejections.Add(candidate);

                    _log?.Warn(barTime,
                        "RANK_VETO [{0}] {1} conf={2}",
                        candidate.ConditionSetId,
                        isLong ? "L" : "S",
                        conf.ToString());
                    continue;
                }

                // ── NET SCORE FLOOR: discard weak confluence ───────────────
                if (conf.NetScore < effectiveFloor)
                {
                    candidate.RawScore = (int)(candidate.RawScore * conf.Multiplier);
                    candidate.Label = string.Format("WEAK {0}/{1}", conf.NetScore, effectiveFloor);
                    Rejections.Add(candidate);

                    _log?.Warn(barTime,
                        "RANK_WEAK [{0}] {1}",
                        candidate.ConditionSetId,
                        conf.ToString());
                    continue;
                }

                double finalScore = candidate.RawScore * conf.Multiplier;

                if (rankedCount < MAX_CANDIDATES)
                {
                    _ranked[rankedCount].Decision   = candidate;
                    _ranked[rankedCount].Confluence = conf;
                    _ranked[rankedCount].FinalScore = finalScore;
                    rankedCount++;
                }
            }

            if (rankedCount == 0) return RawDecision.None;

            // ── Select winner: highest FinalScore ────────────────────────
            // Tiebreak: Higher Risk:Reward ratio (Target - Entry) / (Entry - Stop).
            // If R:R is also equal, fallback to lower buffer index (CreateLogic() order).
            int winIdx = 0;
            for (int i = 1; i < rankedCount; i++)
            {
                bool higherScore = _ranked[i].FinalScore > _ranked[winIdx].FinalScore;
                bool tiedScore   = Math.Abs(_ranked[i].FinalScore - _ranked[winIdx].FinalScore) < 0.001;

                if (higherScore)
                {
                    winIdx = i;
                }
                else if (tiedScore)
                {
                    // Calculate R:R for both candidates
                    double rrNew = CalculateRR(_ranked[i].Decision);
                    double rrWin = CalculateRR(_ranked[winIdx].Decision);

                    if (rrNew > rrWin) winIdx = i;
                }
            }

            _log?.Warn(barTime,
                "RANK_WIN [{0}] Raw={1} {2} Final={3:F1}",
                _ranked[winIdx].Decision.ConditionSetId,
                _ranked[winIdx].Decision.RawScore,
                _ranked[winIdx].Confluence.ToDetailString(),
                _ranked[winIdx].FinalScore);

            LastWinnerDetail = _ranked[winIdx].Confluence.Detail ?? "";
            return _ranked[winIdx].Decision;
        }

        private static double CalculateRR(RawDecision d)
        {
            double risk = Math.Abs(d.EntryPrice - d.StopPrice);
            double reward = Math.Abs(d.TargetPrice - d.EntryPrice);
            return risk > 0 ? reward / risk : 0.0;
        }
    }

    // =========================================================================
    // SCRATCH STRUCT — value type, no allocation
    // =========================================================================

    /// <summary>
    /// Internal ranking scratch record. Not exposed outside SignalRankingEngine.
    /// Struct to avoid array-of-references overhead.
    /// </summary>
    internal struct RankedCandidate
    {
        public RawDecision     Decision;
        public ConfluenceResult Confluence;
        public double           FinalScore;
    }
}
