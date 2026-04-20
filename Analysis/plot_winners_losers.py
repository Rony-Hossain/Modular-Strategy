#!/usr/bin/env python3
"""
plot_winners_losers.py  —  Overlay winner/loser trades on the TicData footprint chart.

For each accepted trade, the outcome (TARGET=win, STOP=loss) is read from
TOUCH_OUTCOME rows.  The script loads actual tick data for the chosen date,
builds footprint bars, then overlays:

  Winners  (green)  : entry arrow -> T1 line -> exit diamond at T1 price
  Losers   (red)    : entry arrow -> stop line -> exit X at stop price
  Both               : entry line, horizontal bands showing risk/reward zone

Usage:
    python plot_winners_losers.py --date 2026-01-05           # one day
    python plot_winners_losers.py --date 2026-01-05:2026-01-06
    python plot_winners_losers.py --date 2026-01-05 --filter winners
    python plot_winners_losers.py --date 2026-01-05 --filter losers
    python plot_winners_losers.py --date 2026-01-05 --source ORB_Value_v2
    python plot_winners_losers.py --date 2026-01-05 --bar-seconds 300

Requires: pandas numpy pyqtgraph PyQt5
"""

import sys, re, math, argparse
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple
from pathlib import Path

import pandas as pd
import numpy as np
import pyqtgraph as pg
from pyqtgraph.Qt import QtCore, QtGui, QtWidgets

# ─── Paths ────────────────────────────────────────────────────────────────────
ROOT     = Path(__file__).parent.parent
LOG_PATH = ROOT / 'backtest' / 'Log.csv'
TIC_PATH = ROOT / 'backtest' / 'TicData.csv'

# ─── Colors ───────────────────────────────────────────────────────────────────
C_BG     = (19,  23,  34)
C_BULL   = (38,  166, 154)
C_BEAR   = (239, 83,  80)
C_WIN    = (0,   220, 100)
C_LOSS   = (239, 83,  80)
C_ENTRY  = (200, 200, 255)
C_T1     = (38,  166, 154)
C_STOP   = (239, 83,  80)
C_TEXT   = (210, 215, 220)

# ─── Regex ────────────────────────────────────────────────────────────────────
_PNL_RE  = re.compile(r'SIM_PNL=([-\d.]+)')
_MFE_RE  = re.compile(r'MFE=([\d.]+)')
_MAE_RE  = re.compile(r'MAE=([\d.]+)')
_BTH_RE  = re.compile(r'BARS_TO_HIT=(\d+)')
_GNM_RE  = re.compile(r':(\d+)(?::REJ)?$')
_CE_RE   = re.compile(r'CLOSE_END=([\d.]+)')


# ═══════════════════════════════════════════════════════════════════════════════
# DATA CLASSES
# ═══════════════════════════════════════════════════════════════════════════════

@dataclass
class Trade:
    bar_num:     int
    time:        pd.Timestamp
    direction:   str        # 'Long' | 'Short'
    source:      str
    cond_set:    str
    score:       int
    grade:       str
    entry:       float
    stop:        float
    t1:          float
    t2:          float
    outcome:     str        # 'WIN' | 'LOSS' | 'NEITHER'
    sim_pnl:     float
    mfe:         float
    mae:         float
    bars_to_hit: int
    close_end:   float
    x_idx:       int = 0
    exit_x:      int = 0


@dataclass
class FPBar:
    bar_num:  int
    time:     pd.Timestamp
    open:     float
    high:     float
    low:      float
    close:    float
    buy_vol:  int
    sell_vol: int
    delta:    int
    cvd:      float = 0.0
    x_idx:    int   = 0


# ═══════════════════════════════════════════════════════════════════════════════
# PARSE TRADES FROM LOG
# ═══════════════════════════════════════════════════════════════════════════════

def parse_trades(log_path: Path, date_filter: Optional[str],
                 source_filter: Optional[str],
                 outcome_filter: Optional[str]) -> List[Trade]:
    """Return list of accepted trades with outcomes, filtered as requested."""
    df = pd.read_csv(log_path, dtype=str)
    df.columns = [c.strip() for c in df.columns]
    df['Timestamp'] = pd.to_datetime(df['Timestamp'], errors='coerce')

    # Build signal map from ALL SIGNAL_ACCEPTED (no date filter).
    # TOUCH_OUTCOME rows reference bar numbers from the originating session,
    # which can span the previous evening even when filtering for a single day.
    sig_rows = df[df['Tag'] == 'SIGNAL_ACCEPTED']

    # Apply date filter only to TOUCH_OUTCOME rows
    touch_df = df.copy()
    if date_filter:
        parts = date_filter.split(':')
        d0 = pd.Timestamp(parts[0])
        d1 = pd.Timestamp(parts[-1]) + pd.Timedelta(days=1)
        touch_df = touch_df[(touch_df['Timestamp'] >= d0) & (touch_df['Timestamp'] < d1)].reset_index(drop=True)

    def _f(row, col, default=0.0):
        try: return float(str(row.get(col, '')).strip())
        except: return default

    sig_map: Dict[str, dict] = {}
    for _, r in sig_rows.iterrows():
        bar  = int(_f(r, 'Bar'))
        cond = str(r.get('ConditionSetId', '')).strip()
        key  = f"{cond}:{bar}"
        sig_map[key] = dict(
            bar_num   = bar,
            time      = r['Timestamp'],
            direction = str(r.get('Direction', '')).strip(),
            source    = str(r.get('Source', '')).strip(),
            cond_set  = cond,
            score     = int(_f(r, 'Score')),
            grade     = str(r.get('Grade', '')).strip(),
            entry     = _f(r, 'EntryPrice'),
            stop      = _f(r, 'StopPrice'),
            t1        = _f(r, 'T1Price'),
            t2        = _f(r, 'T2Price'),
        )

    # Match TOUCH_OUTCOME back to signals
    trades: List[Trade] = []
    touch_rows = touch_df[touch_df['Tag'] == 'TOUCH_OUTCOME']

    for _, r in touch_rows.iterrows():
        gate  = str(r.get('GateReason', ''))
        if ':REJ' in gate:
            continue  # skip rejected signal outcomes

        det   = str(r.get('Detail', ''))
        label = str(r.get('Label', '')).strip()

        gm = _GNM_RE.search(gate)
        if not gm:
            continue
        src_bar  = int(gm[1])
        cond_key = gate.split(':')[0] if ':' in gate else ''
        key      = f"{cond_key}:{src_bar}"

        # Fallback: search by bar number only
        if key not in sig_map:
            for k, s in sig_map.items():
                if s['bar_num'] == src_bar:
                    key = k
                    break

        if key not in sig_map:
            continue

        s = sig_map[key]

        # Source filter
        if source_filter and s['source'].lower() != source_filter.lower():
            if s['cond_set'].lower() != source_filter.lower():
                continue

        pm  = _PNL_RE.search(det)
        mfe = _MFE_RE.search(det)
        mae = _MAE_RE.search(det)
        bth = _BTH_RE.search(det)
        ce  = _CE_RE.search(det)

        outcome = ('WIN' if label == 'TARGET'
                   else 'LOSS' if label == 'STOP'
                   else 'NEITHER')

        # Outcome filter
        if outcome_filter and outcome_filter.lower() != 'all':
            if outcome_filter.lower() == 'winners' and outcome != 'WIN':
                continue
            if outcome_filter.lower() == 'losers' and outcome != 'LOSS':
                continue

        trades.append(Trade(
            bar_num     = s['bar_num'],
            time        = s['time'],
            direction   = s['direction'],
            source      = s['source'],
            cond_set    = s['cond_set'],
            score       = s['score'],
            grade       = s['grade'],
            entry       = s['entry'],
            stop        = s['stop'],
            t1          = s['t1'],
            t2          = s['t2'],
            outcome     = outcome,
            sim_pnl     = float(pm[1])  if pm  else 0.0,
            mfe         = float(mfe[1]) if mfe else 0.0,
            mae         = float(mae[1]) if mae else 0.0,
            bars_to_hit = int(bth[1])   if bth else 0,
            close_end   = float(ce[1])  if ce  else 0.0,
        ))

    trades.sort(key=lambda t: t.time)
    wins   = sum(1 for t in trades if t.outcome == 'WIN')
    losses = sum(1 for t in trades if t.outcome == 'LOSS')
    print(f"  Trades loaded: {len(trades)}  "
          f"WIN={wins}  LOSS={losses}  "
          f"win%={wins/max(wins+losses,1)*100:.1f}%  "
          f"net=${sum(t.sim_pnl for t in trades):,.0f}")
    return trades


# ═══════════════════════════════════════════════════════════════════════════════
# LOAD TICKS (chunked date-range reader)
# ═══════════════════════════════════════════════════════════════════════════════

def load_ticks(tic_path: Path, date_filter: str) -> pd.DataFrame:
    parts  = date_filter.split(':')
    d0     = pd.Timestamp(parts[0]).date()
    d1     = pd.Timestamp(parts[-1]).date()
    CHUNK  = 200_000
    chunks = []
    total  = 0

    print(f"  Scanning ticks for {d0} -> {d1} ...")
    for chunk in pd.read_csv(tic_path, chunksize=CHUNK):
        chunk.columns = [c.strip() for c in chunk.columns]
        chunk['Timestamp'] = pd.to_datetime(chunk['Timestamp'], errors='coerce')
        chunk_min = chunk['Timestamp'].min().date()
        chunk_max = chunk['Timestamp'].max().date()
        total += len(chunk)
        if chunk_max < d0:
            if total % 2_000_000 < CHUNK:
                print(f"    ...skipped {total:,} rows, at {chunk_max}")
            continue
        if chunk_min > d1:
            break
        mask = (chunk['Timestamp'].dt.date >= d0) & (chunk['Timestamp'].dt.date <= d1)
        chunks.append(chunk[mask])

    if not chunks:
        return pd.DataFrame()
    df = pd.concat(chunks, ignore_index=True)
    print(f"  {len(df):,} ticks loaded")
    return df


# ═══════════════════════════════════════════════════════════════════════════════
# BUILD FOOTPRINT BARS
# ═══════════════════════════════════════════════════════════════════════════════

def build_bars(ticks: pd.DataFrame, bar_seconds: int = 300) -> List[FPBar]:
    if ticks.empty:
        return []
    ticks = ticks.copy()
    ticks['Timestamp'] = pd.to_datetime(ticks['Timestamp'], errors='coerce')
    ticks['Price']     = pd.to_numeric(ticks['Price'],     errors='coerce')
    ticks['Volume']    = pd.to_numeric(ticks['Volume'],    errors='coerce').fillna(1)
    ticks = ticks.dropna(subset=['Timestamp', 'Price'])

    # Bar time bucket
    epoch = ticks['Timestamp'].min().normalize()
    secs  = (ticks['Timestamp'] - epoch).dt.total_seconds()
    ticks['BarTime'] = epoch + pd.to_timedelta(
        (secs // bar_seconds * bar_seconds).astype(int), unit='s'
    )

    # Classify tick direction
    prices = ticks['Price'].values
    signs  = np.zeros(len(prices), dtype=np.int8)
    last_p, last_s = 0.0, 0
    for i, p in enumerate(prices):
        if last_p == 0.0: s = 0
        elif p > last_p:  s = 1
        elif p < last_p:  s = -1
        else:             s = last_s
        signs[i] = s
        last_p, last_s = p, s
    ticks['Side'] = signs

    bars: List[FPBar] = []
    bar_num = 0
    cvd = 0.0

    for bt, grp in ticks.groupby('BarTime', sort=True):
        o = grp['Price'].iloc[0]
        h = grp['Price'].max()
        l = grp['Price'].min()
        c = grp['Price'].iloc[-1]
        buy_v  = int(grp.loc[grp['Side'] >  0, 'Volume'].sum())
        sell_v = int(grp.loc[grp['Side'] <  0, 'Volume'].sum())
        delta  = buy_v - sell_v
        cvd   += delta
        bars.append(FPBar(
            bar_num  = bar_num,
            time     = bt,
            open=o, high=h, low=l, close=c,
            buy_vol=buy_v, sell_vol=sell_v,
            delta=delta, cvd=cvd, x_idx=bar_num
        ))
        bar_num += 1

    return bars


# ═══════════════════════════════════════════════════════════════════════════════
# PYQTGRAPH ITEMS
# ═══════════════════════════════════════════════════════════════════════════════

class CandleItem(pg.GraphicsObject):
    def __init__(self, bars: List[FPBar]):
        super().__init__()
        self._bars = bars
        self._pic  = QtGui.QPicture()
        self._build()
        if bars:
            xs = [b.x_idx for b in bars]
            ys = [b.low for b in bars] + [b.high for b in bars]
            self._bounds = QtCore.QRectF(min(xs)-1, min(ys)-5,
                                         max(xs)-min(xs)+2, max(ys)-min(ys)+10)
        else:
            self._bounds = QtCore.QRectF(0, 0, 1, 1)

    def _build(self):
        p  = QtGui.QPainter(self._pic)
        hw = 0.38
        for b in self._bars:
            col = C_BULL if b.close >= b.open else C_BEAR
            p.setPen(pg.mkPen(col, width=0.8))
            p.drawLine(QtCore.QPointF(b.x_idx, b.low), QtCore.QPointF(b.x_idx, b.high))
            top = max(b.open, b.close); bot = min(b.open, b.close)
            p.setPen(pg.mkPen(None))
            p.setBrush(pg.mkBrush(QtGui.QColor(*col, 210)))
            p.drawRect(QtCore.QRectF(b.x_idx - hw, bot, hw*2, max(top-bot, 0.125)))
        p.end()

    def paint(self, painter, option, widget): painter.drawPicture(0, 0, self._pic)
    def boundingRect(self): return self._bounds


class TradeOverlayItem(pg.GraphicsObject):
    """Draws entry/stop/T1 bands and exit markers for each trade."""

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
            self._bounds = QtCore.QRectF(
                min(xs)-2, min(ys)-10, max(xs)-min(xs)+20, max(ys)-min(ys)+20)
        else:
            self._bounds = QtCore.QRectF(0, 0, 1, 1)

    def _build(self):
        p    = QtGui.QPainter(self._pic)
        ext  = 8  # bars to extend lines right

        for t in self._trades:
            x0  = t.x_idx
            x1  = x0 + max(t.bars_to_hit + 1, ext)
            col = C_WIN if t.outcome == 'WIN' else C_LOSS

            # Shaded risk zone (entry to stop or T1)
            target = t.t1 if t.t1 else t.entry
            lo     = min(t.entry, t.stop,  target)
            hi     = max(t.entry, t.stop,  target)
            zone   = QtCore.QRectF(x0, lo, x1 - x0, hi - lo)
            p.setPen(pg.mkPen(None))
            p.setBrush(pg.mkBrush(QtGui.QColor(*col[:3], 18)))
            p.drawRect(zone)

            # Entry line
            p.setPen(pg.mkPen(C_ENTRY, width=1.2, style=QtCore.Qt.DashLine))
            p.drawLine(QtCore.QPointF(x0, t.entry), QtCore.QPointF(x1, t.entry))

            # Stop line
            if t.stop:
                p.setPen(pg.mkPen(C_STOP, width=1.0, style=QtCore.Qt.DotLine))
                p.drawLine(QtCore.QPointF(x0, t.stop), QtCore.QPointF(x1, t.stop))

            # T1 line
            if t.t1:
                p.setPen(pg.mkPen(C_T1, width=1.0, style=QtCore.Qt.DotLine))
                p.drawLine(QtCore.QPointF(x0, t.t1), QtCore.QPointF(x1, t.t1))

            # Vertical connector at entry bar
            p.setPen(pg.mkPen(col, width=1.5))
            p.drawLine(QtCore.QPointF(x0, t.stop or t.entry - 5),
                       QtCore.QPointF(x0, t.t1  or t.entry + 5))

        p.end()

    def paint(self, painter, option, widget): painter.drawPicture(0, 0, self._pic)
    def boundingRect(self): return self._bounds


class TradeLabelItem(pg.GraphicsObject):
    """Screen-space labels on each trade bubble."""

    def __init__(self, trades: List[Trade]):
        super().__init__()
        self._trades = [t for t in trades if t.x_idx >= 0]
        if self._trades:
            xs = [t.x_idx for t in self._trades]
            ys = [t.entry  for t in self._trades]
            self._bounds = QtCore.QRectF(min(xs)-1, min(ys)-20,
                                         max(xs)-min(xs)+2, max(ys)-min(ys)+40)
        else:
            self._bounds = QtCore.QRectF(0, 0, 1, 1)

    def paint(self, painter, option, widget):
        lod = option.levelOfDetailFromTransform(painter.worldTransform())
        if lod < 2.5:
            return
        t_xform = painter.worldTransform()
        R = 18

        for t in self._trades:
            y_off = -3 if t.direction == 'Long' else 3
            pt    = t_xform.map(QtCore.QPointF(t.x_idx, t.entry + y_off))
            cx, cy = pt.x(), pt.y()
            col    = QtGui.QColor(*C_WIN, 220) if t.outcome == 'WIN' \
                     else QtGui.QColor(*C_LOSS, 220)

            painter.save()
            painter.resetTransform()

            # Bubble
            painter.setPen(pg.mkPen(QtGui.QColor(255,255,255,100), width=1))
            painter.setBrush(pg.mkBrush(col))
            painter.drawEllipse(QtCore.QPointF(cx, cy), R, R)

            # Source label
            src = t.source[:6]
            painter.setPen(pg.mkPen(QtGui.QColor(255,255,255)))
            painter.setFont(QtGui.QFont("Arial", 6, QtGui.QFont.Bold))
            painter.drawText(QtCore.QRectF(cx-R, cy-R, R*2, R*1.1),
                             QtCore.Qt.AlignCenter, src)

            # PnL
            pnl_txt = f"${t.sim_pnl:+.0f}"
            col2    = QtGui.QColor(200,255,200) if t.sim_pnl >= 0 else QtGui.QColor(255,200,200)
            painter.setPen(pg.mkPen(col2))
            painter.setFont(QtGui.QFont("Arial", 6))
            painter.drawText(QtCore.QRectF(cx-R, cy+R*0.05, R*2, R*0.9),
                             QtCore.Qt.AlignCenter, pnl_txt)

            # Direction arrow in corner
            arr = "B" if t.direction == 'Long' else "S"
            painter.setPen(pg.mkPen(QtGui.QColor(255,255,200)))
            painter.setFont(QtGui.QFont("Arial", 7, QtGui.QFont.Bold))
            painter.drawText(QtCore.QRectF(cx+R*0.4, cy-R, R*0.7, R*0.8),
                             QtCore.Qt.AlignCenter, arr)

            painter.restore()

    def boundingRect(self): return self._bounds


# ═══════════════════════════════════════════════════════════════════════════════
# TIME AXIS
# ═══════════════════════════════════════════════════════════════════════════════

def make_time_axis(bars: List[FPBar]) -> List[Tuple]:
    if not bars: return []
    stride = max(1, len(bars) // 20)
    ticks, prev_d = [], None
    for i, b in enumerate(bars):
        if i % stride == 0:
            d = b.time.date()
            lbl = b.time.strftime('%m/%d %H:%M') if d != prev_d else b.time.strftime('%H:%M')
            ticks.append((b.x_idx, lbl))
            prev_d = d
    return ticks


# ═══════════════════════════════════════════════════════════════════════════════
# MAP TRADE BAR NUMS TO X INDEX
# ═══════════════════════════════════════════════════════════════════════════════

def assign_x_indices(trades: List[Trade], bars: List[FPBar], bar_seconds: int):
    """Map each trade's timestamp to the closest bar x_idx."""
    if not bars:
        return

    # Build time->x map
    time_map: Dict[pd.Timestamp, int] = {b.time: b.x_idx for b in bars}
    bar_td = pd.Timedelta(seconds=bar_seconds)

    for t in trades:
        # Snap trade time to bar boundary
        bt = t.time.floor(f'{bar_seconds}s')
        x  = time_map.get(bt)
        if x is None:
            # Find nearest bar within 2 bar widths
            diffs = [(abs((b.time - t.time).total_seconds()), b.x_idx) for b in bars]
            diffs.sort()
            x = diffs[0][1] if diffs else -1
        t.x_idx  = x if x is not None else -1
        t.exit_x = t.x_idx + t.bars_to_hit if t.x_idx >= 0 else -1


# ═══════════════════════════════════════════════════════════════════════════════
# MAIN CHART
# ═══════════════════════════════════════════════════════════════════════════════

def run_chart(date_filter: str, outcome_filter: str,
              source_filter: Optional[str], bar_seconds: int):

    print(f"Loading trades from {LOG_PATH} ...")
    trades = parse_trades(LOG_PATH, date_filter, source_filter, outcome_filter)
    if not trades:
        print("No trades found for the given filters.")
        return

    print(f"Loading tick data for {date_filter} ...")
    ticks = load_ticks(TIC_PATH, date_filter)
    if ticks.empty:
        print("No tick data found — check the date range.")
        return

    print("Building footprint bars ...")
    bars = build_bars(ticks, bar_seconds)
    print(f"  {len(bars)} bars built  {bars[0].time} -> {bars[-1].time}")

    assign_x_indices(trades, bars, bar_seconds)
    valid = [t for t in trades if t.x_idx >= 0]
    print(f"  {len(valid)} / {len(trades)} trades mapped to bars")

    wins   = [t for t in valid if t.outcome == 'WIN']
    losses = [t for t in valid if t.outcome == 'LOSS']

    # ── PyQtGraph ─────────────────────────────────────────────────────────────
    app = QtWidgets.QApplication.instance() or QtWidgets.QApplication(sys.argv)
    pg.setConfigOptions(antialias=True, background=pg.mkColor(*C_BG))

    filt_lbl = outcome_filter.upper() if outcome_filter != 'all' else 'ALL'
    win_pct  = len(wins) / max(len(wins)+len(losses), 1) * 100
    net_pnl  = sum(t.sim_pnl for t in valid)

    win = pg.GraphicsLayoutWidget(
        show=True,
        title=(f"Winners & Losers  |  {date_filter}  |  {filt_lbl}  |  "
               f"{len(wins)}W / {len(losses)}L  ({win_pct:.0f}%)  net=${net_pnl:,.0f}")
    )
    win.resize(1800, 980)

    # Price + overlays
    price_plot = win.addPlot(
        row=0, col=0,
        title=f"{len(bars)} bars  |  {bars[0].time:%Y-%m-%d %H:%M} -> {bars[-1].time:%Y-%m-%d %H:%M}"
    )
    price_plot.showGrid(x=True, y=True, alpha=0.12)
    price_plot.setLabel('left', 'Price')
    price_plot.getAxis('bottom').setStyle(showValues=False)

    y_min = min(b.low  for b in bars) - 5
    y_max = max(b.high for b in bars) + 10
    price_plot.setYRange(y_min, y_max, padding=0.01)

    # Candlesticks
    price_plot.addItem(CandleItem(bars))

    # Trade risk zones
    price_plot.addItem(TradeOverlayItem(valid))

    # Bubbles with labels
    price_plot.addItem(TradeLabelItem(valid))

    # Exit scatter markers
    def _scatter(plot, xs, ys, sym, col, sz, tip=''):
        if not xs: return
        s = pg.ScatterPlotItem(
            x=xs, y=ys, symbol=sym, size=sz,
            pen=pg.mkPen('w', width=0.5),
            brush=pg.mkBrush(QtGui.QColor(*col[:3], 230)),
        )
        if tip: s.setToolTip(tip)
        plot.addItem(s)

    _scatter(price_plot,
             [t.exit_x for t in wins if t.exit_x >= 0],
             [t.close_end or t.t1 for t in wins if t.exit_x >= 0],
             'd', (120,255,120), 12, 'Win exit (T1 hit)')

    _scatter(price_plot,
             [t.exit_x for t in losses if t.exit_x >= 0],
             [t.close_end or t.stop for t in losses if t.exit_x >= 0],
             'x', (255,80,80), 12, 'Loss exit (stop hit)')

    # CVD sub-panel
    cvd_plot = win.addPlot(row=1, col=0)
    cvd_plot.showGrid(x=True, y=True, alpha=0.12)
    cvd_plot.setLabel('left', 'CVD')
    cvd_plot.setXLink(price_plot)
    cvd_plot.setMaximumHeight(100)
    cvd_plot.addItem(pg.InfiniteLine(0, angle=0, pen=pg.mkPen((180,180,180), width=0.5)))
    xs_arr  = np.array([b.x_idx for b in bars], dtype=float)
    cvd_arr = np.array([b.cvd   for b in bars], dtype=float)
    cvd_plot.plot(xs_arr, cvd_arr, pen=pg.mkPen((100,180,255), width=1.5))

    # Stats sidebar
    stats_plot = win.addPlot(row=0, col=1, rowspan=2)
    stats_plot.setMaximumWidth(240)
    stats_plot.hideAxis('bottom')
    stats_plot.hideAxis('left')
    stats_plot.setTitle('Trade Stats', size='8pt')

    # Build stats HTML
    by_src: Dict[str, dict] = {}
    for t in valid:
        k = by_src.setdefault(t.source, {'w':0,'l':0,'pnl':0.0})
        if t.outcome == 'WIN':   k['w'] += 1; k['pnl'] += t.sim_pnl
        elif t.outcome == 'LOSS':k['l'] += 1; k['pnl'] += t.sim_pnl

    html_lines = [
        f"<b>{filt_lbl}: {len(valid)} trades</b>",
        f"W {len(wins)} / L {len(losses)}  ({win_pct:.0f}%)",
        f"Net PnL: <b>${net_pnl:,.0f}</b>",
        f"Avg PnL: ${net_pnl/max(len(valid),1):.1f}",
        "<hr/>",
        "<b>By Source:</b>",
    ]
    for src, v in sorted(by_src.items(), key=lambda x: -x[1]['pnl']):
        wr = v['w'] / max(v['w']+v['l'],1)*100
        html_lines.append(f"<b>{src[:18]}</b><br/>"
                          f"W{v['w']}/L{v['l']} {wr:.0f}%  ${v['pnl']:,.0f}")
    html_lines += ["<hr/>",
                   "<b>Top Winners:</b>"]
    for t in sorted(wins, key=lambda x: -x.sim_pnl)[:5]:
        html_lines.append(f"{t.source[:12]} {t.direction[0]}  "
                          f"+${t.sim_pnl:.0f}  MFE={t.mfe:.0f}")
    html_lines += ["<b>Top Losers:</b>"]
    for t in sorted(losses, key=lambda x: x.sim_pnl)[:5]:
        html_lines.append(f"{t.source[:12]} {t.direction[0]}  "
                          f"${t.sim_pnl:.0f}  MAE={t.mae:.0f}")

    txt = pg.TextItem(html='<br/>'.join(html_lines), color=C_TEXT, anchor=(0,0))
    txt.setPos(0, 1)
    stats_plot.addItem(txt)
    stats_plot.setXRange(0, 1)
    stats_plot.setYRange(0, 1)

    # Time axis
    price_plot.getAxis('bottom').setStyle(showValues=True)
    price_plot.getAxis('bottom').setTicks([make_time_axis(bars)])

    # Layout
    win.ci.layout.setRowStretchFactor(0, 5)
    win.ci.layout.setRowStretchFactor(1, 1)
    win.ci.layout.setColumnStretchFactor(0, 6)
    win.ci.layout.setColumnStretchFactor(1, 1)

    # Initial view — zoom to last N bars or region with trades
    if valid:
        x_lo = max(0, min(t.x_idx for t in valid) - 5)
        x_hi = max(t.exit_x for t in valid if t.exit_x >= 0) + 5
        price_plot.setXRange(x_lo, x_hi, padding=0.02)
    else:
        price_plot.setXRange(bars[-80].x_idx, bars[-1].x_idx + 2)

    print("Chart ready. Close window to exit.")
    sys.exit(app.exec())


# ═══════════════════════════════════════════════════════════════════════════════
# CLI
# ═══════════════════════════════════════════════════════════════════════════════

if __name__ == '__main__':
    ap = argparse.ArgumentParser(description='Plot winners and losers on TicData footprint chart')
    ap.add_argument('--date',        required=True,
                    help='"2026-01-05" or "2026-01-05:2026-01-06"')
    ap.add_argument('--filter',      default='all',
                    choices=['all', 'winners', 'losers'],
                    help='Show all, only winners, or only losers (default: all)')
    ap.add_argument('--source',      default=None,
                    help='Filter by Source or ConditionSetId')
    ap.add_argument('--bar-seconds', type=int, default=300,
                    help='Bar size in seconds (default 300 = 5-min)')
    args = ap.parse_args()

    run_chart(
        date_filter    = args.date,
        outcome_filter = args.filter,
        source_filter  = args.source,
        bar_seconds    = args.bar_seconds,
    )
