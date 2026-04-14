#region Using declarations
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Strategies;
using MathLogic.Strategy;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ========================================================================
    // STRATEGY LOGGER
    // ========================================================================
    // High-performance async-ready logger.
    // Handles CSV audit trail and NinjaTrader Output window.
    // ========================================================================
    public sealed class StrategyLogger : IDisposable
    {
        public enum LogLevel { None = 0, Normal = 1, Verbose = 2, Debug = 3 }

        public LogLevel Level { get; set; } = LogLevel.Normal;
        public bool     WriteCsv { get; set; } = true;
        public bool     SuppressNoSignalBars { get; set; } = true;

        private readonly Strategy _host;
        private readonly string   _filePath;
        private StreamWriter      _writer;
        private readonly StringBuilder _sb = new StringBuilder(1024);

        // Session stats
        private int _sessionTrades;
        private int _sessionSignals;
        private int _sessionFiltered;

        // Forward bar context state
        private const int CONTEXT_BARS = 5;
        private int       _contextSignalBar  = -1;
        private int       _contextBarsLogged = 0;
        private string    _contextSignalId   = "";

        // Context buffers
        private readonly DateTime[] _fwdT = new DateTime[CONTEXT_BARS];
        private readonly double[]   _fwdO = new double[CONTEXT_BARS];
        private readonly double[]   _fwdH = new double[CONTEXT_BARS];
        private readonly double[]   _fwdL = new double[CONTEXT_BARS];
        private readonly double[]   _fwdC = new double[CONTEXT_BARS];
        private readonly double[]   _fwdV = new double[CONTEXT_BARS];
        private readonly double[]   _fwdD = new double[CONTEXT_BARS];

        public int CurrentBar => _host != null ? _host.CurrentBar : 0;

        public StrategyLogger(Strategy host, string subFolder = "")
        {
            _host = host;
            string folder = Path.Combine(
                NinjaTrader.Core.Globals.UserDataDir,
                "logs", "ModularStrategy", subFolder);

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string filename = string.Format("Log_{0}_{1:yyyyMMdd_HHmm}.csv",
                _host.Name, DateTime.Now);
            _filePath = Path.Combine(folder, filename);

            if (WriteCsv)
            {
                try
                {
                    _writer = new StreamWriter(_filePath, false, Encoding.UTF8);
                    _writer.AutoFlush = true;
                    // Header
                    _writer.WriteLine("Time,Tag,Bar,Direction,Source,ConditionSetId,Score,Grade,Contracts,EntryPrice,StopPrice,StopTicks,T1Price,T2Price,RR,ExitPrice,PnL,SessionPnL,GateReason,Label,Detail");
                }
                catch (Exception ex)
                {
                    _host.Print("ERROR: Could not initialize CSV logger: " + ex.Message);
                    WriteCsv = false;
                }
            }
        }

        public void Dispose()
        {
            if (_writer != null)
            {
                _writer.Flush();
                _writer.Close();
                _writer = null;
            }
        }

        private void Print(string format, params object[] args)
        {
            if (_host != null)
                _host.Print(string.Format(format, args));
        }

        // =========================================================================
        // LIFECYCLE
        // =========================================================================

        public void SessionOpen(DateTime time, double vwap, double atr)
        {
            Print("[SESSION] OPEN {0:HH:mm} vwap={1:F2} atr={2:F2}", time, vwap, atr);
            WriteCsvRow(time, "SESSION_OPEN", CurrentBar, "", "", "", 0, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", "", string.Format("vwap={0:F2} atr={1:F2}", vwap, atr));
        }

        public void SessionSummary(DateTime time, double totalPnL, int count)
        {
            Print("[SESSION] END  {0:HH:mm} trades={1} pnl={2:F2}", time, count, totalPnL);
            WriteCsvRow(time, "SESSION_SUMMARY", CurrentBar, "", "", "", 0, "", 0, 0, 0, 0, 0, 0, 0, 0, totalPnL, 0, "", "", string.Format("trades={0}", count));
        }

        // =========================================================================
        // MARKET STATE
        // =========================================================================

        /// <summary>
        /// Logs high-resolution snapshot of market bus channels.
        /// Use sparingly (e.g. once per minute) or only on setups to avoid massive CSV bloat.
        /// </summary>
        public void LogMarketSnapshot(DateTime time, MarketSnapshot snap)
        {
            if (Level < LogLevel.Verbose) return;
            if (!snap.IsValid) return;

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
                "REGIME={0} STR={1:F2} | BD={2:F0} CD={3:F0} DSL={4:F0} DSH={5:F0} | " +
                "DEX={6} BDIV={7} BERDIV={8} | ABS={9:F1} SBULL={10:F0} SBEAR={11:F0} | " +
                "IZB={12} IZS={13} | HASVOL={14} SW={15} | " +
                "TRD={16} CH_L={17} CH_S={18} BOS_L={19} BOS_S={20} | " +
                "FVG_BL={21} FVG_BR={22} OB_BL={23} OB_BR={24} MAX_PX={25:F2}",
                regime > 0 ? "+1" : regime < 0 ? "-1" : "0",
                snap.Get(SnapKeys.Strength),
                bd, cd, snap.Get(SnapKeys.VolDeltaSl), snap.Get(SnapKeys.VolDeltaSh),
                (int)snap.Get(SnapKeys.DeltaExhaustion),
                bdiv, berdiv,
                absScore, sbull, sbear,
                izb, izs,
                hasvol, sw,
                trd > 0 ? "+1" : trd < 0 ? "-1" : "0",
                chL, chS, bosL, bosS,
                fvgBL, fvgBR, obBL, obBR,
                maxComPx);

            WriteCsvRow(time, "FLOW", CurrentBar, "", "", "", 0, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", "", detail);

            // Struct row
            string structDetail = string.Format(
                "POC={0:F2} VAH={1:F2} VAL={2:F2} | " +
                "NEAR_SUP={3:F2}({4:F1}t) NEAR_RES={5:F2}({6:F1}t) | " +
                "H4={7:F2}/{8:F2} H1={9:F2}/{10:F2} | PP={11:F2} SKEW={12:F2}",
                poc, vah, val,
                snap.Get(SnapKeys.NearSupportPrice), snap.Get(SnapKeys.NearSupportTicks),
                snap.Get(SnapKeys.NearResistancePrice), snap.Get(SnapKeys.NearResistanceTicks),
                snap.Higher4.Open, snap.Higher4.Close,
                snap.Higher1.Open, snap.Higher1.Close,
                snap.Get(SnapKeys.PrevPivot),
                snap.Get(SnapKeys.SessionSkew));

            WriteCsvRow(time, "STRUCT", CurrentBar, "", "", "", 0, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", "", structDetail);
        }

        // =========================================================================
        // TOUCH LOG
        // =========================================================================
        // Each row freezes the full snapshot state at touch time so we can
        // measure which bus channels predict trade outcomes. Rows are joinable
        // to PositionClosed rows via SignalId.
        //
        // Read the Detail column — every field is labelled.
        // =========================================================================

        /// <summary>
        /// Log a touch event with a frozen snapshot of all relevant bus channels.
        /// Called by condition sets at the moment of a tradeable touch detection.
        /// </summary>
        /// <param name="signalId">Signal ID — used to join to PositionClosed rows in post-processing.</param>
        /// <param name="conditionSetId">Which condition set fired this touch.</param>
        /// <param name="direction">Long or short.</param>
        /// <param name="touchPrice">Price at the touch bar (typically Close[0]).</param>
        /// <param name="zoneLow">Lower bound of the zone being touched.</param>
        /// <param name="zoneHigh">Upper bound of the zone being touched.</param>
        /// <param name="stopPrice">Stop price if entered at this touch.</param>
        /// <param name="targetPrice">Target price if entered at this touch.</param>
        /// <param name="zoneType">Free-form label for the zone, e.g. "ORB_RELOAD", "FVG_BULL", "OB_BEAR".</param>
        /// <param name="touchTime">Bar time of the touch.</param>
        /// <param name="snap">Full market snapshot — all bus channels are read and frozen here.</param>
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
        }

        // =========================================================================
        // TOUCH OUTCOME LOG (forward-return data)
        // =========================================================================
        //
        // Tag = TOUCH_OUTCOME. Written by ForwardReturnTracker N bars after a 
        // TOUCH event. Contains simulated forward-return data: MFE, MAE, whether 
        // stop/target would have been hit, hypothetical PnL, etc.
        //
        // Joins to TOUCH rows via SignalId. Strategy-agnostic — any condition 
        // set's touches get tracked automatically once registered with the 
        // ForwardReturnTracker.
        // =========================================================================

        /// <summary>
        /// Log the forward-return outcome of a touch event N bars after it occurred.
        /// Joinable to the original TOUCH row via signalId.
        /// </summary>
        public void LogTouchOutcome(
            string signalId,
            string conditionSetId,
            SignalDirection direction,
            double mfeDollars,          // max favorable excursion
            double maeDollars,          // max adverse excursion
            int hitStop,                // 1 if stop would have been hit
            int hitTarget,              // 1 if target would have been hit
            string firstHit,            // "STOP" | "TARGET" | "NEITHER" | "BOTH_SAMEBAR"
            double simPnL,              // simulated PnL (dollars)
            int barsToFirstHit,         // bars until whichever hit first, 0 if neither
            double closeAtWindowEnd,    // close price at end of simulation window
            int windowBars,             // actual bars tracked (may be < default if session closed)
            DateTime outcomeTime)       // bar time when outcome was finalized
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

        public void OrderSubmitted(SignalObject sig, bool isMarketFallback = false)
        {
            if (Level < LogLevel.Normal) return;

            Print("[ORDER]  ↑ SUBMITTED {0:HH:mm}  {1,-6} {2,-20}  " +
                  "entry={3:F2}  stop={4:F2}  t1={5:F2}  t2={6:F2}  RR={7:F2}{8}",
                sig.SignalTime, sig.Direction, sig.Source,
                sig.EntryPrice, sig.StopPrice, sig.Target1Price, sig.Target2Price,
                sig.RRRatio, isMarketFallback ? " (MARKET)" : "");

            WriteCsvRow(sig.SignalTime, "ORDER_SUBMITTED", CurrentBar,
                sig.Direction.ToString(), sig.Source.ToString(),
                sig.ConditionSetId,
                sig.Score, sig.Grade, sig.Contracts,
                sig.EntryPrice, sig.StopPrice, sig.StopTicks,
                sig.Target1Price, sig.Target2Price, sig.RRRatio,
                0, 0, 0,
                isMarketFallback ? "MARKET_FALLBACK" : "", sig.Label, "");
        }

        public void OrderFilled(DateTime barTime, string orderName, SignalDirection direction,
            int quantity, double fillPrice, bool isEntry)
        {
            if (Level < LogLevel.Normal) return;

            string type = isEntry ? "ENTRY" : "EXIT";
            Print("[ORDER]  ✓ FILLED    {0:HH:mm}  {1,-5}  {2} @ {3:F2}  (q={4})",
                barTime, type, direction, fillPrice, quantity);

            string tag = isEntry ? "ENTRY_FILL" : "EXIT_FILL";
            WriteCsvRow(barTime, tag, CurrentBar,
                direction.ToString(), "", "",
                0, "", quantity,
                fillPrice, 0, 0, 0, 0, 0,
                fillPrice, 0, 0,
                orderName, "", "");
        }

        public void PositionClosed(DateTime barTime, SignalObject sig, double exitPrice,
            double pnl, string exitReason)
        {
            if (Level < LogLevel.Normal) return;
            _sessionTrades++;

            Print("[TRADE]  ⚐ CLOSED    {0:HH:mm}  {1,-6} {2,-20}  " +
                  "exit={3:F2}  pnl={4:F2}  reason={5}",
                barTime, sig.Direction, sig.Source, exitPrice, pnl, exitReason);

            string outcome = pnl > 0 ? "TRADE_WIN" : pnl < 0 ? "TRADE_LOSS" : "TRADE_FLAT";

            WriteCsvRow(barTime, outcome, CurrentBar,
                sig.Direction.ToString(), sig.Source.ToString(),
                sig.ConditionSetId,
                sig.Score, sig.Grade, sig.Contracts,
                sig.EntryPrice, sig.StopPrice, sig.StopTicks,
                sig.Target1Price, sig.Target2Price, sig.RRRatio,
                exitPrice, pnl, 0,
                exitReason, sig.Label, "");
        }

        public void PositionReset(DateTime barTime, string reason)
        {
            if (Level < LogLevel.Normal) return;

            Print("[TRADE]  ⚠ RESET     {0:HH:mm}  {1}", barTime, reason);

            WriteCsvRow(barTime, "TRADE_RESET", CurrentBar,
                "", "", "", 0, "", 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                reason, "", "");
        }

        public void LogMitigation(bool isBull, DateTime originalTime, double low, double high, double close)
        {
            if (Level < LogLevel.Normal) return;

            string side = isBull ? "BULL" : "BEAR";
            Print("[ZONE]   - MITIGATED {0} {1:HH:mm}  {2:F2}-{3:F2}  close={4:F2}",
                side, originalTime, low, high, close);

            WriteCsvRow(_host.Time[0], "ZONE_MITIGATED", CurrentBar,
                side, "", "", 0, "", 0,
                close, 0, 0, 0, 0, 0,
                0, 0, 0,
                "", string.Format("{0:yyyyMMdd_HHmm}", originalTime),
                string.Format("LO={0:F2} HI={1:F2}", low, high));
        }

        public void Warn(DateTime time, string format, params object[] args)
        {
            string msg = string.Format(format, args);
            Print("[WARN]   {0:HH:mm}  {1}", time, msg);
            WriteCsvRow(time, "WARN", CurrentBar, "", "", "", 0, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", "", msg);
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
                // Tag / Metadata
                _sb.Append(tag); _sb.Append(',');
                _sb.Append(bar); _sb.Append(',');
                _sb.Append(direction); _sb.Append(',');
                _sb.Append(source); _sb.Append(',');
                _sb.Append(conditionSetId); _sb.Append(',');
                // Scoring
                _sb.Append(score); _sb.Append(',');
                _sb.Append(grade); _sb.Append(',');
                _sb.Append(contracts); _sb.Append(',');
                // Prices
                _sb.Append(entryPrice.ToString("F2")); _sb.Append(',');
                _sb.Append(stopPrice.ToString("F2")); _sb.Append(',');
                _sb.Append(stopTicks.ToString("F1")); _sb.Append(',');
                _sb.Append(t1Price.ToString("F2")); _sb.Append(',');
                _sb.Append(t2Price.ToString("F2")); _sb.Append(',');
                _sb.Append(rrRatio.ToString("F2")); _sb.Append(',');
                // PnL
                _sb.Append(exitPrice.ToString("F2")); _sb.Append(',');
                _sb.Append(pnl.ToString("F2")); _sb.Append(',');
                _sb.Append(sessionPnL.ToString("F2")); _sb.Append(',');
                // Logic trace
                _sb.Append(gateReason); _sb.Append(',');
                _sb.Append(label); _sb.Append(',');
                _sb.Append(detail.Replace(',', ';')); // protect CSV structure

                _writer.WriteLine(_sb.ToString());
            }
            catch { }
        }
    }
}
