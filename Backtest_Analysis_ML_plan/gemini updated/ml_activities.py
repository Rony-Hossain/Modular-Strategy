"""
Activities for the autonomous ML pipeline.

Stage 1 fixes:
- generic list_files(dir, extensions) replaces the bugged list_artifacts-for-scripts
- hardcoded D:/Ninjatrader-Modular-Startegy/ removed; repo_root flows via context
- script review budget raised from 8k to 20k chars
- accumulated_insights kept sharp via head-and-tail summarization
- audit_phase_confidence activity added (confidence gate before Phase 17)
- build_ml_proposal activity added (structured MLProposal output)
- claude_plan_ml_phase handles inline_prompt for micro-phases
- claude_evaluate_ml_output now returns confidence_level + optional micro-phase
"""

import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, List, Optional

from temporalio import activity

from app.config import Settings
from app.roles import get_runner
from app.pipeline_logger import PipelineLogger
from app.ml_models import (
    ConfidenceAudit,
    ConfidenceLevel,
    GeminiScriptRequest,
    MLPhaseContext,
    MLProposal,
    PhaseEvaluation,
    PhaseResult,
    ScriptReviewDecision,
    ScriptRunResult,
)


def _gemini(role: str):
    """DEPRECATED — preserved for legacy callers. Use get_runner() instead.

    Maps the old GEMINI_KEYS roles (COMPILER/FALLBACK/DATA_MINER) onto the
    new script_writer role so existing code keeps working until fully migrated.
    """
    return get_runner("script_writer")


# ---------------------------------------------------------------------------
# File system activities (generalized — fixes the list_artifacts bug)
# ---------------------------------------------------------------------------

@activity.defn
async def list_files(directory: str, extensions: Optional[List[str]] = None) -> List[str]:
    """Generic file lister. If extensions is None, returns all files."""
    d = Path(directory)
    if not d.exists():
        return []
    if extensions:
        ext_set = {e if e.startswith(".") else f".{e}" for e in extensions}
        return sorted(f.name for f in d.iterdir() if f.is_file() and f.suffix in ext_set)
    return sorted(f.name for f in d.iterdir() if f.is_file())


@activity.defn
async def list_artifacts(artifacts_dir: str) -> List[str]:
    """Backward-compatible: parquet, csv, md, json artifacts."""
    d = Path(artifacts_dir)
    if not d.exists():
        return []
    exts = {".parquet", ".csv", ".md", ".json"}
    return sorted(f.name for f in d.iterdir() if f.is_file() and f.suffix in exts)


@activity.defn
async def list_scripts(scripts_dir: str) -> List[str]:
    """Lists .py scripts — what the old list_artifacts call in the workflow
    was trying (and failing) to do."""
    d = Path(scripts_dir)
    if not d.exists():
        return []
    return sorted(f.name for f in d.iterdir() if f.is_file() and f.suffix == ".py")


@activity.defn
async def read_plan_document(plan_path: str) -> str:
    p = Path(plan_path)
    return p.read_text(encoding="utf-8") if p.exists() else ""


@activity.defn
async def read_schema(schema_path: str) -> str:
    p = Path(schema_path)
    if not p.exists():
        return ""
    return "\n".join(p.read_text(encoding="utf-8").splitlines()[:200])


@activity.defn
async def read_strategy_config_snippet(config_path: str) -> str:
    p = Path(config_path)
    if not p.exists():
        return ""
    return "\n".join(p.read_text(encoding="utf-8").splitlines()[:120])


# ---------------------------------------------------------------------------
# Intelligence: artifact content sampling (so Claude sees real data shapes)
# ---------------------------------------------------------------------------

@activity.defn
async def sample_artifact_contents(
    artifacts_dir: str,
    artifact_names: List[str],
    max_per_file: int = 20,
) -> Dict[str, str]:
    """
    Read the first N rows of each artifact so Claude knows exact column names,
    dtypes, and data shapes — not just file names.

    Returns {filename: preview_string}.
    """
    log = PipelineLogger.get()
    d = Path(artifacts_dir)
    previews: Dict[str, str] = {}

    for name in artifact_names[:10]:  # cap at 10 files
        p = d / name
        if not p.exists():
            continue

        try:
            if p.suffix == ".parquet":
                import pyarrow.parquet as pq
                table = pq.read_table(str(p))
                schema_str = str(table.schema)
                n_rows = table.num_rows
                # Show schema + first few rows as string
                df = table.slice(0, min(max_per_file, n_rows)).to_pandas()
                previews[name] = (
                    f"Schema: {schema_str}\n"
                    f"Rows: {n_rows}\n"
                    f"Head:\n{df.to_string(max_rows=max_per_file, max_cols=15)}"
                )
            elif p.suffix == ".csv":
                lines = p.read_text(encoding="utf-8").splitlines()
                previews[name] = (
                    f"Rows: {len(lines) - 1}\n"
                    f"Head:\n" + "\n".join(lines[:max_per_file + 1])
                )
            elif p.suffix in (".md", ".json"):
                text = p.read_text(encoding="utf-8")
                previews[name] = text[:2000]
        except Exception as e:
            previews[name] = f"ERROR reading: {e}"
            log.warning("artifacts", f"Failed to sample {name}: {e}")

    log.event("ARTIFACT_SAMPLE", count=len(previews),
              files=list(previews.keys()))
    return previews


# ---------------------------------------------------------------------------
# Intelligence: sample dependent scripts (so Gemini sees output shapes)
# ---------------------------------------------------------------------------

@activity.defn
async def sample_dependent_scripts(
    scripts_dir: str,
    dependency_script_names: List[str],
    max_lines: int = 40,
) -> Dict[str, str]:
    """
    Read the tail of each dependency script (the part that writes outputs)
    so Gemini knows exact column names and output paths.
    """
    d = Path(scripts_dir)
    snippets: Dict[str, str] = {}

    for name in dependency_script_names[:5]:
        p = d / name
        if not p.exists():
            continue
        lines = p.read_text(encoding="utf-8").splitlines()
        # The output logic is usually in the last N lines
        if len(lines) <= max_lines * 2:
            snippets[name] = "\n".join(lines)
        else:
            # Head (imports + constants) + tail (save logic)
            head = lines[:15]
            tail = lines[-max_lines:]
            snippets[name] = (
                "\n".join(head) +
                f"\n\n# ... ({len(lines) - 15 - max_lines} lines omitted) ...\n\n" +
                "\n".join(tail)
            )

    return snippets


# ---------------------------------------------------------------------------
# Helper: summarize accumulated insights (keep it sharp, not truncated-dumb)
# ---------------------------------------------------------------------------

def _summarize_insights(insights: str, max_chars: int = 4000) -> str:
    """
    Keep accumulated insights useful even after many phases. Strategy:
    - Always keep lines marked [ANOMALY] or FOLLOW-UP (high-signal)
    - Fill remaining budget with most-recent lines
    - Never silently truncate — note what was dropped
    """
    if not insights or len(insights) <= max_chars:
        return insights or ""

    lines = insights.splitlines()
    high_signal = [ln for ln in lines if "ANOMALY" in ln or "FOLLOW-UP" in ln]
    other = [ln for ln in lines if ln not in high_signal]

    kept = list(high_signal)
    budget_remaining = max_chars - sum(len(ln) + 1 for ln in kept)

    # Fill from the tail (most recent) of the "other" list
    for ln in reversed(other):
        if budget_remaining - len(ln) - 1 < 0:
            break
        kept.append(ln)
        budget_remaining -= len(ln) + 1

    dropped = len(lines) - len(kept)
    header = f"# Accumulated insights ({len(kept)} kept, {dropped} older dropped — anomalies always retained)\n"
    return header + "\n".join(kept)


# ---------------------------------------------------------------------------
# CLAUDE — Plan a phase
# ---------------------------------------------------------------------------

@activity.defn
async def claude_plan_ml_phase(context: MLPhaseContext) -> GeminiScriptRequest:
    """
    Claude reads the plan (or inline prompt for micro-phases), thinks through
    the 5-layer analysis protocol, and builds an enriched Gemini prompt.

    Path uses context.repo_root — no more hardcoded D:/ paths.
    """
    log = PipelineLogger.get()
    log.step("PLAN", context.phase_id,
             f"[{get_runner('planner').provider_name}] planning {context.script_name}"
             + (" (RETRY)" if context.retry_error else "")
             + (" (MICRO)" if context.is_micro_phase else ""),
             prompt_snippet=context.plan_prompt[:200])
    runner = get_runner("planner")

    retry_block = ""
    if context.retry_error:
        retry_block = f"""
CRITICAL — Previous script attempt FAILED with this error:
```
{context.retry_error[:2500]}
```
Diagnose the ROOT CAUSE. If it's a data issue, add defensive checks.
If it's a logic error, rethink the methodology — don't just patch.
"""

    prev_block = ""
    if context.previous_phase_summary:
        prev_block = f"\nPrevious phase findings:\n{context.previous_phase_summary}\n"

    insights_block = ""
    if context.accumulated_insights:
        # Already summarized upstream, but re-cap for safety
        trimmed = _summarize_insights(context.accumulated_insights, max_chars=4000)
        insights_block = f"""
ACCUMULATED INSIGHTS FROM PRIOR PHASES (anomalies retained in full):
{trimmed}
"""

    # For micro-phases, plan_prompt IS the full request — no plan doc parsing
    if context.is_micro_phase:
        source_block = f"""
THIS IS A CLAUDE-INITIATED MICRO-PHASE. The full analytical request:

{context.plan_prompt}

Do NOT treat this as a plan document to extract from. The text above
IS the direct request — enrich and specify it, then pass to Gemini.
"""
    else:
        source_block = f"""
PLAN DOCUMENT (starting point — ENHANCE it, don't just relay):
{context.plan_prompt[:8000]}
"""

    prompt = f"""You are the ANALYTICAL BRAIN of an autonomous ML pipeline for a NinjaTrader
strategy backtest. You are NOT a prompt relay — you are a quantitative researcher
thinking deeply about every angle before dispatching Gemini.

Phase {context.phase_id} — writing: {context.script_name}

REPO ROOT (use this verbatim in paths you write into the Gemini prompt):
{context.repo_root}

MINIMUM SAMPLE SIZE for any reported finding: n ≥ {context.min_sample_size}
(findings below this threshold must be flagged as INSUFFICIENT, not reported).

{source_block}

EXISTING ARTIFACTS (parquet/csv/md/json available to read):
{', '.join(context.existing_artifacts) if context.existing_artifacts else 'None yet'}

STRATEGY CONFIG SNIPPET:
{context.strategy_config_snippet[:2000]}

SCHEMA (check for underused columns):
{context.schema_content[:3000]}
{prev_block}
{insights_block}
{retry_block}

WALK THE 5-LAYER ANALYSIS PROTOCOL BEFORE YOU WRITE THE PROMPT:

1. OBJECTIVE — What question does this phase answer? What decision does it inform?
2. GAP ANALYSIS — What did the plan miss? (distributions? regimes? non-stationarity?
   cross-correlations? known pathologies like inverted scores or dead signals?)
3. STATISTICAL RIGOR — Require: confidence intervals, effect sizes, distribution
   tests before parametric methods, bootstrap for n<50, multiple-comparison correction.
4. CROSS-PHASE COHERENCE — What prior insights must shape this analysis?
5. FAILURE MODES — How could this produce a technically correct but wrong answer?
   (empty bins, div-by-zero, overfitting, confounding, data leakage.)

THEN BUILD THE GEMINI PROMPT WITH THESE SECTIONS IN ORDER:
- MISSION (one paragraph)
- INPUT FILES (exact paths rooted at {context.repo_root})
- OUTPUT ARTIFACTS (exact paths under Analysis/artifacts/)
- REQUIRED ANALYSIS (numbered, including specific statistical tests)
- ANALYTICAL DEPTH REQUIREMENTS (CIs, bootstraps, regime splits)
- OUTPUT SECTIONS (script MUST print: [INPUT] [CHECK] [RESULT] [SAVED]
  plus [DISCOVERY] [ANOMALY] [INSIGHT] when relevant)
- PRIOR PHASE CONTEXT (relevant accumulated insights)
- DEFENSIVE REQUIREMENTS (empty DFs, missing cols, zero-division, n<min_sample_size)

Return JSON:
- prompt (the COMPLETE enriched prompt — self-contained, ready for Gemini)
- phase_id (string)
- script_name (string)
"""
    result = await runner.prompt_json(prompt, GeminiScriptRequest)
    log.step_done("PLAN", context.phase_id,
                  f"Prompt built ({len(result.prompt)} chars)",
                  response_snippet=result.prompt[:300])
    return result


# ---------------------------------------------------------------------------
# GEMINI — Write the Python script
# ---------------------------------------------------------------------------

@activity.defn
async def gemini_write_ml_script(
    request: GeminiScriptRequest,
    feedback: Optional[str],
) -> str:
    log = PipelineLogger.get()
    log.step("WRITE", request.phase_id,
             f"Gemini writing {request.script_name}"
             + (" (with feedback)" if feedback else ""),
             prompt_snippet=request.prompt[:200])
    feedback_block = ""
    if feedback:
        feedback_block = f"""
IMPORTANT — Your previous script was rejected:
{feedback}
Fix all issues listed above.
"""

    prompt = f"""{request.prompt}

{feedback_block}

CRITICAL RULES:
- Write ONE complete Python script. No placeholders, no TODOs, no `pass` in live paths.
- Libraries: pandas, numpy, pyarrow, scipy, sklearn, optuna as needed.
- All paths use pathlib.Path.
- Script must be self-contained and runnable.
- Print [INPUT], [PARSE], [CHECK], [RESULT], [SAVED] sections.
- Print [DISCOVERY], [ANOMALY], [INSIGHT] when findings warrant.
- np.random.seed(42) wherever randomness appears.
- Save outputs to Analysis/artifacts/.
- Handle empty DataFrames, missing columns, division by zero defensively.
- Flag findings with n < minimum sample size as INSUFFICIENT; do not report them.

Return the COMPLETE Python script content. No markdown fences, no explanation
outside the script. Just the raw .py file content.
"""
    runner = get_runner("script_writer")
    result = await runner.prompt(prompt)
    log.step_done("WRITE", request.phase_id,
                  f"Script written ({len(result)} chars, {result.count(chr(10))} lines)",
                  response_snippet=result[:300])
    return result


# ---------------------------------------------------------------------------
# CLAUDE — Review the script (full length, not 8k-truncated)
# ---------------------------------------------------------------------------

@activity.defn
async def claude_review_ml_script(
    request: GeminiScriptRequest,
    script_content: str,
) -> ScriptReviewDecision:
    log = PipelineLogger.get()
    log.step("REVIEW", request.phase_id,
             f"[{get_runner('reviewer').provider_name}] reviewing "
             f"{request.script_name} ({len(script_content)} chars)")
    runner = get_runner("reviewer")

    # Stage 1: raise review budget from 8k to 20k. Long analysis scripts
    # have their save-logic and main block at the bottom — don't cut it.
    max_script_chars = 20_000
    if len(script_content) > max_script_chars:
        # Keep head and tail — skip middle
        head = script_content[: max_script_chars // 2]
        tail = script_content[-(max_script_chars // 2):]
        script_view = f"{head}\n\n# ... ({len(script_content) - max_script_chars} chars elided from middle) ...\n\n{tail}"
    else:
        script_view = script_content

    prompt = f"""You are a senior quantitative researcher reviewing a Python analysis script
BEFORE it runs. You catch both bugs AND analytical weaknesses.

Phase {request.phase_id} ({request.script_name})

WHAT IT SHOULD ACCOMPLISH:
{request.prompt[:3500]}

SCRIPT:
```python
{script_view}
```

REVIEW ON TWO AXES:

A. CORRECTNESS (hard reject if any fail):
   1. Reads correct input files from Analysis/artifacts/
   2. Saves outputs to Analysis/artifacts/
   3. No syntax errors, undefined vars, missing imports
   4. No Windows-broken paths
   5. No placeholder/TODO/pass in live code paths
   6. Prints required sections: [INPUT] [CHECK] [RESULT] [SAVED]
   7. Handles empty DataFrames, missing columns, division by zero
   8. Respects the minimum sample size requirement (flags n<threshold findings)

B. ANALYTICAL DEPTH (flag as "DEPTH:" in risk_flags, don't hard-reject):
   1. Computes CIs, not just point estimates
   2. Tests statistical significance (not just descriptives)
   3. Checks distributions before parametric assumptions
   4. Regime / session splits where relevant
   5. Cross-correlations between related variables
   6. Addresses known pathologies from accumulated insights

Reject if: incomplete code, will crash, reads wrong files, has TODOs, ignores min n.
Approve with flags if: code will run correctly and depth is at least minimal.

Return JSON:
- approved (bool)
- verdict (one sentence covering both axes)
- risk_flags (list — depth concerns prefixed with "DEPTH:")
- notes (concrete suggestion for highest-impact improvement)
"""
    result = await runner.prompt_json(prompt, ScriptReviewDecision)
    status = "APPROVED" if result.approved else "REJECTED"
    log.step_done("REVIEW", request.phase_id,
                  f"{status}: {result.verdict}",
                  extra={"approved": result.approved,
                         "risk_flags": result.risk_flags})
    if not result.approved:
        log.warning(request.phase_id,
                    f"Review rejected: {result.notes}")
    return result


# ---------------------------------------------------------------------------
# Save and run the script
# ---------------------------------------------------------------------------

@activity.defn
async def save_and_run_ml_script(
    script_name: str,
    script_content: str,
    repo_root: str,
) -> ScriptRunResult:
    log = PipelineLogger.get()
    log.step("RUN", script_name, f"Saving and executing {script_name}")
    scripts_dir = Path(repo_root) / "Analysis" / "scripts"
    scripts_dir.mkdir(parents=True, exist_ok=True)
    script_path = scripts_dir / script_name

    # Strip markdown fences if Gemini wrapped the code
    content = script_content.strip()
    if content.startswith("```"):
        first_newline = content.index("\n")
        content = content[first_newline + 1:]
    if content.endswith("```"):
        content = content[:-3].rstrip()

    script_path.write_text(content, encoding="utf-8")

    venv_python = Path(repo_root) / "venv" / "Scripts" / "python.exe"
    if not venv_python.exists():
        venv_python = Path(repo_root) / "venv" / "bin" / "python"
    if not venv_python.exists():
        venv_python = "python"

    try:
        result = subprocess.run(
            [str(venv_python), str(script_path)],
            cwd=repo_root,
            capture_output=True,
            text=True,
            timeout=600,
        )

        artifacts_dir = Path(repo_root) / "Analysis" / "artifacts"
        artifacts = []
        if artifacts_dir.exists():
            for f in artifacts_dir.iterdir():
                if f.suffix in {".parquet", ".csv", ".md", ".json"}:
                    artifacts.append(f.name)

        stdout = result.stdout
        if len(stdout) > 5000:
            stdout = stdout[:2000] + "\n...(truncated)...\n" + stdout[-2000:]
        stderr = result.stderr
        if len(stderr) > 3000:
            stderr = stderr[:1500] + "\n...(truncated)...\n" + stderr[-1500:]

        run_result = ScriptRunResult(
            success=result.returncode == 0,
            script_path=str(script_path),
            stdout=stdout,
            stderr=stderr,
            artifacts_produced=sorted(artifacts),
        )
        status = "SUCCESS" if run_result.success else "FAILED"
        log.step_done("RUN", script_name,
                      f"{status} — {len(run_result.artifacts_produced)} artifacts",
                      response_snippet=stdout[:300],
                      extra={"returncode": result.returncode,
                             "artifacts": run_result.artifacts_produced})
        if not run_result.success:
            log.error(script_name, f"Script failed: {stderr[:300]}")
        return run_result
    except subprocess.TimeoutExpired:
        log.error(script_name, "Script timed out after 600 seconds")
        return ScriptRunResult(
            success=False,
            script_path=str(script_path),
            stdout="",
            stderr="Script timed out after 600 seconds",
            artifacts_produced=[],
        )
    except Exception as e:
        log.error(script_name, f"Script crashed: {e}")
        return ScriptRunResult(
            success=False,
            script_path=str(script_path),
            stdout="",
            stderr=str(e),
            artifacts_produced=[],
        )


# ---------------------------------------------------------------------------
# CLAUDE — Evaluate output (now returns confidence + optional micro-phase)
# ---------------------------------------------------------------------------

@activity.defn
async def claude_evaluate_ml_output(
    phase_id: str,
    script_name: str,
    run_result: ScriptRunResult,
    expected_artifacts: str,
) -> PhaseEvaluation:
    log = PipelineLogger.get()
    log.step("EVALUATE", phase_id,
             f"[{get_runner('evaluator').provider_name}] evaluating {script_name} output"
             + (" (script failed)" if not run_result.success else ""))
    runner = get_runner("evaluator")

    prompt = f"""You are a senior quant evaluating Phase {phase_id} ({script_name}).
Go beyond pass/fail — mine the output for insights, spot anomalies, and assess
confidence.

Return code: {"SUCCESS" if run_result.success else "FAILED"}

STDOUT:
{run_result.stdout[:4000]}

STDERR:
{run_result.stderr[:1500]}

Artifacts produced: {', '.join(run_result.artifacts_produced)}

Expected: {expected_artifacts}

EVALUATE ON THREE LEVELS:

1. EXECUTION — clean exit? expected artifacts? data-quality warnings?

2. RESULTS — numbers in reasonable ranges? distributions make sense? sample
   sizes adequate? CIs tight or fragile? contradictions with prior phases?

3. INSIGHTS — top 3-5 findings with numbers and variables; anomalies needing
   follow-up; unplanned patterns; hypotheses for later phases.

CONFIDENCE ASSESSMENT (critical for later finalization gate):
- HIGH: clean execution, n ≥ 30 per cell, tight CIs, p < 0.05 on key findings,
  results stable across temporal splits
- MEDIUM: results plausible but some samples small, CIs wide, or stability
  uncertain
- LOW: execution succeeded but findings are likely noise (small n, wide CIs,
  no significance, or contradicts prior phases without explanation)

MICRO-PHASE PROPOSAL (use ONLY when an anomaly materially threatens later phases):
If the output reveals a fundamental issue — e.g. replay harness can't reproduce
backtest PnL within 5%, a key variable has unexpected distribution, or a prior
insight is contradicted — propose a targeted micro-phase via `proposed_micro_phase`.
Otherwise leave it null. Do NOT propose micro-phases for every finding.

Return JSON:
- passed (bool)
- issues (list of strings — empty if passed)
- artifacts_validated (list)
- summary (2-3 sentences: what was FOUND, not "ran successfully")
- next_action (CONTINUE | RETRY | STOP | INSERT_MICRO_PHASE)
- key_insights (list — specific findings with numbers, variables, directions)
- suggested_follow_ups (list — unplanned angles the data suggests)
- confidence_level (HIGH | MEDIUM | LOW)
- confidence_rationale (one sentence — justify the level)
- proposed_micro_phase (null OR object with phase_id, script_name, rationale,
  inline_prompt, dependencies, insert_before)
"""
    result = await runner.prompt_json(prompt, PhaseEvaluation)
    status = "PASSED" if result.passed else "FAILED"
    log.step_done("EVALUATE", phase_id,
                  f"{status} [{result.confidence_level.value}]: {result.summary[:150]}",
                  extra={"passed": result.passed,
                         "confidence": result.confidence_level.value,
                         "n_insights": len(result.key_insights),
                         "has_micro_phase": result.proposed_micro_phase is not None})
    for ins in result.key_insights:
        log.insight(phase_id, ins)
    if result.proposed_micro_phase:
        log.event("MICRO_PHASE_PROPOSED", phase_id=phase_id,
                  micro_id=result.proposed_micro_phase.phase_id,
                  rationale=result.proposed_micro_phase.rationale)
    return result


# ---------------------------------------------------------------------------
# CLAUDE — Confidence audit (gate before finalization)
# ---------------------------------------------------------------------------

@activity.defn
async def audit_phase_confidence(phase_results: List[PhaseResult]) -> ConfidenceAudit:
    """
    Deterministic audit: counts confidence levels. No Claude call needed —
    the assessment was made at each phase's evaluation.

    Gate policy (from role doc): passes when >= 3 HIGH confidence phases.
    """
    log = PipelineLogger.get()
    log.event("CONFIDENCE_AUDIT", total_phases=len(phase_results))
    high = [r for r in phase_results if r.confidence_level == ConfidenceLevel.HIGH]
    medium = [r for r in phase_results if r.confidence_level == ConfidenceLevel.MEDIUM]
    low = [r for r in phase_results if r.confidence_level == ConfidenceLevel.LOW]

    concerning = [f"{r.phase_id} ({r.confidence_rationale[:80]})" for r in low]

    passes = len(high) >= 3
    if passes:
        rationale = (
            f"{len(high)} HIGH-confidence phases meet the finalization bar. "
            f"Proposal can be issued with HIGH confidence."
        )
    else:
        rationale = (
            f"Only {len(high)} HIGH-confidence phases (need ≥3). "
            f"Proposal must be marked LOW_CONFIDENCE; recommend additional "
            f"data collection before deployment."
        )

    audit = ConfidenceAudit(
        total_phases_evaluated=len(phase_results),
        high_confidence_count=len(high),
        medium_confidence_count=len(medium),
        low_confidence_count=len(low),
        passes_finalization_gate=passes,
        rationale=rationale,
        concerning_phases=concerning,
    )
    gate_status = "PASSED" if passes else "FAILED"
    log.event("CONFIDENCE_RESULT",
              gate=gate_status,
              high=len(high), medium=len(medium), low=len(low),
              rationale=rationale)
    return audit


# ---------------------------------------------------------------------------
# CLAUDE — Build the final ML proposal (Phase 17)
# ---------------------------------------------------------------------------

@activity.defn
async def build_ml_proposal(
    phase_results: List[PhaseResult],
    confidence_audit: ConfidenceAudit,
    proposal_version: str,
) -> MLProposal:
    """
    Claude synthesizes all phase findings into a structured MLProposal.
    Each change must trace to a specific phase — no unsourced claims.
    """
    log = PipelineLogger.get()
    log.step("PROPOSAL", "finalize",
             f"[{get_runner('proposal_builder').provider_name}] building proposal "
             f"v{proposal_version} from {len(phase_results)} phases")
    runner = get_runner("proposal_builder")

    # Build a compact briefing of all phase results
    briefing_lines = []
    for r in phase_results:
        briefing_lines.append(
            f"## Phase {r.phase_id} — {r.status.value} — {r.confidence_level.value}\n"
            f"Summary: {r.evaluation_summary}\n"
            f"Insights:\n" + "\n".join(f"  - {i}" for i in r.key_insights) + "\n"
        )
    briefing = "\n".join(briefing_lines)

    gate_note = (
        f"Confidence audit: {confidence_audit.high_confidence_count} HIGH, "
        f"{confidence_audit.medium_confidence_count} MEDIUM, "
        f"{confidence_audit.low_confidence_count} LOW. "
        f"Gate {'PASSED' if confidence_audit.passes_finalization_gate else 'FAILED'}. "
        f"{confidence_audit.rationale}"
    )

    prompt = f"""You are the senior quant finalizing a config proposal for deployment.
Synthesize all phase findings into a structured proposal.

{gate_note}

PHASE RESULTS:
{briefing[:10000]}

RULES:
- Every weight_change, veto_change, and threshold_change MUST cite the specific
  phase and finding that justifies it. Unsourced changes are not allowed.
- If the confidence gate FAILED, set confidence_level to LOW and explain in
  confidence_rationale. Be honest — don't inflate confidence to satisfy the
  structure.
- expected_improvement.confidence_interval must come from a bootstrap or
  Monte Carlo phase (typically Phase 14). If no such phase exists, set the
  interval to [pnl_estimate * 0.5, pnl_estimate * 1.5] and note this as
  'ESTIMATE ONLY — no bootstrap basis' in the basis field.
- bugs_to_fix should come from the bug-hunt branch (Phases 25-28).
- risks must list at least 3 items — overfitting, regime dependency, small-n
  findings, correlated changes, etc.

Proposal version: {proposal_version}

Return JSON matching the MLProposal schema:
- proposal_version (string)
- confidence_level (HIGH | MEDIUM | LOW)
- confidence_rationale (sentence)
- weight_changes (list of {{source_name, old_value, new_value, basis}})
- veto_changes (list of {{signal_name, action, basis}})
- threshold_changes (list of {{parameter, old_value, new_value, basis}})
- trade_management_changes (dict of parameter -> new_value, with "_basis" keys)
- bugs_to_fix (list of {{id, severity, description, phase_source}})
- expected_improvement (object: pnl_delta_estimate, win_rate_delta,
  confidence_interval: [lo, hi], basis)
- risks (list of strings)
- recommended_next_backtest (dict)
"""
    result = await runner.prompt_json(prompt, MLProposal)
    log.step_done("PROPOSAL", "finalize",
                  f"Proposal built: {result.confidence_level.value} confidence, "
                  f"{len(result.weight_changes)} weight changes, "
                  f"{len(result.bugs_to_fix)} bugs",
                  extra={"confidence": result.confidence_level.value,
                         "weight_changes": len(result.weight_changes),
                         "veto_changes": len(result.veto_changes),
                         "threshold_changes": len(result.threshold_changes),
                         "bugs": len(result.bugs_to_fix)})
    return result


# ---------------------------------------------------------------------------
# Deterministic write of the proposal JSON to disk
# ---------------------------------------------------------------------------

@activity.defn
async def write_proposal_to_disk(proposal: MLProposal, output_path: str) -> str:
    p = Path(output_path)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(proposal.model_dump_json(indent=2), encoding="utf-8")
    return str(p)
