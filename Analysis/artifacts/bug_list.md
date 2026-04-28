# Consolidated Bug List & Strategy Audit

### TL;DR
- **[bug-001]** RANK_WEAK Net mismatch (559 cases) - **BLOCKS OPTIMIZATION**
- **[bug-002]** TA_TIGHTEN execution failure (62.6% gap) - **Impact: ~$6,895**
- **[bug-003]** Grade calibration inversion (FAIL) - **Impact: ~$2,831**

### Health scorecard
- Strategy expectancy (3.5mo): **$1,029.50** (794 trades, 50.9% WR)
- Grade calibration: **INVERTED**
- Score predictive power: **WEAK**
- Trade management MFE leak: **$6,895.00**
- Stale zone signals: **15%** (Win rate 35% vs 55%)
- Logging completeness: **~85%** (16k EVAL orphans)

### Bugs requiring backtest re-run after fix
- **bug-003** (Scoring weights/variable mapping)
- **bug-005** (CVD Slope logic)
- **bug-006** (Layer C/Penalty calibration)

### Bugs NOT requiring backtest re-run
- **bug-001** (Logging arithmetic)
- **bug-002** (Action execution gap - if purely state-tracking, though usually changes outcome)
- **bug-004** (Logging coverage)

### Out of scope
- Zone interaction analysis (Requires zone lifecycle logs currently missing).

---

## [bug-001] [LOGGING] RANK_WEAK Net score arithmetic mismatch

**Severity:** CRITICAL
**Found by:** Phase 27 section 5
**Area:** logging

**Evidence:**
559 rows where A+B+C+D-Pen != Net. This blocks all weight optimization work.

**Dollar impact:** Analysis blocker

**Hypothesis:**
The `NetScore` calculation in `ConfluenceEngine.Evaluate` includes bonuses and penalties that are applied to `rawTotal` but are not added to any of the specific Layer A, B, C, or D fields logged in the `RANK_WEAK` row.

**Suggested investigation:**
- **File:** `ModularStrategy/ConfluenceEngine.cs`
- **Method:** `Evaluate(...)`
- **Specific Logic:** Around line 354, `netScore = Math.Max(0, rawTotal - penalty)`. Around line 364, `rawTotal += StrategyConfig.Confluence.BONUS_DEEP_DISCOUNT;`. Since `rawTotal` is not part of the component logs, the components no longer sum to the final `NetScore`.
- **Fix:** Update `ConfluenceResult` struct in `CommonTypes.cs` to include a `Bonus` field, or fold these specific values into `LayerB` (Structure) so the sum `A+B+C+D-Pen` equals `Net`.

**Test after fix:**
Re-run Phase 27 — Net mismatches should be 0.

---

## [bug-002] [TRADE MANAGEMENT] TA_TIGHTEN execution failure (Tighten Gap)

**Severity:** CRITICAL
**Found by:** Phase 26 section 3
**Area:** trade management

**Evidence:**
62.6% of TA_TIGHTEN decisions failed to produce a STOP_MOVE event within 2 bars.

**Dollar impact:** ~$6,895.00 (Total MFE Leak identified in Phase 23)

**Hypothesis:**
The runner leg (T2) calculation in `OrderManager.cs` is hardcoded to ignore the Trade Advisor's tightening multiplier.

**Suggested investigation:**
- **File:** `ModularStrategy/OrderManager.cs`
- **Method:** `ManagePositionCore(...)` (Stage 7: Stop Logic)
- **Specific Logic:** Observe that `newStopT1` uses `advisorTrailFactor`, but the `newStopT2` calculation (used for the runner after T1 is hit) **omits the multiplier**, using raw distance only.
- **Fix:** Apply `advisorTrailFactor` to the `newStopT2` calculation so the TA engine can protect runners during CVD reversals or exhaustion.

**Test after fix:**
Re-run Phase 26 — Tighten gap should be < 5%.

---

## [bug-003] [SCORING] Grade-Profit Inversion (Miscalibrated scoring)

**Severity:** HIGH
**Found by:** Phase 23 section 5
**Area:** scoring

**Evidence:**
Profit Factor order is 1.07 -> 0.67 -> 0.81 -> 1.44 (A+ to C). Lower grades sometimes outperform higher ones.

**Dollar impact:** $2,831.50 (Losing source drag)

**Hypothesis:**
MTFA variable mapping bug swaps the Macro and Execution timeframe biases.

**Suggested investigation:**
- **File:** `ModularStrategy/ConfluenceEngine.cs`
- **Method:** `Evaluate(...)`
- **Specific Lines:** Lines 98–101.
- **Current Code:** `double h4b = snap.Get(SnapKeys.H1EmaBias);` and `double h1b = snap.Get(SnapKeys.H4HrEmaBias);`
- **Fix:** Swap the `SnapKeys` so `h4b` reads `H4HrEmaBias` and `h1b` reads `H1EmaBias`.

**Test after fix:**
Re-run Phase 23 — PF order should be monotone descending.

---

## [bug-004] [LOGGING] Massive EVAL orphan count (16k)

**Severity:** HIGH
**Found by:** Phase 27 section 2
**Area:** logging

**Evidence:**
16,740 EVAL rows with no follow-up. Silent filter stages prevent debugging.

**Hypothesis:**
`ApplyFootprintEntryAdvisor` silently vetoes signals without logging the rejection.

**Suggested investigation:**
- **File:** `ModularStrategy/HostStrategy.cs`
- **Method:** `OnBarUpdate()`
- **Specific Logic:** The `ApplyFootprintEntryAdvisor` loop (around line 550) identifies vetoes and adds them to `_filteredCandidates` for the UI, but it **never calls `_log.SignalRejected(...)`**.
- **Fix:** Add a call to `_log.SignalRejected` inside the `if (ea.IsVetoed)` block to close the loop on the `EVAL` row.

**Test after fix:**
Re-run Phase 27 — EVAL orphans should be < 1% of total.

---

## [bug-005] [TRADE MANAGEMENT] CVD Slope reversal ignored

**Severity:** HIGH
**Found by:** Phase 26 section 5
**Area:** trade management

**Evidence:**
Among 452 trades where CVD reversed against the position, the system reacted in 0% of cases.

**Hypothesis:**
The CVD acceleration trigger is gated by a "Target 1 not yet hit" check.

**Suggested investigation:**
- **File:** `ModularStrategy/OrderManager.cs`
- **Method:** `ManagePositionCore(...)` (Stage 4: Break-Even)
- **Specific Logic:** The `SnapKeys.CvdSlope` check is nested inside the `if (!_t1Hit)` block.
- **Fix:** Move the CVD Slope check out of the `!_t1Hit` block so the system can tighten or exit runners when the tape reverses.

**Test after fix:**
Re-run Phase 26 — Flip reaction rate should be > 20%.

---

## [bug-006] [SCORING] Negative correlation in Layer C and Penalty

**Severity:** MEDIUM
**Found by:** Phase 24
**Area:** scoring

**Evidence:**
Layer C rho_pnl: -0.0002, Penalty rho_pnl: -0.027. These layers are adding noise or counter-productive bias.

**Dollar impact:** $200.00

**Hypothesis:**
Order flow (Layer C) or specific penalties are miscalibrated for the current market regime.

**Suggested investigation:**
MathLibrary/MathOrderFlow.cs and scoring config.

**Test after fix:**
Re-run Phase 24; All layers should have positive correlation to PnL.

---
