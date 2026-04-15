#region Using declarations
using System;
using System.IO;
using System.Text;
using NinjaTrader.NinjaScript;
using MathLogic;
using MathLogic.Strategy;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // =========================================================================
    // LOG LEVELS
    // =========================================================================

    public enum LogLevel
    {
        /// <summary>Only ORDER SUBMITTED and fatal errors. Use in live trading.</summary>
        Minimal = 0,

        /// <summary>Signals, rejections, fills, exits. Default for backtesting.</summary>
        Normal = 1,

        /// <summary>Every bar's indicator state + all the above. Verbose — debugging only.</summary>
        Verbose = 2
    }

    // =========================================================================
    // STRATEGY LOGGER
    // =========================================================================

    /// <summary>
    /// Central logging hub. Writes to:
    ///   1. NinjaTrader Output tab (all levels)
    ///   2. CSV log file (one row per event, path configurable)
    ///
    /// CSV columns:
    ///   Timestamp, Tag, Bar, Direction, Source, Score, Grade, Contracts,
    ///   EntryPrice, StopPrice, StopTicks, T1Price, T2Price, RRRatio,
    ///   ExitPrice, PnL, SessionPnL, GateReason, Label, Detail
    ///
    /// CSV file: Documents\NinjaTrader 8\log\ModularStrategy_YYYYMMDD.csv
    /// A new file is created each calendar day the strategy runs.
    /// </summary>
    public class StrategyLogger : IDisposable
    {
        // ── Configuration ────────────────────────────────────────────────────
        public LogLevel Level { get; set; } = LogLevel.Normal;
        public bool SuppressNoSignalBars { get; set; } = true;
        public bool WriteCsv { get; set; } = true;

        // ── Internals ────────────────────────────────────────────────────────
        private readonly NinjaScriptBase _host;
        private readonly StringBuilder _sb = new StringBuilder(512);
        private StreamWriter _writer;
        private string _currentFilePath;
        private DateTime _currentFileDate = DateTime.MinValue;
        // Unique run ID — stamped at logger construction so each backtest run
        // gets its own file even if run multiple times on the same calendar day.
        private readonly string _runStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // Session-level counters
        private int _sessionSignals;
        private int _sessionFills;
        private int _sessionFiltered;
        private double _sessionPnL;

        // ── Bar context: pre/post signal candlestick capture ────────────────
        // Stores the bar index when the last signal was accepted so we can
        // emit the next 5 primary bars as a BAR_FORWARD row once they close.
        private int      _contextSignalBar  = -1;
        private int      _contextBarsLogged = 0;
        private const int CONTEXT_BARS      = 5;
        // Pre-allocated forward bar buffer — 5 bars × 7 fields (T,O,H,L,C,V,D)
        // Written incrementally as bars close after signal.
        private readonly double[] _fwdO = new double[CONTEXT_BARS];
        private readonly double[] _fwdH = new double[CONTEXT_BARS];
        private readonly double[] _fwdL = new double[CONTEXT_BARS];
        private readonly double[] _fwdC = new double[CONTEXT_BARS];
        private readonly double[] _fwdV = new double[CONTEXT_BARS];
        private readonly double[] _fwdD = new double[CONTEXT_BARS];
        private readonly DateTime[] _fwdT = new DateTime[CONTEXT_BARS];
        private string _contextSignalId = "";

        // ── CSV column headers ────────────────────────────────────────────────
        private static readonly string CSV_HEADER =
              "Timestamp,Tag,Bar,Direction,Source,ConditionSetId,Score,Grade,Contracts," +
              "EntryPrice,StopPrice,StopTicks,T1Price,T2Price,RRRatio," +
              "ExitPrice,PnL,SessionPnL,GateReason,Label,Detail";

        // ── Current bar index ─────────────────────────────────────────────────
        // Updated by HostStrategy each primary bar so all WriteCsvRow calls
        // write the real bar number instead of 0.
        public int CurrentBar { get; set; } = 0;

        // Set by HostStrategy after construction. When non-null, LogTouchEvent
        // automatically registers the touch for forward-return tracking.
        public ConditionSets.ForwardReturnTracker Tracker { get; set; }

        public StrategyLogger(NinjaScriptBase host, LogLevel level = LogLevel.Normal)
        {
            _host = host;
            Level = level;
        }

        // =========================================================================
        // SESSION
        // =========================================================================

        public void SessionOpen(DateTime time, double vwap, double atr)
        {
            _sessionSignals = 0;
            _sessionFills = 0;
            _sessionFiltered = 0;
            _sessionPnL = 0;

            EnsureCsvOpen(time);

            if (Level >= LogLevel.Normal)
                Print("[SESSION] ── OPEN ── {0:yyyy-MM-dd HH:mm}  VWAP:{1:F2}  ATR:{2:F2}",
                    time, vwap, atr);

            WriteCsvRow(time, "SESSION", 0, "", "", "", 0, "", 0, 
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                "", "", $"OPEN VWAP={vwap:F2} ATR={atr:F2}");
        }

        public void SessionClose(DateTime time)
        {
            if (Level >= LogLevel.Normal)
                Print("[SESSION] ── CLOSE ── {0:HH:mm}  signals={1}  fills={2}  filtered={3}  sessionPnL={4:C2}",
                    time, _sessionSignals, _sessionFills, _sessionFiltered, _sessionPnL);

            WriteCsvRow(time, "SESSION", 0, "", "", "", 0, "", 0,
                0, 0, 0, 0, 0, 0, 0, 0, _sessionPnL,
                "", "", $"CLOSE signals={_sessionSignals} fills={_sessionFills} filtered={_sessionFiltered}");
        }

        // =========================================================================
        // FEED (Verbose only)
        // =========================================================================

        public void FeedBar(int bar, DateTime time, double close, double vwap,
            double sdHybrid, double atr, double atrTicks, SessionPhase session,
            bool orbComplete, double orbHigh, double orbLow)
        {
            if (Level < LogLevel.Verbose) return;

            Print("[FEED] bar={0} {1:HH:mm}  C:{2:F2}  VWAP:{3:F2}  SD:{4:F2}  ATR:{5:F2}({6:F1}t)  {7}  ORB:{8}",
                bar, time, close, vwap, sdHybrid, atr, atrTicks, session,
                orbComplete ? $"{orbLow:F2}-{orbHigh:F2}" : "pending");

            WriteCsvRow(time, "FEED", bar, "", "", "", 0, "", 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                "", "", $"C={close:F2} VWAP={vwap:F2} SD={sdHybrid:F2} ATR={atr:F2} {session}");
        }

        // =========================================================================
        // ORDERFLOW BAR LOG
        // =========================================================================

        /// <summary>
        /// Logs the full order flow state every primary bar so you can see
        /// exactly what the delta exhaustion and imbalance zone logic is reading,
        /// even on bars where no BOSWave signal fires.
        ///
        /// Written at LogLevel.Normal so it always appears in the CSV.
        /// Tag = FLOW. Read the Detail column — every field is labelled.
        ///
        /// Labels guide:
        ///   REGIME   — SMF flow regime: +1=bull -1=bear 0=flat
        ///   STR      — SMF flow strength 0.0–1.0 (>0.5 = meaningful flow)
        ///   BD       — Bar delta (AskVol−BidVol). Positive = buyers aggressive
        ///   CD       — Cumulative delta since session open
        ///   DSL      — Delta since low (buying at bar low = institutional bid)
        ///   DSH      — Delta since high (selling at bar high = institutional ask)
        ///   DEX      — Delta exhaustion: +1=bull exhausting(short confirm)
        ///                                -1=bear exhausting(long confirm) 0=none
        ///   BDIV     — Bull CVD divergence: price down but CVD up (1=yes)
        ///   BERDIV   — Bear CVD divergence: price up but CVD down (1=yes)
        ///   ABS      — Absorption score (>30=notable >50=strong)
        ///   SBULL    — Stacked bull imbalance count (>=3 = zone)
        ///   SBEAR    — Stacked bear imbalance count (>=3 = zone)
        ///   IZB      — Price at historical bull imbalance zone (1=yes)
        ///   IZS      — Price at historical bear imbalance zone (1=yes)
        ///   HASVOL   — Volumetric data active this bar (1=yes 0=fallback)
        ///   SW       — Confirmed swing count (need >=4 for Layer D)
        /// </summary>
        public void OrderFlowBar(DateTime barTime, MarketSnapshot snap)
        {
            if (!WriteCsv || _writer == null) return;

            // SMF state
            int    regime  = (int)snap.Get(SnapKeys.Regime);
            double str     = snap.Get(SnapKeys.Strength);

            // Order flow — bar level
            double bd      = snap.Get(SnapKeys.BarDelta);
            double cd      = snap.Get(SnapKeys.CumDelta);
            double dsl     = snap.Get(SnapKeys.VolDeltaSl);
            double dsh     = snap.Get(SnapKeys.VolDeltaSh);

            // Delta exhaustion
            int    dex     = (int)snap.Get(SnapKeys.DeltaExhaustion);

            // CVD divergence
            int    bdiv    = snap.GetFlag(SnapKeys.BullDivergence) ? 1 : 0;
            int    berdiv  = snap.GetFlag(SnapKeys.BearDivergence) ? 1 : 0;

            // Footprint
            double abs     = snap.Get(SnapKeys.AbsorptionScore);
            double sbull   = snap.Get(SnapKeys.StackedImbalanceBull);
            double sbear   = snap.Get(SnapKeys.StackedImbalanceBear);

            // Imbalance zones (historical)
            int    izb     = snap.GetFlag(SnapKeys.ImbalZoneAtBull) ? 1 : 0;
            int    izs     = snap.GetFlag(SnapKeys.ImbalZoneAtBear) ? 1 : 0;

            // Meta
            int    hasvol  = snap.GetFlag(SnapKeys.HasVolumetric) ? 1 : 0;
            int    sw      = (int)snap.Get(SnapKeys.ConfirmedSwings);

            // Structural producer outputs (StructuralLabeler)
            int    trd     = (int)snap.Get(SnapKeys.SwingTrend);
            int    chL     = (int)snap.Get(SnapKeys.CHoCHFiredLong);
            int    chS     = (int)snap.Get(SnapKeys.CHoCHFiredShort);
            int    bosL    = (int)snap.Get(SnapKeys.BOSFiredLong);
            int    bosS    = (int)snap.Get(SnapKeys.BOSFiredShort);

            // Build labelled detail string — every value named so the CSV is
            // self-documenting when opened in Excel
            string detail = string.Format(
                "REGIME={0} STR={1:F2} | BD={2:F0} CD={3:F0} DSL={4:F0} DSH={5:F0} | " +
                "DEX={6} BDIV={7} BERDIV={8} | " +
                "ABS={9:F1} SBULL={10:F0} SBEAR={11:F0} | " +
                "IZB={12} IZS={13} | " +
                "HASVOL={14} SW={15} | " +
                "TRD={16} CH_L={17} CH_S={18} BOS_L={19} BOS_S={20}",
                regime > 0 ? "+1" : regime < 0 ? "-1" : "0",
                str,
                bd, cd, dsl, dsh,
                dex > 0 ? "+1" : dex < 0 ? "-1" : "0",
                bdiv, berdiv,
                abs, sbull, sbear,
                izb, izs,
                hasvol, sw,
                trd > 0 ? "+1" : trd < 0 ? "-1" : "0",
                chL, chS, bosL, bosS);

            WriteCsvRow(barTime, "FLOW", CurrentBar,
                "", "", "",
                0, "", 0,
                0, 0, 0, 0, 0, 0,
                0, 0, 0,
                "", "", detail);
        }

        public void EvalNoSignal(int bar, DateTime time, string reason)
        {
            if (Level < LogLevel.Verbose) return;
            if (SuppressNoSignalBars && reason == "no_match") return;

            Print("[EVAL]  bar={0} {1:HH:mm}  → none  ({2})", bar, time, reason);
        }

        public void EvalDecision(DateTime barTime, RawDecision d,
            MarketSnapshot snap)
        {
            if (Level < LogLevel.Normal) return;
            _sessionSignals++;

            // Read actual bias values from snap bag
            double h4b = snap.Get(SnapKeys.H4HrEmaBias);
            double h2b = snap.Get(SnapKeys.H2HrEmaBias);
            double h1b = snap.Get(SnapKeys.H1EmaBias);
            int    reg = (int)snap.Get(SnapKeys.Regime);
            double str = snap.Get(SnapKeys.Strength);
            int    sw  = (int)snap.Get(SnapKeys.ConfirmedSwings);

            string h4s = h4b > 0 ? "+" : h4b < 0 ? "-" : "0";
            string h2s = h2b > 0 ? "+" : h2b < 0 ? "-" : "0";
            string h1s = h1b > 0 ? "+" : h1b < 0 ? "-" : "0";
            string regs = reg > 0 ? "bull" : reg < 0 ? "bear" : "flat";

            string detail = string.Format(
                "h4={0} h2={1} h1={2} smf={3} str={4:F2} sw={5}",
                h4s, h2s, h1s, regs, str, sw);

            Print("[EVAL]  {0:HH:mm}  {1,-6} {2,-20}  score={3,3}  " +
                  "entry={4:F2}  stop={5:F2}  t1={6:F2}  {7}  [{8}]",
                barTime, d.Direction, d.Source, d.RawScore,
                d.EntryPrice, d.StopPrice, d.TargetPrice,
                detail, d.Label);

            WriteCsvRow(barTime, "EVAL", CurrentBar,
                d.Direction.ToString(), d.Source.ToString(),
                d.ConditionSetId,
                d.RawScore, "", 0,
                d.EntryPrice, d.StopPrice, 0,
                d.TargetPrice, d.Target2Price, 0,
                0, 0, 0,
                "", d.Label, detail);
        }

        // =========================================================================
        // SIGNAL
        // =========================================================================

        public void SignalAccepted(SignalObject sig, MarketSnapshot snap)
        {
            if (Level < LogLevel.Normal) return;

            Print("[SIGNAL] ✓ ACCEPTED  {0:HH:mm}  {1,-6} {2,-20}  " +
                  "grade={3}  score={4}  contracts={5}  " +
                  "entry={6:F2}  stop={7:F2}({8:F1}t)  t1={9:F2}  t2={10:F2}  RR={11:F2}",
                sig.SignalTime, sig.Direction, sig.Source,
                sig.Grade, sig.Score, sig.Contracts,
                sig.EntryPrice, sig.StopPrice, sig.StopTicks,
                sig.Target1Price, sig.Target2Price, sig.RRRatio);

            WriteCsvRow(sig.SignalTime, "SIGNAL_ACCEPTED", CurrentBar,
                sig.Direction.ToString(), sig.Source.ToString(),
                sig.ConditionSetId,
                sig.Score, sig.Grade, sig.Contracts,
                sig.EntryPrice, sig.StopPrice, sig.StopTicks,
                sig.Target1Price, sig.Target2Price, sig.RRRatio,
                0, 0, 0,
                "", sig.Label, "");

            // ── Log 5 pre-signal bars as BAR_CONTEXT row ──────────────────
            // snap.Primary.Closes[0] = current bar close (signal bar)
            // snap.Primary.Closes[1] = 1 bar ago, [4] = 4 bars ago
            // Format per bar: O:N H:N L:N C:N V:N D:N  (pipe-separated bars)
            if (snap.IsValid)
            {
                var p   = snap.Primary;
                var psb = new System.Text.StringBuilder(256);
                int depth = (p.Closes != null) ? Math.Min(p.Closes.Length, CONTEXT_BARS + 1) : 0;

                for (int i = Math.Min(CONTEXT_BARS, depth - 1); i >= 0; i--)
                {
                    if (i < Math.Min(CONTEXT_BARS, depth - 1)) psb.Append('|');
                    // Time: reconstruct from signal time minus i bars
                    // Use HH:mm only — enough to read the chart
                    psb.AppendFormat("O:{0:F2} H:{1:F2} L:{2:F2} C:{3:F2} V:{4:F0}",
                        p.Opens  != null && i < p.Opens.Length  ? p.Opens[i]  : 0,
                        p.Highs  != null && i < p.Highs.Length  ? p.Highs[i]  : 0,
                        p.Lows   != null && i < p.Lows.Length   ? p.Lows[i]   : 0,
                        p.Closes != null && i < p.Closes.Length ? p.Closes[i] : 0,
                        p.Volumes!= null && i < p.Volumes.Length? p.Volumes[i]: 0);
                    if (p.BarDeltas != null && i < p.BarDeltas.Length && p.BarDeltas[i] != 0)
                        psb.AppendFormat(" D:{0:F0}", p.BarDeltas[i]);
                }

                WriteCsvRow(sig.SignalTime, "BAR_CONTEXT", CurrentBar,
                    sig.Direction.ToString(), sig.Source.ToString(), sig.ConditionSetId,
                    0, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    "PRE5", "", psb.ToString());
            }

            // ── Arm the forward bar capture ────────────────────────────────
            _contextSignalBar  = CurrentBar;
            _contextBarsLogged = 0;
            _contextSignalId   = sig.ConditionSetId ?? "";
        }

        /// <summary>
        /// Call once per primary bar from HostStrategy.OnBarUpdate.
        /// Captures the 5 bars after a signal fires and writes BAR_FORWARD row.
        /// No-op when no signal is pending forward capture.
        /// </summary>
        public void BarContext_Tick(MarketSnapshot snap, int currentBar)
        {
            if (_contextSignalBar < 0) return;
            if (!snap.IsValid) return;

            int barsAfter = currentBar - _contextSignalBar;
            if (barsAfter < 1 || barsAfter > CONTEXT_BARS) return;

            // Buffer this bar (barsAfter=1 is the first bar after signal)
            int idx = barsAfter - 1;
            var p = snap.Primary;
            _fwdT[idx] = p.Time;
            _fwdO[idx] = p.Opens  != null && p.Opens.Length  > 0 ? p.Opens[0]  : 0;
            _fwdH[idx] = p.Highs  != null && p.Highs.Length  > 0 ? p.Highs[0]  : 0;
            _fwdL[idx] = p.Lows   != null && p.Lows.Length   > 0 ? p.Lows[0]   : 0;
            _fwdC[idx] = p.Closes != null && p.Closes.Length > 0 ? p.Closes[0] : 0;
            _fwdV[idx] = p.Volumes!= null && p.Volumes.Length> 0 ? p.Volumes[0]: 0;
            _fwdD[idx] = p.BarDeltas != null && p.BarDeltas.Length > 0 ? p.BarDeltas[0] : 0;
            _contextBarsLogged++;

            // Once 5 bars collected, write the BAR_FORWARD row and reset
            if (_contextBarsLogged >= CONTEXT_BARS)
            {
                var fsb = new System.Text.StringBuilder(256);
                for (int i = 0; i < CONTEXT_BARS; i++)
                {
                    if (i > 0) fsb.Append('|');
                    fsb.AppendFormat("T:{0:HH:mm} O:{1:F2} H:{2:F2} L:{3:F2} C:{4:F2} V:{5:F0}",
                        _fwdT[i], _fwdO[i], _fwdH[i], _fwdL[i], _fwdC[i], _fwdV[i]);
                    if (_fwdD[i] != 0)
                        fsb.AppendFormat(" D:{0:F0}", _fwdD[i]);
                }

                WriteCsvRow(_fwdT[CONTEXT_BARS - 1], "BAR_FORWARD", currentBar,
                    "", _contextSignalId, _contextSignalId,
                    0, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    "POST5", "", fsb.ToString());

                _contextSignalBar  = -1;
                _contextBarsLogged = 0;
                _contextSignalId   = "";
            }
        }

        // =========================================================================
        // TOUCH LOG (strategy-agnostic)
        // =========================================================================
        // Tag = TOUCH. Written by any condition set at the moment a qualifying
        // touch is detected. Snapshots all relevant bus channels for post-hoc
        // analysis of what made it through vs. dropped. Paired with a
        // TOUCH_OUTCOME row written N bars later by ForwardReturnTracker.
        // =========================================================================

        /// <summary>
        /// Log a touch event with a frozen snapshot of all relevant bus channels.
        /// Called by condition sets at the moment of a tradeable touch detection.
        /// </summary>
        public void LogTouchEvent(
            string signalId,
            string conditionSetId,
            SignalDirection direction,
            double touchPrice,
            double zoneLow,
            double zoneHigh,
            double stopPrice,
            double targetPrice,
            string zoneType,
            DateTime touchTime,
            MarketSnapshot snap)
        {
            if (!WriteCsv || _writer == null) return;

            // Trend / structure
            int    trd     = (int)snap.Get(SnapKeys.SwingTrend);
            int    lastHL  = (int)snap.Get(SnapKeys.LastHighLabel);
            int    lastLL  = (int)snap.Get(SnapKeys.LastLowLabel);
            int    bosL    = (int)snap.Get(SnapKeys.BOSFiredLong);
            int    bosS    = (int)snap.Get(SnapKeys.BOSFiredShort);
            int    chL     = (int)snap.Get(SnapKeys.CHoCHFiredLong);
            int    chS     = (int)snap.Get(SnapKeys.CHoCHFiredShort);
            int    sw      = (int)snap.Get(SnapKeys.ConfirmedSwings);

            // FVG state
            int    fvgBL   = (int)snap.Get(SnapKeys.FvgBullActive);
            double fvgBLLo = snap.Get(SnapKeys.FvgBullLow);
            double fvgBLHi = snap.Get(SnapKeys.FvgBullHigh);
            int    fvgBR   = (int)snap.Get(SnapKeys.FvgBearActive);
            double fvgBRLo = snap.Get(SnapKeys.FvgBearLow);
            double fvgBRHi = snap.Get(SnapKeys.FvgBearHigh);

            // OB state
            int    obBL    = (int)snap.Get(SnapKeys.ObBullActive);
            double obBLLo  = snap.Get(SnapKeys.ObBullLow);
            double obBLHi  = snap.Get(SnapKeys.ObBullHigh);
            int    obBR    = (int)snap.Get(SnapKeys.ObBearActive);
            double obBRLo  = snap.Get(SnapKeys.ObBearLow);
            double obBRHi  = snap.Get(SnapKeys.ObBearHigh);

            // Footprint — bar-level
            double bd      = snap.Get(SnapKeys.BarDelta);
            double cd      = snap.Get(SnapKeys.CumDelta);
            double absScore= snap.Get(SnapKeys.AbsorptionScore);
            double sbull   = snap.Get(SnapKeys.StackedImbalanceBull);
            double sbear   = snap.Get(SnapKeys.StackedImbalanceBear);

            // Footprint — location
            double maxBidPx= snap.Get(SnapKeys.MaxBidVolPrice);
            double maxAskPx= snap.Get(SnapKeys.MaxAskVolPrice);
            double maxComPx= snap.Get(SnapKeys.MaxCombinedVolPrice);

            // Footprint imbalance zones
            int    izb     = snap.GetFlag(SnapKeys.ImbalZoneAtBull) ? 1 : 0;
            int    izs     = snap.GetFlag(SnapKeys.ImbalZoneAtBear) ? 1 : 0;

            // Volume profile
            double poc     = snap.Get(SnapKeys.POC);
            double vah     = snap.Get(SnapKeys.VAHigh);
            double val     = snap.Get(SnapKeys.VALow);

            // MTF EMA bias
            double h1Bias  = snap.Get(SnapKeys.H1EmaBias);
            double h2Bias  = snap.Get(SnapKeys.H2HrEmaBias);
            double h4Bias  = snap.Get(SnapKeys.H4HrEmaBias);

            // Divergence
            int    bdiv    = snap.GetFlag(SnapKeys.BullDivergence) ? 1 : 0;
            int    berdiv  = snap.GetFlag(SnapKeys.BearDivergence) ? 1 : 0;

            // Market state
            int    regime  = (int)snap.Get(SnapKeys.Regime);
            double atr     = snap.ATR;
            int    hasvol  = snap.GetFlag(SnapKeys.HasVolumetric) ? 1 : 0;

            string detail = string.Format(
                "ZONE_TYPE={0} ZONE_LO={1:F2} ZONE_HI={2:F2} TOUCH_PX={3:F2} STOP_PX={4:F2} TARGET_PX={5:F2} | " +
                "TRD={6} LAST_HL={7} LAST_LL={8} BOS_L={9} BOS_S={10} CH_L={11} CH_S={12} SW={13} | " +
                "FVG_BL={14} FVG_BL_LO={15:F2} FVG_BL_HI={16:F2} FVG_BR={17} FVG_BR_LO={18:F2} FVG_BR_HI={19:F2} | " +
                "OB_BL={20} OB_BL_LO={21:F2} OB_BL_HI={22:F2} OB_BR={23} OB_BR_LO={24:F2} OB_BR_HI={25:F2} | " +
                "BD={26:F0} CD={27:F0} ABS={28:F1} SBULL={29:F0} SBEAR={30:F0} | " +
                "MAX_BID_PX={31:F2} MAX_ASK_PX={32:F2} MAX_COM_PX={33:F2} | " +
                "IZB={34} IZS={35} | " +
                "POC={36:F2} VAH={37:F2} VAL={38:F2} | " +
                "H1B={39:F2} H2B={40:F2} H4B={41:F2} | " +
                "BDIV={42} BERDIV={43} | " +
                "REGIME={44} ATR={45:F2} HASVOL={46}",
                zoneType, zoneLow, zoneHigh, touchPrice, stopPrice, targetPrice,
                trd > 0 ? "+1" : trd < 0 ? "-1" : "0",
                lastHL, lastLL, bosL, bosS, chL, chS, sw,
                fvgBL, fvgBLLo, fvgBLHi, fvgBR, fvgBRLo, fvgBRHi,
                obBL, obBLLo, obBLHi, obBR, obBRLo, obBRHi,
                bd, cd, absScore, sbull, sbear,
                maxBidPx, maxAskPx, maxComPx,
                izb, izs,
                poc, vah, val,
                h1Bias, h2Bias, h4Bias,
                bdiv, berdiv,
                regime > 0 ? "+1" : regime < 0 ? "-1" : "0",
                atr, hasvol);

            string dirStr = direction == SignalDirection.Long ? "L" : "S";

            WriteCsvRow(touchTime, "TOUCH", CurrentBar,
                dirStr, "", conditionSetId,
                0, "", 0,
                touchPrice, 0, 0, 0, 0, 0,
                0, 0, 0,
                signalId, zoneType, detail);

            // Auto-register with forward-return tracker (if wired).
            // Strategy-agnostic — any condition set that logs a touch with
            // valid stop/target gets outcome tracking.
            Tracker?.Register(signalId, conditionSetId, direction,
                              touchPrice, stopPrice, targetPrice,
                              CurrentBar, touchTime);
        }

        // =========================================================================
        // TOUCH OUTCOME LOG (forward-return data)
        // =========================================================================
        // Tag = TOUCH_OUTCOME. Written by ForwardReturnTracker N bars after a
        // TOUCH event. Contains simulated forward-return data: MFE, MAE, whether
        // stop/target would have been hit, hypothetical PnL, etc.
        // Joins to TOUCH rows via SignalId.
        // =========================================================================

        /// <summary>
        /// Log the forward-return outcome of a touch event N bars after it occurred.
        /// </summary>
        public void LogTouchOutcome(
            string signalId,
            string conditionSetId,
            SignalDirection direction,
            double mfeDollars,
            double maeDollars,
            int hitStop,
            int hitTarget,
            string firstHit,
            double simPnL,
            int barsToFirstHit,
            double closeAtWindowEnd,
            int windowBars,
            DateTime outcomeTime)
        {
            if (!WriteCsv || _writer == null) return;

            string detail = string.Format(
                "MFE={0:F2} MAE={1:F2} HIT_STOP={2} HIT_TARGET={3} " +
                "FIRST_HIT={4} SIM_PNL={5:F2} BARS_TO_HIT={6} " +
                "CLOSE_END={7:F2} WINDOW_BARS={8}",
                mfeDollars, maeDollars, hitStop, hitTarget,
                firstHit, simPnL, barsToFirstHit,
                closeAtWindowEnd, windowBars);

            string dirStr = direction == SignalDirection.Long ? "L" : "S";

            WriteCsvRow(outcomeTime, "TOUCH_OUTCOME", CurrentBar,
                dirStr, "", conditionSetId,
                0, "", 0,
                simPnL, 0, 0, 0, 0, 0,
                0, 0, 0,
                signalId, firstHit, detail);
        }

        public void SignalRejected(DateTime barTime, SignalSource source,
            SignalDirection dir, int score, string gateReason,
             string conditionSetId = "")
        {
            if (Level < LogLevel.Normal) return;
            _sessionFiltered++;

            Print("[SIGNAL] ✗ REJECTED  {0:HH:mm}  {1,-6} {2,-20}  score={3}  gate={4}",
                barTime, dir, source, score, gateReason);

            WriteCsvRow(barTime, "SIGNAL_REJECTED", 0,
                dir.ToString(), source.ToString(),
                conditionSetId,
                score, "", 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                gateReason, "", "");
        }

        public void SignalBlocked(DateTime barTime, string reason)
        {
            if (Level < LogLevel.Normal) return;

            Print("[SIGNAL] ⊘ BLOCKED   {0:HH:mm}  {1}", barTime, reason);

            WriteCsvRow(barTime, "SIGNAL_BLOCKED", 0,
                "", "", "", 0, "", 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                reason, "", "");
        }

        // =========================================================================
        // ORDER
        // =========================================================================

        public void OrderSubmitted(SignalObject sig, bool isMarketFallback = false)
        {
            if (Level < LogLevel.Normal) return;

            string type = isMarketFallback ? "MARKET(fallback)" : "LIMIT";
            Print("[ORDER]  ▶ SUBMIT    {0:HH:mm}  {1,-6}  {2}  qty={3}  " +
                  "at={4:F2}  stop={5:F2}  t1={6:F2}",
                sig.SignalTime, sig.Direction, type, sig.Contracts,
                sig.EntryPrice, sig.StopPrice, sig.Target1Price);

            WriteCsvRow(sig.SignalTime, isMarketFallback ? "ORDER_MKT" : "ORDER_LMT", 0,
                sig.Direction.ToString(), sig.Source.ToString(),
                sig.ConditionSetId,
                sig.Score, sig.Grade, sig.Contracts,
                sig.EntryPrice, sig.StopPrice, sig.StopTicks,
                sig.Target1Price, sig.Target2Price, sig.RRRatio,
                0, 0, 0,
                "", sig.Label, type);
        }

        public void OrderFilled(DateTime barTime, string name, SignalDirection dir,
            int qty, double fillPrice, bool isEntry)
        {
            if (Level < LogLevel.Normal) return;
            if (isEntry) _sessionFills++;

            string tag = isEntry ? "ENTRY_FILL" : "EXIT_FILL";
            Print("[ORDER]  ● {0}  {1:HH:mm}  {2,-6}  qty={3}  @{4:F2}  name={5}",
                tag, barTime, dir, qty, fillPrice, name);

            WriteCsvRow(barTime, tag, 0,
                dir.ToString(), "", "", 0, "", qty,
                isEntry ? fillPrice : 0,
                0, 0, 0, 0, 0,
                isEntry ? 0 : fillPrice,
                0, 0,
                "", name, "");
        }

        public void StopUpdated(DateTime barTime, double oldStop, double newStop, double tickSize, bool afterT1)
        {
            if (Level < LogLevel.Normal) return;

            double moveTicks = Math.Abs(newStop - oldStop) / tickSize;
            Print("[ORDER]  ↑ STOP MOVE  {0:HH:mm}  {1:F2} → {2:F2}  ({3:+F1}t)  afterT1={4}",
                barTime, oldStop, newStop, moveTicks, afterT1);

            WriteCsvRow(barTime, "STOP_MOVE", 0,
                "", "", "", 0, "", 0,
                0, newStop, moveTicks, 0, 0, 0, 0, 0, 0,
                "", "", $"old={oldStop:F2} afterT1={afterT1}");
        }

        public void T1Hit(DateTime barTime, double price, int contractsExited, int contractsRemaining)
        {
            if (Level < LogLevel.Normal) return;

            Print("[ORDER]  ★ T1 HIT    {0:HH:mm}  @{1:F2}  exited={2}  remaining={3}",
                barTime, price, contractsExited, contractsRemaining);

            WriteCsvRow(barTime, "T1_HIT", 0,
                "", "", "", 0, "", contractsExited,
                0, 0, 0, price, 0, 0, price, 0, 0,
                "", "", $"remaining={contractsRemaining}");
        }

        public void T2Hit(DateTime barTime, double price)
        {
            if (Level < LogLevel.Normal) return;

            Print("[ORDER]  ★ T2 HIT    {0:HH:mm}  @{1:F2}  full exit", barTime, price);

            WriteCsvRow(barTime, "T2_HIT", 0,
                "", "", "", 0, "", 0,
                0, 0, 0, 0, price, 0, price, 0, 0,
                "", "", "full_exit");
        }

        // =========================================================================
        // FILL — trade close summary
        // =========================================================================

        public void PositionClosed(DateTime barTime, SignalObject sig, double exitPrice,
            double pnl, string exitReason)
        {
            if (Level < LogLevel.Normal) return;
            _sessionPnL += pnl;

            string marker = pnl >= 0 ? "WIN " : "LOSS";
            Print("[FILL]   {0}  {1:HH:mm}  {2,-6}  qty={3}  " +
                  "entry={4:F2}  exit={5:F2}  via={6}  pnl={7:C2}  sessionPnL={8:C2}",
                marker, barTime, sig.Direction, sig.Contracts,
                sig.EntryPrice, exitPrice, exitReason, pnl, _sessionPnL);

            WriteCsvRow(barTime, pnl >= 0 ? "TRADE_WIN" : "TRADE_LOSS", 0,
                sig.Direction.ToString(), sig.Source.ToString(),
                sig.ConditionSetId,
                sig.Score, sig.Grade, sig.Contracts,
                sig.EntryPrice, sig.StopPrice, sig.StopTicks,
                sig.Target1Price, sig.Target2Price, sig.RRRatio,
                exitPrice, pnl, _sessionPnL,
                exitReason, sig.Label, "");
        }

        // =========================================================================
        // STRUCTURAL / ZONES
        // =========================================================================

        /// <summary>
        /// Logs the full state of the SupportResistanceEngine each bar.
        /// Tag = STRUCT.
        /// </summary>
        public void StructuralBar(DateTime barTime, in SupportResistanceResult sr)
        {
            if (!WriteCsv || _writer == null || !sr.IsValid) return;

            string detail = string.Format(
                "POC={0:F2} VAH={1:F2} VAL={2:F2} | NEAR_SUP={3:F2}({4:F1}t) NEAR_RES={5:F2}({6:F1}t) | " +
                "H4={7:F2}/{8:F2} H1={9:F2}/{10:F2} | PP={11:F2} SKEW={12:F2}",
                sr.POC, sr.VAHigh, sr.VALow,
                sr.NearestSupport.ZonePrice, sr.TicksToSupport,
                sr.NearestResistance.ZonePrice, sr.TicksToResistance,
                sr.SwingHighH4, sr.SwingLowH4,
                sr.SwingHighH1, sr.SwingLowH1,
                sr.PivotPP, sr.POCSkew);

            WriteCsvRow(barTime, "STRUCT", CurrentBar,
                "", "", "",
                0, "", 0,
                0, 0, 0, 0, 0, 0,
                0, 0, 0,
                "", "", detail);
        }

        public void ImbalZoneCreated(DateTime time, bool isBull, double low, double high, int bar)
        {
            if (Level < LogLevel.Verbose) return;

            string side = isBull ? "BULL" : "BEAR";
            Print("[ZONE]   + NEW {0}   {1:HH:mm}  {2:F2}-{3:F2}  bar={4}",
                side, time, low, high, bar);

            WriteCsvRow(time, "ZONE_CREATE", bar,
                isBull ? "Long" : "Short", "Footprint", "",
                0, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                "", side, $"{low:F2}-{high:F2}");
        }

        public void ImbalZoneMitigated(DateTime time, bool isBull, double low, double high, double close)
        {
            if (Level < LogLevel.Normal) return;

            string side = isBull ? "BULL" : "BEAR";
            Print("[ZONE]   - MITIGATED {0} {1:HH:mm}  {2:F2}-{3:F2}  close={4:F2}",
                side, time, low, high, close);

            WriteCsvRow(time, "ZONE_MITIGATED", 0,
                isBull ? "Long" : "Short", "PriceAction", "",
                0, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                "mitigated", side, $"close={close:F2} zone={low:F2}-{high:F2}");
        }

        // =========================================================================
        // TRADE ADVISOR
        // =========================================================================

        public void TA_Decision(DateTime time, string sid, string action, int severity, string reason, string diagnostics)
        {
            if (Level < LogLevel.Normal) return;

            Print("[TA]     {0}  sid={1}  sev={2}  reason={3}  {4}",
                action, sid, severity, reason, diagnostics);

            WriteCsvRow(time, "TA_DECISION", 0,
                "", "", sid,
                0, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                action, reason, $"sev={severity} {diagnostics}");
        }

        public void ResetTrade(DateTime time, SignalObject sig, double fill, double stop, int rem, bool t1, double mfe)
        {
            if (Level < LogLevel.Normal) return;

            string detail = string.Format("fill={0:F2} stop={1:F2} rem={2} t1={3} mfe={4:F1}",
                fill, stop, rem, t1 ? 1 : 0, mfe);

            Print("[ORDER]  ↻ RESET     {0:HH:mm}  sid={1}  {2}",
                time, sig != null ? sig.SignalId : "NA", detail);

            WriteCsvRow(time, "TRADE_RESET", 0,
                sig != null ? sig.Direction.ToString() : "",
                sig != null ? sig.Source.ToString() : "",
                sig != null ? sig.ConditionSetId : "",
                0, "", rem,
                fill, stop, 0, 0, 0, 0, 0, 0, 0,
                "", "", detail);
        }

        // =========================================================================
        // RISK
        // =========================================================================

        public void RiskCircuitBreaker(DateTime barTime, string reason, double dailyPnL, double limit)
        {
            Print("[RISK]   ⛔ CIRCUIT BREAKER  {0:HH:mm}  reason={1}  dailyPnL={2:C2}  limit={3:C2}",
                barTime, reason, dailyPnL, limit);

            WriteCsvRow(barTime, "RISK_CIRCUIT_BREAKER", 0,
                "", "", "", 0, "", 0,
                0, 0, 0, 0, 0, 0, 0, dailyPnL, 0,
                reason, "", $"limit={limit:C2}");
        }

        public void RiskConsecutiveLoss(DateTime barTime, int count, int max)
        {
            if (count < 2) return;  // only log when approaching limit

            Print("[RISK]   ⚠ CONSEC LOSS  {0:HH:mm}  count={1}/{2}", barTime, count, max);

            WriteCsvRow(barTime, "RISK_CONSEC_LOSS", 0,
                "", "", "", 0, "", 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                "", "", $"count={count} max={max}");
        }

        public void Warn(DateTime barTime, string message, params object[] args)
        {
            // Prepend barTime to args array without LINQ
            var printArgs = new object[args.Length + 1];
            printArgs[0] = barTime;
            args.CopyTo(printArgs, 1);
            Print("[WARN]   {0:HH:mm}  " + message, printArgs);

            WriteCsvRow(barTime, "WARN", 0,
                "", "", "", 0, "", 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                "", "", string.Format(message, args));
        }

        // =========================================================================
        // CSV FILE MANAGEMENT
        // =========================================================================

        private void EnsureCsvOpen(DateTime date)
        {
            if (!WriteCsv) return;

            // File is unique per run (stamped at construction) — only open once
            if (_writer != null) return;

            CloseCsv();

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "log");

                Directory.CreateDirectory(dir);

                string fileName = $"ModularStrategy_{_runStamp}.csv";
                _currentFilePath = Path.Combine(dir, fileName);
                _currentFileDate = date;

                bool fileExists = File.Exists(_currentFilePath);
                _writer = new StreamWriter(_currentFilePath, append: true, encoding: Encoding.UTF8);
                _writer.AutoFlush = true;

                // Write header only if file is new
                if (!fileExists)
                    _writer.WriteLine(CSV_HEADER);

                Print("[SESSION] CSV log: {0}", _currentFilePath);
            }
            catch (Exception ex)
            {
                Print("[WARN]   CSV open failed: {0}", ex.Message);
                _writer = null;
            }
        }

        private void CloseCsv()
        {
            try { _writer?.Flush(); _writer?.Dispose(); }
            catch { }
            _writer = null;
        }

        private void WriteCsvRow(
            DateTime time, string tag, int bar,
            string direction, string source, string conditionSetId,
            int score, string grade, int contracts,
            double entryPrice, double stopPrice, double stopTicks,
            double t1Price, double t2Price, double rrRatio,
            double exitPrice, double pnl, double sessionPnL,
            string gateReason, string label, string detail)
        {
            if (!WriteCsv || _writer == null) return;

            try
            {
                _sb.Clear();
                // Timestamp
                _sb.Append(time.ToString("yyyy-MM-dd HH:mm:ss")); _sb.Append(',');
                // Tag
                _sb.Append(tag); _sb.Append(',');
                // Bar
                _sb.Append(bar); _sb.Append(',');
                // Direction, Source
                _sb.Append(direction); _sb.Append(',');
                _sb.Append(source); _sb.Append(',');
                _sb.Append(conditionSetId); _sb.Append(',');
                // Score, Grade, Contracts
                _sb.Append(score > 0 ? score.ToString() : ""); _sb.Append(',');
                _sb.Append(grade); _sb.Append(',');
                _sb.Append(contracts > 0 ? contracts.ToString() : ""); _sb.Append(',');
                // Prices
                _sb.Append(entryPrice > 0 ? entryPrice.ToString("F2") : ""); _sb.Append(',');
                _sb.Append(stopPrice > 0 ? stopPrice.ToString("F2") : ""); _sb.Append(',');
                _sb.Append(stopTicks > 0 ? stopTicks.ToString("F1") : ""); _sb.Append(',');
                _sb.Append(t1Price > 0 ? t1Price.ToString("F2") : ""); _sb.Append(',');
                _sb.Append(t2Price > 0 ? t2Price.ToString("F2") : ""); _sb.Append(',');
                _sb.Append(rrRatio > 0 ? rrRatio.ToString("F2") : ""); _sb.Append(',');
                _sb.Append(exitPrice > 0 ? exitPrice.ToString("F2") : ""); _sb.Append(',');
                // PnL columns
                _sb.Append(pnl != 0 ? pnl.ToString("F2") : ""); _sb.Append(',');
                _sb.Append(sessionPnL != 0 ? sessionPnL.ToString("F2") : ""); _sb.Append(',');
                // Text columns — wrap in quotes to handle commas
                _sb.Append(CsvQuote(gateReason)); _sb.Append(',');
                _sb.Append(CsvQuote(label)); _sb.Append(',');
                _sb.Append(CsvQuote(detail));

                _writer.WriteLine(_sb.ToString());
            }
            catch (Exception ex)
            {
                try { Print("[WARN]   CSV write error: {0}", ex.Message); } catch { }
            }
        }

        private static string CsvQuote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        // =========================================================================
        // PRINT HELPER
        // =========================================================================

        private void Print(string fmt, params object[] args)
        {
            try
            {
                _sb.Clear();
                _sb.AppendFormat(fmt, args);
                _host.Print(_sb.ToString());
            }
            catch
            {
                try { _host.Print("[WARN] Logger format error: " + fmt); } catch { }
            }
        }

        // =========================================================================
        // DISPOSE
        // =========================================================================

        public void Dispose()
        {
            CloseCsv();
        }
    }
}
