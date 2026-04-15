# Phase 2.4 — Per-Level Ring Buffer (LevelHistoryTracker)

Stateful per-price-level history across the last N primary bars. Feeds Phase 2.5 (Iceberg) and any future multi-bar level detector. This spec lands the infrastructure only — no detector consumes it yet.

## Files
- **New**: `ModularStrategy/LevelHistoryTracker.cs`
- **Modified**: `ModularStrategy/FootprintCore.cs` (owns one tracker instance; feeds it each bar)

## New Type

```csharp
public readonly struct LevelStat
{
    public double Price    { get; }
    public double AskVol   { get; }
    public double BidVol   { get; }
    public double TotalVol { get; }  // Ask + Bid
    public double Delta    { get; }  // Ask - Bid

    public LevelStat(double price, double askVol, double bidVol);
}

public sealed class LevelHistoryTracker
{
    public LevelHistoryTracker(int capacityBars, int maxLevelsPerBar);
    // capacityBars    > 0 — number of primary bars retained
    // maxLevelsPerBar > 0 — per-bar level array cap (pre-allocated)

    /// Start ingesting a new primary bar. Must be followed by AppendLevel* calls,
    /// then EndBar(). BeginBar overwrites the oldest slot in the ring.
    public void BeginBar(DateTime primaryBarEndTime, double tickSize);

    /// Append one price level for the current bar in-progress. No-op after
    /// maxLevelsPerBar is reached.
    public void AppendLevel(double price, double askVol, double bidVol);

    /// Close the current bar. Only after EndBar is the bar visible to queries.
    public void EndBar();

    /// Look up a price across the last `lookbackBars` CLOSED bars.
    /// Matches within +/- 0.5 * storedTickSize. Writes up to `outStats.Length`
    /// matches (most-recent-first) into `outStats` and returns the count written.
    public int QueryPrice(double price, int lookbackBars, Span<LevelStat> outStats);

    public int Count       { get; }  // closed bars currently stored (<= capacityBars)
    public int Capacity    => _capacityBars;
}
```

All buffers are pre-allocated in the ctor. `BeginBar`/`AppendLevel`/`EndBar`/`QueryPrice` are zero-allocation in steady state.

## Wiring in `FootprintCore`

1. Add `FootprintCoreConfig` fields:
   ```csharp
   public int LevelHistoryCapacityBars { get; }   // default 5
   public int LevelHistoryMaxLevels    { get; }   // default 128
   ```
   Validate `> 0` in ctor. Update `FootprintCoreConfig.Default`.

2. Add `private readonly LevelHistoryTracker _levelHistory;` on `FootprintCore`. Construct in `Initialize(...)` from config.

3. In `TryComputeCurrentBar`, **only on the valid primary-assembly path** (the same path that reaches `FinalizeResult`), after `FinalizeResult` returns and **before** returning to the caller:
   ```csharp
   _levelHistory.BeginBar(result.PrimaryBarEndTime, result.TickSize);
   for (int i = 0; i < bar.LevelCount; i++)
       _levelHistory.AppendLevel(bar.Levels[i].Price, bar.Levels[i].AskVol, bar.Levels[i].BidVol);
   _levelHistory.EndBar();
   ```
   (Actual field names come from the `FootprintBar` struct — this spec does not constrain them; use what exists.)

4. Do **not** ingest on the invalid / zero paths (`BuildZeroResult` or any `false`-returning branch). Ring buffer only carries valid bars.

## Invariants
- Zero allocation in the hot path (`BeginBar`/`AppendLevel`/`EndBar`/`QueryPrice`).
- Tracker is a private field of `FootprintCore`. No public getter. No external mutation.
- `QueryPrice` tolerance is `0.5 * tickSize` stored at `BeginBar` time.
- `lookbackBars` is clamped to `[1, Count]`.
- `QueryPrice` skips bars that have no level matching `price` (so the returned count can be `< lookbackBars`).
- `FootprintCore` construction with `capacityBars <= 0` or `maxLevelsPerBar <= 0` throws at init.

## Do Not
- Expose the tracker publicly or via `FootprintResult`. Consumers read through detector fields added in 2.5 / 2.6.
- Put this tracker in `FootprintAssembler`. Assembler stays stateless at the primary-aggregate level.
- Touch `ConfluenceEngine`, `FootprintTradeAdvisor`, `HostStrategy`, or any other file.
- Add new fields to `FootprintResult` in this phase.
- Add `SnapKeys` or any logging column — that's 2.6/2.7.

## Validation
- [ ] Build passes, no warnings.
- [ ] Temporary smoke test (inside a `#if DEBUG` block in `FootprintCore.Initialize`, removed before commit): ingest 6 synthetic bars with `capacityBars=5`, confirm `Count == 5`, `QueryPrice(knownPrice, 3, buf)` returns 3, prices match within tolerance.
- [ ] Run 6-week backtest. `Summary.csv` must match `baselines/phase2_3_2026-04-15/Summary.csv` **exactly** — this phase adds state but no downstream reads it.

## Rationale
Iceberg (2.5) needs "same price absorbed >= 2 of last 3 bars" — inherently multi-bar. One shared ring buffer owned by `FootprintCore` keeps cross-bar level semantics in a single place, with zero hot-path allocation. Capacity 5 covers the 3-bar Iceberg window with headroom for a future 5-bar rotation detector without re-tuning.
