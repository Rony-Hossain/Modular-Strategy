#region Using declarations
using System;
#endregion

namespace MathLogic
{
    // ===================================================================
    // SMC ENUMS — outside class per NinjaScript best practices
    // ===================================================================

    /// <summary>
    /// Swing point type — high or low pivot.
    /// </summary>
    public enum SwingType { None = 0, High = 1, Low = 2 }

    /// <summary>
    /// Order Block type.
    /// Bullish OB = last BEARISH candle before a bullish impulse / BOS.
    /// Bearish OB = last BULLISH candle before a bearish impulse / BOS.
    /// </summary>
    public enum OBType { None = 0, Bullish = 1, Bearish = 2 }

    /// <summary>
    /// Breaker Block type — an Order Block that has been fully violated and
    /// now acts in the opposite role (former support becomes resistance, vice versa).
    /// BullishBreaker = former Bearish OB broken above — now acts as support on retest.
    /// BearishBreaker = former Bullish OB broken below — now acts as resistance on retest.
    /// </summary>
    public enum BreakerType { None = 0, BullishBreaker = 1, BearishBreaker = 2 }

    /// <summary>
    /// Support / Resistance level strength based on confirmed touch count.
    /// </summary>
    public enum SRStrength { Weak = 0, Moderate = 1, Strong = 2 }

    // ===================================================================
    // RESULT OBJECTS — Immutable sealed classes, matching library convention
    // ===================================================================

    /// <summary>
    /// A confirmed swing point with its structural label and location.
    /// </summary>
    public sealed class SwingPoint
    {
        public SwingType  Type        { get; }
        public SwingLabel Label       { get; }   // HH, HL, LH, LL
        public double     Price       { get; }
        public int        BarIndex    { get; }   // absolute bar index when confirmed
        public bool       IsConfirmed { get; }

        public SwingPoint(SwingType type, SwingLabel label, double price, int barIndex, bool isConfirmed)
        {
            Type        = type;
            Label       = label;
            Price       = price;
            BarIndex    = barIndex;
            IsConfirmed = isConfirmed;
        }

        public static readonly SwingPoint None = new SwingPoint(SwingType.None, SwingLabel.None, 0, 0, false);

        /// <summary>Convenience — returns None when no swing is detected at this bar.</summary>
        public static SwingPoint NotDetected() => SwingPoint.None;
    }

    /// <summary>
    /// Break of Structure detection result.
    /// </summary>
    public sealed class BOSResult
    {
        public BOSType Type        { get; }
        public double  BrokenLevel { get; }   // the swing level that was breached
        public double  BreakTicks  { get; }   // distance beyond the level in ticks
        public bool    IsStrong    { get; }   // break > 50% of ATR
        public bool    IsValid     { get; }

        public BOSResult(BOSType type, double brokenLevel, double breakTicks, bool isStrong, bool isValid)
        {
            Type        = type;
            BrokenLevel = brokenLevel;
            BreakTicks  = breakTicks;
            IsStrong    = isStrong;
            IsValid     = isValid;
        }

        public static readonly BOSResult None = new BOSResult(BOSType.None, 0, 0, false, false);
    }

    /// <summary>
    /// Change of Character detection result.
    /// </summary>
    public sealed class CHoCHResult
    {
        public CHoCHType Type        { get; }
        public double    BrokenLevel { get; }
        public double    Strength    { get; }   // breakTicks / atrTicks — higher = more significant
        public bool      IsValid     { get; }

        public CHoCHResult(CHoCHType type, double brokenLevel, double strength, bool isValid)
        {
            Type        = type;
            BrokenLevel = brokenLevel;
            Strength    = strength;
            IsValid     = isValid;
        }

        public static readonly CHoCHResult None = new CHoCHResult(CHoCHType.None, 0, 0, false);
    }

    /// <summary>
    /// Order Block zone — immutable snapshot of the OB candle.
    /// Call OB_AsMitigated() to get an updated copy when price enters the zone.
    /// </summary>
    public sealed class OrderBlock
    {
        public OBType Type        { get; }
        public double High        { get; }
        public double Low         { get; }
        public double Open        { get; }
        public double Close       { get; }
        public int    BarIndex    { get; }
        public bool   IsMitigated { get; }   // true once price has traded into the zone
        public bool   IsValid     { get; }

        public OrderBlock(OBType type, double high, double low, double open, double close,
            int barIndex, bool isMitigated, bool isValid)
        {
            Type        = type;
            High        = high;
            Low         = low;
            Open        = open;
            Close       = close;
            BarIndex    = barIndex;
            IsMitigated = isMitigated;
            IsValid     = isValid;
        }

        public static readonly OrderBlock None = new OrderBlock(OBType.None, 0, 0, 0, 0, 0, false, false);
    }

    /// <summary>
    /// Breaker Block — a former Order Block that has been fully violated and flipped role.
    /// </summary>
    public sealed class BreakerBlock
    {
        public BreakerType Type             { get; }
        public double      High            { get; }
        public double      Low             { get; }
        public int         OriginalBarIndex { get; }   // bar index of the original OB candle
        public int         BrokenBarIndex  { get; }   // bar index when the OB was broken to create breaker
        public bool        IsValid         { get; }

        public BreakerBlock(BreakerType type, double high, double low,
            int originalBarIndex, int brokenBarIndex, bool isValid)
        {
            Type             = type;
            High             = high;
            Low              = low;
            OriginalBarIndex = originalBarIndex;
            BrokenBarIndex   = brokenBarIndex;
            IsValid          = isValid;
        }

        public static readonly BreakerBlock None = new BreakerBlock(BreakerType.None, 0, 0, 0, 0, false);
    }

    /// <summary>
    /// A tracked Support or Resistance level derived from confirmed swing points.
    /// Use SR_AddTouch() to get an updated copy when the level is retested.
    /// </summary>
    public sealed class SRLevel
    {
        public double     Price             { get; }
        public int        Touches           { get; }
        public SRStrength Strength          { get; }
        public bool       IsSupport         { get; }   // true = support, false = resistance
        public int        FirstBarIndex     { get; }
        public int        LastTouchBarIndex { get; }
        public bool       IsValid           { get; }

        public SRLevel(double price, int touches, SRStrength strength, bool isSupport,
            int firstBarIndex, int lastTouchBarIndex, bool isValid)
        {
            Price             = price;
            Touches           = touches;
            Strength          = strength;
            IsSupport         = isSupport;
            FirstBarIndex     = firstBarIndex;
            LastTouchBarIndex = lastTouchBarIndex;
            IsValid           = isValid;
        }

        public static readonly SRLevel None = new SRLevel(0, 0, SRStrength.Weak, true, 0, 0, false);
    }

    // ===================================================================
    // MATH SMC — Smart Money Concepts
    // ===================================================================

    /// <summary>
    /// SMC MODULE — BOS, CHoCH, Swing Structure, Order Blocks, Breaker Blocks, S/R.
    ///
    /// Design contract (same as rest of library):
    /// - Pure stateless functions. Caller owns and maintains all state arrays.
    /// - No LINQ, no allocations in hot paths (result objects created only on signal).
    /// - bars[0] = current (most recent), bars[1] = 1 bar ago, etc.
    /// - Thread-safe: no shared mutable state.
    /// - ComputationTier: STRUCTURE_ONLY minimum for swing detection; FULL_ANALYSIS for S/R arrays.
    /// </summary>
    public static class MathSMC
    {
        // ===================================================================
        // SWING DETECTION
        // ===================================================================

        /// <summary>
        /// Detect a confirmed swing high at bars[strength].
        /// Requires `strength` bars on BOTH sides all with lower highs than the pivot.
        /// bars[0] = most recent, bars[strength] = pivot candidate.
        /// Array must have at least (2 * strength + 1) elements.
        ///
        /// NOTE: Because confirmation requires `strength` bars to the right (more recent),
        /// a swing high confirmed today was actually the high `strength` bars ago.
        /// Account for this offset when storing BarIndex.
        /// </summary>
        public static bool IsSwingHigh(double[] highs, int strength)
        {
            if (highs == null || strength <= 0 || highs.Length < 2 * strength + 1)
                return false;

            double pivot = highs[strength];

            // Right side: bars more recent than pivot (indices 0 .. strength-1)
            for (int i = 0; i < strength; i++)
                if (highs[i] >= pivot) return false;

            // Left side: bars older than pivot (indices strength+1 .. 2*strength)
            for (int i = strength + 1; i <= 2 * strength; i++)
                if (highs[i] >= pivot) return false;

            return true;
        }

        /// <summary>
        /// Detect a confirmed swing low at bars[strength].
        /// Requires `strength` bars on BOTH sides all with higher lows than the pivot.
        /// </summary>
        public static bool IsSwingLow(double[] lows, int strength)
        {
            if (lows == null || strength <= 0 || lows.Length < 2 * strength + 1)
                return false;

            double pivot = lows[strength];

            for (int i = 0; i < strength; i++)
                if (lows[i] <= pivot) return false;

            for (int i = strength + 1; i <= 2 * strength; i++)
                if (lows[i] <= pivot) return false;

            return true;
        }

        // ===================================================================
        // SWING STRUCTURE CLASSIFICATION
        // ===================================================================

        /// <summary>
        /// Classify a new swing high relative to the previous confirmed swing high.
        /// Returns HH if higher, LH if lower, None if equal or no prior reference.
        /// </summary>
        public static SwingLabel Classify_SwingHigh(double newHigh, double prevHigh)
        {
            if (prevHigh <= 0.0)       return SwingLabel.None;
            if (newHigh > prevHigh)    return SwingLabel.HH;
            if (newHigh < prevHigh)    return SwingLabel.LH;
            return SwingLabel.None; // Equal — not a structural event
        }

        /// <summary>
        /// Classify a new swing low relative to the previous confirmed swing low.
        /// Returns HL if higher, LL if lower, None if equal or no prior reference.
        /// </summary>
        public static SwingLabel Classify_SwingLow(double newLow, double prevLow)
        {
            if (prevLow <= 0.0)       return SwingLabel.None;
            if (newLow > prevLow)     return SwingLabel.HL;
            if (newLow < prevLow)     return SwingLabel.LL;
            return SwingLabel.None;
        }

        /// <summary>
        /// Derive structural trend from the most recent swing high and low labels.
        /// Full bullish structure = HH + HL. Full bearish = LH + LL.
        /// Mixed signals lean toward whichever label is more recent (caller decides weight).
        /// </summary>
        public static StructureTrend Structure_Trend(SwingLabel lastHighLabel, SwingLabel lastLowLabel)
        {
            bool bullHigh = (lastHighLabel == SwingLabel.HH);
            bool bullLow  = (lastLowLabel  == SwingLabel.HL);
            bool bearHigh = (lastHighLabel == SwingLabel.LH);
            bool bearLow  = (lastLowLabel  == SwingLabel.LL);

            if (bullHigh && bullLow) return StructureTrend.Bullish;
            if (bearHigh && bearLow) return StructureTrend.Bearish;

            // Partial — lean toward whatever partial signal exists
            if (bullHigh || bullLow) return StructureTrend.Bullish;
            if (bearHigh || bearLow) return StructureTrend.Bearish;

            return StructureTrend.Undefined;
        }

        /// <summary>
        /// Require a minimum number of confirmed swings before trusting structure.
        /// Recommended minimum: 4 (at least 2 highs + 2 lows).
        /// </summary>
        public static bool Structure_IsValid(int confirmedSwingCount, int minSwings = 4)
            => confirmedSwingCount >= minSwings;

        // ===================================================================
        // BREAK OF STRUCTURE (BOS)
        // Continuation break in the direction of the established trend.
        // ===================================================================

        /// <summary>
        /// Detect a BOS on bar close.
        ///
        /// Bullish BOS:  close > lastSwingHigh  AND trend == Bullish  (continuation higher)
        /// Bearish BOS:  close < lastSwingLow   AND trend == Bearish  (continuation lower)
        ///
        /// A break AGAINST the current trend is NOT a BOS — it is a CHoCH.
        /// See CHoCH_Detect for that case.
        ///
        /// atrTicks: used to classify whether the break is strong (> 50% of ATR).
        /// </summary>
        public static BOSResult BOS_Detect(
            double          close,
            double          lastSwingHigh,
            double          lastSwingLow,
            StructureTrend  trend,
            double          atrTicks,
            double          tickSize,
            double          minBreakTicks = 1.0)
        {
            if (tickSize <= 0.0 || lastSwingHigh <= 0.0 || lastSwingLow <= 0.0)
                return BOSResult.None;

            if (trend == StructureTrend.Bullish && close > lastSwingHigh)
            {
                double breakTicks = (close - lastSwingHigh) / tickSize;
                if (breakTicks < minBreakTicks) return BOSResult.None;

                bool isStrong = (atrTicks > 0.0) && (breakTicks >= 0.5 * atrTicks);
                return new BOSResult(BOSType.Bullish, lastSwingHigh, breakTicks, isStrong, true);
            }

            if (trend == StructureTrend.Bearish && close < lastSwingLow)
            {
                double breakTicks = (lastSwingLow - close) / tickSize;
                if (breakTicks < minBreakTicks) return BOSResult.None;

                bool isStrong = (atrTicks > 0.0) && (breakTicks >= 0.5 * atrTicks);
                return new BOSResult(BOSType.Bearish, lastSwingLow, breakTicks, isStrong, true);
            }

            return BOSResult.None;
        }

        // ===================================================================
        // CHANGE OF CHARACTER (CHoCH)
        // The FIRST break of structure against the prevailing trend.
        // Signals potential reversal. After a CHoCH, the next same-direction
        // break becomes a BOS in the new trend direction.
        // ===================================================================

        /// <summary>
        /// Detect a CHoCH on bar close.
        ///
        /// Bullish CHoCH:  close > lastSwingHigh  AND trend == Bearish (first break up)
        /// Bearish CHoCH:  close < lastSwingLow   AND trend == Bullish (first break down)
        ///
        /// When trend == Undefined, either direction qualifies as CHoCH (first structural event).
        ///
        /// After detecting CHoCH, the caller MUST flip the trend state and reset swing references
        /// so the next same-direction break registers as BOS rather than another CHoCH.
        /// </summary>
        public static CHoCHResult CHoCH_Detect(
            double          close,
            double          lastSwingHigh,
            double          lastSwingLow,
            StructureTrend  trend,
            double          atr,
            double          tickSize,
            double          minBreakTicks = 1.0)
        {
            if (tickSize <= 0.0 || lastSwingHigh <= 0.0 || lastSwingLow <= 0.0)
                return CHoCHResult.None;

            // Bullish CHoCH — break above swing high while in bearish (or undefined) trend
            if ((trend == StructureTrend.Bearish || trend == StructureTrend.Undefined)
                && close > lastSwingHigh)
            {
                double breakTicks = (close - lastSwingHigh) / tickSize;
                if (breakTicks < minBreakTicks) return CHoCHResult.None;

                double strength = CHoCH_Strength(breakTicks, atr, tickSize);
                return new CHoCHResult(CHoCHType.Bullish, lastSwingHigh, strength, true);
            }

            // Bearish CHoCH — break below swing low while in bullish (or undefined) trend
            if ((trend == StructureTrend.Bullish || trend == StructureTrend.Undefined)
                && close < lastSwingLow)
            {
                double breakTicks = (lastSwingLow - close) / tickSize;
                if (breakTicks < minBreakTicks) return CHoCHResult.None;

                double strength = CHoCH_Strength(breakTicks, atr, tickSize);
                return new CHoCHResult(CHoCHType.Bearish, lastSwingLow, strength, true);
            }

            return CHoCHResult.None;
        }

        /// <summary>
        /// CHoCH strength as a ratio of breakTicks to ATR ticks.
        /// > 1.0 = break exceeds a full ATR (very significant reversal signal)
        /// 0.3 to 1.0 = moderate
        /// less than 0.3 = weak — treat with caution
        /// </summary>
        public static double CHoCH_Strength(double breakTicks, double atr, double tickSize)
        {
            if (atr <= 0.0 || tickSize <= 0.0) return 0.0;
            double atrTicks = atr / tickSize;
            return (atrTicks > 0.0) ? breakTicks / atrTicks : 0.0;
        }

        // ===================================================================
        // ORDER BLOCKS
        // ===================================================================

        /// <summary>
        /// Find a Bullish Order Block — the last BEARISH candle (close less than open)
        /// before a bullish impulse move or BOS.
        ///
        /// bars[0] = current (most recent). bosBarOffset = how many bars ago the BOS
        /// or impulse was confirmed (typically 0 or 1). lookback = how many additional
        /// bars to search back from the BOS bar.
        ///
        /// Returns the first (most recent) bearish candle found in the search window.
        /// currentBarIndex: the caller's absolute CurrentBar value, used to stamp BarIndex.
        /// </summary>
        public static OrderBlock OB_FindBullish(
            double[] opens,
            double[] closes,
            double[] highs,
            double[] lows,
            int      bosBarOffset,
            int      lookback,
            int      currentBarIndex)
        {
            if (opens == null || closes == null || highs == null || lows == null)
                return OrderBlock.None;

            int maxIdx = Math.Min(bosBarOffset + lookback, opens.Length - 1);

            for (int i = bosBarOffset; i <= maxIdx; i++)
            {
                if (closes[i] < opens[i]) // bearish candle
                {
                    return new OrderBlock(
                        OBType.Bullish,
                        highs[i],
                        lows[i],
                        opens[i],
                        closes[i],
                        currentBarIndex - i,
                        false,
                        true);
                }
            }

            return OrderBlock.None;
        }

        /// <summary>
        /// Find a Bearish Order Block — the last BULLISH candle (close greater than open)
        /// before a bearish impulse move or BOS.
        /// </summary>
        public static OrderBlock OB_FindBearish(
            double[] opens,
            double[] closes,
            double[] highs,
            double[] lows,
            int      bosBarOffset,
            int      lookback,
            int      currentBarIndex)
        {
            if (opens == null || closes == null || highs == null || lows == null)
                return OrderBlock.None;

            int maxIdx = Math.Min(bosBarOffset + lookback, opens.Length - 1);

            for (int i = bosBarOffset; i <= maxIdx; i++)
            {
                if (closes[i] > opens[i]) // bullish candle
                {
                    return new OrderBlock(
                        OBType.Bearish,
                        highs[i],
                        lows[i],
                        opens[i],
                        closes[i],
                        currentBarIndex - i,
                        false,
                        true);
                }
            }

            return OrderBlock.None;
        }

        /// <summary>
        /// Return a mitigated copy of an OrderBlock (price has traded into the zone).
        /// Since OrderBlock is immutable, this creates a new instance with IsMitigated = true.
        /// Bullish OB mitigated: current low traded into or below the OB high.
        /// Bearish OB mitigated: current high traded into or above the OB low.
        /// </summary>
        public static OrderBlock OB_AsMitigated(OrderBlock ob)
        {
            if (ob == null || !ob.IsValid) return OrderBlock.None;
            return new OrderBlock(ob.Type, ob.High, ob.Low, ob.Open, ob.Close,
                ob.BarIndex, true, true);
        }

        /// <summary>
        /// Check if price has entered (mitigated) an Order Block zone this bar.
        /// Call this every bar; if true, call OB_AsMitigated() to update your stored OB.
        /// </summary>
        public static bool OB_IsMitigated(OrderBlock ob, double currentHigh, double currentLow)
        {
            if (ob == null || !ob.IsValid || ob.IsMitigated) return false;

            if (ob.Type == OBType.Bullish)
                return currentLow <= ob.High; // price pulled back into bullish OB from above

            if (ob.Type == OBType.Bearish)
                return currentHigh >= ob.Low; // price rallied back into bearish OB from below

            return false;
        }

        /// <summary>
        /// Check if price is currently inside the Order Block zone.
        /// Useful for entry timing — wait for price to enter before acting.
        /// </summary>
        public static bool OB_IsInZone(OrderBlock ob, double price)
        {
            if (ob == null || !ob.IsValid) return false;
            return price >= ob.Low && price <= ob.High;
        }

        /// <summary>
        /// Check if an Order Block has been fully invalidated (price closed through it).
        /// Bullish OB invalidated: close below OB low (price rejected the zone completely).
        /// Bearish OB invalidated: close above OB high.
        /// An invalidated OB may become a Breaker Block — see Breaker_FromBullishOB/Bearish.
        /// </summary>
        public static bool OB_IsInvalidated(OrderBlock ob, double close)
        {
            if (ob == null || !ob.IsValid) return true;

            if (ob.Type == OBType.Bullish)  return close < ob.Low;
            if (ob.Type == OBType.Bearish)  return close > ob.High;

            return false;
        }

        // ===================================================================
        // BREAKER BLOCKS
        // ===================================================================

        /// <summary>
        /// Check if a Bullish OB has become a Bearish Breaker Block.
        ///
        /// A Bullish OB (last bearish candle before a bullish move) becomes a
        /// Bearish Breaker when price CLOSES BELOW the OB low — the zone that
        /// acted as support has been broken and now acts as resistance.
        ///
        /// Typical workflow:
        /// 1. BOS up detected  → store BullishOB.
        /// 2. Price returns and OB_IsMitigated → mark mitigated.
        /// 3. Price then closes below OB low  → Breaker_FromBullishOB returns BearishBreaker.
        /// 4. Discard the OB. Store the Breaker. Short retest of the zone.
        /// </summary>
        public static BreakerBlock Breaker_FromBullishOB(
            OrderBlock ob,
            double     close,
            int        currentBarIndex,
            double     tickSize,
            double     minBreakTicks = 1.0)
        {
            if (ob == null || !ob.IsValid || ob.Type != OBType.Bullish)
                return BreakerBlock.None;

            if (close < ob.Low)
            {
                double breakTicks = (ob.Low - close) / tickSize;
                if (breakTicks < minBreakTicks) return BreakerBlock.None;

                return new BreakerBlock(
                    BreakerType.BearishBreaker,
                    ob.High,
                    ob.Low,
                    ob.BarIndex,
                    currentBarIndex,
                    true);
            }

            return BreakerBlock.None;
        }

        /// <summary>
        /// Check if a Bearish OB has become a Bullish Breaker Block.
        ///
        /// A Bearish OB (last bullish candle before a bearish move) becomes a
        /// Bullish Breaker when price CLOSES ABOVE the OB high — the zone that
        /// acted as resistance has been broken and now acts as support.
        /// </summary>
        public static BreakerBlock Breaker_FromBearishOB(
            OrderBlock ob,
            double     close,
            int        currentBarIndex,
            double     tickSize,
            double     minBreakTicks = 1.0)
        {
            if (ob == null || !ob.IsValid || ob.Type != OBType.Bearish)
                return BreakerBlock.None;

            if (close > ob.High)
            {
                double breakTicks = (close - ob.High) / tickSize;
                if (breakTicks < minBreakTicks) return BreakerBlock.None;

                return new BreakerBlock(
                    BreakerType.BullishBreaker,
                    ob.High,
                    ob.Low,
                    ob.BarIndex,
                    currentBarIndex,
                    true);
            }

            return BreakerBlock.None;
        }

        /// <summary>
        /// Check if price is currently testing a Breaker Block zone (retest entry).
        /// BullishBreaker test: price pulls back INTO the zone from above — long opportunity.
        /// BearishBreaker test: price rallies INTO the zone from below — short opportunity.
        /// </summary>
        public static bool Breaker_IsTested(BreakerBlock breaker, double currentHigh, double currentLow)
        {
            if (breaker == null || !breaker.IsValid) return false;

            if (breaker.Type == BreakerType.BullishBreaker)
                return currentLow <= breaker.High && currentLow >= breaker.Low;

            if (breaker.Type == BreakerType.BearishBreaker)
                return currentHigh >= breaker.Low && currentHigh <= breaker.High;

            return false;
        }

        /// <summary>
        /// Check if a Breaker Block has been invalidated (price broke through it again).
        /// BullishBreaker invalidated: close below Breaker low.
        /// BearishBreaker invalidated: close above Breaker high.
        /// </summary>
        public static bool Breaker_IsInvalidated(BreakerBlock breaker, double close)
        {
            if (breaker == null || !breaker.IsValid) return true;

            if (breaker.Type == BreakerType.BullishBreaker) return close < breaker.Low;
            if (breaker.Type == BreakerType.BearishBreaker) return close > breaker.High;

            return false;
        }

        // ===================================================================
        // SUPPORT / RESISTANCE TRACKING
        // ===================================================================

        /// <summary>
        /// Create a new S/R level from a confirmed swing point.
        /// Support levels come from swing lows, resistance from swing highs.
        /// isSupport: true for swing low → support, false for swing high → resistance.
        /// </summary>
        public static SRLevel SR_CreateLevel(double price, bool isSupport, int barIndex)
            => new SRLevel(price, 1, SRStrength.Weak, isSupport, barIndex, barIndex, true);

        /// <summary>
        /// Return an updated S/R level with one additional touch recorded.
        /// Since SRLevel is immutable, this creates a new instance.
        /// Call this when SR_IsNearLevel returns true on a new swing at the same price.
        /// </summary>
        public static SRLevel SR_AddTouch(SRLevel level, int barIndex)
        {
            if (level == null || !level.IsValid) return SRLevel.None;

            int newTouches = level.Touches + 1;
            return new SRLevel(
                level.Price,
                newTouches,
                SR_ClassifyStrength(newTouches),
                level.IsSupport,
                level.FirstBarIndex,
                barIndex,
                true);
        }

        /// <summary>
        /// Classify S/R strength from touch count.
        /// 1 touch = Weak, 2 = Moderate, 3+ = Strong.
        /// </summary>
        public static SRStrength SR_ClassifyStrength(int touches)
        {
            if (touches >= 3) return SRStrength.Strong;
            if (touches >= 2) return SRStrength.Moderate;
            return SRStrength.Weak;
        }

        /// <summary>
        /// Check if a price is close enough to an existing S/R level to be considered
        /// a touch rather than a new level.
        /// proximityTicks: how close counts as "at the level" (typical: 3-5 ticks).
        /// </summary>
        public static bool SR_IsNearLevel(double price, double levelPrice, double proximityTicks, double tickSize)
        {
            if (tickSize <= 0.0) return false;
            return Math.Abs(price - levelPrice) / tickSize <= proximityTicks;
        }

        /// <summary>
        /// Check if an S/R level has been decisively invalidated by a close beyond it.
        /// Support invalidated: close below level price minus buffer.
        /// Resistance invalidated: close above level price plus buffer.
        /// Recommended invalidationBufferTicks: 2-5 ticks depending on instrument.
        /// </summary>
        public static bool SR_IsInvalidated(
            SRLevel level,
            double  close,
            double  tickSize,
            double  invalidationBufferTicks = 3.0)
        {
            if (level == null || !level.IsValid || tickSize <= 0.0) return true;

            double buffer = invalidationBufferTicks * tickSize;

            if (level.IsSupport)    return close < level.Price - buffer;
            return close > level.Price + buffer; // resistance
        }

        /// <summary>
        /// Find the nearest valid S/R level to a given price.
        /// levels[]: caller-managed array. count: number of populated entries.
        /// Pass isSupport = true to search only support levels, false for resistance only.
        /// </summary>
        public static SRLevel SR_FindNearest(SRLevel[] levels, int count, double price, bool isSupport)
        {
            if (levels == null || count <= 0) return SRLevel.None;

            SRLevel nearest = SRLevel.None;
            double  minDist = double.MaxValue;

            for (int i = 0; i < count && i < levels.Length; i++)
            {
                if (levels[i] == null || !levels[i].IsValid)      continue;
                if (levels[i].IsSupport != isSupport)             continue;

                double dist = Math.Abs(price - levels[i].Price);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = levels[i];
                }
            }

            return nearest;
        }

        /// <summary>
        /// Find the STRONGEST S/R level within a proximity range of a given price.
        /// Useful for identifying high-confluence zones — prefer strong levels over nearby weak ones.
        /// </summary>
        public static SRLevel SR_FindStrongest(
            SRLevel[] levels,
            int       count,
            double    price,
            double    proximityTicks,
            double    tickSize)
        {
            if (levels == null || count <= 0 || tickSize <= 0.0) return SRLevel.None;

            SRLevel strongest  = SRLevel.None;
            int     maxTouches = 0;

            for (int i = 0; i < count && i < levels.Length; i++)
            {
                if (levels[i] == null || !levels[i].IsValid)                    continue;
                if (!SR_IsNearLevel(price, levels[i].Price, proximityTicks, tickSize)) continue;
                if (levels[i].Touches <= maxTouches)                            continue;

                maxTouches = levels[i].Touches;
                strongest  = levels[i];
            }

            return strongest;
        }

        // ===================================================================
        // CONFLUENCE HELPERS
        // ===================================================================

        /// <summary>
        /// Check if price is at the confluence of an Order Block AND an S/R level.
        /// This is one of the highest-probability entry conditions in SMC.
        /// </summary>
        public static bool IsOBAndSRConfluence(
            OrderBlock ob,
            SRLevel    sr,
            double     price,
            double     proximityTicks,
            double     tickSize)
        {
            if (ob == null || !ob.IsValid) return false;
            if (sr == null || !sr.IsValid) return false;

            bool inOB   = OB_IsInZone(ob, price);
            bool nearSR = SR_IsNearLevel(price, sr.Price, proximityTicks, tickSize);

            return inOB && nearSR;
        }

        /// <summary>
        /// Score a SMC setup for integration with MathPolicy.Grade_Master().
        /// Returns 0-100. Feed this into MathPolicy.Grade_Master() alongside
        /// your existing absorption/delta/VWAP scores.
        ///
        /// Scoring breakdown (100 max):
        ///   Structural context     : 0-25  (valid trend + BOS or CHoCH present)
        ///   Order block quality    : 0-30  (valid OB, bonus for unmitigated)
        ///   S/R confluence         : 0-25  (level present, bonus for strength)
        ///   Breaker block present  : 0-20  (strongest confluence signal)
        /// </summary>
        public static int SMC_SetupScore(
            BOSResult      bos,
            CHoCHResult    choch,
            OrderBlock     ob,
            SRLevel        sr,
            StructureTrend trend,
            bool           hasBreaker)
        {
            int score = 0;

            // Structural context (0-25)
            if (trend != StructureTrend.Undefined)           score += 10;
            if (bos   != null && bos.IsValid)                score += 5;
            if (bos   != null && bos.IsValid && bos.IsStrong) score += 5;
            if (choch != null && choch.IsValid)              score += 15; // CHoCH > BOS for reversal entries

            // Order block quality (0-30)
            if (ob != null && ob.IsValid)
            {
                score += 15;
                if (!ob.IsMitigated) score += 15; // fresh, untested OB is highest quality
            }

            // S/R confluence (0-25)
            if (sr != null && sr.IsValid)
            {
                score += 10;
                if (sr.Strength == SRStrength.Moderate) score += 7;
                if (sr.Strength == SRStrength.Strong)   score += 15;
            }

            // Breaker block (0-20)
            if (hasBreaker) score += 20;

            return Math.Min(score, 100);
        }
    }
}
