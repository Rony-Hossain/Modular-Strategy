#!/usr/bin/env python3
"""
tick_chart.py  —  Comprehensive footprint / order-flow chart from tick data.

Usage (new tick log format):
    python tick_chart.py --ticks ModularStrategy_Ticks_20260104.csv
                         --log   ModularStrategy_20260104.csv
                         --bar-minutes 5  --bars 80  --output chart.png

Usage (old Ticks_Raw.csv from backtest folder):
    python tick_chart.py --ticks backtest/Ticks_Raw.csv --raw
                         --bar-minutes 5 --bars 100 --output chart.png

Panels (top → bottom):
  1. Footprint candles   OHLC + bid/ask volume per price level + imbalance highlights
  2. Volume bars         per-bar total volume, coloured by delta sign
  3. Cumulative Delta    running (ask_vol − bid_vol) line
  4. Bar Delta           per-bar delta histogram
  Right sidebar:         Volume Profile with POC / VAH / VAL

Optional overlays from strategy log CSV:
  • SIGNAL_ACCEPTED  ▲/▼ triangles
  • SIGNAL_REJECTED  × markers
  • TRADE_WIN/LOSS   ◆ diamonds

Requirements:  pandas  numpy  matplotlib
    pip install pandas numpy matplotlib
"""

import os, sys, re, argparse
from collections import defaultdict
import pandas as pd
import numpy as np
import matplotlib
matplotlib.use('Agg')          # headless-safe; swap to 'TkAgg' for interactive
import matplotlib.pyplot as plt
import matplotlib.patches as patches
import matplotlib.gridspec as gridspec

# ─────────────────────────────────────────────────────────────────────────────
# CONSTANTS
# ─────────────────────────────────────────────────────────────────────────────

SIDE_UNKNOWN = 0
SIDE_BUY     = 1
SIDE_SELL    = 2

C_BG       = '#131722'
C_GRID     = '#1e2230'
C_AXIS     = '#2a2e39'
C_TEXT     = '#d1d4dc'
C_BULL     = '#26a69a'
C_BEAR     = '#ef5350'
C_CVD      = '#2196f3'
C_VP       = '#374151'
C_POC      = '#f59e0b'
C_VA       = '#6366f1'
C_ACCEPT   = '#00e676'
C_REJECT   = '#ffd600'
C_WIN      = '#26a69a'
C_LOSS     = '#ef5350'

IMBALANCE_RATIO = 3.0   # flag a level when one side ≥ 3× the other
VALUE_AREA_PCT  = 0.70  # 70% value area for VP


# ─────────────────────────────────────────────────────────────────────────────
# DATA LOADING
# ─────────────────────────────────────────────────────────────────────────────

def load_ticks_new(path: str) -> pd.DataFrame:
    """New format: Timestamp,SeqNo,TimeMs,Price,Volume,Bid,Ask,Side"""
    df = pd.read_csv(path, dtype=str)
    df.columns = [c.strip() for c in df.columns]

    df['time']   = pd.to_datetime(df['Timestamp'])
    df['price']  = df['Price'].astype(float)
    df['volume'] = pd.to_numeric(df['Volume'], errors='coerce').fillna(1).astype(int)
    df['bid']    = pd.to_numeric(df['Bid'],    errors='coerce').fillna(0.0)
    df['ask']    = pd.to_numeric(df['Ask'],    errors='coerce').fillna(0.0)

    side_map = {'Buy': SIDE_BUY, 'Sell': SIDE_SELL, 'Unknown': SIDE_UNKNOWN,
                '1': SIDE_BUY,   '2': SIDE_SELL,    '0': SIDE_UNKNOWN}
    df['side'] = df['Side'].map(lambda x: side_map.get(str(x).strip(), SIDE_UNKNOWN))

    return df[['time', 'price', 'volume', 'bid', 'ask', 'side']].sort_values('time').reset_index(drop=True)


def load_ticks_raw(path: str, session_start='2026-01-01 18:00:00') -> pd.DataFrame:
    """Old format: TimeMs,Seq,Price,Vol,Bid,Ask,Side  (TimeMs = ms since session open)"""
    df = pd.read_csv(path)
    df.columns = [c.strip() for c in df.columns]

    base = pd.Timestamp(session_start)
    df['time']   = base + pd.to_timedelta(df['TimeMs'].astype(int), unit='ms')
    df['price']  = df['Price'].astype(float)
    df['volume'] = df['Vol'].astype(int)
    df['bid']    = df['Bid'].astype(float)
    df['ask']    = df['Ask'].astype(float)
    df['side']   = df['Side'].astype(int)

    # Side=1 always in old raw file (before BBO fix) — re-classify by tick direction
    df = _reclassify_side_by_tick_direction(df)

    return df[['time', 'price', 'volume', 'bid', 'ask', 'side']].sort_values('time').reset_index(drop=True)


def _reclassify_side_by_tick_direction(df: pd.DataFrame) -> pd.DataFrame:
    """
    Fallback when BBO is unavailable: classify by uptick/downtick vs previous trade.
    This is the tick-test portion of Lee-Ready.
    """
    sides = np.full(len(df), SIDE_UNKNOWN, dtype=int)
    last_price = 0.0
    last_side  = SIDE_UNKNOWN

    for i, row in df.iterrows():
        p = row['price']
        if last_price == 0:
            sides[i] = SIDE_UNKNOWN
        elif p > last_price:
            sides[i] = SIDE_BUY
        elif p < last_price:
            sides[i] = SIDE_SELL
        else:
            sides[i] = last_side  # continuation rule
        last_price = p
        last_side  = sides[i]

    df = df.copy()
    df['side'] = sides
    return df


def load_signals(path: str) -> pd.DataFrame:
    """Load SIGNAL_ACCEPTED / SIGNAL_REJECTED / TRADE rows from strategy log CSV."""
    df = pd.read_csv(path, dtype=str)
    df.columns = [c.strip() for c in df.columns]
    keep = {'SIGNAL_ACCEPTED', 'SIGNAL_REJECTED', 'EVAL', 'TRADE_WIN', 'TRADE_LOSS'}
    df = df[df['Tag'].isin(keep)].copy()
    if df.empty:
        return df
    df['time']      = pd.to_datetime(df['Timestamp'], errors='coerce')
    df['direction'] = df['Direction'].fillna('')
    df['entry']     = pd.to_numeric(df['EntryPrice'], errors='coerce')
    df['stop']      = pd.to_numeric(df['StopPrice'],  errors='coerce')
    df['t1']        = pd.to_numeric(df['T1Price'],    errors='coerce')
    return df.dropna(subset=['time'])


# ─────────────────────────────────────────────────────────────────────────────
# BAR + FOOTPRINT BUILDING
# ─────────────────────────────────────────────────────────────────────────────

def build_bars(ticks: pd.DataFrame, bar_minutes: int, tick_size: float):
    """
    Aggregate ticks into time bars.

    Returns
    -------
    bars       list[dict]   — OHLCV + delta + cvd per bar
    footprint  dict         — bar_time → {price → {'ask':int, 'bid':int}}
    """
    ticks = ticks.copy()
    ticks['bar_time'] = ticks['time'].dt.floor(f'{bar_minutes}min')

    bars      = []
    footprint = {}
    cvd       = 0

    for bar_time, grp in ticks.groupby('bar_time', sort=True):
        prices = grp['price'].values
        vols   = grp['volume'].values
        sides  = grp['side'].values

        ask_mask = sides == SIDE_BUY
        bid_mask = sides == SIDE_SELL
        unk_mask = sides == SIDE_UNKNOWN

        ask_vol = int(vols[ask_mask].sum())
        bid_vol = int(vols[bid_mask].sum())
        # Split unknown volume 50/50 (backtest approximation)
        unk_vol = int(vols[unk_mask].sum())
        ask_vol += unk_vol // 2
        bid_vol += unk_vol - unk_vol // 2

        delta  = ask_vol - bid_vol
        cvd   += delta

        bars.append({
            'time':    bar_time,
            'open':    float(prices[0]),
            'high':    float(prices.max()),
            'low':     float(prices.min()),
            'close':   float(prices[-1]),
            'volume':  int(vols.sum()),
            'ask_vol': ask_vol,
            'bid_vol': bid_vol,
            'delta':   delta,
            'cvd':     cvd,
        })

        # Footprint: bid/ask volume at each price level
        fp = defaultdict(lambda: {'ask': 0, 'bid': 0})
        for price, vol, side in zip(prices, vols, sides):
            px = round(float(price) / tick_size) * tick_size
            if side == SIDE_BUY:
                fp[px]['ask'] += int(vol)
            elif side == SIDE_SELL:
                fp[px]['bid'] += int(vol)
            else:
                fp[px]['ask'] += int(vol) // 2
                fp[px]['bid'] += int(vol) - int(vol) // 2
        footprint[bar_time] = dict(fp)

    return bars, footprint


def build_volume_profile(ticks: pd.DataFrame, tick_size: float) -> dict:
    """Total volume by price level across all ticks."""
    vp = defaultdict(int)
    for _, row in ticks.iterrows():
        px = round(float(row['price']) / tick_size) * tick_size
        vp[px] += int(row['volume'])
    return dict(vp)


def compute_vp_levels(vp: dict):
    """POC + 70% value area (VAH / VAL)."""
    if not vp:
        return None, None, None

    sorted_px  = sorted(vp)
    total_vol  = sum(vp.values())
    poc        = max(vp, key=vp.get)
    va_target  = total_vol * VALUE_AREA_PCT

    lo = hi    = sorted_px.index(poc)
    va_vol     = vp[poc]

    while va_vol < va_target:
        down = vp[sorted_px[lo - 1]] if lo > 0                    else 0
        up   = vp[sorted_px[hi + 1]] if hi < len(sorted_px) - 1   else 0
        if up == 0 and down == 0:
            break
        if up >= down and hi < len(sorted_px) - 1:
            hi += 1;  va_vol += vp[sorted_px[hi]]
        elif lo > 0:
            lo -= 1;  va_vol += vp[sorted_px[lo]]
        else:
            hi += 1;  va_vol += vp[sorted_px[hi]]

    return poc, sorted_px[hi], sorted_px[lo]


# ─────────────────────────────────────────────────────────────────────────────
# CHART STYLE
# ─────────────────────────────────────────────────────────────────────────────

def apply_dark_style():
    plt.rcParams.update({
        'figure.facecolor':  C_BG,
        'axes.facecolor':    C_BG,
        'axes.edgecolor':    C_AXIS,
        'axes.labelcolor':   C_TEXT,
        'grid.color':        C_GRID,
        'grid.linewidth':    0.4,
        'xtick.color':       C_TEXT,
        'ytick.color':       C_TEXT,
        'text.color':        C_TEXT,
        'font.size':         8,
        'font.family':       'monospace',
        'axes.titlecolor':   C_TEXT,
    })


# ─────────────────────────────────────────────────────────────────────────────
# PANEL RENDERERS
# ─────────────────────────────────────────────────────────────────────────────

def render_footprint(ax, bars, footprint, tick_size, show_fp=True):
    """
    Panel 1: OHLC candles with footprint bid/ask cells at each price level.

    Colour coding:
      • Green (dark)  = buy volume at that price
      • Red (dark)    = sell volume at that price
      • Bright green  = imbalance: ask ≥ 3× bid (aggressive buying)
      • Bright red    = imbalance: bid ≥ 3× ask (aggressive selling)
    """
    # Max volume at any single price level — used to scale cell widths
    max_cell_vol = 1
    if show_fp and footprint:
        for fp in footprint.values():
            for lvl in fp.values():
                max_cell_vol = max(max_cell_vol, lvl['ask'] + lvl['bid'])

    for xi, b in enumerate(bars):
        is_bull  = b['close'] >= b['open']
        body_col = C_BULL if is_bull else C_BEAR
        lo, hi   = b['low'], b['high']
        bot      = min(b['open'], b['close'])
        top      = max(b['open'], b['close'])
        body_h   = max(top - bot, tick_size * 0.4)

        # Wick
        ax.plot([xi, xi], [lo, hi], color='#555', linewidth=0.8, zorder=2)

        # Candle body
        ax.add_patch(patches.Rectangle(
            (xi - 0.38, bot), 0.76, body_h,
            facecolor=body_col, edgecolor=body_col,
            alpha=0.9, linewidth=0.4, zorder=3
        ))

        # Footprint cells
        if show_fp and footprint and b['time'] in footprint:
            fp       = footprint[b['time']]
            half_w   = 0.36           # half the bar width allocated to FP cells

            for px, vols in fp.items():
                ask = vols['ask']
                bid = vols['bid']
                if ask + bid == 0:
                    continue

                cell_h = tick_size * 0.80
                y0     = px - cell_h / 2

                imb_ask = bid > 0 and ask / bid >= IMBALANCE_RATIO
                imb_bid = ask > 0 and bid / ask >= IMBALANCE_RATIO

                # Ask bar (buy vol) — extends to the right
                if ask > 0:
                    w = half_w * ask / max_cell_vol
                    col = '#00e676' if imb_ask else '#1b5e20'
                    ax.barh(px, w, height=cell_h, left=xi,
                            color=col, alpha=0.75, linewidth=0, zorder=4)

                # Bid bar (sell vol) — extends to the left
                if bid > 0:
                    w = half_w * bid / max_cell_vol
                    col = '#ff1744' if imb_bid else '#7f0000'
                    ax.barh(px, -w, height=cell_h, left=xi,
                            color=col, alpha=0.75, linewidth=0, zorder=4)

                # Volume text label — only if large enough to read
                total = ask + bid
                if total >= max(5, max_cell_vol * 0.02):
                    label = f"{bid}×{ask}"
                    ax.text(xi, px, label, ha='center', va='center',
                            fontsize=4.2, color=C_TEXT, zorder=5, alpha=0.85)

    n = len(bars)
    ax.set_xlim(-0.6, n - 0.4)
    ax.set_ylabel('Price', fontsize=7)
    ax.grid(axis='y', linewidth=0.3, alpha=0.5)
    ax.set_xticks([])


def render_volume(ax, bars):
    """Panel 2: Volume bars, green when bar delta ≥ 0 else red."""
    xs     = list(range(len(bars)))
    colors = [C_BULL if b['delta'] >= 0 else C_BEAR for b in bars]
    ax.bar(xs, [b['volume'] for b in bars],
           color=colors, width=0.8, alpha=0.85, linewidth=0)
    ax.set_xlim(-0.6, len(bars) - 0.4)
    ax.set_ylabel('Volume', fontsize=7)
    ax.grid(axis='y', linewidth=0.3, alpha=0.5)
    ax.set_xticks([])


def render_cvd(ax, bars):
    """Panel 3: Cumulative volume delta line with fill."""
    xs   = list(range(len(bars)))
    cvds = [b['cvd'] for b in bars]
    ax.plot(xs, cvds, color=C_CVD, linewidth=1.1, zorder=3)
    ax.fill_between(xs, cvds, 0,
                    where=[v >= 0 for v in cvds],
                    color=C_BULL, alpha=0.15, zorder=2)
    ax.fill_between(xs, cvds, 0,
                    where=[v < 0 for v in cvds],
                    color=C_BEAR, alpha=0.15, zorder=2)
    ax.axhline(0, color=C_TEXT, linewidth=0.4, alpha=0.35)
    ax.set_xlim(-0.6, len(bars) - 0.4)
    ax.set_ylabel('CVD', fontsize=7)
    ax.grid(axis='y', linewidth=0.3, alpha=0.5)
    ax.set_xticks([])


def render_delta(ax, bars):
    """Panel 4: Per-bar delta histogram with time labels."""
    xs     = list(range(len(bars)))
    deltas = [b['delta'] for b in bars]
    colors = [C_BULL if d >= 0 else C_BEAR for d in deltas]
    ax.bar(xs, deltas, color=colors, width=0.8, alpha=0.85, linewidth=0)
    ax.axhline(0, color=C_TEXT, linewidth=0.4, alpha=0.35)
    ax.set_xlim(-0.6, len(bars) - 0.4)
    ax.set_ylabel('Δ Delta', fontsize=7)
    ax.grid(axis='y', linewidth=0.3, alpha=0.5)

    # Time labels — show every N bars so they don't overlap
    n           = len(bars)
    label_every = max(1, n // 20)
    ticks_x     = xs[::label_every]
    ax.set_xticks(ticks_x)
    ax.set_xticklabels(
        [bars[i]['time'].strftime('%H:%M') for i in ticks_x],
        rotation=45, ha='right', fontsize=6
    )


def render_volume_profile(ax, vp, poc, vah, val, y_min, y_max, tick_size):
    """Right sidebar: horizontal volume profile with POC / VA lines."""
    if not vp:
        ax.set_visible(False)
        return

    max_vol = max(vp.values())
    for px, vol in sorted(vp.items()):
        if not (y_min <= px <= y_max):
            continue
        frac  = vol / max_vol
        color = C_POC if px == poc else (C_VA if val <= px <= vah else C_VP)
        ax.barh(px, frac, height=tick_size * 0.75,
                color=color, alpha=0.80, linewidth=0)

    if poc: ax.axhline(poc, color=C_POC, linewidth=0.9, linestyle='--', alpha=0.85)
    if vah: ax.axhline(vah, color=C_VA,  linewidth=0.6, linestyle=':',  alpha=0.65)
    if val: ax.axhline(val, color=C_VA,  linewidth=0.6, linestyle=':',  alpha=0.65)

    ax.set_xlim(0, 1.25)
    ax.set_ylim(y_min, y_max)
    ax.set_xticks([])
    ax.yaxis.set_visible(False)
    ax.set_title('VP', fontsize=7, pad=2)


def render_poc_vah_val_lines(ax, poc, vah, val, n):
    """Overlay POC / VAH / VAL horizontal lines on the candle panel."""
    if poc:
        ax.axhline(poc, color=C_POC, linewidth=0.8, linestyle='--',
                   alpha=0.65, label=f'POC {poc:.2f}', zorder=1)
    if vah:
        ax.axhline(vah, color=C_VA,  linewidth=0.6, linestyle=':',
                   alpha=0.55, label=f'VAH {vah:.2f}', zorder=1)
    if val:
        ax.axhline(val, color=C_VA,  linewidth=0.6, linestyle=':',
                   alpha=0.55, label=f'VAL {val:.2f}', zorder=1)


def render_signal_overlay(ax, signals, bars, bar_minutes):
    """Overlay accepted/rejected signals and trade outcomes on the candle panel."""
    if signals is None or signals.empty:
        return

    bar_times = {b['time']: i for i, b in enumerate(bars)}

    for _, sig in signals.iterrows():
        bt = sig['time'].floor(f'{bar_minutes}min')
        if bt not in bar_times:
            continue
        xi    = bar_times[bt]
        tag   = sig['Tag']
        entry = sig['entry']
        dirn  = sig['direction']

        if pd.isna(entry) or entry == 0:
            continue

        if tag == 'SIGNAL_ACCEPTED':
            mk  = '^' if dirn == 'Long' else 'v'
            col = C_ACCEPT
            off = -1.5 if dirn == 'Long' else +1.5
            ax.scatter(xi, entry + off, marker=mk, color=col,
                       s=90, zorder=10, edgecolors='white', linewidths=0.5)

        elif tag == 'SIGNAL_REJECTED':
            ax.scatter(xi, entry, marker='x', color=C_REJECT,
                       s=50, zorder=10, linewidths=1.2)

        elif tag == 'TRADE_WIN':
            ax.scatter(xi, entry, marker='D', color=C_WIN,
                       s=55, zorder=10, edgecolors='white', linewidths=0.4, alpha=0.9)

        elif tag == 'TRADE_LOSS':
            ax.scatter(xi, entry, marker='D', color=C_LOSS,
                       s=55, zorder=10, edgecolors='white', linewidths=0.4, alpha=0.9)


# ─────────────────────────────────────────────────────────────────────────────
# MAIN CHART ASSEMBLY
# ─────────────────────────────────────────────────────────────────────────────

def build_chart(ticks_path, log_path=None, bar_minutes=5, max_bars=80,
                tick_size=0.25, show_footprint=True, raw_format=False,
                session_start='2026-01-01 18:00:00', output_path=None):

    apply_dark_style()

    # ── Load data ─────────────────────────────────────────────────────────────
    print(f"Loading ticks: {ticks_path}")
    if raw_format:
        ticks = load_ticks_raw(ticks_path, session_start)
    else:
        ticks = load_ticks_new(ticks_path)
    print(f"  {len(ticks):,} ticks  |  "
          f"price {ticks['price'].min():.2f}–{ticks['price'].max():.2f}  |  "
          f"Buy {(ticks['side']==SIDE_BUY).sum():,}  "
          f"Sell {(ticks['side']==SIDE_SELL).sum():,}")

    signals = None
    if log_path and os.path.exists(log_path):
        print(f"Loading signals: {log_path}")
        signals = load_signals(log_path)
        print(f"  {len(signals)} signal events")

    # ── Build bars ────────────────────────────────────────────────────────────
    print(f"Building {bar_minutes}m bars...")
    bars, footprint = build_bars(ticks, bar_minutes, tick_size)
    print(f"  {len(bars)} bars total")

    # Volume profile over the full dataset
    vp_full     = build_volume_profile(ticks, tick_size)
    poc_f, vah_f, val_f = compute_vp_levels(vp_full)

    # Trim to last N bars
    if len(bars) > max_bars:
        bars = bars[-max_bars:]
        bar_set  = {b['time'] for b in bars}
        footprint = {k: v for k, v in footprint.items() if k in bar_set}
        # Recompute VP for the visible window
        visible_ticks = ticks[ticks['time'].dt.floor(f'{bar_minutes}min').isin(bar_set)]
        vp = build_volume_profile(visible_ticks, tick_size)
    else:
        vp = vp_full

    poc, vah, val = compute_vp_levels(vp)
    n             = len(bars)
    if n == 0:
        print("No bars to render.")
        return

    y_min = min(b['low']  for b in bars) - tick_size * 6
    y_max = max(b['high'] for b in bars) + tick_size * 6

    print(f"  Rendering {n} bars  |  "
          f"POC={poc:.2f}  VAH={vah:.2f}  VAL={val:.2f}"
          f"  range {y_min:.2f}–{y_max:.2f}")

    # ── Figure layout ─────────────────────────────────────────────────────────
    fig_w = max(22, n * 0.22 + 3)
    fig   = plt.figure(figsize=(fig_w, 16), dpi=120)
    fig.patch.set_facecolor(C_BG)

    outer = gridspec.GridSpec(1, 2, figure=fig,
                              width_ratios=[n * 0.22, 1.8],
                              wspace=0.008)

    inner = gridspec.GridSpecFromSubplotSpec(
        4, 1, subplot_spec=outer[0],
        height_ratios=[5, 1.2, 1.2, 1.2],
        hspace=0.03
    )

    ax_fp  = fig.add_subplot(inner[0])   # footprint candles
    ax_vol = fig.add_subplot(inner[1])   # volume
    ax_cvd = fig.add_subplot(inner[2])   # cumulative delta
    ax_dlt = fig.add_subplot(inner[3])   # bar delta
    ax_vp  = fig.add_subplot(outer[1])   # volume profile sidebar

    # ── Render panels ─────────────────────────────────────────────────────────
    render_footprint(ax_fp, bars, footprint, tick_size, show_footprint)
    render_poc_vah_val_lines(ax_fp, poc, vah, val, n)
    render_signal_overlay(ax_fp, signals, bars, bar_minutes)
    ax_fp.set_ylim(y_min, y_max)
    ax_fp.legend(loc='upper left', fontsize=6,
                 framealpha=0.35, facecolor=C_BG, edgecolor=C_AXIS)

    render_volume(ax_vol, bars)
    render_cvd(ax_cvd, bars)
    render_delta(ax_dlt, bars)
    render_volume_profile(ax_vp, vp, poc, vah, val, y_min, y_max, tick_size)
    ax_vp.set_facecolor(C_BG)

    # ── Title + stats ─────────────────────────────────────────────────────────
    t0   = bars[0]['time']
    t1   = bars[-1]['time']
    nv   = sum(b['volume'] for b in bars)
    nd   = sum(b['delta']  for b in bars)
    ax_fp.set_title(
        f"Footprint  {bar_minutes}m  │  {t0:%Y-%m-%d %H:%M} → {t1:%H:%M}  │  "
        f"{n} bars  {nv:,} vol  Δ {nd:+,}",
        fontsize=9, pad=5
    )

    legend_txt = (
        "▲ ACCEPTED  × REJECTED  ◆ WIN/LOSS\n"
        "Cell: sell×buy  |  bright = imbalance ≥3:1\n"
        f"POC={poc:.2f}  VAH={vah:.2f}  VAL={val:.2f}"
    )
    fig.text(0.01, 0.005, legend_txt, color=C_TEXT, fontsize=6.5,
             va='bottom', ha='left', family='monospace', alpha=0.75)

    # ── Save / show ───────────────────────────────────────────────────────────
    plt.tight_layout(pad=0.4)

    if output_path:
        plt.savefig(output_path, dpi=150, bbox_inches='tight',
                    facecolor=C_BG, edgecolor='none')
        print(f"Chart saved → {output_path}")
    else:
        matplotlib.use('TkAgg')
        plt.show()

    plt.close(fig)


# ─────────────────────────────────────────────────────────────────────────────
# CLI
# ─────────────────────────────────────────────────────────────────────────────

if __name__ == '__main__':
    ap = argparse.ArgumentParser(
        description='Footprint / order-flow chart from NinjaTrader tick data')

    ap.add_argument('--ticks',       required=True,
                    help='Tick CSV path (new: Timestamp,SeqNo,... or old: TimeMs,...)')
    ap.add_argument('--log',         default=None,
                    help='Strategy log CSV for signal overlays (optional)')
    ap.add_argument('--raw',         action='store_true',
                    help='Use old Ticks_Raw.csv format (TimeMs-based)')
    ap.add_argument('--session-start', default='2026-01-01 18:00:00',
                    help='Session open timestamp for --raw format (default: 2026-01-01 18:00:00)')
    ap.add_argument('--bar-minutes', type=int,   default=5,
                    help='Bar size in minutes (default 5)')
    ap.add_argument('--bars',        type=int,   default=80,
                    help='Max bars to display (default 80)')
    ap.add_argument('--tick-size',   type=float, default=0.25,
                    help='Instrument tick size (default 0.25 for NQ/MES)')
    ap.add_argument('--no-footprint', action='store_true',
                    help='Disable footprint bid/ask cell rendering')
    ap.add_argument('--output',      default=None,
                    help='Save to PNG instead of showing interactively')

    args = ap.parse_args()

    build_chart(
        ticks_path    = args.ticks,
        log_path      = args.log,
        bar_minutes   = args.bar_minutes,
        max_bars      = args.bars,
        tick_size     = args.tick_size,
        show_footprint= not args.no_footprint,
        raw_format    = args.raw,
        session_start = args.session_start,
        output_path   = args.output,
    )
