# Phase 1.1 — Long-Side Layer C Order-Flow Vetos

## File
`ModularStrategy/ConfluenceEngine.cs`

## Insertion Point
After line 172 (`if (!isLong && snap.GetFlag(SnapKeys.NonConfShort)) isVetoed = true;`), before line 174 (`layerC = Math.Min(layerC, 30);`).

## Code

```csharp
const double CUMDELTA_EXHAUSTED = 2500.0;
const double WEAK_STACK_COUNT   = 3.0;

// Rule 1 — opposing divergence
if (isLong  && snap.GetFlag(SnapKeys.BearDivergence)) { isVetoed = true; }
if (!isLong && snap.GetFlag(SnapKeys.BullDivergence)) { isVetoed = true; }

// Rule 2 — opposing stacked-imbalance zone at price
if (isLong  && snap.GetFlag(SnapKeys.ImbalZoneAtBear)) { isVetoed = true; }
if (!isLong && snap.GetFlag(SnapKeys.ImbalZoneAtBull)) { isVetoed = true; }

// Rule 3 — exhausted CumDelta without fresh same-side stack support
double cd       = snap.Get(SnapKeys.CumDelta);
double sbullCnt = snap.Get(SnapKeys.StackedImbalanceBull);
double sbearCnt = snap.Get(SnapKeys.StackedImbalanceBear);

if (isLong  && cd >  CUMDELTA_EXHAUSTED && sbullCnt < WEAK_STACK_COUNT) { isVetoed = true; }
if (!isLong && cd < -CUMDELTA_EXHAUSTED && sbearCnt < WEAK_STACK_COUNT) { isVetoed = true; }
```

## Diagnostic Tags
In the Layer C reason string builder (search for `sb.Append(" C:")` or the Layer C reasons block), append when rule fires:

```csharp
if (isLong) {
    if (snap.GetFlag(SnapKeys.BearDivergence))  sb.Append("vBDIV");
    if (snap.GetFlag(SnapKeys.ImbalZoneAtBear)) sb.Append("vZB");
    if (cd > CUMDELTA_EXHAUSTED && sbullCnt < WEAK_STACK_COUNT) sb.Append("vEXHL");
} else {
    if (snap.GetFlag(SnapKeys.BullDivergence))  sb.Append("vBULLDIV");
    if (snap.GetFlag(SnapKeys.ImbalZoneAtBull)) sb.Append("vZU");
    if (cd < -CUMDELTA_EXHAUSTED && sbearCnt < WEAK_STACK_COUNT) sb.Append("vEXHS");
}
```

## Do Not
- Touch any other file.
- Add new SnapKeys (all used here already exist).
- Change Layer A/B/D logic.
- Introduce CvdAccel (Phase 1.2).

## Validation Checklist
- [ ] Project builds, no warnings.
- [ ] Run 6-week backtest, compare to `baselines/phase0_2026-04-14/`.
- [ ] Long PnL: −$3,556 → ≥ −$500.
- [ ] Short PnL unchanged.
- [ ] `filter_autopsy.csv` shows `vBDIV` / `vZB` / `vEXHL` tags on dropped longs.

## Must-Be-Vetoed Sample (7 baseline losers)

| Timestamp    | BERDIV | CD   | SBULL | SBEAR | PnL       | Expected Rule |
|--------------|--------|------|-------|-------|-----------|---------------|
| 03-11 10:15  | 0      | 1048 | 2     | 4     | −$1655.76 | 2 |
| 03-16 10:55  | 1      | −543 | 4     | 2     | −$155.76  | 1 |
| 03-16 15:15  | 0      | 1971 | 2     | 3     | −$1060.76 | 2 |
| 03-23 11:50  | 0      | 4590 | 2     | 1     | −$2440.76 | 3 |
| 04-06 10:15  | 0      | 4264 | 2     | 2     | −$1035.76 | 3 |
| 04-06 10:45  | 0      | 4336 | 3     | 3     | −$90.76   | 2 or 3 |
| 04-06 12:50  | 0      | 4555 | 2     | 2     | −$1210.76 | 3 |
