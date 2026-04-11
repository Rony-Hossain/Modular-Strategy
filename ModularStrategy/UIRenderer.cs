#region Using declarations
using System;
using System.Collections.Generic;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using MathLogic;
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// UI RENDERER — chart drawing layer.
    ///
    /// Reads SignalObject queue and MarketSnapshot.
    /// Draws:
    ///   - Green chat bubble below candle for long signals
    ///   - Red chat bubble above candle for short signals
    ///   - VWAP + SD band lines
    ///   - Order block zones via ZoneDTO (decoupled from MathLogic.OrderBlock)
    ///   - S/R level lines via LevelDTO (decoupled from MathLogic.SRLevel)
    ///   - ORB high/low dashed lines
    ///   - Signal label text
    ///
    /// Zero strategy logic. Zero order management.
    /// Implements IUIRenderer — swap this class to change all visuals.
    /// All rendering via SharpDX Direct2D (NT8 hardware-accelerated layer).
    /// </summary>
    public class UIRenderer : IUIRenderer
    {
        // ===================================================================
        // CONFIGURATION — set via properties before first OnRender
        // ===================================================================

        public bool ShowVWAP          { get; set; } = true;
        public bool ShowSDbands        { get; set; } = true;
        public bool ShowSignalBubbles  { get; set; } = true;
        public bool ShowOBZones        { get; set; } = true;
        public bool ShowSRLevels       { get; set; } = true;
        public bool ShowORBLines       { get; set; } = true;
        public bool ShowLabels         { get; set; } = true;
        public float BubbleSize        { get; set; } = 14f;

        // ===================================================================
        // ZONE / LEVEL DATA (fed per bar from strategy logic via interface)
        // ===================================================================

        private readonly ZoneDTO[]  _zones  = new ZoneDTO[20];
        private int        _zoneCount;
        private readonly LevelDTO[] _levels = new LevelDTO[30];
        private int        _levelCount;

        // TODO #18 fix: copy elements into pre-allocated fixed arrays rather than
        // replacing the array reference (which discards the pre-alloc on first call).
        public void SetZones(ZoneDTO[] zones, int count)
        {
            _zoneCount = Math.Min(count, _zones.Length);
            for (int i = 0; i < _zoneCount; i++)
                _zones[i] = zones[i];
        }

        public void SetLevels(LevelDTO[] levels, int count)
        {
            _levelCount = Math.Min(count, _levels.Length);
            for (int i = 0; i < _levelCount; i++)
                _levels[i] = levels[i];
        }

        // ===================================================================
        // SHARPEX RESOURCES
        // ===================================================================

        private SharpDX.Direct2D1.Brush _bullBrush;
        private SharpDX.Direct2D1.Brush _bearBrush;
        private SharpDX.Direct2D1.Brush _vwapBrush;
        private SharpDX.Direct2D1.Brush _sd1Brush;
        private SharpDX.Direct2D1.Brush _sd2Brush;
        private SharpDX.Direct2D1.Brush _obBullBrush;
        private SharpDX.Direct2D1.Brush _obBearBrush;
        private SharpDX.Direct2D1.Brush _srbBrush;
        private SharpDX.Direct2D1.Brush _orbBrush;
        private SharpDX.Direct2D1.Brush _textBrush;
        private TextFormat               _labelFormat;
        private TextFormat               _bubbleTextFormat;   // Bold — for BUY/SELL bubbles
        private SharpDX.DirectWrite.Factory _writeFactory;
        private bool                     _resourcesCreated;

        // ===================================================================
        // SIGNAL QUEUE (last N signals to display)
        // ===================================================================

        private const int MAX_SIGNALS = 20;
        private readonly Queue<SignalObject> _signalQueue = new Queue<SignalObject>(MAX_SIGNALS);

        // ===================================================================
        // RESOURCE LIFECYCLE
        // ===================================================================

        // IUIRenderer explicit implementation — HostStrategy calls this via interface
        // so it doesn't need to know about SharpDX types.
        void IUIRenderer.CreateResources(object renderTarget, object writeFactory)
        {
            var rt      = renderTarget  as RenderTarget;
            var factory = writeFactory  as SharpDX.DirectWrite.Factory;

            if (rt == null)
            {
                NinjaTrader.Code.Output.Process(
                    "UIRenderer.CreateResources: renderTarget cast to RenderTarget failed.",
                    PrintTo.OutputTab1);
                return;
            }
            if (factory == null)
            {
                NinjaTrader.Code.Output.Process(
                    "UIRenderer.CreateResources: writeFactory cast to SharpDX.DirectWrite.Factory failed.",
                    PrintTo.OutputTab1);
                return;
            }

            CreateResources(rt, factory);
        }

        // Concrete overload used internally with proper SharpDX types
        public void CreateResources(RenderTarget renderTarget, SharpDX.DirectWrite.Factory writeFactory)
        {
            if (renderTarget == null) return;
            DisposeResources();

            _bullBrush   = new SolidColorBrush(renderTarget, new Color4(0.13f, 0.70f, 0.30f, 0.95f));
            _bearBrush   = new SolidColorBrush(renderTarget, new Color4(0.85f, 0.18f, 0.18f, 0.95f));
            _vwapBrush   = new SolidColorBrush(renderTarget, new Color4(0.95f, 0.75f, 0.10f, 0.90f));
            _sd1Brush    = new SolidColorBrush(renderTarget, new Color4(0.95f, 0.75f, 0.10f, 0.40f));
            _sd2Brush    = new SolidColorBrush(renderTarget, new Color4(0.95f, 0.75f, 0.10f, 0.20f));
            _obBullBrush = new SolidColorBrush(renderTarget, new Color4(0.10f, 0.70f, 0.35f, 0.15f));
            _obBearBrush = new SolidColorBrush(renderTarget, new Color4(0.85f, 0.18f, 0.18f, 0.15f));
            _srbBrush    = new SolidColorBrush(renderTarget, new Color4(0.55f, 0.40f, 0.85f, 0.60f));
            _orbBrush    = new SolidColorBrush(renderTarget, new Color4(0.30f, 0.65f, 0.95f, 0.70f));
            _textBrush   = new SolidColorBrush(renderTarget, new Color4(0.95f, 0.95f, 0.95f, 1.00f));

            _labelFormat  = new TextFormat(writeFactory, "Segoe UI", 11f);

            _bubbleTextFormat = new TextFormat(
                writeFactory,
                "Segoe UI",
                SharpDX.DirectWrite.FontWeight.Bold,
                SharpDX.DirectWrite.FontStyle.Normal,
                13f)
            {
                TextAlignment      = SharpDX.DirectWrite.TextAlignment.Center,
                ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center
            };

            _writeFactory = writeFactory;

            _resourcesCreated = true;
        }

        public void DisposeResources()
        {
            _resourcesCreated = false;

            _bullBrush?.Dispose();   _bearBrush?.Dispose();
            _vwapBrush?.Dispose();   _sd1Brush?.Dispose();   _sd2Brush?.Dispose();
            _obBullBrush?.Dispose(); _obBearBrush?.Dispose();
            _srbBrush?.Dispose();    _orbBrush?.Dispose();   _textBrush?.Dispose();
            _labelFormat?.Dispose(); _bubbleTextFormat?.Dispose();

            _bullBrush = _bearBrush = _vwapBrush = _sd1Brush = _sd2Brush = null;
            _obBullBrush = _obBearBrush = _srbBrush = _orbBrush = _textBrush = null;
            _labelFormat = null; _bubbleTextFormat = null;
        }

        // ===================================================================
        // SIGNAL QUEUE MANAGEMENT
        // ===================================================================

        public void AddSignal(SignalObject signal)
        {
            if (signal == null) return;
            while (_signalQueue.Count >= MAX_SIGNALS)
                _signalQueue.Dequeue();
            _signalQueue.Enqueue(signal);
        }

        // ===================================================================
        // MAIN RENDER
        // ===================================================================

        /// <summary>
        /// Call from OnRender(). Draws all visual elements.
        /// Uses ZoneDTO and LevelDTO — decoupled from MathLogic.OrderBlock and MathLogic.SRLevel.
        /// Strategy logic converts its MathLogic types to these DTOs before calling.
        /// </summary>
        public void OnRender(
            ChartControl    chartControl,
            ChartScale      chartScale,
            RenderTarget    renderTarget,
            NinjaTrader.Gui.Chart.ChartBars chartBars,
            MarketSnapshot  snapshot,
            ORBContext      orb)
        {
            if (!_resourcesCreated || renderTarget == null) return;
            if (chartControl == null || chartScale == null) return;

            // ── VWAP + SD Bands ──
            if (ShowVWAP && snapshot.VWAP > 0)
                RenderHorizontalLine(renderTarget, chartControl, chartScale,
                    snapshot.VWAP, _vwapBrush, 1.5f, false);

            if (ShowSDbands)
            {
                if (snapshot.VWAPUpperSD1 > 0)
                {
                    RenderHorizontalLine(renderTarget, chartControl, chartScale,
                        snapshot.VWAPUpperSD1, _sd1Brush, 1.0f, true);
                    RenderHorizontalLine(renderTarget, chartControl, chartScale,
                        snapshot.VWAPLowerSD1, _sd1Brush, 1.0f, true);
                }
                if (snapshot.VWAPUpperSD2 > 0)
                {
                    RenderHorizontalLine(renderTarget, chartControl, chartScale,
                        snapshot.VWAPUpperSD2, _sd2Brush, 1.0f, true);
                    RenderHorizontalLine(renderTarget, chartControl, chartScale,
                        snapshot.VWAPLowerSD2, _sd2Brush, 1.0f, true);
                }
            }

            // ── ORB Lines ──
            if (ShowORBLines && orb.IsComplete)
            {
                RenderHorizontalLine(renderTarget, chartControl, chartScale,
                    orb.High, _orbBrush, 1.5f, true);
                RenderHorizontalLine(renderTarget, chartControl, chartScale,
                    orb.Low,  _orbBrush, 1.5f, true);
                RenderHorizontalLine(renderTarget, chartControl, chartScale,
                    orb.Midpoint, _orbBrush, 0.8f, true);
            }

            // ── S/R Levels (from LevelDTO — no MathLogic dependency) ──
            if (ShowSRLevels && _levels != null)
            {
                for (int i = 0; i < _levelCount && i < _levels.Length; i++)
                {
                    if (!_levels[i].IsValid) continue;
                    RenderHorizontalLine(renderTarget, chartControl, chartScale,
                        _levels[i].Price, _srbBrush, 0.8f, true);
                }
            }

            // ── Zone Boxes (from ZoneDTO — no MathLogic dependency) ──
            if (ShowOBZones && _zones != null)
            {
                for (int i = 0; i < _zoneCount && i < _zones.Length; i++)
                {
                    if (!_zones[i].IsValid) continue;
                    var brush = _zones[i].IsBullish ? _obBullBrush : _obBearBrush;
                    RenderZoneBox(renderTarget, chartControl, chartScale, chartBars,
                        _zones[i].BarIndex, _zones[i].High, _zones[i].Low, brush);
                }
            }

            // ── Signal Bubbles ──
            if (ShowSignalBubbles)
            {
                foreach (var sig in _signalQueue)
                    RenderSignalBubble(renderTarget, chartControl, chartScale, chartBars, sig);
            }
        }

        // ===================================================================
        // DRAWING PRIMITIVES
        // ===================================================================

        private void RenderHorizontalLine(
            RenderTarget  rt,
            ChartControl  cc,
            ChartScale    cs,
            double        price,
            SharpDX.Direct2D1.Brush brush,
            float         strokeWidth,
            bool          dashed)
        {
            float y = (float)cs.GetYByValue(price);
            if (y < 0 || y > cs.Height) return;

            float x0 = (float)cc.CanvasLeft;
            float x1 = (float)cc.CanvasRight;

            if (dashed)
            {
                var props  = new StrokeStyleProperties { DashStyle = DashStyle.Dash };
                using var style = new StrokeStyle(rt.Factory, props);
                rt.DrawLine(new Vector2(x0, y), new Vector2(x1, y), brush, strokeWidth, style);
            }
            else
            {
                rt.DrawLine(new Vector2(x0, y), new Vector2(x1, y), brush, strokeWidth);
            }
        }

        private void RenderZoneBox(
            RenderTarget  rt,
            ChartControl  cc,
            ChartScale    cs,
            NinjaTrader.Gui.Chart.ChartBars chartBars,
            int           barIndex,
            double        high,
            double        low,
            SharpDX.Direct2D1.Brush brush)
        {
            float yTop    = (float)cs.GetYByValue(high);
            float yBottom = (float)cs.GetYByValue(low);
            float xLeft   = (float)cc.GetXByBarIndex(chartBars, barIndex);
            float xRight  = (float)cc.CanvasRight;

            if (yTop < 0 && yBottom < 0)  return;
            if (xLeft > cc.CanvasRight)    return;

            var rect = new RectangleF(xLeft, yTop, xRight - xLeft, yBottom - yTop);
            rt.FillRectangle(rect, brush);
        }

        private void RenderSignalBubble(
            RenderTarget  rt,
            ChartControl  cc,
            ChartScale    cs,
            NinjaTrader.Gui.Chart.ChartBars chartBars,
            SignalObject  sig)
        {
            if (sig == null) return;

            float x = (float)cc.GetXByBarIndex(chartBars, sig.BarIndex);
            if (x < cc.CanvasLeft || x > cc.CanvasRight) return;

            bool isLong = sig.Direction == SignalDirection.Long;
            var  brush  = isLong ? _bullBrush : _bearBrush;

            string label = !string.IsNullOrEmpty(sig.Grade)
                ? (isLong ? "BUY" : "SELL") + " " + sig.Grade
                : (isLong ? "BUY" : "SELL");

            // Body is slightly wider when a grade suffix is appended (e.g. "BUY A+")
            float bodyW    = string.IsNullOrEmpty(sig.Grade) ? 60f : 80f;
            const float BODY_H    = 25f;
            const float CORNER_R  = 6f;
            const float POINTER_W = 12f;
            const float POINTER_H = 8f;

            // Anchor tip to bar edge so the bubble touches the candle, not entry price
            float tipY = isLong
                ? (float)cs.GetYByValue(sig.CandleLow)  + 5f   // below bar low
                : (float)cs.GetYByValue(sig.CandleHigh) - 5f;  // above bar high

            if (isLong)
                RenderBubbleUp(rt, x, tipY, bodyW, BODY_H, CORNER_R, POINTER_W, POINTER_H, brush);
            else
                RenderBubbleDown(rt, x, tipY, bodyW, BODY_H, CORNER_R, POINTER_W, POINTER_H, brush);

            // Text rect — centred inside the body (same arithmetic as the indicator)
            if (ShowLabels && _bubbleTextFormat != null && _textBrush != null)
            {
                float textY = isLong
                    ? tipY + POINTER_H          // body top for BubbleUp
                    : tipY - POINTER_H - BODY_H; // body top for BubbleDown

                var textRect = new RectangleF(x - bodyW * 0.5f, textY, bodyW, BODY_H);
                rt.DrawText(label, _bubbleTextFormat, textRect, _textBrush);
            }
        }

        // ── Ported from SmartMoneyFlowCloudBOSWaves ──────────────────────────
        // Points UP — tip at (tipX, tipY), body below. Use for long (BUY) signals.
        private static void RenderBubbleUp(
            RenderTarget rt,
            float tipX, float tipY,
            float w, float h, float r, float pW, float pH,
            SharpDX.Direct2D1.Brush brush)
        {
            var bodyRect = new RectangleF(tipX - w * 0.5f, tipY + pH, w, h);
            var rr       = new RoundedRectangle { Rect = bodyRect, RadiusX = r, RadiusY = r };

            using (var geo  = new PathGeometry(rt.Factory))
            using (var sink = geo.Open())
            {
                sink.BeginFigure(new Vector2(tipX,             tipY     ), FigureBegin.Filled);
                sink.AddLine   (new Vector2(tipX - pW * 0.5f, tipY + pH));
                sink.AddLine   (new Vector2(tipX + pW * 0.5f, tipY + pH));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
                rt.FillRoundedRectangle(rr, brush);
                rt.FillGeometry(geo, brush);
            }
        }

        // Points DOWN — tip at (tipX, tipY), body above. Use for short (SELL) signals.
        private static void RenderBubbleDown(
            RenderTarget rt,
            float tipX, float tipY,
            float w, float h, float r, float pW, float pH,
            SharpDX.Direct2D1.Brush brush)
        {
            var bodyRect = new RectangleF(tipX - w * 0.5f, tipY - pH - h, w, h);
            var rr       = new RoundedRectangle { Rect = bodyRect, RadiusX = r, RadiusY = r };

            using (var geo  = new PathGeometry(rt.Factory))
            using (var sink = geo.Open())
            {
                sink.BeginFigure(new Vector2(tipX,             tipY     ), FigureBegin.Filled);
                sink.AddLine   (new Vector2(tipX - pW * 0.5f, tipY - pH));
                sink.AddLine   (new Vector2(tipX + pW * 0.5f, tipY - pH));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
                rt.FillRoundedRectangle(rr, brush);
                rt.FillGeometry(geo, brush);
            }
        }

    }
}
