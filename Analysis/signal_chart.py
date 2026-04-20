#!/usr/bin/env python3
"""
signal_chart.py  --  Signal analysis chart from Log.csv alone (no tick file needed)

Reconstructs 5-min OHLC bars from BAR_CONTEXT / BAR_FORWARD rows, then overlays:
  - SIGNAL_ACCEPTED  : entry arrow + stop line (red) + T1 line (green) + T2 line (lime)
  - SIGNAL_REJECTED  : yellow X at signal price
  - TOUCH_OUTCOME    : green diamond (win) / red diamond (loss) at exit price
  - Stats sidebar    : win rate, avg PnL, MFE/MAE by source

Usage:
    python signal_chart.py --log ../backtest/Log.csv
    python signal_chart.py --log ../backtest/Log.csv --date 2026-01-04
    python signal_chart.py --log ../backtest/Log.csv --source SMC_FVG_v1
    python signal_chart.py --log ../backtest/Log.csv --direction Long --grade A,B

Requires:  pip install pandas numpy pyqtgraph PyQt5
"""

import sys
import re
import math
import argparse
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

import pandas as pd
import numpy as np
import pyqtgraph as pg
from pyqtgraph.Qt import QtCore, QtGui, QtWidgets

# ─────────────────────────────────────────────────────────────────────────────
# COLOURS
# ─────────────────────────────────────────────────────────────────────────────
C_BG       = (19,  23,  34)
C_BULL     = (38,  166, 154)
C_BEAR     = (239, 83,  80)
C_ACCEPTED = (0,   200, 120)
C_REJECTED = (255, 214, 0)
C_WIN      = (38,  166, 154)
C_LOSS     = (239, 83,  80)
C_STOP     = (239, 83,  80)
C_T1       = (38,  166, 154)
C_T2       = (100, 220, 160)
C_ENTRY    = (200, 200, 255)
C_TEXT     = (200, 205, 215)

BAR_SECONDS = 300  # inferred from log; can be overridden with --bar-seconds

# ─────────────────────────────────────────────────────────────────────────────
# DATA CLASSES
# ─────────────────────────────────────────────────────────────────────────────
@dataclass
class Outcome:
    bar_num:     int
    direction:   str    # "Long" | "Short"
    label:       str    # "TARGET" | "STOP" | "BOTH_SAMEBAR" | "NEITHER"
    close_end:   float
    sim_pnl:     float
    mfe:         float
    mae:         float
    bars_to_hit: int
    rejected:    bool   # True = came from a rejected signal
    x_idx:       int = 0


@dataclass
class OHLCBar:
    bar_num:   int
    time:      pd.Timestamp
    open:      float
    high:      float
    low:       float
    close:     float
    volume:    int
    delta:     int
    x_idx:     int = 0


@dataclass
class Signal:
    bar_num:       int
    time:          pd.Timestamp
    direction:     str          # "Long" | "Short"
    entry:         float
    stop:          float
    t1:            float
    t2:            float
    rr:            float
    source:        str
    cond_set:      str
    score:         int
    grade:         str
    label:         str
    accepted:      bool
    gate_reason:   str = ""
    # filled in from TOUCH_OUTCOME
    outcome:       str = ""     # "WIN" | "LOSS" | "PENDING"
    sim_pnl:       float = 0.0
    mfe:           float = 0.0
    mae:           float = 0.0
    exit_price:    float = 0.0
    bars_to_hit:   int   = 0
    x_idx:         int   = 0


# ─────────────────────────────────────────────────────────────────────────────
# LOG PARSER
# ─────────────────────────────────────────────────────────────────────────────
_BAR_RE  = re.compile(
    r'O:([\d.]+)\s+H:([\d.]+)\s+L:([\d.]+)\s+C:([\d.]+)\s+V:(\d+)\s+D:(-?\d+)'
)
_MFE_RE  = re.compile(r'MFE=([\d.]+)')
_MAE_RE  = re.compile(r'MAE=([\d.]+)')
_PNL_RE  = re.compile(r'SIM_PNL=(-?[\d.]+)')
_BTH_RE  = re.compile(r'BARS_TO_HIT=(\d+)')
_BAR_NUM_RE = re.compile(r':(\d+)(?::REJ)?$')


def _parse_bar_str(s: str) -> Optional[Tuple]:
    """Parse 'O:x H:x L:x C:x V:x D:x' into (o,h,l,c,v,d)."""
    m = _BAR_RE.search(s)
    if not m: return None
    return (float(m[1]), float(m[2]), float(m[3]), float(m[4]),
            int(m[5]), int(m[6]))


def _parse_bars_from_detail(detail: str) -> List[Tuple]:
    """Split on '|' and parse each bar segment."""
    bars = []
    for seg in detail.split('|'):
        b = _parse_bar_str(seg.strip())
        if b: bars.append(b)
    return bars


def load_log(path: str,
             date_filter:  Optional[str] = None,
             source_filter:Optional[str] = None,
             dir_filter:   Optional[str] = None,
             grade_filter: Optional[str] = None,
             bar_seconds:  int = BAR_SECONDS
             ) -> Tuple[List[OHLCBar], List[Signal], List[Outcome], dict]:
    """Parse Log.csv and return (bars, signals, outcomes, stats)."""

    df = pd.read_csv(path, dtype=str)
    df.columns = [c.strip() for c in df.columns]
    df['Timestamp'] = pd.to_datetime(df['Timestamp'], errors='coerce')
    df = df.dropna(subset=['Timestamp'])

    # Date filter
    if date_filter:
        parts = date_filter.split(':')
        d0 = pd.Timestamp(parts[0])
        d1 = pd.Timestamp(parts[-1]) + pd.Timedelta(days=1)
        df = df[(df['Timestamp'] >= d0) & (df['Timestamp'] < d1)].reset_index(drop=True)

    # Helper columns
    def col(row, name, default=''):
        v = row.get(name, default)
        return '' if pd.isna(v) else str(v).strip()

    def fcol(row, name, default=0.0):
        v = row.get(name, '')
        try: return float(v)
        except: return default

    def icol(row, name, default=0):
        v = row.get(name, '')
        try: return int(float(v))
        except: return default

    # ── Pass 1: Collect OHLC bars from BAR_CONTEXT and BAR_FORWARD ──────────
    bar_dict: Dict[int, OHLCBar] = {}

    ctx_rows  = df[df['Tag'].isin(['BAR_CONTEXT', 'BAR_FORWARD'])].iterrows()
    for _, row in ctx_rows:
        bar_num  = icol(row, 'Bar')
        sig_time = row['Timestamp']
        detail   = col(row, 'Detail')
        if not detail: continue

        parsed = _parse_bars_from_detail(detail)
        # BAR_CONTEXT detail: 5 pre-bars + signal bar (oldest first)
        # BAR_FORWARD detail: up to 5 forward bars (oldest=bar+1 first)
        tag = col(row, 'Tag')
        if tag == 'BAR_CONTEXT':
            # Last entry = signal bar, earlier = older
            for j, b_ohlc in enumerate(reversed(parsed)):
                bn   = bar_num - j
                btime = sig_time - pd.Timedelta(seconds=bar_seconds * j)
                if bn not in bar_dict:
                    bar_dict[bn] = OHLCBar(bn, btime, *b_ohlc)
        else:  # BAR_FORWARD
            for j, b_ohlc in enumerate(parsed):
                bn   = bar_num + j + 1
                btime = sig_time + pd.Timedelta(seconds=bar_seconds * (j + 1))
                if bn not in bar_dict:
                    bar_dict[bn] = OHLCBar(bn, btime, *b_ohlc)

    # Assign sequential x_idx to bars
    sorted_bars = sorted(bar_dict.values(), key=lambda b: b.bar_num)
    for i, b in enumerate(sorted_bars):
        b.x_idx = i
    bar_num_to_x: Dict[int, int] = {b.bar_num: b.x_idx for b in sorted_bars}

    # ── Pass 2: SIGNAL_ACCEPTED and SIGNAL_REJECTED ──────────────────────────
    sig_rows = df[df['Tag'].isin(['SIGNAL_ACCEPTED', 'SIGNAL_REJECTED'])].iterrows()
    signals_by_key: Dict[str, Signal] = {}

    for _, row in sig_rows:
        bar_num   = icol(row, 'Bar')
        accepted  = col(row, 'Tag') == 'SIGNAL_ACCEPTED'
        direction = col(row, 'Direction')
        source    = col(row, 'Source')
        cond_set  = col(row, 'ConditionSetId')
        score     = icol(row, 'Score')
        grade     = col(row, 'Grade')
        label     = col(row, 'Label')
        entry     = fcol(row, 'EntryPrice')
        stop      = fcol(row, 'StopPrice')
        t1        = fcol(row, 'T1Price')
        t2        = fcol(row, 'T2Price')
        rr        = fcol(row, 'RRRatio')
        gate      = col(row, 'GateReason')

        # Apply filters
        if source_filter and source.lower() != source_filter.lower():
            if cond_set.lower() != source_filter.lower():
                continue
        if dir_filter and direction.lower() != dir_filter.lower():
            continue
        if grade_filter:
            allowed = [g.strip().upper() for g in grade_filter.split(',')]
            if grade.upper() not in allowed:
                continue

        # If entry price is missing, try to get from nearest bar
        if entry == 0.0 and bar_num in bar_dict:
            entry = bar_dict[bar_num].close

        key = f"{cond_set}:{bar_num}"
        sig = Signal(
            bar_num=bar_num, time=row['Timestamp'],
            direction=direction, entry=entry, stop=stop,
            t1=t1, t2=t2, rr=rr,
            source=source, cond_set=cond_set, score=score,
            grade=grade, label=label, accepted=accepted,
            gate_reason=gate,
            x_idx=bar_num_to_x.get(bar_num, -1),
        )
        signals_by_key[key] = sig

    # ── Pass 3: TOUCH_OUTCOME — match back to signals ────────────────────────
    touch_rows = df[df['Tag'] == 'TOUCH_OUTCOME'].iterrows()
    for _, row in touch_rows:
        gate_label = col(row, 'GateReason')   # e.g. "SMC_FVG_v1:20260104:316:REJ"
        detail     = col(row, 'Detail')
        result     = col(row, 'Label')         # "TARGET" or "STOP"
        exit_p     = fcol(row, 'EntryPrice')   # reused for exit

        # Extract bar num from label
        m = _BAR_NUM_RE.search(gate_label)
        if not m: continue
        src_bn = int(m[1])

        # Try to find matching cond_set from gate_label prefix
        cond_guess = gate_label.split(':')[0] if ':' in gate_label else ''
        key = f"{cond_guess}:{src_bn}"

        # Fallback: search by bar_num alone
        if key not in signals_by_key:
            for k, s in signals_by_key.items():
                if s.bar_num == src_bn:
                    key = k; break

        if key not in signals_by_key:
            continue

        sig = signals_by_key[key]
        mfe_m = _MFE_RE.search(detail)
        mae_m = _MAE_RE.search(detail)
        pnl_m = _PNL_RE.search(detail)
        bth_m = _BTH_RE.search(detail)

        sig.mfe        = float(mfe_m[1]) if mfe_m else 0.0
        sig.mae        = float(mae_m[1]) if mae_m else 0.0
        sig.sim_pnl    = float(pnl_m[1]) if pnl_m else 0.0
        sig.bars_to_hit= int(bth_m[1])   if bth_m else 0
        sig.exit_price = exit_p if exit_p else (sig.t1 if result == 'TARGET' else sig.stop)
        sig.outcome    = 'WIN' if result == 'TARGET' else 'LOSS' if result == 'STOP' else 'PENDING'
        # Backfill x_idx for outcome marker (signal bar + bars_to_hit)
        outcome_bar = sig.bar_num + sig.bars_to_hit
        sig.x_idx   = bar_num_to_x.get(sig.bar_num, -1)

    signals = list(signals_by_key.values())
    # Mark signals without a TOUCH_OUTCOME as PENDING
    for s in signals:
        if not s.outcome:
            s.outcome = 'PENDING'

    # ── Stats ─────────────────────────────────────────────────────────────────
    acc    = [s for s in signals if s.accepted]
    wins   = [s for s in acc if s.outcome == 'WIN']
    losses = [s for s in acc if s.outcome == 'LOSS']
    rej    = [s for s in signals if not s.accepted]

    stats = {
        'total':    len(signals),
        'accepted': len(acc),
        'rejected': len(rej),
        'wins':     len(wins),
        'losses':   len(losses),
        'pending':  len([s for s in acc if s.outcome == 'PENDING']),
        'win_rate': len(wins) / max(len(wins)+len(losses), 1) * 100,
        'avg_pnl':  np.mean([s.sim_pnl for s in acc]) if acc else 0.0,
        'total_pnl':sum(s.sim_pnl for s in acc),
        'avg_mfe':  np.mean([s.mfe for s in acc]) if acc else 0.0,
        'avg_mae':  np.mean([s.mae for s in acc]) if acc else 0.0,
    }
    print(f"  Signals: {stats['accepted']} accepted ({stats['wins']}W/"
          f"{stats['losses']}L/{stats['pending']}P) + {stats['rejected']} rejected")
    print(f"  Win rate: {stats['win_rate']:.1f}%  "
          f"Avg PnL: {stats['avg_pnl']:.1f}  "
          f"Total PnL: {stats['total_pnl']:.0f}")

    return sorted_bars, signals, stats


# ─────────────────────────────────────────────────────────────────────────────
# CANDLESTICK ITEM
# ─────────────────────────────────────────────────────────────────────────────
class CandlestickItem(pg.GraphicsObject):
    def __init__(self, bars: List[OHLCBar]):
        super().__init__()
        self.bars    = bars
        self.picture = QtGui.QPicture()
        if bars:
            min_x = bars[0].x_idx  - 1
            max_x = bars[-1].x_idx + 1
            min_y = min(b.low  for b in bars) - 5
            max_y = max(b.high for b in bars) + 5
            self._bounds = QtCore.QRectF(min_x, min_y, max_x - min_x, max_y - min_y)
        else:
            self._bounds = QtCore.QRectF(0, 0, 1, 1)
        self._build()

    def _build(self):
        p = QtGui.QPainter(self.picture)
        hw = 0.38
        for b in self.bars:
            x   = b.x_idx
            col = C_BULL if b.close >= b.open else C_BEAR
            # Wick
            p.setPen(pg.mkPen(col, width=1.0))
            p.drawLine(QtCore.QPointF(x, b.low),  QtCore.QPointF(x, b.high))
            # Body
            body_top = max(b.open, b.close)
            body_bot = min(b.open, b.close)
            body_h   = max(body_top - body_bot, 0.125)
            r = QtCore.QRectF(x - hw, body_bot, hw * 2, body_h)
            p.setPen(pg.mkPen(None))
            p.setBrush(pg.mkBrush(QtGui.QColor(*col, 220)))
            p.drawRect(r)
        p.end()

    def paint(self, painter, option, widget): painter.drawPicture(0, 0, self.picture)
    def boundingRect(self):                   return self._bounds


# ─────────────────────────────────────────────────────────────────────────────
# SIGNAL RISK LINES ITEM  (entry / stop / T1 / T2 horizontal lines per signal)
# ─────────────────────────────────────────────────────────────────────────────
class SignalLinesItem(pg.GraphicsObject):
    def __init__(self, signals: List[Signal], bar_num_to_x: Dict[int, int]):
        super().__init__()
        self.signals       = signals
        self.bar_num_to_x  = bar_num_to_x
        self.picture       = QtGui.QPicture()
        if signals:
            all_x = [s.x_idx for s in signals if s.x_idx >= 0]
            self._bounds = QtCore.QRectF(
                min(all_x, default=0) - 1, 0,
                max(all_x, default=1) + 3, 1,
            ) if all_x else QtCore.QRectF(0, 0, 1, 1)
        else:
            self._bounds = QtCore.QRectF(0, 0, 1, 1)
        self._build()

    def _build(self):
        p   = QtGui.QPainter(self.picture)
        ext = 6   # bars the line extends to the right
        for s in self.signals:
            if s.x_idx < 0 or not s.accepted:
                continue
            x0 = s.x_idx
            x1 = x0 + max(s.bars_to_hit + 1, ext)

            # Entry line
            p.setPen(pg.mkPen(C_ENTRY, width=0.8, style=QtCore.Qt.DotLine))
            p.drawLine(QtCore.QPointF(x0, s.entry), QtCore.QPointF(x1, s.entry))

            # Stop loss line
            if s.stop:
                p.setPen(pg.mkPen(C_STOP, width=0.9, style=QtCore.Qt.DashLine))
                p.drawLine(QtCore.QPointF(x0, s.stop), QtCore.QPointF(x1, s.stop))

            # T1 target line
            if s.t1:
                p.setPen(pg.mkPen(C_T1, width=0.9, style=QtCore.Qt.DashLine))
                p.drawLine(QtCore.QPointF(x0, s.t1), QtCore.QPointF(x1, s.t1))

            # T2 target line
            if s.t2:
                p.setPen(pg.mkPen(C_T2, width=0.7, style=QtCore.Qt.DotLine))
                p.drawLine(QtCore.QPointF(x0, s.t2), QtCore.QPointF(x1, s.t2))

        p.end()

    def paint(self, painter, option, widget): painter.drawPicture(0, 0, self.picture)
    def boundingRect(self):                   return self._bounds


# ─────────────────────────────────────────────────────────────────────────────
# SIGNAL LABEL ITEM  (score / grade / source text near entry, LOD-gated)
# ─────────────────────────────────────────────────────────────────────────────
class SignalLabelItem(pg.GraphicsObject):
    def __init__(self, signals: List[Signal]):
        super().__init__()
        self.signals = [s for s in signals if s.x_idx >= 0]
        if self.signals:
            all_x = [s.x_idx for s in self.signals]
            self._bounds = QtCore.QRectF(min(all_x)-1, 0, max(all_x)+2, 1)
        else:
            self._bounds = QtCore.QRectF(0, 0, 1, 1)

    def paint(self, painter, option, widget):
        lod = option.levelOfDetailFromTransform(painter.worldTransform())
        if lod < 5.0:
            return
        t = painter.worldTransform()
        for s in self.signals:
            if not s.accepted or s.entry == 0:
                continue
            ctr = t.map(QtCore.QPointF(s.x_idx, s.entry))
            offset = -18 if s.direction == 'Long' else 6
            painter.save()
            painter.resetTransform()
            painter.setFont(QtGui.QFont("Arial", 7))
            col = (0, 200, 120) if s.outcome == 'WIN' else \
                  (239, 83, 80) if s.outcome == 'LOSS' else (180, 180, 180)
            painter.setPen(pg.mkPen(col))
            painter.drawText(
                QtCore.QRectF(ctr.x() + 4, ctr.y() + offset, 120, 14),
                QtCore.Qt.AlignLeft,
                f"{s.source[:12]} {s.grade} {s.score}"
            )
            painter.restore()

    def boundingRect(self): return self._bounds


# ─────────────────────────────────────────────────────────────────────────────
# SCATTER HELPERS  (kept for rejected X markers)
# ─────────────────────────────────────────────────────────────────────────────
def _add_scatter(plot, xs, ys, symbol, color, size, tip=''):
    if not xs: return
    s = pg.ScatterPlotItem(
        x=xs, y=ys, symbol=symbol, size=size,
        pen=pg.mkPen('w', width=0.5),
        brush=pg.mkBrush(QtGui.QColor(*color[:3], 230)),
    )
    if tip:
        s.setToolTip(tip)
    plot.addItem(s)


def _short_name(src: str) -> str:
    """Abbreviate source name to fit inside a bubble (2 lines, <=7 chars each)."""
    parts = src.split('_')
    if len(parts) == 1:
        return src[:9]
    # Map known long tokens to short ones
    abbr = {'Signal': 'Sig', 'Cross': 'X', 'Trend': 'Trnd',
            'Retest': 'Rtest', 'Reclaim': 'Rclm', 'Auction': 'Auct',
            'Impulse': 'Impl', 'Block': 'Blk', 'Abs': 'Abs',
            'Delta': 'Delt', 'Value': 'Val', 'Order': 'Ord',
            'Flow': 'Flow', 'Native': 'Ntv', 'Full': 'Full'}
    line1 = abbr.get(parts[0], parts[0])[:6]
    rest  = '_'.join(parts[1:])
    line2 = abbr.get(parts[1], parts[1])[:7] if len(parts) > 1 else ''
    return f"{line1}\n{line2}"


# ─────────────────────────────────────────────────────────────────────────────
# SIGNAL BUBBLE ITEM  —  colored circle + white source label, screen-sized
# ─────────────────────────────────────────────────────────────────────────────
class SignalBubbleItem(pg.GraphicsObject):
    """Draws a screen-space bubble at each signal's entry price.
    Buy = green, Sell = red.  White text inside shows abbreviated source name.
    """
    RADIUS = 22   # screen pixels

    def __init__(self, signals: List[Signal]):
        super().__init__()
        self.sigs = [s for s in signals if s.x_idx >= 0 and s.entry != 0]
        if self.sigs:
            xs   = [s.x_idx for s in self.sigs]
            ys   = [s.entry  for s in self.sigs]
            self._bounds = QtCore.QRectF(
                min(xs) - 2, min(ys) - 20,
                max(xs) - min(xs) + 4, max(ys) - min(ys) + 40
            )
        else:
            self._bounds = QtCore.QRectF(0, 0, 1, 1)

    def paint(self, painter, option, widget):
        t   = painter.worldTransform()
        lod = option.levelOfDetailFromTransform(t)
        if lod < 1.5:
            return
        R  = self.RADIUS
        D  = R * 2

        font_src = QtGui.QFont("Arial", 6, QtGui.QFont.Bold)
        font_dir = QtGui.QFont("Arial", 7, QtGui.QFont.Bold)

        for s in self.sigs:
            # Offset bubble below bar for Long, above for Short
            y_data = s.entry - 3 if s.direction == 'Long' else s.entry + 3
            pt = t.map(QtCore.QPointF(s.x_idx, y_data))
            cx, cy = pt.x(), pt.y()

            col_fill = QtGui.QColor(*C_BULL, 220) if s.direction == 'Long' \
                       else QtGui.QColor(*C_BEAR, 220)

            painter.save()
            painter.resetTransform()

            # Circle body
            painter.setPen(pg.mkPen(QtGui.QColor(255, 255, 255, 120), width=1.2))
            painter.setBrush(pg.mkBrush(col_fill))
            painter.drawEllipse(QtCore.QPointF(cx, cy), R, R)

            # Source name (2 lines)
            painter.setPen(pg.mkPen(QtGui.QColor(255, 255, 255, 255)))
            painter.setFont(font_src)
            name_txt = _short_name(s.source or s.cond_set or '?')
            painter.drawText(
                QtCore.QRectF(cx - R, cy - R, D, D),
                QtCore.Qt.AlignCenter,
                name_txt,
            )

            # Direction tag: small "B" or "S" in top-right of bubble
            dir_ch = 'B' if s.direction == 'Long' else 'S'
            painter.setFont(font_dir)
            tag_col = QtGui.QColor(220, 255, 220) if s.direction == 'Long' \
                      else QtGui.QColor(255, 220, 220)
            painter.setPen(pg.mkPen(tag_col))
            painter.drawText(
                QtCore.QRectF(cx + R * 0.35, cy - R, R * 0.7, R * 0.9),
                QtCore.Qt.AlignCenter,
                dir_ch,
            )

            painter.restore()

    def boundingRect(self): return self._bounds


# ─────────────────────────────────────────────────────────────────────────────
# PNL CURVE
# ─────────────────────────────────────────────────────────────────────────────
def build_pnl_curve(signals: List[Signal]) -> Tuple[List[float], List[float]]:
    acc = sorted(
        [s for s in signals if s.accepted and s.outcome in ('WIN', 'LOSS')],
        key=lambda s: s.time
    )
    xs, ys = [], []
    cum = 0.0
    for s in acc:
        cum += s.sim_pnl
        xs.append(float(s.x_idx))
        ys.append(cum)
    return xs, ys


# ─────────────────────────────────────────────────────────────────────────────
# TIME AXIS TICKS
# ─────────────────────────────────────────────────────────────────────────────
def make_time_axis(bars: List[OHLCBar]) -> List[Tuple[int, str]]:
    if not bars: return []
    stride = max(1, len(bars) // 16)
    ticks  = []
    prev_d = None
    for i, b in enumerate(bars):
        if i % stride == 0:
            d = b.time.date()
            label = b.time.strftime('%m/%d %H:%M') if d != prev_d else b.time.strftime('%H:%M')
            ticks.append((b.x_idx, label))
            prev_d = d
    return ticks


# ─────────────────────────────────────────────────────────────────────────────
# STATS TEXT
# ─────────────────────────────────────────────────────────────────────────────
def build_stats_html(stats: dict, signals: List[Signal]) -> str:
    # By source
    sources: Dict[str, dict] = {}
    for s in signals:
        if not s.accepted: continue
        k = sources.setdefault(s.cond_set or s.source, {'w': 0, 'l': 0, 'pnl': 0.0})
        if s.outcome == 'WIN':   k['w'] += 1; k['pnl'] += s.sim_pnl
        elif s.outcome == 'LOSS':k['l'] += 1; k['pnl'] += s.sim_pnl

    lines = [
        f"<b>Signals: {stats['accepted']} accepted / {stats['rejected']} rejected</b>",
        f"W {stats['wins']} / L {stats['losses']}  "
        f"Win%: <b>{stats['win_rate']:.1f}%</b>",
        f"Avg PnL: {stats['avg_pnl']:.1f}  Total: <b>{stats['total_pnl']:.0f}</b>",
        f"Avg MFE: {stats['avg_mfe']:.1f}  Avg MAE: {stats['avg_mae']:.1f}",
        "<hr/>",
    ]
    for src, v in sorted(sources.items(), key=lambda x: -x[1]['pnl']):
        wr = v['w'] / max(v['w'] + v['l'], 1) * 100
        lines.append(f"<b>{src[:20]}</b><br/>"
                     f"  W{v['w']}/L{v['l']} {wr:.0f}%  PnL:{v['pnl']:.0f}")
    return '<br/>'.join(lines)


# ─────────────────────────────────────────────────────────────────────────────
# MAIN CHART
# ─────────────────────────────────────────────────────────────────────────────
def run_chart(log_path: str,
              date_filter:   Optional[str] = None,
              source_filter: Optional[str] = None,
              dir_filter:    Optional[str] = None,
              grade_filter:  Optional[str] = None,
              bar_seconds:   int = BAR_SECONDS):

    print(f"Parsing {log_path} ...")
    bars, signals, stats = load_log(
        log_path, date_filter, source_filter,
        dir_filter, grade_filter, bar_seconds,
    )

    if not bars:
        print("No price bars reconstructed - check --date filter or log file.")
        return
    if not signals:
        print("No signals found after filters.")
        return

    bar_num_to_x = {b.bar_num: b.x_idx for b in bars}

    # Re-assign x_idx to signals using reconstructed bar map
    for s in signals:
        s.x_idx = bar_num_to_x.get(s.bar_num, -1)

    y_min = min(b.low  for b in bars) - 5
    y_max = max(b.high for b in bars) + 10

    # ── PyQtGraph setup ───────────────────────────────────────────────────────
    app = QtWidgets.QApplication.instance() or QtWidgets.QApplication(sys.argv)
    pg.setConfigOptions(antialias=True, background=pg.mkColor(*C_BG))

    title_date = date_filter or 'all dates'
    title_src  = source_filter or 'all sources'
    win = pg.GraphicsLayoutWidget(
        show=True,
        title=f"Signal Chart  |  {title_date}  |  {title_src}  |  "
              f"{stats['wins']}W/{stats['losses']}L "
              f"({stats['win_rate']:.1f}%)"
    )
    win.resize(1700, 1020)

    # ── Price panel ───────────────────────────────────────────────────────────
    price_plot = win.addPlot(row=0, col=0, rowspan=1,
                             title=f"Price  |  {len(bars)} bars  |  "
                                   f"{bars[0].time:%Y-%m-%d %H:%M} -> "
                                   f"{bars[-1].time:%Y-%m-%d %H:%M}")
    price_plot.showGrid(x=True, y=True, alpha=0.15)
    price_plot.setLabel('left', 'Price')
    price_plot.setLabel('right', 'Price')
    price_plot.getAxis('bottom').setStyle(showValues=False)
    price_plot.setYRange(y_min, y_max, padding=0.01)

    # Candlesticks
    price_plot.addItem(CandlestickItem(bars))

    # Risk lines (entry / stop / T1 / T2)
    price_plot.addItem(SignalLinesItem(signals, bar_num_to_x))

    # Score/grade labels
    price_plot.addItem(SignalLabelItem(signals))

    # ── Entry scatter markers ─────────────────────────────────────────────────
    acc   = [s for s in signals if s.accepted and s.x_idx >= 0]
    rej   = [s for s in signals if not s.accepted and s.x_idx >= 0]
    wins  = [s for s in acc if s.outcome == 'WIN']
    losses= [s for s in acc if s.outcome == 'LOSS']
    long_ = [s for s in acc if s.direction == 'Long']
    short_= [s for s in acc if s.direction == 'Short']

    # Signal bubbles — green for Buy, red for Sell, white source label inside
    price_plot.addItem(SignalBubbleItem(acc))

    # Rejected X markers
    _add_scatter(price_plot,
                 [s.x_idx for s in rej if s.entry != 0],
                 [s.entry for s in rej if s.entry != 0],
                 'x', C_REJECTED, 9, 'Rejected')

    # Win exit diamonds (bright, filled) — keep direction color with white border
    _add_scatter(price_plot,
                 [s.x_idx + s.bars_to_hit for s in wins],
                 [s.exit_price or s.t1 for s in wins],
                 'd', (120, 255, 120), 11, 'Win')
    # Loss exit diamonds
    _add_scatter(price_plot,
                 [s.x_idx + s.bars_to_hit for s in losses],
                 [s.exit_price or s.stop for s in losses],
                 'd', (255, 80, 80), 11, 'Loss')

    # ── PnL panel ─────────────────────────────────────────────────────────────
    pnl_plot = win.addPlot(row=1, col=0)
    pnl_plot.showGrid(x=True, y=True, alpha=0.15)
    pnl_plot.setLabel('left', 'Cum PnL')
    pnl_plot.setXLink(price_plot)
    pnl_plot.setMaximumHeight(130)
    pnl_plot.addItem(pg.InfiniteLine(0, angle=0, pen=pg.mkPen((180,180,180), width=0.5)))

    pnl_xs, pnl_ys = build_pnl_curve(signals)
    if pnl_xs:
        pnl_col = C_WIN if pnl_ys[-1] >= 0 else C_LOSS
        pnl_curve = pg.PlotCurveItem(x=pnl_xs, y=pnl_ys,
                                     pen=pg.mkPen(*pnl_col, width=1.8),
                                     fillLevel=0,
                                     brush=pg.mkBrush(QtGui.QColor(*pnl_col[:3], 40)))
        pnl_plot.addItem(pnl_curve)

    # ── Score histogram panel ─────────────────────────────────────────────────
    scr_plot = win.addPlot(row=2, col=0)
    scr_plot.showGrid(x=True, y=True, alpha=0.15)
    scr_plot.setLabel('left', 'Score')
    scr_plot.setXLink(price_plot)
    scr_plot.setMaximumHeight(100)
    scr_plot.addItem(pg.InfiniteLine(0, angle=0, pen=pg.mkPen((180,180,180), width=0.5)))

    if acc:
        scr_xs = [s.x_idx for s in acc]
        scr_ys = [s.score  for s in acc]
        scr_cl = [pg.mkBrush(*C_WIN  if s.outcome == 'WIN'  else
                              C_LOSS if s.outcome == 'LOSS' else
                              (180,180,180))
                  for s in acc]
        scr_plot.addItem(pg.BarGraphItem(x=scr_xs, height=scr_ys,
                                         width=0.6, brushes=scr_cl))

    # Time axis on bottom panel
    scr_plot.getAxis('bottom').setTicks([make_time_axis(bars)])

    # ── Stats sidebar ─────────────────────────────────────────────────────────
    stats_plot = win.addPlot(row=0, col=1, rowspan=3)
    stats_plot.setMaximumWidth(220)
    stats_plot.hideAxis('bottom')
    stats_plot.hideAxis('left')
    stats_plot.setTitle('Stats', size='8pt')

    text_item = pg.TextItem(
        html=build_stats_html(stats, signals),
        color=C_TEXT, anchor=(0, 0),
    )
    text_item.setPos(0, 1)
    stats_plot.addItem(text_item)
    stats_plot.setXRange(0, 1)
    stats_plot.setYRange(0, 1)

    # ── Initial view ──────────────────────────────────────────────────────────
    show_n = min(80, len(bars))
    price_plot.setXRange(bars[-show_n].x_idx - 1, bars[-1].x_idx + 2, padding=0.01)
    price_plot.setYRange(y_min, y_max, padding=0.01)

    win.ci.layout.setRowStretchFactor(0, 5)
    win.ci.layout.setRowStretchFactor(1, 1)
    win.ci.layout.setRowStretchFactor(2, 1)
    win.ci.layout.setColumnStretchFactor(0, 6)
    win.ci.layout.setColumnStretchFactor(1, 1)

    print("Chart ready. Scroll/zoom to explore. Close window to exit.")
    sys.exit(app.exec())


# ─────────────────────────────────────────────────────────────────────────────
# CLI
# ─────────────────────────────────────────────────────────────────────────────
if __name__ == '__main__':
    ap = argparse.ArgumentParser(description='Signal analysis chart from Log.csv')
    ap.add_argument('--log',        required=True,  help='Path to Log.csv')
    ap.add_argument('--date',       default=None,   help='"2026-01-04" or "2026-01-04:2026-01-10"')
    ap.add_argument('--source',     default=None,   help='Filter by Source or ConditionSetId')
    ap.add_argument('--direction',  default=None,   help='Long | Short')
    ap.add_argument('--grade',      default=None,   help='Comma-separated grades: A,B or A,B,C')
    ap.add_argument('--bar-seconds',type=int, default=BAR_SECONDS,
                    help='Bar size in seconds (default 300 = 5-min)')
    args = ap.parse_args()

    run_chart(
        log_path      = args.log,
        date_filter   = args.date,
        source_filter = args.source,
        dir_filter    = args.direction,
        grade_filter  = args.grade,
        bar_seconds   = args.bar_seconds,
    )
