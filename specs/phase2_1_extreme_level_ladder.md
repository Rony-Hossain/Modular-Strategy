# Phase 2.1 — Extreme-Level Ladder Fields

## Goal
Expose bar-extreme ladder volumes (at bar High and bar Low) on `FootprintResult` so downstream detectors can read them without re-walking the ladder. Prereq for Exhaustion (2.3) and Unfinished Auction (2.2).

## Scope
Three files, additive only. No behavior changes.

### 1. `ModularStrategy/FootprintCore.cs` — `FootprintResult` struct

Add 6 properties after `MaxCombinedVolPrice` (line 270):

```csharp
public double TopLevelAskVol { get; }
public double TopLevelBidVol { get; }
public double TopLevelTotalVol { get; }
public double BottomLevelAskVol { get; }
public double BottomLevelBidVol { get; }
public double BottomLevelTotalVol { get; }
```

Add 6 constructor params (at end of param list, before closing paren of line 322) and assign in ctor body:

```csharp
double topLevelAskVol,
double topLevelBidVol,
double topLevelTotalVol,
double bottomLevelAskVol,
double bottomLevelBidVol,
double bottomLevelTotalVol
```

```csharp
TopLevelAskVol = topLevelAskVol;
TopLevelBidVol = topLevelBidVol;
TopLevelTotalVol = topLevelTotalVol;
BottomLevelAskVol = bottomLevelAskVol;
BottomLevelBidVol = bottomLevelBidVol;
BottomLevelTotalVol = bottomLevelTotalVol;
```

Update all 4 `new FootprintResult(...)` call sites (lines 365, 638, 690, 909) to pass `0.0` for each new param when zero/invalid, or real values where available.

### 2. `ModularStrategy/FootprintAssembler.cs` — `FootprintBar`

Add 6 mirror fields to the `FootprintBar` struct/class (search for it in the file — same file as `AssemblePriceLadder`).

### 3. `ModularStrategy/FootprintAssembler.cs` — `AssemblePriceLadder` (line 715)

Immediately after the ladder loop (after line 749), **before the method returns**, copy out the extremes:

```csharp
if (levelCount > 0)
{
    bar.BottomLevelAskVol   = _ask[0];
    bar.BottomLevelBidVol   = _bid[0];
    bar.BottomLevelTotalVol = _total[0];

    int topIdx = levelCount - 1;
    bar.TopLevelAskVol   = _ask[topIdx];
    bar.TopLevelBidVol   = _bid[topIdx];
    bar.TopLevelTotalVol = _total[topIdx];
}
```

Then in whatever factory builds `FootprintResult` from `FootprintBar` (search for the line that passes `bar.MaxAskVol` and `bar.MaxAskVolPrice` into the result ctor), append the 6 new values in the order listed in step 1.

### 4. `ModularStrategy/FootprintCore.cs` — `BuildZeroBar` (line 752) and `Zero` static (line 365)

Initialize all 6 new fields to `0.0`.

## Do Not
- Touch `ConfluenceEngine`, `FootprintTradeAdvisor`, `ImbalanceZoneRegistry`, or any other file.
- Add new `SnapKeys` — this phase only lands the data; wiring happens in Phase 2.7.
- Change existing field semantics. `MaxAskVol` etc. stay as-is.

## Validation
- [ ] Project builds, no warnings.
- [ ] Run 6-week backtest — `Summary.csv` numbers must be **identical** to `baselines/phase1_1_2026-04-15/Summary.csv` (no logic change).
- [ ] Add one-time debug print: `Print($"L0 ask={fp.BottomLevelAskVol} bid={fp.BottomLevelBidVol} | LN ask={fp.TopLevelAskVol} bid={fp.TopLevelBidVol}");` on 10 bars, confirm non-zero values and that `BottomLevelBidVol` is usually higher than `BottomLevelAskVol` (sellers hit bids at lows), and inverse at highs. Remove the print before final commit.

## Rollback
Single additive change across 2 files (FootprintCore, FootprintAssembler). Revert with `git diff` if numbers drift.
