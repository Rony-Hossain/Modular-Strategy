# ML Pipeline — Stage 1 Refactor

## What this delivers

Four files that replace their counterparts in `app/`:

- **`ml_models.py`** — enhanced models + data-driven phase registry with `parallel_group` and `requires_confidence_gate` fields.
- **`ml_activities.py`** — bug fixes + new activities (`list_files`, `list_scripts`, `audit_phase_confidence`, `build_ml_proposal`, `write_proposal_to_disk`).
- **`ml_workflow.py`** — priority-queue scheduler with parallel dispatch, micro-phase insertion, confidence gating, and end-to-end proposal generation.
- **`main_worker.py`** — registers the new activities so Temporal can execute them.

Plus a test file (`test_scheduling.py`) that verifies the scheduling logic offline — no Temporal server needed.

## Bugs fixed in Stage 1

1. **`list_artifacts` vs `list_scripts`.** The old workflow called `list_artifacts` against `Analysis/scripts/` expecting to see `.py` files, but `list_artifacts` only returned `.parquet/.csv/.md`. Result: `skip_existing=True` was a silent no-op. Now there's a proper `list_scripts` that returns `.py` files, and a generic `list_files(dir, extensions)` for future use.

2. **Hardcoded `D:/Ninjatrader-Modular-Startegy/` path.** Was baked into the Claude planning prompt. Removed — `repo_root` now flows through `MLPhaseContext` and gets templated into the Gemini prompt from there.

3. **Script review truncated at 8k chars.** Long analysis scripts have their save-logic and main block at the bottom; the review was cutting them. Raised to 20k with a head-and-tail strategy that preserves both ends of the script.

4. **`accumulated_insights` hard-truncated.** Replaced with a `_summarize_insights` helper that always keeps lines tagged `[ANOMALY]` or `FOLLOW-UP`, then fills the remaining budget with the most recent findings. No more silent loss of load-bearing discoveries.

## Architectural changes

### Priority-queue scheduler (replaces the static for-loop)

Pending phases live in an insertion-ordered `Dict[str, MLPhaseSpec]`. Each iteration:
1. Find phases whose dependencies are all in `completed`
2. Skip any whose script already exists on disk
3. Run the confidence audit if any ready phase has `requires_confidence_gate=True`
4. Pick one group to dispatch (see parallel logic below)
5. Gather results, update completed/failed, insert any proposed micro-phases

There's a safety cap of 50 iterations to prevent runaway loops, and a deadlock detector that logs `no_ready_phases` and breaks cleanly if the dependency graph becomes unresolvable.

### Parallel dispatch within `parallel_group`

Phases sharing a non-`None` `parallel_group` value run concurrently via `asyncio.gather(workflow.execute_activity(...))`. Phases with `parallel_group=None` run sequentially.

The bug-hunt branch (25, 26, 27) is now flagged `parallel_group="bug_hunt"` and dispatches together in a single iteration — the test suite verifies this. Other branches (optimization 13→14, trade mgmt 15→16) stay sequential for now since they have interior dependencies.

### Micro-phase insertion

Claude's `PhaseEvaluation` now has an optional `proposed_micro_phase` field. When set, the workflow:
1. Builds an `MLPhaseSpec` with `is_micro_phase=True` and an `inline_prompt` (no plan doc reference)
2. Inserts it into `pending`
3. Amends the `insert_before` target's dependencies so the target blocks until the micro-phase completes

The planning activity detects `context.is_micro_phase` and uses the inline prompt as the full request rather than as a plan excerpt. Test 2 in `test_scheduling.py` verifies that Phase 12 can trigger `12_a` and block Phase 13 from starting until `12_a` completes.

### Confidence gating

Phases marked `requires_confidence_gate=True` (currently just Phase 17) trigger `audit_phase_confidence` before they run. The audit counts HIGH/MEDIUM/LOW confidence phases and passes only when there are ≥3 HIGH-confidence phases.

**Policy:** the gate does not block the gated phase from running — it flags LOW_CONFIDENCE in the resulting proposal. This matches the role doc's specification ("flag as LOW_CONFIDENCE and recommend additional data collection rather than deploying").

### Structured proposal output

After any confidence-gated phase completes, the workflow calls `build_ml_proposal` which asks Claude to synthesize all phase results into a typed `MLProposal` matching the schema in the role doc. Output is written to `Analysis/artifacts/config_proposal.json`. Every `weight_change`, `veto_change`, and `threshold_change` includes a `basis` field tracing back to the specific phase and finding.

## Test results

```
TEST 1: Full pipeline dry-run  — 9 iterations, 11 phases, bug_hunt parallel ✓
TEST 2: Micro-phase insertion  — 12 → 12_a → 13 order enforced ✓
TEST 3: Low-confidence gate    — flagged, not blocked ✓
TEST 4: Deadlock detection     — empty ready list, clean break ✓
```

## How to install

Drop these four files into `app/` overwriting the current versions:

```
app/ml_models.py       ← replace
app/ml_activities.py   ← replace
app/ml_workflow.py     ← replace
app/main_worker.py     ← replace
```

`run_ml_pipeline.py`, `config.py`, `claude_runner.py`, `gemini_runner.py` are unchanged.

One new payload option when triggering the workflow:
```python
payload = {
    "repo_root": repo_root,
    "plan_dir": plan_dir,
    "phase_ids": args.phases,
    "skip_existing": not args.force,
    # NEW — optional:
    "proposal_version": "v1",
    "proposal_output_path": "Analysis/artifacts/config_proposal.json",
}
```

## What's NOT in Stage 1 (queued for Stage 2)

These came up in the architectural review but are better done after Stage 1 is deployed and working:

- **Minimum-n enforcement inside the review prompt.** The planning prompt now carries `min_sample_size` into the Gemini request, but the review prompt's checklist ("respects the minimum sample size requirement") relies on Claude spotting it. Stage 2 should add a static linter — grep the generated script for `n_min = 30` or equivalent threshold logic and hard-reject if missing.

- **Schema drift detection.** The pipeline loads `schema.md` at startup but doesn't cross-check against actual log content. A new activity should compare the logged Tag types against the schema's declared set and fail loud on new Tag types (remember the seven Tags that appeared last time).

- **Gemini model name verification.** `gemini_runner.py` hard-codes `"gemini-3-flash-preview"` which may be stale. Stage 2 should move model names to `Settings` and add a model health-check similar to `claude_health_check`.

- **Heartbeat safety.** The current heartbeat loop in `ClaudeRunner._heartbeat_loop` doesn't catch exceptions from `heartbeat()` itself. Low-probability issue, worth a `try/except` wrapper.

- **True specialized Gemini workers.** The role doc references `gemini-script-writer`, `gemini-stats-runner`, `gemini-bug-hunter` as distinct workers. Today they share the same runner. Stage 2 could add per-role system prompts or different models per activity.

- **Linux/macOS support for `ClaudeRunner`.** Currently hardcodes `cmd.exe /c`. Stage 2 should detect platform and use a shell-appropriate invocation.

## Recommended validation before production use

1. Run the existing pipeline once with `--phases 25 26 27 --force` and verify the three bug-hunt scripts actually run in parallel (you should see them all starting close in time, not sequentially). Temporal's UI shows activity start timestamps.

2. Force a low-confidence scenario: manually set `confidence_level=LOW` in Claude's evaluation prompts for a test run, and verify Phase 17 still runs and produces a proposal marked LOW_CONFIDENCE.

3. Manually craft a `proposed_micro_phase` response in Phase 12's evaluation and verify that `12_a` gets inserted and Phase 13 waits for it.
