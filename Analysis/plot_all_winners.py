"""
plot_all_winners.py  —  Complete view of ALL winners/losers on a real TicData chart.

TicData is streamed in chunks and aggregated into bars — no OOM even for months of data.

Panels:
  1. Price chart  — real candlesticks from TicData + winner/loser overlays
  2. Equity curve — cumulative sim PnL over time
  3. CVD          — cumulative volume delta
  4. Daily PnL    — bar chart per day
  Side: source breakdown stats

Usage:
    python plot_all_winners.py --filter winners
    python plot_all_winners.py --filter losers
    python plot_all_winners.py --filter all
    python plot_all_winners.py --filter all --bar-seconds 900  # 15-min bars
    python plot_all_winners.py --filter winners --source ORB_Value_v2
"""

import sys, re, argparse
from dataclasses import dataclass, field
from typing import List, Optional, Dict, Tuple
from pathlib import Path

import numpy as np
import pandas as pd
import pyqtgraph as pg
from pyqtgraph.Qt import QtCore, QtGui, QtWidgets

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

LOG_PATH = Path(__file__).parent.parent / 'backtest' / 'Log.csv'
TIC_PATH = Path(__file__).parent.parent / 'backtest' / 'TicData.csv'

C_BG    = (19,  23,  34)
C_BULL  = (38,  166, 154)
C_BEAR  = (239, 83,  80)
C_WIN   = (38,  200, 130)
C_LOSS  = (239, 83,  80)
C_ENTRY = (200, 200, 255)
C_T1    = (38,  166, 154)
C_STOP  = (239, 83,  80)
C_TEXT  = (210, 215, 220)

_PNL_RE = re.compile(r'SIM_PNL=([-\d.]+)')
_MFE_RE = re.compile(r'MFE=([\d.]+)')
_MAE_RE = re.compile(r'MAE=([\d.]+)')
_BTH_RE = re.compile(r'BARS_TO_HIT=(\d+)')
_GNM_RE = re.compile(r':(\d+)(?::REJ)?$')

_SRC_COLORS = [
    (100, 180, 255), (255, 180,  60), ( 80, 220, 120), (220,  80, 220),
    (255, 120,  80), ( 80, 200, 200), (200, 200,  80), (180, 100, 255),
    (255, 200, 100), (100, 255, 200), (200, 100, 100), (100, 150, 200),
]


# ═══════════════════════════════════════════════════════════════════════════════
@dataclass
class Trade:
    time:       pd.Timestamp
    direction:  str
    source:     str
    cond_set:   str
    score:      int
    entry:      float
    stop:       float
    t1:         float
    outcome:    str
    sim_pnl:    float
    mfe:        float
    mae:        float
    bars_to_hit:int
    x_idx:      int = -1
    exit_x:     int = -1


@dataclass
class Bar:
    x_idx:   int
    time:    pd.Timestamp
    open:    float
    high:    float
    low:     float
    close:   float
    buy_vol: int
    sell_vol:int
    delta:   int
    cvd:     float = 0.0


# ═══════════════════════════════════════════════════════════════════════════════
# LOAD TRADES
# ═══════════════════════════════════════════════════════════════════════════════
def load_trades(outcome_filter: str, source_filter: Optional[str]) -> List[Trade]:
    df = pd.read_csv(LOG_PATH, dtype=str)
    df.columns = [c.strip() for c in df.columns]
    df['Timestamp'] = pd.to_datetime(df['Timestamp'], errors='coerce')

    sig_rows = df[df['Tag'] == 'SIGNAL_ACCEPTED']

    def _f(row, col, default=0.0):
        try: return float(str(row.get(col, '')).strip())
        except: return default

    sig_map: Dict[str, dict] = {}
    for _, r in sig_rows.iterrows():
        bar  = int(_f(r, 'Bar'))
        cond = str(r.get('ConditionSetId', '')).strip()
        sig_map[f"{cond}:{bar}"] = dict(
            time      = r['Timestamp'],
            direction = str(r.get('Direction', '')).strip(),
            source    = str(r.get('Source', '')).strip(),
            cond_set  = cond,
            score     = int(_f(r, 'Score')),
            entry     = _f(r, 'EntryPrice'),
            stop      = _f(r, 'StopPrice'),
            t1        = _f(r, 'T1Price'),
        )

    trades: List[Trade] = []
    for _, r in df[df['Tag'] == 'TOUCH_OUTCOME'].iterrows():
        gate  = str(r.get('GateReason', ''))
        if ':REJ' in gate:
            continue
        label   = str(r.get('Label', '')).strip()
        outcome = 'WIN' if label == 'TARGET' else 'LOSS' if label == 'STOP' else 'NEITHER'

        if outcome_filter != 'all':
            wanted = 'WIN' if outcome_filter == 'winners' else 'LOSS'
            if outcome != wanted:
                continue

        gm = _GNM_RE.search(gate)
        if not gm:
            continue
        src_bar  = int(gm[1])
        cond_key = gate.split(':')[0]
        key      = f"{cond_key}:{src_bar}"
        if key not in sig_map:
            key = next((k for k in sig_map if k.endswith(f':{src_bar}')), None)
        if not key or key not in sig_map:
            continue

        s = sig_map[key]
        if source_filter and s['source'].lower() != source_filter.lower():
            if s['cond_set'].lower() != source_filter.lower():
                continue

        det = str(r.get('Detail', ''))
        pm  = _PNL_RE.search(det); mfe = _MFE_RE.search(det)
        mae = _MAE_RE.search(det); bth = _BTH_RE.search(det)

        trades.append(Trade(
            time       = s['time'],
            direction  = s['direction'],
            source     = s['source'],
            cond_set   = s['cond_set'],
            score      = s['score'],
            entry      = s['entry'],
            stop       = s['stop'],
            t1         = s['t1'],
            outcome    = outcome,
            sim_pnl    = float(pm[1])  if pm  else 0.0,
            mfe        = float(mfe[1]) if mfe else 0.0,
            mae        = float(mae[1]) if mae else 0.0,
            bars_to_hit= int(bth[1])   if bth else 0,
        ))

    trades.sort(key=lambda t: t.time)
    wins = sum(1 for t in trades if t.outcome == 'WIN')
    loss = sum(1 for t in trades if t.outcome == 'LOSS')
    net  = sum(t.sim_pnl for t in trades)
    print(f"  {len(trades)} trades  W={wins} L={loss}  "
          f"win%={wins/max(wins+loss,1)*100:.1f}%  net=${net:,.0f}")
    return trades


# ═══════════════════════════════════════════════════════════════════════════════
# STREAM TICDATA → BARS  (no full load, aggregates on the fly)
# ═══════════════════════════════════════════════════════════════════════════════
def stream_bars(bar_seconds: int = 900) -> List[Bar]:
    """Read TicData.csv in chunks, emit complete bars without storing all ticks."""
    CHUNK   = 300_000
    bars    = []
    x_idx   = 0
    cvd     = 0.0
    pending: Dict = {}   # partial bar accumulator keyed by bar-time

    # tick-rule state
    last_p, last_s = 0.0, 0

    def _flush(bt, acc) -> Bar:
        nonlocal cvd, x_idx
        delta = acc['bvol'] - acc['svol']
        cvd  += delta
        b = Bar(x_idx=x_idx, time=bt,
                open=acc['open'], high=acc['high'],
                low=acc['low'],   close=acc['close'],
                buy_vol=acc['bvol'], sell_vol=acc['svol'],
                delta=delta, cvd=cvd)
        x_idx += 1
        return b

    total = 0
    print(f"  Streaming TicData in {CHUNK:,}-row chunks ...")
    for chunk in pd.read_csv(TIC_PATH, chunksize=CHUNK,
                             dtype={'Price': 'float32', 'Volume': 'float32',
                                    'Timestamp': str, 'Side': str}):
        chunk.columns = [c.strip() for c in chunk.columns]
        chunk['ts'] = pd.to_datetime(chunk['Timestamp'], errors='coerce')
        chunk = chunk.dropna(subset=['ts'])
        chunk['Price']  = pd.to_numeric(chunk['Price'],  errors='coerce').fillna(0)
        chunk['Volume'] = pd.to_numeric(chunk['Volume'], errors='coerce').fillna(1)

        epoch = chunk['ts'].iloc[0].normalize()
        secs  = (chunk['ts'] - epoch).dt.total_seconds().values
        bt_secs = (secs // bar_seconds * bar_seconds).astype('int64')

        prices  = chunk['Price'].values
        volumes = chunk['Volume'].values

        for i in range(len(chunk)):
            p   = float(prices[i])
            vol = int(volumes[i])
            bt  = epoch + pd.Timedelta(seconds=int(bt_secs[i]))

            # tick rule
            if   p > last_p: s =  1
            elif p < last_p: s = -1
            else:             s = last_s
            if last_p == 0: s = 0
            last_p, last_s = p, s

            bvol = vol if s > 0 else 0
            svol = vol if s < 0 else 0

            if bt not in pending:
                # flush any older open bar
                if pending:
                    old_bt = next(iter(pending))
                    if old_bt < bt:
                        bars.append(_flush(old_bt, pending.pop(old_bt)))
                pending[bt] = dict(open=p, high=p, low=p, close=p,
                                   bvol=bvol, svol=svol)
            else:
                acc = pending[bt]
                if p > acc['high']: acc['high'] = p
                if p < acc['low']:  acc['low']  = p
                acc['close']  = p
                acc['bvol']  += bvol
                acc['svol']  += svol

        total += len(chunk)
        if total % 3_000_000 < CHUNK:
            last_ts = chunk['ts'].iloc[-1]
            print(f"    {total:>10,} rows processed  @ {last_ts:%Y-%m-%d %H:%M}")

    # flush remaining
    for bt, acc in sorted(pending.items()):
        bars.append(_flush(bt, acc))

    print(f"  {len(bars):,} bars built  "
          f"{bars[0].time:%Y-%m-%d} -> {bars[-1].time:%Y-%m-%d}")
    return bars


# ═══════════════════════════════════════════════════════════════════════════════
# ASSIGN X INDICES
# ═══════════════════════════════════════════════════════════════════════════════
def assign_x(trades: List[Trade], bars: List[Bar], bar_seconds: int):
    time_map = {b.time: b.x_idx for b in bars}
    for t in trades:
        bt = t.time.floor(f'{bar_seconds}s')
        x  = time_map.get(bt)
        if x is None:
            diffs = sorted((abs((b.time - t.time).total_seconds()), b.x_idx) for b in bars)
            x = diffs[0][1] if diffs else -1
        t.x_idx  = x if x is not None else -1
        t.exit_x = (t.x_idx + t.bars_to_hit) if t.x_idx >= 0 else -1


# ═══════════════════════════════════════════════════════════════════════════════
# GRAPHICS ITEMS
# ═══════════════════════════════════════════════════════════════════════════════
class CandleItem(pg.GraphicsObject):
    def __init__(self, bars: List[Bar]):
        super().__init__()
        self._bars = bars
        self._pic  = QtGui.QPicture()
        self._build()
        xs = [b.x_idx for b in bars]
        ys = [b.low for b in bars] + [b.high for b in bars]
        self._br = QtCore.QRectF(min(xs)-1, min(ys)-5,
                                  max(xs)-min(xs)+2, max(ys)-min(ys)+10)

    def _build(self):
        p  = QtGui.QPainter(self._pic)
        hw = 0.38
        for b in self._bars:
            col = C_BULL if b.close >= b.open else C_BEAR
            p.setPen(pg.mkPen(col, width=0.5))
            p.drawLine(QtCore.QPointF(b.x_idx, b.low),
                       QtCore.QPointF(b.x_idx, b.high))
            top = max(b.open, b.close)
            bot = min(b.open, b.close)
            p.setPen(pg.mkPen(None))
            p.setBrush(pg.mkBrush(QtGui.QColor(*col, 200)))
            p.drawRect(QtCore.QRectF(b.x_idx - hw, bot,
                                     hw*2, max(top - bot, 0.125)))
        p.end()

    def paint(self, painter, option, widget):
        painter.drawPicture(0, 0, self._pic)

    def boundingRect(self): return self._br


class TradeOverlayItem(pg.GraphicsObject):
    def __init__(self, trades: List[Trade]):
        super().__init__()
        self._trades = [t for t in trades if t.x_idx >= 0]
        self._pic    = QtGui.QPicture()
        self._build()
        if self._trades:
            xs = [t.x_idx for t in self._trades]
            ys = ([t.entry for t in self._trades] +
                  [t.stop  for t in self._trades] +
                  [t.t1    for t in self._trades if t.t1])
            self._br = QtCore.QRectF(min(xs)-2, min(ys)-10,
                                      max(xs)-min(xs)+30, max(ys)-min(ys)+20)
        else:
            self._br = QtCore.QRectF(0, 0, 1, 1)

    def _build(self):
        p   = QtGui.QPainter(self._pic)
        ext = 6
        for t in self._trades:
            x0  = t.x_idx
            x1  = x0 + max(t.bars_to_hit + 1, ext)
            col = C_WIN if t.outcome == 'WIN' else C_LOSS

            # Shaded zone
            lo = min(t.entry, t.stop or t.entry, t.t1 or t.entry)
            hi = max(t.entry, t.stop or t.entry, t.t1 or t.entry)
            p.setPen(pg.mkPen(None))
            p.setBrush(pg.mkBrush(QtGui.QColor(*col, 15)))
            p.drawRect(QtCore.QRectF(x0, lo, x1 - x0, hi - lo))

            # Entry line
            p.setPen(pg.mkPen(C_ENTRY, width=1.0,
                              style=QtCore.Qt.DashLine))
            p.drawLine(QtCore.QPointF(x0, t.entry),
                       QtCore.QPointF(x1, t.entry))

            if t.stop:
                p.setPen(pg.mkPen(C_STOP, width=0.8,
                                  style=QtCore.Qt.DotLine))
                p.drawLine(QtCore.QPointF(x0, t.stop),
                           QtCore.QPointF(x1, t.stop))

            if t.t1:
                p.setPen(pg.mkPen(C_T1, width=0.8,
                                  style=QtCore.Qt.DotLine))
                p.drawLine(QtCore.QPointF(x0, t.t1),
                           QtCore.QPointF(x1, t.t1))

            # Vertical spine
            p.setPen(pg.mkPen(col, width=1.8))
            p.drawLine(QtCore.QPointF(x0, t.stop or t.entry - 5),
                       QtCore.QPointF(x0, t.t1  or t.entry + 5))
        p.end()

    def paint(self, painter, option, widget):
        painter.drawPicture(0, 0, self._pic)

    def boundingRect(self): return self._br


class TradeLabelItem(pg.GraphicsObject):
    def __init__(self, trades: List[Trade], src_col: Dict[str, tuple]):
        super().__init__()
        self._trades  = [t for t in trades if t.x_idx >= 0]
        self._src_col = src_col
        if self._trades:
            xs = [t.x_idx for t in self._trades]
            ys = [t.entry  for t in self._trades]
            self._br = QtCore.QRectF(min(xs)-1, min(ys)-20,
                                      max(xs)-min(xs)+2, max(ys)-min(ys)+40)
        else:
            self._br = QtCore.QRectF(0, 0, 1, 1)

    def paint(self, painter, option, widget):
        lod = option.levelOfDetailFromTransform(painter.worldTransform())
        if lod < 1.0:
            return
        xform = painter.worldTransform()
        R = 14 if lod < 3 else 18

        for t in self._trades:
            y_off = -2 if t.direction == 'Long' else 2
            pt    = xform.map(QtCore.QPointF(t.x_idx, t.entry + y_off))
            cx, cy = pt.x(), pt.y()
            col   = QtGui.QColor(*C_WIN, 220) if t.outcome == 'WIN' \
                    else QtGui.QColor(*C_LOSS, 220)

            painter.save()
            painter.resetTransform()

            painter.setPen(pg.mkPen(QtGui.QColor(255, 255, 255, 80), width=1))
            painter.setBrush(pg.mkBrush(col))
            painter.drawEllipse(QtCore.QPointF(cx, cy), R, R)

            if lod >= 1.5:
                src = t.source[:6]
                painter.setPen(pg.mkPen(QtGui.QColor(255, 255, 255)))
                painter.setFont(QtGui.QFont("Arial", 5, QtGui.QFont.Bold))
                painter.drawText(QtCore.QRectF(cx-R, cy-R, R*2, R*1.1),
                                 QtCore.Qt.AlignCenter, src)

                pnl_txt = f"${t.sim_pnl:+.0f}"
                c2 = QtGui.QColor(200, 255, 200) if t.sim_pnl >= 0 \
                     else QtGui.QColor(255, 200, 200)
                painter.setPen(pg.mkPen(c2))
                painter.setFont(QtGui.QFont("Arial", 5))
                painter.drawText(QtCore.QRectF(cx-R, cy+R*0.05, R*2, R*0.9),
                                 QtCore.Qt.AlignCenter, pnl_txt)

            painter.restore()

    def boundingRect(self): return self._br


# ═══════════════════════════════════════════════════════════════════════════════
# TIME AXIS
# ═══════════════════════════════════════════════════════════════════════════════
def make_time_axis(bars: List[Bar], max_labels: int = 60) -> List[Tuple]:
    if not bars: return []
    stride  = max(1, len(bars) // max_labels)
    ticks   = []
    prev_d  = None
    for i, b in enumerate(bars):
        if i % stride == 0:
            d   = b.time.date()
            lbl = b.time.strftime('%m/%d') if d != prev_d else b.time.strftime('%H:%M')
            ticks.append((b.x_idx, lbl))
            prev_d = d
    return ticks


# ═══════════════════════════════════════════════════════════════════════════════
# MAIN
# ═══════════════════════════════════════════════════════════════════════════════
def run_chart(outcome_filter: str, source_filter: Optional[str],
              bar_seconds: int):

    print("Loading trades ...")
    trades = load_trades(outcome_filter, source_filter)
    if not trades:
        print("No trades found."); return

    print("Streaming TicData into bars ...")
    bars = stream_bars(bar_seconds)
    if not bars:
        print("No tick data."); return

    assign_x(trades, bars, bar_seconds)
    valid  = [t for t in trades if t.x_idx >= 0]
    wins   = [t for t in valid  if t.outcome == 'WIN']
    losses = [t for t in valid  if t.outcome == 'LOSS']
    net    = sum(t.sim_pnl for t in valid)
    win_pct = len(wins) / max(len(wins)+len(losses), 1) * 100
    print(f"  {len(valid)}/{len(trades)} trades mapped to bars")

    sources = sorted(set(t.source for t in valid))
    src_col = {s: _SRC_COLORS[i % len(_SRC_COLORS)] for i, s in enumerate(sources)}

    # ── App ──────────────────────────────────────────────────────────────────
    app = QtWidgets.QApplication.instance() or QtWidgets.QApplication(sys.argv)
    pg.setConfigOptions(antialias=True, background=pg.mkColor(*C_BG))

    filt_lbl = outcome_filter.upper()
    title = (f"Complete View  |  {filt_lbl}  |  "
             f"{len(wins)}W / {len(losses)}L  ({win_pct:.0f}%)  "
             f"net=${net:,.0f}  |  "
             f"{bars[0].time:%Y-%m-%d} -> {bars[-1].time:%Y-%m-%d}  "
             f"|  {len(bars):,} bars")
    win = pg.GraphicsLayoutWidget(show=True, title=title)
    win.resize(1920, 1060)

    # ── Panel 1: Price + trade overlays ──────────────────────────────────────
    price_plot = win.addPlot(row=0, col=0,
                              title=f"TicData  {bar_seconds//60}-min bars")
    price_plot.showGrid(x=True, y=True, alpha=0.10)
    price_plot.setLabel('left', 'Price')
    price_plot.getAxis('bottom').setStyle(showValues=False)

    price_plot.addItem(CandleItem(bars))
    price_plot.addItem(TradeOverlayItem(valid))
    price_plot.addItem(TradeLabelItem(valid, src_col))

    # Exit markers
    def _scatter(plot, xs, ys, sym, col, sz):
        if not xs: return
        plot.addItem(pg.ScatterPlotItem(
            x=xs, y=ys, symbol=sym, size=sz,
            pen=pg.mkPen('w', width=0.5),
            brush=pg.mkBrush(QtGui.QColor(*col[:3], 220))
        ))

    _scatter(price_plot,
             [t.exit_x for t in wins   if t.exit_x >= 0],
             [t.t1 or t.entry for t in wins   if t.exit_x >= 0],
             'd', C_WIN,  10)
    _scatter(price_plot,
             [t.exit_x for t in losses if t.exit_x >= 0],
             [t.stop or t.entry for t in losses if t.exit_x >= 0],
             'x', C_LOSS, 10)

    # ── Panel 2: CVD ─────────────────────────────────────────────────────────
    cvd_plot = win.addPlot(row=1, col=0)
    cvd_plot.setMaximumHeight(80)
    cvd_plot.showGrid(x=True, y=True, alpha=0.10)
    cvd_plot.setLabel('left', 'CVD', size='7pt')
    cvd_plot.setXLink(price_plot)
    cvd_plot.addItem(pg.InfiniteLine(0, angle=0,
                                     pen=pg.mkPen((180,180,180), width=0.5)))
    xs_arr  = np.array([b.x_idx for b in bars], dtype=float)
    cvd_arr = np.array([b.cvd   for b in bars], dtype=float)
    cvd_plot.plot(xs_arr, cvd_arr, pen=pg.mkPen((100, 180, 255), width=1.2))

    # ── Panel 3: Equity curve ─────────────────────────────────────────────────
    eq_plot = win.addPlot(row=2, col=0, title='Cumulative Sim PnL')
    eq_plot.setMaximumHeight(120)
    eq_plot.showGrid(x=True, y=True, alpha=0.12)
    eq_plot.setLabel('left', 'PnL ($)', size='7pt')
    eq_plot.setXLink(price_plot)

    # Map equity to bar x indices
    eq_xs   = np.array([t.x_idx   for t in valid if t.x_idx >= 0], dtype=float)
    eq_pnls = np.cumsum([t.sim_pnl for t in valid if t.x_idx >= 0])
    if len(eq_xs):
        eq_plot.plot(eq_xs, eq_pnls, pen=pg.mkPen((100,180,255), width=1.5))
        fill_above = pg.FillBetweenItem(
            eq_plot.plot(eq_xs, np.where(eq_pnls>=0, eq_pnls, 0)),
            eq_plot.plot(eq_xs, np.zeros_like(eq_xs)),
            brush=pg.mkBrush(QtGui.QColor(*C_WIN, 40)))
        fill_below = pg.FillBetweenItem(
            eq_plot.plot(eq_xs, np.zeros_like(eq_xs)),
            eq_plot.plot(eq_xs, np.where(eq_pnls<0, eq_pnls, 0)),
            brush=pg.mkBrush(QtGui.QColor(*C_LOSS, 40)))
        eq_plot.addItem(fill_above)
        eq_plot.addItem(fill_below)
    eq_plot.addItem(pg.InfiniteLine(0, angle=0,
                                    pen=pg.mkPen((180,180,180), width=0.5)))

    # ── Side panel: source stats ──────────────────────────────────────────────
    side = win.addPlot(row=0, col=1, rowspan=3)
    side.setMaximumWidth(260)
    side.hideAxis('bottom'); side.hideAxis('left')
    side.setTitle('Trade Stats', size='8pt')

    by_src: Dict[str, dict] = {}
    for t in valid:
        k = by_src.setdefault(t.source, {'w':0,'l':0,'pnl':0.0,'mfe':[]})
        if t.outcome == 'WIN':    k['w']+=1; k['pnl']+=t.sim_pnl; k['mfe'].append(t.mfe)
        elif t.outcome == 'LOSS': k['l']+=1; k['pnl']+=t.sim_pnl

    html = [
        f"<b>{filt_lbl}: {len(valid)} trades</b><br/>",
        f"W {len(wins)} / L {len(losses)}  ({win_pct:.0f}%)<br/>",
        f"Net: <b>${net:,.0f}</b><br/>",
        f"Avg: ${net/max(len(valid),1):.0f}<br/>",
        "<hr/><b>By Source:</b><br/>",
    ]
    for src, v in sorted(by_src.items(), key=lambda x: -x[1]['pnl']):
        wr    = v['w'] / max(v['w']+v['l'], 1) * 100
        avg_m = sum(v['mfe'])/max(len(v['mfe']),1)
        c = '#26c882' if v['pnl'] >= 0 else '#ef5350'
        html.append(
            f"<span style='color:{c}'><b>{src[:18]}</b></span><br/>"
            f"W{v['w']}/L{v['l']} {wr:.0f}%  ${v['pnl']:,.0f}  mfe={avg_m:.0f}<br/>"
        )

    txt = pg.TextItem(html=''.join(html), color=C_TEXT, anchor=(0,0))
    txt.setPos(0, 1)
    side.addItem(txt)
    side.setXRange(0, 1); side.setYRange(0, 1)

    # ── Time axis + layout ────────────────────────────────────────────────────
    eq_plot.getAxis('bottom').setTicks([make_time_axis(bars)])

    win.ci.layout.setRowStretchFactor(0, 6)
    win.ci.layout.setRowStretchFactor(1, 1)
    win.ci.layout.setRowStretchFactor(2, 2)
    win.ci.layout.setColumnStretchFactor(0, 7)
    win.ci.layout.setColumnStretchFactor(1, 1)

    # Initial view — zoom to first trade date
    if valid:
        x_lo = max(0, min(t.x_idx for t in valid) - 20)
        x_hi = max(t.x_idx for t in valid) + 20
        price_plot.setXRange(x_lo, x_hi, padding=0.01)

    print("Chart ready — scroll/zoom to explore. Close window to exit.")
    sys.exit(app.exec())


# ═══════════════════════════════════════════════════════════════════════════════
if __name__ == '__main__':
    ap = argparse.ArgumentParser(description='All winners/losers on TicData chart')
    ap.add_argument('--filter',      default='winners',
                    choices=['all','winners','losers'])
    ap.add_argument('--source',      default=None)
    ap.add_argument('--bar-seconds', type=int, default=900,
                    help='Bar size in seconds (default 900 = 15-min)')
    args = ap.parse_args()
    run_chart(args.filter, args.source, args.bar_seconds)
