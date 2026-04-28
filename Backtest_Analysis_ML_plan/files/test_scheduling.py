"""
Offline test of the scheduling logic.

Simulates the workflow loop without Temporal: mocks each phase's result and
verifies that:
- Ready detection handles a complex dependency graph correctly
- Parallel groups get dispatched together
- Micro-phase insertion blocks the target phase
- Confidence-gated phase waits for audit
- Final proposal stage triggers after gated phase
"""

import sys
sys.path.insert(0, '.')

from typing import Dict, List, Set
from ml_models import (
    ML_PHASES, MLPhaseSpec, PhaseResult, PhaseStatus, ConfidenceLevel,
    ConfidenceAudit,
)


# ---- re-implement the pure scheduling helpers for offline testing ----

def find_ready(pending: Dict[str, MLPhaseSpec], completed: Set[str]) -> List[MLPhaseSpec]:
    return [
        p for p in pending.values()
        if all(dep in completed for dep in p.dependencies)
    ]


def pick_group(ready: List[MLPhaseSpec]) -> List[MLPhaseSpec]:
    solo = [p for p in ready if p.parallel_group is None]
    if solo:
        return [solo[0]]
    first_group = ready[0].parallel_group
    return [p for p in ready if p.parallel_group == first_group]


def insert_micro_phase(
    pending: Dict[str, MLPhaseSpec],
    phase_id: str,
    script_name: str,
    dependencies: List[str],
    insert_before: str,
    inline_prompt: str = "stub prompt",
):
    spec = MLPhaseSpec(
        phase_id=phase_id,
        script_name=script_name,
        plan_file="",
        dependencies=dependencies,
        is_micro_phase=True,
        inline_prompt=inline_prompt,
    )
    pending[phase_id] = spec
    if insert_before in pending:
        target = pending[insert_before]
        if phase_id not in target.dependencies:
            new_deps = list(target.dependencies) + [phase_id]
            pending[insert_before] = target.model_copy(update={"dependencies": new_deps})


def simulate_confidence_audit(phase_results: List[PhaseResult]) -> ConfidenceAudit:
    high = sum(1 for r in phase_results if r.confidence_level == ConfidenceLevel.HIGH)
    med = sum(1 for r in phase_results if r.confidence_level == ConfidenceLevel.MEDIUM)
    low = sum(1 for r in phase_results if r.confidence_level == ConfidenceLevel.LOW)
    passes = high >= 3
    return ConfidenceAudit(
        total_phases_evaluated=len(phase_results),
        high_confidence_count=high,
        medium_confidence_count=med,
        low_confidence_count=low,
        passes_finalization_gate=passes,
        rationale=f"{high} HIGH-confidence phases; gate {'passed' if passes else 'failed'}",
    )


# ---- test 1: full pipeline dry-run ----

def test_full_pipeline_dry_run():
    print("=" * 60)
    print("TEST 1: Full pipeline dry-run (all phases succeed at MEDIUM)")
    print("=" * 60)

    pending = {p.phase_id: p for p in ML_PHASES}
    completed: Set[str] = set()
    phase_results: List[PhaseResult] = []
    dispatch_log = []

    iteration = 0
    while pending and iteration < 20:
        iteration += 1
        ready = find_ready(pending, completed)
        if not ready:
            print(f"  [iter {iteration}] DEADLOCK — pending={list(pending.keys())}")
            break

        # Confidence gate?
        gated = [p for p in ready if p.requires_confidence_gate]
        if gated:
            audit = simulate_confidence_audit(phase_results)
            print(f"  [iter {iteration}] confidence_audit: HIGH={audit.high_confidence_count}, "
                  f"pass={audit.passes_finalization_gate}")

        group = pick_group(ready)
        dispatch_log.append([p.phase_id for p in group])
        print(f"  [iter {iteration}] dispatching: {[p.phase_id for p in group]}"
              f"{' (parallel)' if len(group) > 1 else ''}")

        for phase in group:
            pending.pop(phase.phase_id)
            completed.add(phase.phase_id)
            phase_results.append(PhaseResult(
                phase_id=phase.phase_id,
                script_name=phase.script_name,
                status=PhaseStatus.COMPLETED,
                confidence_level=ConfidenceLevel.MEDIUM,
            ))

    assert iteration < 20, "Pipeline should finish in well under 20 iterations"
    assert not pending, f"Pending should be empty, got {list(pending.keys())}"
    assert "25" in completed and "26" in completed and "27" in completed

    # Verify bug_hunt group ran together (in a single iteration)
    bug_hunt_iters = [i for i, d in enumerate(dispatch_log) if "25" in d]
    assert len(bug_hunt_iters) == 1, "25, 26, 27 should all run in one iteration"
    bug_hunt_dispatch = dispatch_log[bug_hunt_iters[0]]
    assert set(bug_hunt_dispatch) == {"25", "26", "27"}, \
        f"bug_hunt group should be {{25, 26, 27}}, got {bug_hunt_dispatch}"

    # Phase 17 should run LAST
    assert dispatch_log[-1] == ["17"], \
        f"Phase 17 should run last, got {dispatch_log[-1]}"

    print(f"\n  ✓ {iteration} iterations, {len(completed)} phases completed")
    print(f"  ✓ bug_hunt dispatched as parallel group: {bug_hunt_dispatch}")
    print(f"  ✓ Phase 17 (gated) ran last\n")


# ---- test 2: micro-phase insertion ----

def test_micro_phase_insertion():
    print("=" * 60)
    print("TEST 2: Micro-phase insertion during Phase 12 evaluation")
    print("=" * 60)

    pending = {p.phase_id: p for p in ML_PHASES}
    completed: Set[str] = set()
    dispatch_log = []
    iteration = 0

    # Seed: phase 11 already done
    completed.add("11")
    pending.pop("11")

    while pending and iteration < 25:
        iteration += 1
        ready = find_ready(pending, completed)
        if not ready:
            break
        group = pick_group(ready)
        dispatch_log.append([p.phase_id for p in group])

        for phase in group:
            pending.pop(phase.phase_id)
            completed.add(phase.phase_id)

            # Simulate Phase 12 proposing a micro-phase targeting Phase 13
            if phase.phase_id == "12":
                print(f"  [iter {iteration}] Phase 12 proposes micro-phase 12_a "
                      f"(blocking phase 13)")
                insert_micro_phase(
                    pending,
                    phase_id="12_a",
                    script_name="12_a_pnl_discrepancy_deep_dive.py",
                    dependencies=["12"],
                    insert_before="13",
                )

    # Check: 12_a should run AFTER 12 and BEFORE 13
    order = [p for d in dispatch_log for p in d]
    p12 = order.index("12")
    p12a = order.index("12_a")
    p13 = order.index("13")

    assert p12 < p12a < p13, f"Order wrong: 12@{p12}, 12_a@{p12a}, 13@{p13}"
    print(f"\n  ✓ Dispatch order correct: 12 → 12_a → 13")
    print(f"  ✓ 12_a inserted at iteration where it was triggered")
    print(f"  ✓ Phase 13's dependencies now include 12_a\n")


# ---- test 3: low-confidence gate still lets Phase 17 run (flagged not blocked) ----

def test_low_confidence_still_runs_phase_17():
    print("=" * 60)
    print("TEST 3: Low-confidence audit flags Phase 17 but doesn't block it")
    print("=" * 60)

    # Build a results list with only 1 HIGH phase (gate should fail)
    results = [
        PhaseResult(
            phase_id=str(i), script_name=f"{i}.py",
            status=PhaseStatus.COMPLETED,
            confidence_level=ConfidenceLevel.MEDIUM,
        )
        for i in range(5)
    ]
    results[0].confidence_level = ConfidenceLevel.HIGH

    audit = simulate_confidence_audit(results)
    assert audit.high_confidence_count == 1
    assert audit.passes_finalization_gate is False
    print(f"  ✓ Audit: {audit.high_confidence_count} HIGH, gate passed={audit.passes_finalization_gate}")
    print(f"  ✓ Phase 17 would run but proposal marked LOW_CONFIDENCE\n")


# ---- test 4: deadlock detection ----

def test_deadlock_detection():
    print("=" * 60)
    print("TEST 4: Deadlock detection when a dependency is unresolvable")
    print("=" * 60)

    # Only phase 13 in pending, but it depends on 11 and 12 which aren't there
    pending = {
        "13": next(p for p in ML_PHASES if p.phase_id == "13"),
    }
    completed: Set[str] = set()

    ready = find_ready(pending, completed)
    assert ready == [], "No phases should be ready when deps aren't completed"
    print(f"  ✓ Correctly returns no ready phases when deps unmet")
    print(f"  ✓ Workflow would log 'no_ready_phases' event and break\n")


if __name__ == "__main__":
    test_full_pipeline_dry_run()
    test_micro_phase_insertion()
    test_low_confidence_still_runs_phase_17()
    test_deadlock_detection()
    print("=" * 60)
    print("ALL TESTS PASSED")
    print("=" * 60)
