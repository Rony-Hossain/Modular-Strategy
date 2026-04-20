#!/usr/bin/env python3
"""
footprint_chart.py  —  Interactive footprint / order-flow chart (PyQtGraph)

Built on top of the FootprintBuilder engine (proper diagonal imbalance,
stacked detection, unfinished auctions, POC tie-break, LOD text).

Adds:
  • Dual-format loader  — new Timestamp CSV  or  old Ticks_Raw TimeMs CSV
  • 4-panel linked layout
      [1] Footprint candles  (scrollable / zoomable, LOD bid×ask text)
      [2] Volume bars        (coloured by delta sign)
      [3] Cumulative Delta   (CVD line with fill)
      [4] Bar Delta          (per-bar Δ histogram)
  • Volume Profile sidebar  (POC / VAH / VAL, 70% value area)
  • Signal overlay from strategy log CSV
      ▲/▼ = ACCEPTED   × = REJECTED   ◆ = WIN/LOSS

Requirements:
    pip install pandas numpy pyqtgraph PyQt5   (or PyQt6 / PySide6)

Usage:
    # New format (from ModularStrategy_Ticks_*.csv)
    python footprint_chart.py --ticks ModularStrategy_Ticks_20260104_180000.csv
                               --log   ModularStrategy_20260104_180000.csv

    # Old raw format (backtest/Ticks_Raw.csv)
    python footprint_chart.py --ticks backtest/Ticks_Raw.csv --raw
                               --session-start "2026-01-04 18:00:00"
                               --bar-seconds 300
"""

import sys
import math
import argparse
import os
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple
from collections import defaultdict

import pandas as pd
import numpy as np
import pyqtgraph as pg
from pyqtgraph.Qt import QtCore, QtGui, QtWidgets


# ─────────────────────────────────────────────────────────────────────────────
# CONFIG  (override via CLI flags)
# ─────────────────────────────────────────────────────────────────────────────

TICK_SIZE                 = 0.25
BAR_SECONDS               = 300      # 5-minute bars by default
BAR_WIDTH                 = 0.88

IMBALANCE_RATIO           = 3.0
STACKED_IMBALANCE_LEN     = 3
MIN_IMBALANCE_VOLUME      = 1
ALLOW_ZERO_SIDE_IMBALANCE = False
TEXT_LOD_THRESHOLD        = 14.0
POC_TIE_BREAK             = "closest_to_mid"   # first|last|highest|lowest|closest_to_mid|closest_to_close
SESSION_RESET_MODE        = None               # None | "date"
VALUE_AREA_PCT            = 0.70

# colours
C_BG        = (19, 23, 34)
C_GRID      = (30, 34, 48, 80)
C_TEXT      = (209, 212, 220)
C_BULL      = (38, 166, 154)
C_BEAR      = (239, 83, 80)
C_CVD       = (33, 150, 243)
C_POC       = (245, 158, 11)
C_VA_FILL   = (99, 102, 241, 35)
C_ACCEPT    = (0, 230, 118)
C_REJECT    = (255, 214, 0)
C_WIN       = (38, 166, 154)
C_LOSS      = (239, 83, 80)


# ─────────────────────────────────────────────────────────────────────────────
# HELPERS  (unchanged from sample)
# ─────────────────────────────────────────────────────────────────────────────

def is_almost_multiple(value: float, step: float, tol: float = 1e-9) -> bool:
    q = value / step
    return abs(q - round(q)) <= tol


def snap_to_tick(price: float, tick_size: float) -> float:
    return round(round(price / tick_size) * tick_size, 10)


def iter_price_range(low: float, high: float, tick_size: float):
    n = int(round((high - low) / tick_size))
    for i in range(n + 1):
        yield round(low + i * tick_size, 10)


def pick_poc_price(candidates: List[float], bar_mid: float, bar_close: float, mode: str) -> float:
    if len(candidates) == 1:
        return candidates[0]
    if mode == "first":            return candidates[0]
    if mode == "last":             return candidates[-1]
    if mode == "highest":          return max(candidates)
    if mode == "lowest":           return min(candidates)
    if mode == "closest_to_close":
        return sorted(candidates, key=lambda p: (abs(p - bar_close), abs(p - bar_mid), -p))[0]
    return sorted(candidates, key=lambda p: (abs(p - bar_mid), abs(p - bar_close), -p))[0]


# ─────────────────────────────────────────────────────────────────────────────
# DATA MODEL  (unchanged from sample)
# ─────────────────────────────────────────────────────────────────────────────

@dataclass(slots=True)
class PriceLevel:
    price:         float
    bid_vol:       int  = 0
    ask_vol:       int  = 0
    buy_imbalance: bool = False
    sell_imbalance:bool = False
    stacked_buy:   bool = False
    stacked_sell:  bool = False

    @property
    def total_vol(self) -> int:  return self.bid_vol + self.ask_vol
    @property
    def delta(self)     -> int:  return self.ask_vol - self.bid_vol


@dataclass(slots=True)
class FootprintBar:
    x_idx:      int
    start_time: pd.Timestamp
    end_time:   pd.Timestamp
    open:  float
    high:  float
    low:   float
    close: float

    levels:     Dict[float, PriceLevel] = field(default_factory=dict)
    total_vol:  int   = 0
    total_delta:int   = 0
    poc_price:  Optional[float] = None
    max_vol:    int   = 0
    has_unfinished_high: bool = False
    has_unfinished_low:  bool = False
    stacked_buy_prices:  List[float] = field(default_factory=list)
    stacked_sell_prices: List[float] = field(default_factory=list)
    cvd:        int   = 0   # cumulative delta up to and including this bar

    def get_or_create_level(self, price: float) -> PriceLevel:
        lvl = self.levels.get(price)
        if lvl is None:
            lvl = PriceLevel(price=price)
            self.levels[price] = lvl
        return lvl

    def full_ladder(self, tick_size: float) -> List[PriceLevel]:
        out: List[PriceLevel] = []
        for px in iter_price_range(self.low, self.high, tick_size):
            lvl = self.levels.get(px)
            if lvl is None:
                lvl = PriceLevel(price=px)
            out.append(lvl)
        return out


# ─────────────────────────────────────────────────────────────────────────────
# DATA LOADING  (extended to support both CSV formats)
# ─────────────────────────────────────────────────────────────────────────────

def _reclassify_by_tick_direction(df: pd.DataFrame,
                                   last_p: float = 0.0,
                                   last_s: int   = 0,
                                   return_state: bool = False):
    """Lee-Ready tick-test fallback: assign SideSign from price direction.
    Pass return_state=True to get (df, last_p, last_s) for chaining across chunks."""
    signs = np.zeros(len(df), dtype=np.int8)
    for i, p in enumerate(df['Price'].values):
        if last_p == 0.0:
            s = 0
        elif p > last_p:
            s = 1
        elif p < last_p:
            s = -1
        else:
            s = last_s
        signs[i] = s
        last_p    = p
        last_s    = s
    df = df.copy()
    df['SideSign'] = signs
    if return_state:
        return df, last_p, last_s
    return df


def load_ticks_date_range(path: str, date_filter: str, tick_size: float) -> pd.DataFrame:
    """
    Efficiently load only ticks for a date or range using chunked reading.
    Avoids reading gigabytes just to filter one day.
    date_filter: "YYYY-MM-DD" or "YYYY-MM-DD:YYYY-MM-DD"
    """
    parts  = date_filter.split(':')
    d0     = pd.Timestamp(parts[0]).date()
    d1     = pd.Timestamp(parts[-1]).date()
    chunks = []
    CHUNK  = 100_000
    total_read = 0
    found  = False

    print(f"  Scanning for {d0} -> {d1} (chunked, skipping early rows)...")
    for chunk in pd.read_csv(path, chunksize=CHUNK):
        chunk.columns  = [c.strip() for c in chunk.columns]
        chunk['Timestamp'] = pd.to_datetime(chunk['Timestamp'], errors='coerce')
        chunk_min = chunk['Timestamp'].min().date()
        chunk_max = chunk['Timestamp'].max().date()
        total_read += len(chunk)

        if chunk_max < d0:          # not yet at target — skip
            if total_read % 1_000_000 < CHUNK:
                print(f"    ...skipped {total_read:,} rows, at {chunk_max}")
            continue
        if chunk_min > d1:          # past target — stop
            break

        found = True
        mask = (chunk['Timestamp'].dt.date >= d0) & (chunk['Timestamp'].dt.date <= d1)
        chunks.append(chunk[mask])

    if not chunks:
        return pd.DataFrame()
    return pd.concat(chunks, ignore_index=True)


def _finalize_new_ticks(df: pd.DataFrame, tick_size: float) -> pd.DataFrame:
    """Shared post-processing for new-format tick DataFrames."""
    if df.empty:
        return df
    df.columns  = [c.strip() for c in df.columns]
    df['Timestamp'] = pd.to_datetime(df['Timestamp'], errors='coerce')
    df['SeqNo']     = pd.to_numeric(df['SeqNo'],   errors='coerce').fillna(0).astype('int64')
    df['Price']     = df['Price'].astype('float64')
    df['Volume']    = pd.to_numeric(df['Volume'],  errors='coerce').fillna(1).astype('int64')
    df['Bid']       = pd.to_numeric(df['Bid'],     errors='coerce').fillna(0.0)
    df['Ask']       = pd.to_numeric(df['Ask'],     errors='coerce').fillna(0.0)
    side_map = {'Buy': 1, 'Sell': -1, 'Unknown': 0,
                'buy': 1, 'sell': -1, 'unknown': 0}
    df['SideSign'] = df['Side'].astype(str).str.strip().map(
        lambda x: side_map.get(x, 0)).astype('int8')
    df = df.sort_values(['Timestamp', 'SeqNo'], kind='stable').reset_index(drop=True)
    if (df['SideSign'] == 0).any():
        df = _reclassify_by_tick_direction(df)
    print(f"  Buy={( df['SideSign']> 0).sum():,}  "
          f"Sell={(df['SideSign']< 0).sum():,}  "
          f"Unknown={(df['SideSign']==0).sum():,}")
    return df


def load_ticks_new(path: str, tick_size: float, max_ticks: Optional[int] = None) -> pd.DataFrame:
    """New format:  Timestamp,SeqNo,TimeMs,Price,Volume,Bid,Ask,Side"""
    df = pd.read_csv(path, nrows=max_ticks)
    return _finalize_new_ticks(df, tick_size)


def load_ticks_raw(path: str, session_start: str, tick_size: float, max_ticks: Optional[int] = None) -> pd.DataFrame:
    """
    Old format:  TimeMs,Seq,Price,Vol,Bid,Ask,Side
    TimeMs = milliseconds since session open.  Side is int (0/1/2 — all 1 in backtest).
    """
    df = pd.read_csv(path, nrows=max_ticks)
    df.columns = [c.strip() for c in df.columns]

    base = pd.Timestamp(session_start)
    df['Timestamp'] = base + pd.to_timedelta(df['TimeMs'].astype(int), unit='ms')
    df['SeqNo']     = df['Seq'].astype('int64')
    df['Price']     = df['Price'].astype('float64')
    df['Volume']    = df['Vol'].astype('int64')
    df['Bid']       = df['Bid'].astype('float64')
    df['Ask']       = df['Ask'].astype('float64')

    # All ticks in raw file have Side=1; re-classify by tick direction
    df = _reclassify_by_tick_direction(df)

    df = df.sort_values(['Timestamp', 'SeqNo'], kind='stable').reset_index(drop=True)

    print(f"  Buy={( df['SideSign']> 0).sum():,}  "
          f"Sell={(df['SideSign']< 0).sum():,}  "
          f"Unknown={(df['SideSign']==0).sum():,}")
    return df


def load_signals(path: str) -> Optional[pd.DataFrame]:
    """Load signal events from strategy log CSV (optional)."""
    if not path or not os.path.exists(path):
        return None
    df = pd.read_csv(path, dtype=str)
    df.columns = [c.strip() for c in df.columns]
    keep = {'SIGNAL_ACCEPTED', 'SIGNAL_REJECTED', 'TRADE_WIN', 'TRADE_LOSS'}
    df = df[df['Tag'].isin(keep)].copy()
    if df.empty:
        return None
    df['time']  = pd.to_datetime(df['Timestamp'], errors='coerce')
    df['entry'] = pd.to_numeric(df['EntryPrice'], errors='coerce')
    df['dir']   = df['Direction'].fillna('')
    return df.dropna(subset=['time', 'entry'])


# ─────────────────────────────────────────────────────────────────────────────
# FOOTPRINT ENGINE  (unchanged from sample, with SideSign=0 support added)
# ─────────────────────────────────────────────────────────────────────────────

class FootprintBuilder:
    def __init__(
        self,
        tick_size:                float = 0.25,
        bar_seconds:              int   = 300,
        imbalance_ratio:          float = 3.0,
        stacked_len:              int   = 3,
        min_imbalance_volume:     int   = 1,
        allow_zero_side_imbalance:bool  = False,
        poc_tie_break:            str   = "closest_to_mid",
        session_reset_mode:       Optional[str] = None,
    ):
        self.tick_size                 = tick_size
        self.bar_seconds               = bar_seconds
        self.imbalance_ratio           = imbalance_ratio
        self.stacked_len               = stacked_len
        self.min_imbalance_volume      = min_imbalance_volume
        self.allow_zero_side_imbalance = allow_zero_side_imbalance
        self.poc_tie_break             = poc_tie_break
        self.session_reset_mode        = session_reset_mode

    def _assign_bar_keys(self, df: pd.DataFrame) -> pd.DataFrame:
        data = df.copy()
        data['BarTime'] = data['Timestamp'].dt.floor(f'{self.bar_seconds}s')
        if self.session_reset_mode is None:
            bar_map = {t: i for i, t in enumerate(data['BarTime'].drop_duplicates())}
            data['x_idx'] = data['BarTime'].map(bar_map)
            return data
        if self.session_reset_mode == 'date':
            data['SessionKey'] = data['Timestamp'].dt.date
            x_list = []
            for _, grp in data.groupby('SessionKey', sort=False):
                lm = {t: i for i, t in enumerate(grp['BarTime'].drop_duplicates())}
                x_list.extend(grp['BarTime'].map(lm).tolist())
            data['x_idx'] = x_list
            return data
        raise ValueError(f"Unsupported session_reset_mode: {self.session_reset_mode}")

    def build(self, df: pd.DataFrame) -> Tuple[List[FootprintBar], List[pd.Timestamp]]:
        data      = self._assign_bar_keys(df)
        bar_times = list(data['BarTime'].drop_duplicates())
        bars: Dict[Tuple, FootprintBar] = {}

        for row in data.itertuples(index=False):
            x         = int(row.x_idx)
            px        = snap_to_tick(float(row.Price), self.tick_size)
            bar_start = row.BarTime
            sess_key  = getattr(row, 'SessionKey', None)
            key       = (sess_key, x)

            bar = bars.get(key)
            if bar is None:
                bar = FootprintBar(
                    x_idx=x, start_time=bar_start,
                    end_time=bar_start + pd.Timedelta(seconds=self.bar_seconds),
                    open=px, high=px, low=px, close=px,
                )
                bars[key] = bar

            bar.high  = max(bar.high, px)
            bar.low   = min(bar.low,  px)
            bar.close = px

            lvl  = bar.get_or_create_level(px)
            size = int(row.Volume)
            sign = int(row.SideSign)

            if sign > 0:
                lvl.ask_vol += size
            elif sign < 0:
                lvl.bid_vol += size
            else:
                # Unknown side — split 50/50
                lvl.ask_vol += size // 2
                lvl.bid_vol += size - size // 2

        ordered = sorted(bars.values(), key=lambda b: (b.start_time, b.x_idx))

        # Finalise + compute CVD
        cvd = 0
        for bar in ordered:
            self._finalize_bar(bar)
            self._compute_imbalances(bar)
            self._mark_stacked_imbalances(bar)
            self._mark_unfinished_auctions(bar)
            cvd    += bar.total_delta
            bar.cvd = cvd

        return ordered, bar_times

    def build_streaming(self, path: str,
                        date_filter: Optional[str] = None,
                        chunk_size: int = 200_000) -> Tuple[List[FootprintBar], List]:
        """
        Stream-build bars from a huge CSV using chunked reading.
        Only one chunk of raw ticks is held in memory at a time.
        Handles tick-direction state across chunk boundaries.
        """
        d0 = d1 = None
        if date_filter:
            parts = date_filter.split(':')
            d0 = pd.Timestamp(parts[0])
            d1 = pd.Timestamp(parts[-1]) + pd.Timedelta(days=1)

        side_map = {'Buy': 1, 'Sell': -1, 'Unknown': 0,
                    'buy': 1, 'sell': -1, 'unknown': 0}

        bars_dict: Dict[pd.Timestamp, FootprintBar] = {}
        total_read   = 0
        last_p: float = 0.0
        last_s: int   = 0

        print(f"  Streaming {path} in {chunk_size:,}-row chunks...")
        for chunk in pd.read_csv(path, chunksize=chunk_size):
            chunk.columns = [c.strip() for c in chunk.columns]
            chunk['Timestamp'] = pd.to_datetime(chunk['Timestamp'], errors='coerce')
            chunk['Price']     = chunk['Price'].astype('float64')
            chunk['Volume']    = pd.to_numeric(chunk['Volume'], errors='coerce').fillna(1).astype('int64')
            total_read += len(chunk)

            # Skip / stop based on date filter
            if d0 is not None:
                if chunk['Timestamp'].max() < d0:
                    if total_read % 5_000_000 < chunk_size:
                        print(f"    skipped {total_read:,} rows ...")
                    last_p = float(chunk['Price'].iloc[-1])
                    continue
                if chunk['Timestamp'].min() > d1:
                    break
                mask  = (chunk['Timestamp'] >= d0) & (chunk['Timestamp'] < d1)
                chunk = chunk[mask].reset_index(drop=True)

            if chunk.empty:
                continue

            # Side classification with cross-chunk state
            if 'Side' in chunk.columns:
                chunk['SideSign'] = chunk['Side'].astype(str).str.strip().map(
                    lambda x: side_map.get(x, 0)).astype('int8')
            else:
                chunk['SideSign'] = np.zeros(len(chunk), dtype=np.int8)

            if (chunk['SideSign'] == 0).any():
                chunk, last_p, last_s = _reclassify_by_tick_direction(
                    chunk, last_p, last_s, return_state=True)
            else:
                last_p = float(chunk['Price'].iloc[-1])

            # Bar time assignment
            chunk['BarTime']  = chunk['Timestamp'].dt.floor(f'{self.bar_seconds}s')
            chunk['PriceSn']  = (chunk['Price'] / self.tick_size).round() * self.tick_size
            chunk['PriceSn']  = chunk['PriceSn'].round(10)

            # Vectorised volume accumulation by (BarTime, PriceSn)
            ask_df  = chunk[chunk['SideSign'] > 0].groupby(['BarTime', 'PriceSn'])['Volume'].sum()
            bid_df  = chunk[chunk['SideSign'] < 0].groupby(['BarTime', 'PriceSn'])['Volume'].sum()
            unk_df  = chunk[chunk['SideSign'] == 0].groupby(['BarTime', 'PriceSn'])['Volume'].sum()

            # OHLC per bar-time using this chunk
            ohlc = chunk.groupby('BarTime').agg(
                first_p=('Price', 'first'),
                high_p =('Price', 'max'),
                low_p  =('Price', 'min'),
                last_p =('Price', 'last'),
            )

            all_bt = set()
            for idx in (ask_df.index, bid_df.index, unk_df.index):
                if len(idx): all_bt.update(idx.get_level_values(0))

            for bt in all_bt:
                bar = bars_dict.get(bt)
                if bar is None:
                    row = ohlc.loc[bt] if bt in ohlc.index else None
                    op = float(row['first_p']) if row is not None else 0.0
                    hi = float(row['high_p'])  if row is not None else 0.0
                    lo = float(row['low_p'])   if row is not None else 0.0
                    cl = float(row['last_p'])  if row is not None else 0.0
                    bar = FootprintBar(
                        x_idx=0, start_time=bt,
                        end_time=bt + pd.Timedelta(seconds=self.bar_seconds),
                        open=op, high=hi, low=lo, close=cl,
                    )
                    bars_dict[bt] = bar
                else:
                    if bt in ohlc.index:
                        r       = ohlc.loc[bt]
                        bar.high  = max(bar.high,  float(r['high_p']))
                        bar.low   = min(bar.low,   float(r['low_p']))
                        bar.close = float(r['last_p'])

                # ask volumes
                if len(ask_df) and bt in ask_df.index.get_level_values(0):
                    for (_, px), vol in ask_df.xs(bt, level=0).items():
                        bar.get_or_create_level(snap_to_tick(px, self.tick_size)).ask_vol += int(vol)
                # bid volumes
                if len(bid_df) and bt in bid_df.index.get_level_values(0):
                    for (_, px), vol in bid_df.xs(bt, level=0).items():
                        bar.get_or_create_level(snap_to_tick(px, self.tick_size)).bid_vol += int(vol)
                # unknown — split 50/50
                if len(unk_df) and bt in unk_df.index.get_level_values(0):
                    for (_, px), vol in unk_df.xs(bt, level=0).items():
                        lvl  = bar.get_or_create_level(snap_to_tick(px, self.tick_size))
                        half = int(vol) // 2
                        lvl.ask_vol += half
                        lvl.bid_vol += int(vol) - half

            if total_read % 10_000_000 < chunk_size:
                print(f"    {total_read:,} rows processed, {len(bars_dict):,} bars so far ...")

        print(f"  Done reading. {total_read:,} rows -> {len(bars_dict):,} bars.")

        # Sort and assign sequential x_idx
        ordered = sorted(bars_dict.values(), key=lambda b: b.start_time)
        for i, b in enumerate(ordered):
            b.x_idx = i

        # Finalize + CVD
        cvd = 0
        for bar in ordered:
            self._finalize_bar(bar)
            self._compute_imbalances(bar)
            self._mark_stacked_imbalances(bar)
            self._mark_unfinished_auctions(bar)
            cvd     += bar.total_delta
            bar.cvd  = cvd

        return ordered, [b.start_time for b in ordered]

    # ── internals ─────────────────────────────────────────────────────────────

    def _finalize_bar(self, bar: FootprintBar):
        ladder    = bar.full_ladder(self.tick_size)
        total_vol = sum(lvl.total_vol for lvl in ladder)
        max_vol   = max((lvl.total_vol for lvl in ladder), default=0)
        poc_cands = [lvl.price for lvl in ladder if lvl.total_vol == max_vol]
        bar_mid   = (bar.high + bar.low) / 2.0
        bar.total_vol   = total_vol
        bar.total_delta = sum(lvl.delta for lvl in ladder)
        bar.max_vol     = max_vol
        bar.poc_price   = pick_poc_price(poc_cands, bar_mid, bar.close, self.poc_tie_break) if poc_cands else None

    def _is_buy_imb(self, ask: int, cmp_bid: int) -> bool:
        if ask < self.min_imbalance_volume: return False
        if cmp_bid == 0: return self.allow_zero_side_imbalance
        return ask >= cmp_bid * self.imbalance_ratio

    def _is_sell_imb(self, bid: int, cmp_ask: int) -> bool:
        if bid < self.min_imbalance_volume: return False
        if cmp_ask == 0: return self.allow_zero_side_imbalance
        return bid >= cmp_ask * self.imbalance_ratio

    def _compute_imbalances(self, bar: FootprintBar):
        ladder = bar.full_ladder(self.tick_size)
        for i, lvl in enumerate(ladder):
            prev_bid = ladder[i - 1].bid_vol if i > 0                    else 0
            next_ask = ladder[i + 1].ask_vol if i < len(ladder) - 1      else 0
            rl = bar.levels.get(lvl.price)
            if rl is None: continue
            rl.buy_imbalance  = self._is_buy_imb( rl.ask_vol, prev_bid)
            rl.sell_imbalance = self._is_sell_imb(rl.bid_vol, next_ask)

    def _mark_stacked_imbalances(self, bar: FootprintBar):
        ladder = bar.full_ladder(self.tick_size)
        buy_run: List[float] = []
        sell_run: List[float] = []

        def flush_buy():
            if len(buy_run) >= self.stacked_len:
                for p in buy_run:
                    if p in bar.levels: bar.levels[p].stacked_buy = True
                bar.stacked_buy_prices.extend(buy_run)
            buy_run.clear()

        def flush_sell():
            if len(sell_run) >= self.stacked_len:
                for p in sell_run:
                    if p in bar.levels: bar.levels[p].stacked_sell = True
                bar.stacked_sell_prices.extend(sell_run)
            sell_run.clear()

        for lvl in ladder:
            rl       = bar.levels.get(lvl.price)
            is_buy   = bool(rl and rl.buy_imbalance)
            is_sell  = bool(rl and rl.sell_imbalance)
            if is_buy:  buy_run.append(lvl.price)
            else:       flush_buy()
            if is_sell: sell_run.append(lvl.price)
            else:       flush_sell()

        flush_buy()
        flush_sell()

    def _mark_unfinished_auctions(self, bar: FootprintBar):
        hi_lvl = bar.levels.get(bar.high)
        lo_lvl = bar.levels.get(bar.low)
        bar.has_unfinished_high = bool(hi_lvl and hi_lvl.bid_vol > 0 and hi_lvl.ask_vol > 0)
        bar.has_unfinished_low  = bool(lo_lvl and lo_lvl.bid_vol > 0 and lo_lvl.ask_vol > 0)


# ─────────────────────────────────────────────────────────────────────────────
# VOLUME PROFILE
# ─────────────────────────────────────────────────────────────────────────────

def build_volume_profile(bars: List[FootprintBar], tick_size: float):
    vp = defaultdict(int)
    for bar in bars:
        for px, lvl in bar.levels.items():
            vp[px] += lvl.total_vol
    return dict(vp)


def compute_va(vp: dict, pct: float = 0.70):
    if not vp: return None, None, None
    sorted_px = sorted(vp)
    total     = sum(vp.values())
    poc       = max(vp, key=vp.get)
    lo = hi   = sorted_px.index(poc)
    va_vol    = vp[poc]
    target    = total * pct
    while va_vol < target:
        d = vp[sorted_px[lo - 1]] if lo > 0                   else 0
        u = vp[sorted_px[hi + 1]] if hi < len(sorted_px) - 1  else 0
        if d == 0 and u == 0: break
        if u >= d and hi < len(sorted_px) - 1: hi += 1; va_vol += vp[sorted_px[hi]]
        elif lo > 0:                            lo -= 1; va_vol += vp[sorted_px[lo]]
        else:                                   hi += 1; va_vol += vp[sorted_px[hi]]
    return poc, sorted_px[hi], sorted_px[lo]


# ─────────────────────────────────────────────────────────────────────────────
# PYQTGRAPH RENDER ITEMS  (from sample + extensions)
# ─────────────────────────────────────────────────────────────────────────────

class FootprintCellItem(pg.GraphicsObject):
    def __init__(self, bars: List[FootprintBar], tick_size: float, bar_width: float):
        super().__init__()
        self.bars        = bars
        self.tick_size   = tick_size
        self.bar_width   = bar_width
        self.picture     = QtGui.QPicture()
        self._bounds     = self._compute_bounds()
        self._max_delta  = max((abs(lvl.delta) for b in bars for lvl in b.levels.values()), default=1)
        self._build_picture()

    def _compute_bounds(self) -> QtCore.QRectF:
        min_x = min(b.x_idx for b in self.bars) - 1
        max_x = max(b.x_idx for b in self.bars) + 1
        min_y = min(b.low   for b in self.bars) - self.tick_size
        max_y = max(b.high  for b in self.bars) + self.tick_size
        return QtCore.QRectF(min_x, min_y, max_x - min_x, max_y - min_y)

    def _cell_brush(self, lvl: PriceLevel) -> QtGui.QBrush:
        d = lvl.delta
        strength = abs(d) / max(self._max_delta, 1)
        alpha = int(25 + strength * 230)
        if d > 0: return pg.mkBrush(QtGui.QColor(15, 170, 15, alpha))
        if d < 0: return pg.mkBrush(QtGui.QColor(210, 35, 35, alpha))
        return     pg.mkBrush(QtGui.QColor(90, 90, 90, 18))

    def _build_picture(self):
        p = QtGui.QPainter(self.picture)
        ts = self.tick_size
        hw = self.bar_width / 2.0

        for bar in self.bars:
            x = bar.x_idx
            # Dense cells
            p.setPen(pg.mkPen((35, 35, 35), width=0.3))
            for lvl in bar.full_ladder(ts):
                r = QtCore.QRectF(x - hw, lvl.price - ts / 2, self.bar_width, ts)
                p.setBrush(self._cell_brush(lvl))
                p.drawRect(r)

            # OHLC
            p.setPen(pg.mkPen((175, 175, 175), width=0.9))
            p.drawLine(QtCore.QPointF(x, bar.low),  QtCore.QPointF(x, bar.high))
            p.drawLine(QtCore.QPointF(x - hw, bar.open),  QtCore.QPointF(x, bar.open))
            p.drawLine(QtCore.QPointF(x, bar.close), QtCore.QPointF(x + hw, bar.close))

            # Overlay: POC, stacked, imbalance, unfinished
            for lvl in bar.full_ladder(ts):
                r  = QtCore.QRectF(x - hw, lvl.price - ts / 2, self.bar_width, ts)
                rl = bar.levels.get(lvl.price)

                if bar.poc_price == lvl.price:
                    p.setPen(pg.mkPen(C_POC, width=1.4))
                    p.setBrush(QtCore.Qt.NoBrush)
                    p.drawRect(r)

                if rl:
                    if   rl.stacked_buy:   p.setPen(pg.mkPen((0, 255, 120), width=1.6)); p.drawRect(r)
                    elif rl.stacked_sell:  p.setPen(pg.mkPen((255, 80,  80), width=1.6)); p.drawRect(r)
                    elif rl.buy_imbalance: p.setPen(pg.mkPen((0, 210,  90), width=1.0)); p.drawRect(r)
                    elif rl.sell_imbalance:p.setPen(pg.mkPen((255,120,120), width=1.0)); p.drawRect(r)

            if bar.has_unfinished_high:
                p.setPen(pg.mkPen((255, 255, 255), width=2.0))
                p.drawLine(QtCore.QPointF(x - hw, bar.high), QtCore.QPointF(x + hw, bar.high))
            if bar.has_unfinished_low:
                p.setPen(pg.mkPen((255, 255, 255), width=2.0))
                p.drawLine(QtCore.QPointF(x - hw, bar.low),  QtCore.QPointF(x + hw, bar.low))

        p.end()

    def paint(self, painter, option, widget): painter.drawPicture(0, 0, self.picture)
    def boundingRect(self):                   return self._bounds


class FootprintTextItem(pg.GraphicsObject):
    """Bid × Ask labels rendered only when zoomed in (LOD gating).
    Uses screen-coordinate text to avoid PyQtGraph Y-inversion making text upside-down."""
    def __init__(self, bars: List[FootprintBar], tick_size: float, bar_width: float,
                 lod: float = TEXT_LOD_THRESHOLD):
        super().__init__()
        self.bars      = bars
        self.tick_size = tick_size
        self.bar_width = bar_width
        self.lod       = lod
        self._bar_map  = {b.x_idx: b for b in bars}
        self._bounds   = FootprintCellItem(bars, tick_size, bar_width)._compute_bounds()

    def paint(self, painter, option, widget):
        lod = option.levelOfDetailFromTransform(painter.worldTransform())
        if lod < self.lod:
            return
        t = painter.worldTransform()
        cell_h_px = abs(t.m22()) * self.tick_size
        cell_w_px = abs(t.m11()) * self.bar_width
        if cell_h_px < 7:
            return
        font_sz = max(6, min(int(cell_h_px * 0.48), 9))
        rect  = option.exposedRect
        y_lo  = min(rect.top(), rect.bottom()) - self.tick_size
        y_hi  = max(rect.top(), rect.bottom()) + self.tick_size
        x_lo  = rect.left()
        x_hi  = rect.right()
        for x in range(math.floor(x_lo) - 1, math.ceil(x_hi) + 2):
            bar = self._bar_map.get(x)
            if bar is None:
                continue
            for lvl in bar.full_ladder(self.tick_size):
                if not (y_lo <= lvl.price <= y_hi):
                    continue
                ctr = t.map(QtCore.QPointF(x, lvl.price))
                painter.save()
                painter.resetTransform()
                painter.setFont(QtGui.QFont("Consolas", font_sz))
                painter.setPen(pg.mkPen(C_TEXT))
                r = QtCore.QRectF(
                    ctr.x() - cell_w_px / 2,
                    ctr.y() - cell_h_px / 2,
                    cell_w_px, cell_h_px,
                )
                painter.drawText(r, QtCore.Qt.AlignCenter,
                                 f"{lvl.bid_vol}x{lvl.ask_vol}")
                painter.restore()

    def boundingRect(self): return self._bounds


class FootprintMetaItem(pg.GraphicsObject):
    """Δ and Volume totals above each bar (visible when moderately zoomed).
    Uses screen-coordinate text to avoid Y-inversion rendering issues."""
    def __init__(self, bars: List[FootprintBar], tick_size: float):
        super().__init__()
        self.bars      = bars
        self.tick_size = tick_size
        self._bar_map  = {b.x_idx: b for b in bars}
        min_x = min(b.x_idx for b in bars) - 1
        max_x = max(b.x_idx for b in bars) + 1
        min_y = min(b.low  for b in bars) - tick_size
        max_y = max(b.high for b in bars) + tick_size * 7
        self._bounds = QtCore.QRectF(min_x, min_y, max_x - min_x, max_y - min_y)

    def paint(self, painter, option, widget):
        if option.levelOfDetailFromTransform(painter.worldTransform()) < 4.0:
            return
        t    = painter.worldTransform()
        rect = option.exposedRect
        x_lo = rect.left()
        x_hi = rect.right()
        for x in range(math.floor(x_lo) - 1, math.ceil(x_hi) + 2):
            bar = self._bar_map.get(x)
            if bar is None:
                continue
            # Map the position just above bar high to screen coordinates
            top_pt = t.map(QtCore.QPointF(x, bar.high + self.tick_size * 0.5))
            dcol = (0, 220, 0) if bar.total_delta > 0 else (255, 80, 80) if bar.total_delta < 0 else (180, 180, 180)
            painter.save()
            painter.resetTransform()
            painter.setFont(QtGui.QFont("Arial", 7))
            painter.setPen(pg.mkPen(dcol))
            painter.drawText(QtCore.QRectF(top_pt.x() - 32, top_pt.y() - 28, 64, 14),
                             QtCore.Qt.AlignCenter, f"D{bar.total_delta:+d}")
            painter.setPen(pg.mkPen((185, 185, 185)))
            painter.drawText(QtCore.QRectF(top_pt.x() - 32, top_pt.y() - 14, 64, 14),
                             QtCore.Qt.AlignCenter, f"V{bar.total_vol:,}")
            painter.restore()

    def boundingRect(self): return self._bounds


# ─────────────────────────────────────────────────────────────────────────────
# SIGNAL OVERLAY ITEM
# ─────────────────────────────────────────────────────────────────────────────

def add_signal_scatter(plot, signals: pd.DataFrame, bars: List[FootprintBar], bar_seconds: int):
    """Add scatter markers for accepted/rejected signals and trade outcomes."""
    bar_map: Dict[pd.Timestamp, int] = {
        b.start_time: b.x_idx for b in bars
    }

    xs_acc_l, ys_acc_l = [], []
    xs_acc_s, ys_acc_s = [], []
    xs_rej,   ys_rej   = [], []
    xs_win,   ys_win   = [], []
    xs_loss,  ys_loss  = [], []

    for _, row in signals.iterrows():
        bt = row['time'].floor(f'{bar_seconds}s')
        xi = bar_map.get(bt)
        if xi is None: continue
        e = row['entry']
        if pd.isna(e): continue

        tag = row['Tag']
        if tag == 'SIGNAL_ACCEPTED':
            if row['dir'] == 'Long': xs_acc_l.append(xi); ys_acc_l.append(e - 0.5)
            else:                    xs_acc_s.append(xi); ys_acc_s.append(e + 0.5)
        elif tag == 'SIGNAL_REJECTED':
            xs_rej.append(xi); ys_rej.append(e)
        elif tag == 'TRADE_WIN':
            xs_win.append(xi); ys_win.append(e)
        elif tag == 'TRADE_LOSS':
            xs_loss.append(xi); ys_loss.append(e)

    def scatter(x, y, symbol, color, size):
        if x:
            s = pg.ScatterPlotItem(x=x, y=y, symbol=symbol,
                                   size=size, pen=pg.mkPen('w', width=0.5),
                                   brush=pg.mkBrush(color))
            plot.addItem(s)

    scatter(xs_acc_l, ys_acc_l, 't1', C_ACCEPT, 14)  # ▲ long
    scatter(xs_acc_s, ys_acc_s, 't',  C_ACCEPT, 14)  # ▼ short
    scatter(xs_rej,   ys_rej,   'x',  C_REJECT, 10)  # × rejected
    scatter(xs_win,   ys_win,   'd',  C_WIN,    11)  # ◆ win
    scatter(xs_loss,  ys_loss,  'd',  C_LOSS,   11)  # ◆ loss


# ─────────────────────────────────────────────────────────────────────────────
# VOLUME PROFILE ITEM
# ─────────────────────────────────────────────────────────────────────────────

class VolumeProfileItem(pg.GraphicsObject):
    """Horizontal volume histogram with POC / VAH / VAL markers."""
    def __init__(self, vp: dict, poc, vah, val, tick_size: float, y_min: float, y_max: float):
        super().__init__()
        self.vp        = vp
        self.poc       = poc
        self.vah       = vah
        self.val       = val
        self.tick_size = tick_size
        self.y_min     = y_min
        self.y_max     = y_max
        self.picture   = QtGui.QPicture()
        self._bounds   = QtCore.QRectF(0, y_min, 1.3, y_max - y_min)
        self._build()

    def _build(self):
        p = QtGui.QPainter(self.picture)
        if not self.vp:
            p.end(); return

        max_vol = max(self.vp.values())
        ts = self.tick_size

        for px, vol in sorted(self.vp.items()):
            if not (self.y_min <= px <= self.y_max): continue
            frac = vol / max_vol
            if px == self.poc:
                brush = pg.mkBrush(QtGui.QColor(*C_POC))
            elif self.val and self.vah and self.val <= px <= self.vah:
                brush = pg.mkBrush(QtGui.QColor(99, 102, 241, 160))
            else:
                brush = pg.mkBrush(QtGui.QColor(55, 65, 81, 200))

            r = QtCore.QRectF(0, px - ts * 0.38, frac, ts * 0.75)
            p.setPen(QtCore.Qt.NoPen)
            p.setBrush(brush)
            p.drawRect(r)

        # POC / VAH / VAL lines
        if self.poc:
            p.setPen(pg.mkPen(C_POC, width=1.2, style=QtCore.Qt.DashLine))
            p.drawLine(QtCore.QPointF(0, self.poc), QtCore.QPointF(1.3, self.poc))
        for level in [self.vah, self.val]:
            if level:
                p.setPen(pg.mkPen((99, 102, 241), width=0.8, style=QtCore.Qt.DotLine))
                p.drawLine(QtCore.QPointF(0, level), QtCore.QPointF(1.3, level))

        p.end()

    def paint(self, painter, option, widget): painter.drawPicture(0, 0, self.picture)
    def boundingRect(self): return self._bounds


# ─────────────────────────────────────────────────────────────────────────────
# APP
# ─────────────────────────────────────────────────────────────────────────────

def make_time_ticks(bars: List[FootprintBar]) -> List[Tuple[int, str]]:
    stride = max(1, len(bars) // 14)
    return [(b.x_idx, b.start_time.strftime('%H:%M'))
            for i, b in enumerate(bars) if i % stride == 0]


def run_chart(ticks_path: str, log_path: Optional[str], bar_seconds: int,
              tick_size: float, raw_format: bool, session_start: str,
              first_n_bars: Optional[int], max_ticks: Optional[int] = None,
              date_filter: Optional[str] = None, stream: bool = False):

    # Choose loading strategy
    use_stream = stream or (not date_filter and not raw_format and max_ticks is None)

    print(f"Loading ticks: {ticks_path}")

    if use_stream and not raw_format:
        # Build bars directly from streaming CSV — never loads all rows into RAM
        print(f"  Mode: streaming (full dataset, low memory)")
        signals = None
        if log_path:
            signals = load_signals(log_path)
            if signals is not None:
                print(f"  {len(signals)} signal events loaded from {log_path}")
        print(f"Building {bar_seconds}s footprint bars (streaming)...")
        builder = FootprintBuilder(
            tick_size=tick_size, bar_seconds=bar_seconds,
            imbalance_ratio=IMBALANCE_RATIO, stacked_len=STACKED_IMBALANCE_LEN,
            min_imbalance_volume=MIN_IMBALANCE_VOLUME,
            allow_zero_side_imbalance=ALLOW_ZERO_SIDE_IMBALANCE,
            poc_tie_break=POC_TIE_BREAK,
            session_reset_mode=SESSION_RESET_MODE,
        )
        bars, bar_times = builder.build_streaming(ticks_path, date_filter=date_filter)
        if not bars:
            print("No bars built - check your data."); return
        if first_n_bars:
            bars = bars[:first_n_bars]
        _render_chart(bars, bar_times, signals, tick_size, bar_seconds, first_n_bars)
        return

    elif date_filter and not raw_format:
        df = load_ticks_date_range(ticks_path, date_filter, tick_size)
        df = _finalize_new_ticks(df, tick_size)
    elif raw_format:
        df = load_ticks_raw(ticks_path, session_start, tick_size, max_ticks)
        if date_filter:
            parts = date_filter.split(':')
            d0 = pd.Timestamp(parts[0])
            d1 = pd.Timestamp(parts[-1]) + pd.Timedelta(days=1)
            df = df[(df['Timestamp'] >= d0) & (df['Timestamp'] < d1)].reset_index(drop=True)
    else:
        df = load_ticks_new(ticks_path, tick_size, max_ticks)

    if df.empty:
        print("No ticks after filtering - check --date or --max-ticks."); return

    print(f"  {len(df):,} ticks  "
          f"({df['Timestamp'].iloc[0]:%Y-%m-%d %H:%M} -> {df['Timestamp'].iloc[-1]:%Y-%m-%d %H:%M})")

    signals = None
    if log_path:
        signals = load_signals(log_path)
        if signals is not None:
            print(f"  {len(signals)} signal events loaded from {log_path}")

    print(f"Building {bar_seconds}s footprint bars...")
    builder = FootprintBuilder(
        tick_size=tick_size, bar_seconds=bar_seconds,
        imbalance_ratio=IMBALANCE_RATIO, stacked_len=STACKED_IMBALANCE_LEN,
        min_imbalance_volume=MIN_IMBALANCE_VOLUME,
        allow_zero_side_imbalance=ALLOW_ZERO_SIDE_IMBALANCE,
        poc_tie_break=POC_TIE_BREAK,
        session_reset_mode=SESSION_RESET_MODE,
    )
    bars, bar_times = builder.build(df)
    if not bars:
        print("No bars built - check your data."); return

    if first_n_bars:
        bars = bars[:first_n_bars]

    _render_chart(bars, bar_times, signals, tick_size, bar_seconds, first_n_bars)


def _render_chart(bars: List[FootprintBar], bar_times, signals,
                  tick_size: float, bar_seconds: int, first_n_bars):
    """Build and display the PyQtGraph chart window."""
    print(f"  {len(bars)} bars  |  "
          f"{bars[0].start_time:%Y-%m-%d %H:%M} -> {bars[-1].start_time:%H:%M}")

    # Volume profile + value area
    vp            = build_volume_profile(bars, tick_size)
    poc, vah, val = compute_va(vp, VALUE_AREA_PCT)
    print(f"  POC={poc:.2f}  VAH={vah:.2f}  VAL={val:.2f}")

    y_min = min(b.low  for b in bars) - tick_size * 5
    y_max = max(b.high for b in bars) + tick_size * 8

    # ── PyQtGraph setup ────────────────────────────────────────────────────
    app = QtWidgets.QApplication.instance() or QtWidgets.QApplication(sys.argv)
    pg.setConfigOptions(antialias=False, background=pg.mkColor(*C_BG))

    win = pg.GraphicsLayoutWidget(
        show=True,
        title=f"Footprint Chart  {bar_seconds}s bars  |  "
              f"{bars[0].start_time:%Y-%m-%d %H:%M} -> {bars[-1].start_time:%H:%M}"
    )
    win.resize(1650, 1000)

    # ── Footprint panel ────────────────────────────────────────────────────
    fp_plot = win.addPlot(row=0, col=0,
                          title=f"Footprint  |  tick={tick_size}  bar={bar_seconds}s  |  {len(bars)} bars")
    fp_plot.showGrid(x=True, y=True, alpha=0.15)
    fp_plot.setLabel('left', 'Price')
    fp_plot.getAxis('bottom').setStyle(showValues=False)

    print("  Building footprint cells (may take a moment for large datasets)...")
    cell_item = FootprintCellItem(bars, tick_size, BAR_WIDTH)
    text_item = FootprintTextItem(bars, tick_size, BAR_WIDTH)
    meta_item = FootprintMetaItem(bars, tick_size)
    fp_plot.addItem(cell_item)
    fp_plot.addItem(text_item)
    fp_plot.addItem(meta_item)

    if poc:
        fp_plot.addItem(pg.InfiniteLine(poc, angle=0,
            pen=pg.mkPen(C_POC, width=1.0, style=QtCore.Qt.DashLine),
            label=f'POC {poc:.2f}', labelOpts={'color': C_POC, 'position': 0.98}))
    if vah:
        fp_plot.addItem(pg.InfiniteLine(vah, angle=0,
            pen=pg.mkPen((99,102,241), width=0.7, style=QtCore.Qt.DotLine),
            label=f'VAH {vah:.2f}', labelOpts={'color': (99,102,241), 'position': 0.95}))
    if val:
        fp_plot.addItem(pg.InfiniteLine(val, angle=0,
            pen=pg.mkPen((99,102,241), width=0.7, style=QtCore.Qt.DotLine),
            label=f'VAL {val:.2f}', labelOpts={'color': (99,102,241), 'position': 0.95}))

    if signals is not None:
        add_signal_scatter(fp_plot, signals, bars, bar_seconds)

    # ── Volume panel ───────────────────────────────────────────────────────
    vol_plot = win.addPlot(row=1, col=0)
    vol_plot.showGrid(x=True, y=True, alpha=0.15)
    vol_plot.setLabel('left', 'Volume')
    vol_plot.getAxis('bottom').setStyle(showValues=False)
    vol_plot.setXLink(fp_plot)
    vol_plot.setMaximumHeight(140)

    xs     = [b.x_idx for b in bars]
    vols   = [b.total_vol for b in bars]
    colors = [pg.mkBrush(*C_BULL) if b.total_delta >= 0 else pg.mkBrush(*C_BEAR) for b in bars]
    vol_item = pg.BarGraphItem(x=xs, height=vols, width=BAR_WIDTH * 0.85, brushes=colors)
    vol_plot.addItem(vol_item)

    # ── CVD panel ─────────────────────────────────────────────────────────
    cvd_plot = win.addPlot(row=2, col=0)
    cvd_plot.showGrid(x=True, y=True, alpha=0.15)
    cvd_plot.setLabel('left', 'CVD')
    cvd_plot.getAxis('bottom').setStyle(showValues=False)
    cvd_plot.setXLink(fp_plot)
    cvd_plot.setMaximumHeight(120)
    cvd_plot.addItem(pg.InfiniteLine(0, angle=0, pen=pg.mkPen((180,180,180), width=0.5)))

    cvd_arr = np.array([b.cvd for b in bars], dtype=float)
    xs_arr  = np.array(xs, dtype=float)
    zeros   = np.zeros_like(cvd_arr)
    cvd_pos = np.where(cvd_arr > 0, cvd_arr, 0.0)
    cvd_neg = np.where(cvd_arr < 0, cvd_arr, 0.0)

    cvd_line = pg.PlotCurveItem(x=xs_arr, y=cvd_arr, pen=pg.mkPen(*C_CVD, width=1.5))
    cvd_plot.addItem(cvd_line)
    c_pos1 = pg.PlotDataItem(xs_arr, cvd_pos)
    c_zer1 = pg.PlotDataItem(xs_arr, zeros)
    c_neg1 = pg.PlotDataItem(xs_arr, cvd_neg)
    c_zer2 = pg.PlotDataItem(xs_arr, zeros)
    fill_pos = pg.FillBetweenItem(c_pos1, c_zer1, brush=pg.mkBrush(QtGui.QColor(*C_BULL[:3], 55)))
    fill_neg = pg.FillBetweenItem(c_neg1, c_zer2, brush=pg.mkBrush(QtGui.QColor(*C_BEAR[:3], 55)))
    for it in (c_pos1, c_zer1, c_neg1, c_zer2):
        it.setPen(pg.mkPen(None))
        cvd_plot.addItem(it)
    cvd_plot.addItem(fill_pos)
    cvd_plot.addItem(fill_neg)

    # ── Delta panel ────────────────────────────────────────────────────────
    dlt_plot = win.addPlot(row=3, col=0)
    dlt_plot.showGrid(x=True, y=True, alpha=0.15)
    dlt_plot.setLabel('left', 'Delta')
    dlt_plot.setXLink(fp_plot)
    dlt_plot.setMaximumHeight(120)
    dlt_plot.addItem(pg.InfiniteLine(0, angle=0, pen=pg.mkPen((180,180,180), width=0.5)))

    deltas     = [b.total_delta for b in bars]
    dlt_colors = [pg.mkBrush(*C_BULL) if d >= 0 else pg.mkBrush(*C_BEAR) for d in deltas]
    dlt_item   = pg.BarGraphItem(x=xs, height=deltas, width=BAR_WIDTH * 0.85, brushes=dlt_colors)
    dlt_plot.addItem(dlt_item)

    axis = dlt_plot.getAxis('bottom')
    axis.setTicks([make_time_ticks(bars)])

    # ── Volume Profile sidebar ─────────────────────────────────────────────
    vp_plot = win.addPlot(row=0, col=1, rowspan=4)
    vp_plot.showGrid(x=False, y=True, alpha=0.12)
    vp_plot.setLabel('left', '')
    vp_plot.setMaximumWidth(120)
    vp_plot.getAxis('bottom').setStyle(showValues=False)
    vp_plot.setYLink(fp_plot)
    vp_item = VolumeProfileItem(vp, poc, vah, val, tick_size, y_min, y_max)
    vp_plot.addItem(vp_item)
    vp_plot.setTitle('VP', size='8pt')

    # ── Initial view: show last 60 bars ───────────────────────────────────
    show_n = min(60, len(bars))
    x0     = bars[-show_n].x_idx - 1
    x1     = bars[-1].x_idx + 2
    fp_plot.setXRange(x0, x1, padding=0.01)
    fp_plot.setYRange(y_min, y_max, padding=0.01)

    win.ci.layout.setRowStretchFactor(0, 5)
    win.ci.layout.setRowStretchFactor(1, 1)
    win.ci.layout.setRowStretchFactor(2, 1)
    win.ci.layout.setRowStretchFactor(3, 1)

    print("Chart ready. Scroll/zoom to explore. Close window to exit.")
    sys.exit(app.exec())


# ─────────────────────────────────────────────────────────────────────────────
# CLI
# ─────────────────────────────────────────────────────────────────────────────

if __name__ == '__main__':
    ap = argparse.ArgumentParser(description='Interactive footprint / order-flow chart')
    ap.add_argument('--ticks',          required=True)
    ap.add_argument('--log',            default=None,   help='Strategy log CSV (optional signals overlay)')
    ap.add_argument('--raw',            action='store_true', help='Old Ticks_Raw TimeMs format')
    ap.add_argument('--session-start',  default='2026-01-01 18:00:00')
    ap.add_argument('--bar-seconds',    type=int,   default=BAR_SECONDS)
    ap.add_argument('--tick-size',      type=float, default=TICK_SIZE)
    ap.add_argument('--first-n-bars',   type=int,   default=None,
                    help='Use only first N bars (useful for huge files)')
    ap.add_argument('--max-ticks',      type=int,   default=500_000,
                    help='Max rows to read from tick CSV (default 500000). Use 0 for all.')
    ap.add_argument('--date',           default=None,
                    help='Filter to date or range: "2026-01-04" or "2026-01-04:2026-01-05"')
    ap.add_argument('--stream',         action='store_true',
                    help='Stream entire file without loading all rows (for full dataset view)')
    args = ap.parse_args()

    run_chart(
        ticks_path    = args.ticks,
        log_path      = args.log,
        bar_seconds   = args.bar_seconds,
        tick_size     = args.tick_size,
        raw_format    = args.raw,
        session_start = args.session_start,
        first_n_bars  = args.first_n_bars,
        max_ticks     = args.max_ticks if args.max_ticks > 0 else None,
        date_filter   = args.date,
        stream        = args.stream,
    )
