#!/usr/bin/env python3
import sys
import os
import pandas as pd
import numpy as np
import pyqtgraph as pg
from pyqtgraph.Qt import QtCore, QtGui, QtWidgets
from pathlib import Path
from typing import Dict, List, Optional, Tuple, Any
from dataclasses import dataclass, field

# ─────────────────────────────────────────────────────────────────────────────
# CONFIGURATION
# ─────────────────────────────────────────────────────────────────────────────
@dataclass
class ChartConfig:
    bar_spacing_equidistant: bool = True
    right_side_margin: int = 10
    time_format: str = '%m-%d %H:%M'
    show_cvd: bool = True
    bar_minutes: int = 5
    font_family: str = "Segoe UI"
    font_size: int = 9
    font_bold: bool = False
    color_bg: tuple = (10, 10, 10)
    color_text: tuple = (220, 220, 220)
    color_axis: tuple = (80, 80, 80)
    color_grid: tuple = (43, 49, 67, 80)
    color_crosshair: tuple = (150, 150, 150, 180)
    grid_h_visible: bool = True
    grid_v_visible: bool = True
    axis_width: float = 1.0
    tick_size: float = 0.25
    show_wins: bool = True
    show_losses: bool = True
    show_rejected: bool = False
    source_filter: str = "All"

# ─────────────────────────────────────────────────────────────────────────────
# DATA MODELS
# ─────────────────────────────────────────────────────────────────────────────
@dataclass
class Bar:
    x: int; time: pd.Timestamp; open: float; high: float; low: float; close: float
    levels: Dict[float, Tuple[int, int]]

@dataclass
class Signal:
    ts: pd.Timestamp; direction: str; source: str; entry: float; outcome: str; x: float; accepted: bool

# ─────────────────────────────────────────────────────────────────────────────
# CUSTOM COMPONENTS
# ─────────────────────────────────────────────────────────────────────────────
class TimeAxisItem(pg.AxisItem):
    def __init__(self, engine, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.engine = engine
    def tickStrings(self, values, scale, spacing):
        ticks = []
        for x in values:
            idx = int(x)
            if 0 <= idx < len(self.engine.bars):
                ticks.append(self.engine.bars[idx].time.strftime('%m-%d %H:%M'))
            else: ticks.append("")
        return ticks

class NinjaPriceLabel(pg.GraphicsObject):
    def __init__(self, color=(220, 220, 220)):
        super().__init__()
        self.text = ""
        self.color = color
        self.font = QtGui.QFont("Segoe UI", 12, QtGui.QFont.Bold) 
        self.bg_color = QtGui.QColor(50, 50, 55, 255)
        self.border_color = QtGui.QColor(255, 255, 255, 255)
        self.setZValue(2000)
        self.setFlag(pg.GraphicsObject.ItemIgnoresTransformations)

    def setText(self, text):
        if self.text != text:
            self.text = text
            self.update()

    def boundingRect(self):
        return QtCore.QRectF(0, -20, 100, 40)

    def paint(self, p, *args):
        if not self.text: return
        p.setRenderHint(QtGui.QPainter.Antialiasing)
        w, h = 95, 28
        path = QtGui.QPainterPath()
        path.moveTo(w, -h/2)
        path.lineTo(15, -h/2)
        path.lineTo(0, 0)
        path.lineTo(15, h/2)
        path.lineTo(w, h/2)
        path.closeSubpath()
        p.setPen(pg.mkPen(self.border_color, width=2))
        p.setBrush(pg.mkBrush(self.bg_color))
        p.drawPath(path)
        p.setPen(pg.mkPen(self.color))
        p.setFont(self.font)
        p.drawText(QtCore.QRectF(15, -h/2, w-15, h), QtCore.Qt.AlignCenter, self.text)

class NinjaTimeLabel(pg.GraphicsObject):
    def __init__(self, color=(220, 220, 220)):
        super().__init__()
        self.text = ""
        self.color = color
        self.font = QtGui.QFont("Segoe UI", 12, QtGui.QFont.Bold) 
        self.bg_color = QtGui.QColor(50, 50, 55, 255)
        self.border_color = QtGui.QColor(255, 255, 255, 255)
        self.setZValue(2000)
        self.setFlag(pg.GraphicsObject.ItemIgnoresTransformations)

    def setText(self, text):
        if self.text != text:
            self.text = text
            self.update()

    def boundingRect(self):
        return QtCore.QRectF(-75, 0, 150, 40)

    def paint(self, p, *args):
        if not self.text: return
        p.setRenderHint(QtGui.QPainter.Antialiasing)
        w, h = 140, 28
        rect = QtCore.QRectF(-w/2, 0, w, h)
        p.setPen(pg.mkPen(self.border_color, width=2))
        p.setBrush(pg.mkBrush(self.bg_color))
        p.drawRect(rect)
        p.setPen(pg.mkPen(self.color))
        p.setFont(self.font)
        p.drawText(rect, QtCore.Qt.AlignCenter, self.text)

# ─────────────────────────────────────────────────────────────────────────────
# DATA ENGINE
# ─────────────────────────────────────────────────────────────────────────────
class TradingEngine:
    def __init__(self, tic_path: Path, log_path: Optional[Path]):
        self.tic_path = tic_path
        self.log_path = log_path
        self.bar_seconds = 300
        self.cache_path = None
        self.bars: List[Bar] = []
        self.ohlc_df = pd.DataFrame()
        self.signals: List[Signal] = []
        self.sources: List[str] = ["All"]

    def set_timeframe(self, minutes: int):
        self.bar_seconds = minutes * 60
        self.cache_path = self.tic_path.with_suffix(f'.unified_{self.bar_seconds}s.parquet')
        self.bars = []; self.signals = []

    def load(self):
        if self.cache_path and self.cache_path.exists() and self.cache_path.stat().st_mtime > self.tic_path.stat().st_mtime:
            df = pd.read_parquet(self.cache_path)
        else:
            df = self._aggregate()
            if self.cache_path: df.to_parquet(self.cache_path, compression='snappy')
        self._build_bars(df)
        if self.log_path and self.log_path.exists(): self._load_log()

    def _aggregate(self) -> pd.DataFrame:
        print(f"Aggregating {self.tic_path.name} ({self.bar_seconds//60}m)...")
        CHUNK = 2_000_000
        bars_dict = {}; last_p, last_s = 0.0, 0
        encoding = 'utf-8-sig'
        for chunk in pd.read_csv(self.tic_path, chunksize=CHUNK, encoding=encoding):
            chunk.columns = [c.strip() for c in chunk.columns]
            chunk['ts'] = pd.to_datetime(chunk['Timestamp'], errors='coerce')
            chunk = chunk.dropna(subset=['ts'])
            chunk['bt'] = chunk['ts'].dt.floor(f'{self.bar_seconds}s')
            chunk['px'] = (chunk['Price'] / 0.25).round() * 0.25
            side_map = {'Buy': 1, 'Sell': -1, 'Unknown': 0}
            chunk['s'] = chunk['Side'].astype(str).str.strip().map(lambda x: side_map.get(x, 0)).astype('int8')
            prices, sides = chunk['px'].values, chunk['s'].values.copy()
            for i in range(len(chunk)):
                if sides[i] == 0:
                    if prices[i] > last_p: sides[i] = 1
                    elif prices[i] < last_p: sides[i] = -1
                    else: sides[i] = last_s if last_p != 0 else 0
                last_p, last_s = prices[i], sides[i]
            chunk['bv'] = np.where(sides < 0, chunk['Volume'], 0)
            chunk['av'] = np.where(sides > 0, chunk['Volume'], 0)
            l_agg = chunk.groupby(['bt', 'px']).agg({'bv': 'sum', 'av': 'sum'}).reset_index()
            o_agg = chunk.groupby('bt').agg({'Price': ['first', 'max', 'min', 'last']})
            for r in l_agg.itertuples():
                if r.bt not in bars_dict:
                    o = o_agg.loc[r.bt]
                    bars_dict[r.bt] = {'o': o[('Price','first')], 'h': o[('Price','max')], 'l': o[('Price','min')], 'c': o[('Price','last')], 'lvls': {}}
                else:
                    o = o_agg.loc[r.bt]; bars_dict[r.bt]['h'] = max(bars_dict[r.bt]['h'], o[('Price','max')])
                    bars_dict[r.bt]['l'] = min(bars_dict[r.bt]['l'], o[('Price','min')]); bars_dict[r.bt]['c'] = o[('Price','last')]
                lvls = bars_dict[r.bt]['lvls']
                if r.px not in lvls: lvls[r.px] = [r.bv, r.av]
                else: lvls[r.px][0] += r.bv; lvls[r.px][1] += r.av
        rows = []
        for bt, d in bars_dict.items():
            for px, v in d['lvls'].items():
                rows.append({'BarTime': bt, 'Price': px, 'BidVol': v[0], 'AskVol': v[1], 'Open': d['o'], 'High': d['h'], 'Low': d['l'], 'Close': d['c']})
        return pd.DataFrame(rows)

    def _build_bars(self, df: pd.DataFrame):
        grouped = df.groupby('BarTime')
        ohlc = grouped.agg({'Open':'first','High':'first','Low':'first','Close':'last','BidVol':'sum','AskVol':'sum'}).reset_index().sort_values('BarTime')
        ohlc['Delta'] = ohlc['AskVol'] - ohlc['BidVol']; ohlc['CVD'] = ohlc['Delta'].cumsum()
        ohlc['SMA50'] = ohlc['Close'].rolling(50).mean(); ohlc['EMA20'] = ohlc['Close'].ewm(span=20, adjust=False).mean()
        ohlc['x'] = range(len(ohlc)); self.ohlc_df = ohlc
        x_map = ohlc.set_index('BarTime')['x'].to_dict()
        self.bars = []
        for bt, grp in sorted(grouped):
            x = x_map[bt]; lvls = {r.Price: (r.BidVol, r.AskVol) for r in grp.itertuples()}
            r = ohlc[ohlc['x'] == x].iloc[0]; self.bars.append(Bar(x=x, time=bt, open=r.Open, high=r.High, low=r.Low, close=r.Close, levels=lvls))

    def _load_log(self):
        df = pd.read_csv(self.log_path, dtype=str); df.columns = [c.strip() for c in df.columns]
        outcomes = {str(r.get('GateReason','')): 'WIN' if 'TARGET' in str(r.get('Label','')) else 'LOSS' for _, r in df[df['Tag'] == 'TOUCH_OUTCOME'].iterrows()}
        sigs = df[df['Tag'].isin(['SIGNAL_ACCEPTED', 'SIGNAL_REJECTED'])]
        self.sources = ["All"] + sorted(sigs['Source'].dropna().unique().tolist())
        self.signals = []
        for _, r in sigs.iterrows():
            ts = pd.to_datetime(r['Timestamp']); bt = ts.floor(f'{self.bar_seconds}s'); match = self.ohlc_df[self.ohlc_df['BarTime'] == bt]
            if not match.empty:
                acc = (r['Tag'] == 'SIGNAL_ACCEPTED'); gate = f"{r.get('Source','')}:{r.get('Direction','')}:{r.get('Bar','')}"
                self.signals.append(Signal(ts, r.get('Direction',''), r.get('Source',''), float(r.get('EntryPrice',0)),
                    outcomes.get(gate, 'REJECTED' if not acc else 'PENDING'), match['x'].iloc[0], acc))

# ─────────────────────────────────────────────────────────────────────────────
# VISUAL ITEMS
# ─────────────────────────────────────────────────────────────────────────────
class CandlestickItem(pg.GraphicsObject):
    def __init__(self, engine, config):
        super().__init__(); self.engine = engine; self.config = config; self.picture = QtGui.QPicture()
    def generate(self):
        self.picture = QtGui.QPicture(); p = QtGui.QPainter(self.picture)
        for b in self.engine.bars:
            bull = b.close >= b.open; color = (38, 166, 154) if bull else (239, 83, 80)
            p.setPen(pg.mkPen(color, width=1)); p.drawLine(QtCore.QPointF(b.x, b.low), QtCore.QPointF(b.x, b.high))
            if bull: p.setBrush(QtCore.Qt.NoBrush)
            else: p.setBrush(pg.mkBrush(color))
            p.drawRect(QtCore.QRectF(b.x - 0.33, b.open, 0.66, b.close - b.open))
        p.end()
    def paint(self, p, *args): p.drawPicture(0, 0, self.picture)
    def boundingRect(self): 
        if self.engine.ohlc_df.empty: return QtCore.QRectF(0,0,1,1)
        df = self.engine.ohlc_df; return QtCore.QRectF(0, df.Low.min(), len(self.engine.bars), df.High.max() - df.Low.min())

class FootprintLayer(pg.GraphicsObject):
    def __init__(self, engine, config):
        super().__init__(); self.engine = engine; self.config = config
    def paint(self, p, option, widget):
        rect = option.exposedRect
        if rect.width() > 35: return
        p.setFont(QtGui.QFont(self.config.font_family, self.config.font_size - 2))
        x0, x1 = int(rect.left()), int(rect.right()) + 1
        for x in range(max(0, x0), min(len(self.engine.bars), x1)):
            bar = self.engine.bars[x]
            for px, (bv, av) in bar.levels.items():
                if px < rect.top() or px > rect.bottom(): continue
                p.setPen(pg.mkPen((255, 80, 80) if bv > av*3 else (80, 255, 80) if av > bv*3 else self.config.color_text))
                p.drawText(QtCore.QRectF(bar.x - 0.45, px - 0.125, 0.9, 0.25), QtCore.Qt.AlignCenter, f"{int(bv)}x{int(av)}")
    def boundingRect(self): 
        if self.engine.ohlc_df.empty: return QtCore.QRectF(0,0,1,1)
        df = self.engine.ohlc_df; return QtCore.QRectF(0, df.Low.min(), len(self.engine.bars), df.High.max() - df.Low.min())

# ─────────────────────────────────────────────────────────────────────────────
# MAIN UI
# ─────────────────────────────────────────────────────────────────────────────
class TradingUI(QtWidgets.QMainWindow):
    def __init__(self, tic, log):
        super().__init__(); self.config = ChartConfig(); self.engine = TradingEngine(Path(tic), Path(log) if log else None)
        self.engine.set_timeframe(self.config.bar_minutes); self.engine.load()
        self.setWindowTitle(f"Terminal — {self.engine.tic_path.name}"); self.resize(1650, 1000)
        self.cw = QtWidgets.QWidget(); self.setCentralWidget(self.cw)
        self.main_window_layout = QtWidgets.QHBoxLayout(self.cw); self.main_window_layout.setContentsMargins(0,0,0,0); self.main_window_layout.setSpacing(0)
        self._init_sidebar()
        self.gw = pg.GraphicsLayoutWidget(); self.gw.setBackground(self.config.color_bg); self.main_window_layout.addWidget(self.gw, 5)
        
        self.p1 = self.gw.addPlot(row=0, col=0, axisItems={'bottom': TimeAxisItem(self.engine, orientation='bottom')})
        self.p2 = self.gw.addPlot(row=1, col=0, axisItems={'bottom': TimeAxisItem(self.engine, orientation='bottom')})
        self.p2.setMaximumHeight(130); self.p2.setXLink(self.p1)
        
        self.v_line = pg.InfiniteLine(angle=90, movable=False, pen=pg.mkPen(self.config.color_crosshair))
        self.h_line = pg.InfiniteLine(angle=0, movable=False, pen=pg.mkPen(self.config.color_crosshair))
        self.p1.addItem(self.v_line, ignoreBounds=True); self.p1.addItem(self.h_line, ignoreBounds=True)
        
        self.price_label = NinjaPriceLabel(); self.time_label = NinjaTimeLabel()
        # Add labels to the SCENE instead of the plot so they don't get clipped
        self.gw.scene().addItem(self.price_label); self.gw.scene().addItem(self.time_label)
        
        self.chart_item = CandlestickItem(self.engine, self.config); self.fp_item = FootprintLayer(self.engine, self.config)
        self.p1.addItem(self.chart_item); self.p1.addItem(self.fp_item)
        self.sma_line = pg.PlotCurveItem(pen=pg.mkPen(255,211,25,width=1.2)); self.ema_line = pg.PlotCurveItem(pen=pg.mkPen(33,150,243,width=1.2))
        self.cvd_curve = pg.PlotCurveItem(pen=pg.mkPen((33,150,243), width=1.5)); self.p2.addItem(self.cvd_curve)
        self.legend = pg.TextItem(anchor=(0,0)); self.p1.addItem(self.legend, ignoreBounds=True)
        
        self.p1.scene().sigMouseMoved.connect(self.on_mouse_moved)
        self.sp_win = pg.ScatterPlotItem(symbol='t', size=12, brush=pg.mkBrush(38,166,154), pen=pg.mkPen(255,255,255,100))
        self.sp_loss = pg.ScatterPlotItem(symbol='t1', size=12, brush=pg.mkBrush(239,83,80), pen=pg.mkPen(255,255,255,100))
        self.sp_rej = pg.ScatterPlotItem(symbol='x', size=10, brush=pg.mkBrush(255,211,25), pen=pg.mkPen(255,255,255,100))
        self.sp_pending = pg.ScatterPlotItem(symbol='t', size=12, brush=pg.mkBrush(0,150,255), pen=pg.mkPen(255,255,255,100))
        for item in [self.sp_win, self.sp_loss, self.sp_rej, self.sp_pending]: self.p1.addItem(item)
        
        self.refresh_ui_items(); self.apply_config()
        if self.engine.bars:
            lx = len(self.engine.bars); self.p1.setXRange(max(0, lx - 70), lx + self.config.right_side_margin)
            lb = self.engine.bars[-1]; self.p1.setYRange(lb.low - 5, lb.high + 5)

    def _init_sidebar(self):
        self.tabs = QtWidgets.QTabWidget(); self.tabs.setFixedWidth(320)
        prop_scroll = QtWidgets.QScrollArea(); prop_scroll.setWidgetResizable(True)
        self.sidebar_widget = QtWidgets.QWidget(); self.sidebar_widget.setStyleSheet("background: #1a1a1a; color: #ccc; font-size: 11px;")
        lay = QtWidgets.QVBoxLayout(self.sidebar_widget); lay.setAlignment(QtCore.Qt.AlignTop)
        def group(title):
            gb = QtWidgets.QGroupBox(title); gb.setStyleSheet("QGroupBox { font-weight: bold; border: 1px solid #333; margin-top: 15px; padding-top: 10px; }")
            l = QtWidgets.QVBoxLayout(gb); lay.addWidget(gb); return l
        def row(l, label, w):
            h = QtWidgets.QHBoxLayout(); h.addWidget(QtWidgets.QLabel(label)); h.addWidget(w); l.addLayout(h)
        g_tf = group("Data Controls")
        self.cb_tf = QtWidgets.QComboBox(); self.cb_tf.addItems(["1 Minute", "3 Minute", "5 Minute", "15 Minute"]); self.cb_tf.setCurrentText("5 Minute"); row(g_tf, "Timeframe", self.cb_tf)
        g_sig_f = group("Outcome Toggles")
        self.chk_win = QtWidgets.QCheckBox("Show Wins"); self.chk_win.setChecked(True); g_sig_f.addWidget(self.chk_win)
        self.chk_loss = QtWidgets.QCheckBox("Show Losses"); self.chk_loss.setChecked(True); g_sig_f.addWidget(self.chk_loss)
        self.chk_rej = QtWidgets.QCheckBox("Show Rejected"); self.chk_rej.setChecked(False); g_sig_f.addWidget(self.chk_rej)
        for c in [self.chk_win, self.chk_loss, self.chk_rej]: c.toggled.connect(self.update_signal_visibility)
        g_view = group("View")
        self.chk_cvd = QtWidgets.QCheckBox("Show CVD Panel"); self.chk_cvd.setChecked(True); self.chk_cvd.toggled.connect(self.apply_config); g_view.addWidget(self.chk_cvd)
        self.s_margin = QtWidgets.QSpinBox(); self.s_margin.setValue(10); row(g_view, "Right Margin", self.s_margin)
        g_ind = group("Indicators")
        self.c_sma = QtWidgets.QCheckBox("SMA 50"); self.c_sma.toggled.connect(self.apply_config); g_ind.addWidget(self.c_sma)
        self.c_ema = QtWidgets.QCheckBox("EMA 20"); self.c_ema.toggled.connect(self.apply_config); g_ind.addWidget(self.c_ema)
        btn = QtWidgets.QPushButton("APPLY & RELOAD"); btn.setStyleSheet("background: #285; font-weight: bold; padding: 10px; margin-top: 10px;"); btn.clicked.connect(self.handle_reload); lay.addWidget(btn)
        prop_scroll.setWidget(self.sidebar_widget); self.tabs.addTab(prop_scroll, "Properties")
        sig_pane = QtWidgets.QWidget(); sig_lay = QtWidgets.QVBoxLayout(sig_pane); sig_lay.setContentsMargins(0,0,0,0)
        cat_box = QtWidgets.QWidget(); cat_lay = QtWidgets.QHBoxLayout(cat_box); cat_lay.setContentsMargins(5,5,5,5)
        cat_lay.addWidget(QtWidgets.QLabel("Category:"))
        self.cb_src = QtWidgets.QComboBox(); self.cb_src.addItems(self.engine.sources); self.cb_src.currentIndexChanged.connect(self.update_signal_visibility); cat_lay.addWidget(self.cb_src)
        sig_lay.addWidget(cat_box)
        self.sig_list = QtWidgets.QListWidget(); self.sig_list.setStyleSheet("background: #1a1a1a; color: #ddd; border: none; font-size: 10px; outline: none;")
        self.sig_list.itemClicked.connect(self.jump_to_signal); sig_lay.addWidget(self.sig_list)
        self.tabs.addTab(sig_pane, "Signal List"); self.main_window_layout.addWidget(self.tabs)

    def jump_to_signal(self, item):
        sig = item.data(QtCore.Qt.UserRole)
        self.p1.setXRange(sig.x - 30, sig.x + 30); self.p1.setYRange(sig.entry - 10, sig.entry + 10)

    def populate_signal_list(self):
        self.sig_list.clear(); src_filter = self.cb_src.currentText()
        win, loss, rej = self.chk_win.isChecked(), self.chk_loss.isChecked(), self.chk_rej.isChecked()
        for s in self.engine.signals:
            if src_filter != "All" and s.source != src_filter: continue
            if s.outcome == 'WIN' and not win: continue
            if s.outcome == 'LOSS' and not loss: continue
            if s.outcome == 'REJECTED' and not rej: continue
            item = QtWidgets.QListWidgetItem(f"[{s.ts.strftime('%m-%d %H:%M:%S')}] {s.source} | {s.direction} | {s.outcome}")
            if s.outcome == 'WIN': item.setForeground(QtGui.QColor(100, 255, 100))
            elif s.outcome == 'LOSS': item.setForeground(QtGui.QColor(255, 100, 100))
            elif s.outcome == 'REJECTED': item.setForeground(QtGui.QColor(255, 255, 100))
            item.setData(QtCore.Qt.UserRole, s); self.sig_list.addItem(item)

    def handle_reload(self):
        tf_map = {"1 Minute": 1, "3 Minute": 3, "5 Minute": 5, "15 Minute": 15}; new_tf = tf_map[self.cb_tf.currentText()]
        if new_tf != self.config.bar_minutes:
            self.config.bar_minutes = new_tf; self.engine.set_timeframe(new_tf); self.engine.load()
            self.cb_src.clear(); self.cb_src.addItems(self.engine.sources); self.refresh_ui_items()
        self.config.right_side_margin = self.s_margin.value(); self.apply_config()

    def update_signal_visibility(self):
        win, loss, rej, src = self.chk_win.isChecked(), self.chk_loss.isChecked(), self.chk_rej.isChecked(), self.cb_src.currentText()
        def filter_group(outcome_list, source_str):
            xs, ys = [], []
            for s in self.engine.signals:
                if s.outcome in outcome_list and (source_str == "All" or s.source == source_str):
                    xs.append(s.x); ys.append(s.entry)
            return xs, ys
        w_x, w_y = filter_group(['WIN'], src); l_x, l_y = filter_group(['LOSS'], src); r_x, r_y = filter_group(['REJECTED'], src); p_x, p_y = filter_group(['PENDING'], src)
        self.sp_win.setData(x=w_x, y=w_y) if win else self.sp_win.setData(x=[], y=[])
        self.sp_loss.setData(x=l_x, y=l_y) if loss else self.sp_loss.setData(x=[], y=[])
        self.sp_rej.setData(x=r_x, y=r_y) if rej else self.sp_rej.setData(x=[], y=[])
        self.sp_pending.setData(x=p_x, y=p_y) if (win or loss) else self.sp_pending.setData(x=[], y=[])
        self.populate_signal_list()

    def refresh_ui_items(self):
        self.chart_item.generate()
        self.sma_line.setData(self.engine.ohlc_df.x.values, self.engine.ohlc_df.SMA50.values)
        self.ema_line.setData(self.engine.ohlc_df.x.values, self.engine.ohlc_df.EMA20.values)
        self.cvd_curve.setData(self.engine.ohlc_df.x.values, self.engine.ohlc_df.CVD.values)
        self.update_signal_visibility()

    def apply_config(self):
        c = self.config; self.gw.setBackground(c.color_bg); show_cvd = self.chk_cvd.isChecked()
        f = QtGui.QFont(c.font_family, 12, QtGui.QFont.Bold) # 16px equivalent in some systems is 12pt bold
        if show_cvd: self.p2.show(); self.p2.setMaximumHeight(130); self.p1.getAxis('bottom').setStyle(showValues=False)
        else: self.p2.hide(); self.p2.setMaximumHeight(0); self.p1.getAxis('bottom').setStyle(showValues=True)
        for p in [self.p1, self.p2]:
            p.showAxis('left', False); p.showAxis('right', True)
            ax_r = p.getAxis('right')
            ax_r.setPen(pg.mkPen(c.color_axis)); ax_r.setTextPen(c.color_text)
            ax_r.setTickFont(f)
            ax_r.setWidth(80) # Widen price axis strip
            
            p.showGrid(x=c.grid_v_visible, y=c.grid_h_visible, alpha=0.2)
            ax_b = p.getAxis('bottom')
            ax_b.setPen(pg.mkPen(c.color_axis)); ax_b.setTextPen(c.color_text)
            ax_b.setTickFont(f)
        if self.c_sma.isChecked(): self.p1.addItem(self.sma_line)
        else: self.p1.removeItem(self.sma_line)
        if self.c_ema.isChecked(): self.p1.addItem(self.ema_line)
        else: self.p1.removeItem(self.ema_line)
        self.p1.update()

    def on_mouse_moved(self, pos):
        if self.p1.sceneBoundingRect().contains(pos):
            m = self.p1.vb.mapSceneToView(pos); self.v_line.setPos(m.x()); self.h_line.setPos(m.y()); idx = int(m.x())
            
            # Absolute scene coordinate for the right axis
            ax_right = self.p1.getAxis('right')
            ax_bottom = self.p2.getAxis('bottom') if self.p2.isVisible() else self.p1.getAxis('bottom')
            
            # Map view coordinates to scene pixels
            pixel_pos = self.p1.vb.mapViewToScene(m)
            
            # Position Price label on the Right Axis strip
            self.price_label.setPos(ax_right.scenePos().x(), pixel_pos.y())
            self.price_label.setText(f"{m.y():.2f}")
            
            # Position Time label on the Bottom Axis strip
            self.time_label.setPos(pixel_pos.x(), ax_bottom.scenePos().y())
            
            if 0 <= idx < len(self.engine.bars):
                b = self.engine.bars[idx]
                dt_str = b.time.strftime('%m-%d %H:%M')
                self.time_label.setText(dt_str)
                self.legend.setHtml(f"<div style='background:rgba(0,0,0,150); padding:5px; color:#ddd; font-family:Consolas; font-size:10pt;'>"
                                    f"<b>{b.time.strftime('%Y-%m-%d %H:%M')}</b> | O:{b.open:.2f} H:{b.high:.2f} L:{b.low:.2f} C:{b.close:.2f}</div>")
                self.legend.setPos(self.p1.vb.viewRect().left(), self.p1.vb.viewRect().top())

if __name__ == "__main__":
    app = QtWidgets.QApplication(sys.argv); tic, log = "backtest/TicData.csv", "backtest/Log.csv"
    if len(sys.argv) > 1: tic = sys.argv[1]; log = sys.argv[2] if len(sys.argv) > 2 else None
    ui = TradingUI(tic, log); ui.show(); sys.exit(app.exec_())
