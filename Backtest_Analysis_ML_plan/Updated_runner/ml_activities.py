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

v3.1 fix:
- fast_plan_ml_phase now accepts optional artifact_previews: str = "" second arg
  to match what ml_workflow._build_phase passes via args=[context, artifact_previews]
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
from app.script_validator import validate_script, ValidationResult
from app.insights_filter import filter_insights
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
    """DEPRECATED — preserved for legacy callers. Use get_runner() instead."""
    return get_runner("script_writer")


# ---------------------------------------------------------------------------
# File system activities
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
# Intelligence: artifact content sampling
# ---------------------------------------------------------------------------

@activity.defn
async def sample_artifact_contents(
    artifacts_dir: str,
    artifact_names: List[str],
    max_per_file: int = 20,
) -> Dict[str, str]:
    """
    Read the first N rows of each artifact so Claude/Gemini knows exact
    column names, dtypes, and data shapes — not just file names.
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
# Intelligence: sample dependent scripts
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
        if len(lines) <= max_lines * 2:
            snippets[name] = "\n".join(lines)
        else:
            head = lines[:15]
            tail = lines[-max_lines:]
            snippets[name] = (
                "\n".join(head) +
                f"\n\n# ... ({len(lines) - 15 - max_lines} lines omitted) ...\n\n" +
                "\n".join(tail)
            )

    return snippets


# ---------------------------------------------------------------------------
# Helper: summarize accumulated insights
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

    for ln in reversed(other):
        if budget_remaining - len(ln) - 1 < 0:
            break
        kept.append(ln)
        budget_remaining -= len(ln) + 1

    dropped = len(lines) - len(kept)
    header = (
        f"# Accumulated insights ({len(kept)} kept, {dropped} older dropped"
        f" — anomalies always retained)\n"
    )
    return header + "\n".join(kept)


# ---------------------------------------------------------------------------
# CLAUDE — Plan a phase (two-stage: Gemini drafts → Claude edits)
# ---------------------------------------------------------------------------

def _build_micro_phase_draft(context: MLPhaseContext) -> dict:
    """For micro-phases, Claude already wrote the plan inline."""
    return {
        "mission": "Claude-initiated micro-phase deep-dive.",
        "inline_request": context.plan_prompt,
        "input_files_specified": [],
        "output_artifacts_specified": [],
        "required_analysis": "(see inline_request above)",
    }


async def _gemini_draft_plan(
    context: MLPhaseContext,
    relevant_insights: str,
) -> dict:
    """
    Stage 1: ask Gemini to read everything and produce a structured draft.
    Uses Gemini's native structured output for reliability.
    """
    from pydantic import BaseModel
    from typing import List as _List

    class DraftPlan(BaseModel):
        mission: str
        input_files: _List[str]
        output_artifacts: _List[str]
        required_analyses: _List[str]
        statistical_methods: _List[str]
        defensive_requirements: _List[str]
        cross_phase_dependencies: _List[str]
        known_failure_modes: _List[str]

    drafter = get_runner("plan_drafter")

    prompt = f"""Draft a structured plan for Phase {context.phase_id} ({context.script_name}).

You are the DRAFTER role. A senior reviewer (Claude) will critique your draft
before it goes to the script writer. Be specific, reference exact filenames
and column names from the schema, do NOT invent tags or columns that aren't
listed in the schema.

PLAN DOCUMENT:
{context.plan_prompt[:6000]}

SCHEMA (available columns and dtypes):
{context.schema_content[:3000]}

AVAILABLE ARTIFACTS (use ONLY these exact filenames):
{', '.join(context.existing_artifacts)}

STRATEGY CONFIG SNIPPET:
{context.strategy_config_snippet[:1500]}

REPO ROOT: {context.repo_root}
MIN SAMPLE SIZE: {context.min_sample_size}

PRIOR PHASE SUMMARY: {context.previous_phase_summary or '(none)'}

RELEVANT INSIGHTS FROM PRIOR PHASES:
{relevant_insights or '(none)'}

Draft the plan as structured JSON.
"""
    return await drafter.prompt_json(prompt, DraftPlan)


@activity.defn
async def claude_plan_ml_phase(
    context: MLPhaseContext,
    artifact_previews: str = "",
    dependent_script_previews: str = "",
) -> GeminiScriptRequest:
    """
    Two-stage planning:
      Stage 1: Gemini drafter reads context, produces a structured draft plan.
      Stage 2: Claude editor critiques the draft and emits the FINAL prompt
               that Gemini's script writer will receive.
    """
    log = PipelineLogger.get()
    log.step("PLAN", context.phase_id,
             f"[Gemini draft → Claude edit] planning {context.script_name}")

    # Micro-phases skip Stage 1 — Claude wrote the inline prompt
    if context.is_micro_phase and context.plan_prompt:
        draft = _build_micro_phase_draft(context)
        draft_text = json.dumps(draft, indent=2)
    else:
        relevant_insights = _summarize_insights(context.accumulated_insights or "")
        draft = await _gemini_draft_plan(context, relevant_insights)
        draft_text = json.dumps(draft if isinstance(draft, dict) else draft.model_dump(), indent=2)

    editor = get_runner("plan_editor")

    cacheable_prefix = f"""You are a senior quantitative researcher acting as plan editor.
Your job: critique Gemini's draft plan, fix its mistakes, and emit the FINAL
prompt that the script writer (Gemini) will receive.

SCHEMA (authoritative source of truth for column names):
{context.schema_content[:3000]}

AVAILABLE ARTIFACTS (the ONLY files the script may read):
{', '.join(context.existing_artifacts)}

ARTIFACT PREVIEWS (actual data shapes and column names):
{artifact_previews[:3000] if artifact_previews else '(none)'}

DEPENDENT SCRIPT PREVIEWS (output shapes of upstream scripts):
{dependent_script_previews[:2000] if dependent_script_previews else '(none)'}
"""

    edit_prompt = f"""GEMINI'S DRAFT PLAN:
{draft_text[:4000]}

ORIGINAL PHASE MISSION:
{context.plan_prompt[:2000]}

PRIOR PHASE SUMMARY: {context.previous_phase_summary or '(none)'}
RETRY ERROR (if any): {context.retry_error or '(none)'}
ACCUMULATED INSIGHTS:
{_summarize_insights(context.accumulated_insights or '', max_chars=2000)}

YOUR CRITIQUE CHECKLIST:
1. Does the draft's input_files list ONLY files in the available artifacts?
   If it references files not in the list, REMOVE or REPLACE them.
2. Did the draft hallucinate tag types or column names? Check against schema.
3. Does the draft cover EVERY check in the phase plan doc, or did it skip some?
4. Did the draft miss the n≥{context.min_sample_size} requirement?
5. Are the statistical methods appropriate for the data sizes available?
6. Are there cross-phase coherence issues (contradicts a prior insight)?

Then emit the FINAL writer prompt with these sections in order:
- MISSION (one paragraph from the plan doc, refined)
- INPUT FILES (exact paths under {context.repo_root}/Analysis/artifacts/,
  ONLY referencing files in the available list above)
- OUTPUT ARTIFACTS (exact paths the script will write)
- REQUIRED ANALYSIS (numbered checks from the plan doc, with the corrections
  you made above; specify EVERY check the plan doc lists)
- STATISTICAL METHODS (concrete: which test, what CI level, bootstrap n)
- OUTPUT SECTIONS (script MUST call print('[INPUT] ...'), print('[CHECK] ...'),
  print('[RESULT] ...'), print('[SAVED] ...') — these are runtime print
  calls, NOT bare lines in the source code)
- DEFENSIVE REQUIREMENTS (specific edge cases for this phase)
- MINIMUM SAMPLE SIZE: enforce n ≥ {context.min_sample_size} via a constant
  MIN_N at the top; flag findings below as INSUFFICIENT
- CROSS-PHASE CONTEXT (only what's relevant — don't dump prior insights blindly)

Return JSON:
- prompt (the complete final writer prompt — self-contained for Gemini)
- phase_id ({context.phase_id})
- script_name ({context.script_name})"""

    result = await editor.prompt_json(
        edit_prompt,
        GeminiScriptRequest,
        cached_prefix=cacheable_prefix,
    )
    log.step_done("PLAN", context.phase_id,
                  f"Plan ready ({len(result.prompt)} chars)")
    return result


# ---------------------------------------------------------------------------
# Fast-path planning for skip_planning phases (templated bug-hunt audits)
# ---------------------------------------------------------------------------

@activity.defn
async def fast_plan_ml_phase(
    context: MLPhaseContext,
    artifact_previews: str = "",
) -> GeminiScriptRequest:
    """
    No Claude in the loop. The plan doc is wrapped with minimum scaffolding
    and handed straight to the writer. Used for phases marked skip_planning
    in the registry — typically the bug-hunt audits that follow a
    deterministic template.

    The script_validator gate still runs after the writer, so we don't
    lose quality protection — we just lose the upfront plan critique
    where it isn't earning its keep.

    v3.1: accepts optional artifact_previews so the writer sees real column
    names even on the fast path.
    """
    log = PipelineLogger.get()
    log.step("PLAN-FAST", context.phase_id,
             f"templated planning for {context.script_name}",
             prompt_snippet=context.plan_prompt[:200])

    previews_section = ""
    if artifact_previews:
        previews_section = f"""
# Artifact previews (actual column names and sample rows — use these exact names)
{artifact_previews[:3000]}
"""

    prompt = f"""# Mission
{context.plan_prompt[:6000]}

# Repo root
{context.repo_root}

# Available input artifacts (use ONLY these exact filenames)
{', '.join(context.existing_artifacts) if context.existing_artifacts else '(none)'}
{previews_section}
# Output dir
{context.repo_root}/Analysis/artifacts/

# Hard requirements
- Minimum sample size: n >= {context.min_sample_size}. Define `MIN_N = {context.min_sample_size}` near the top. Flag findings on n<MIN_N as INSUFFICIENT.
- Defensive: handle empty DataFrames, missing columns, division by zero.
- Reproducibility: np.random.seed(42) wherever randomness appears.
- Output: print() with [INPUT], [CHECK], [RESULT], [SAVED] tags. These are RUNTIME prints, NOT bare lines in the source.
- All paths via pathlib.Path.
- The first line of your output must be valid Python (import / # comment / etc).
"""

    result = GeminiScriptRequest(
        prompt=prompt,
        phase_id=context.phase_id,
        script_name=context.script_name,
    )
    log.step_done("PLAN-FAST", context.phase_id,
                  f"templated prompt ready ({len(result.prompt)} chars)")
    return result


# ---------------------------------------------------------------------------
# Local script validation gate (runs before Claude review)
# ---------------------------------------------------------------------------

@activity.defn
async def validate_generated_script(
    script_content: str,
    available_artifacts: List[str],
    min_sample_size: int = 30,
) -> ValidationResult:
    """
    Pre-Claude-review validation. Catches predictable failures cheaply:
    syntax errors, missing print sections, missing min_n enforcement,
    references to nonexistent artifacts, etc.

    If this returns passed=False, the workflow hands the issues back
    to the writer as feedback — Claude review is skipped for that
    iteration (no point burning a review call on broken code).
    """
    return validate_script(script_content, available_artifacts, min_sample_size)


# ---------------------------------------------------------------------------
# GEMINI — Write the script
# ---------------------------------------------------------------------------

@activity.defn
async def gemini_write_ml_script(
    request: GeminiScriptRequest,
) -> str:
    """
    Write a script from request.prompt. Feedback is already baked into the
    prompt by the workflow (via req_with_feedback) — no separate feedback arg.
    This keeps the Temporal call signature to a single argument, avoiding
    strict-deserializer issues with Optional parameters.
    """
    log = PipelineLogger.get()
    has_feedback = "PREVIOUS ATTEMPT FEEDBACK" in request.prompt
    log.step("WRITE", request.phase_id,
             f"Gemini writing {request.script_name}"
             + (" (with feedback)" if has_feedback else ""))

    prompt = request.prompt
    prompt += """

CRITICAL OUTPUT RULES:
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
    try:
        result = await runner.prompt(prompt)
    except Exception as e:
        log.warning(request.phase_id, f"Primary writer failed: {e}, using fallback")
        # _FallbackWrapper in roles.py handles this automatically if configured;
        # if no fallback configured, re-raise so Temporal can retry the activity.
        raise

    log.step_done("WRITE", request.phase_id,
                  f"Script written ({len(result)} chars, {result.count(chr(10))} lines)",
                  response_snippet=result[:300])
    return result


# ---------------------------------------------------------------------------
# CLAUDE — Review the script
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

    # Raise review budget from 8k to 20k. Long analysis scripts have their
    # save-logic and main block at the bottom — don't cut it.
    max_script_chars = 20_000
    if len(script_content) > max_script_chars:
        head = script_content[: max_script_chars // 2]
        tail = script_content[-(max_script_chars // 2):]
        script_view = (
            f"{head}\n\n"
            f"# ... ({len(script_content) - max_script_chars} chars elided from middle) ...\n\n"
            f"{tail}"
        )
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
        log.warning(request.phase_id, f"Review rejected: {result.notes}")
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

    artifacts_before = set(
        f.name for f in (Path(repo_root) / "Analysis" / "artifacts").iterdir()
        if f.is_file()
    ) if (Path(repo_root) / "Analysis" / "artifacts").exists() else set()

    try:
        proc = subprocess.run(
            ["python", str(script_path)],
            capture_output=True,
            text=True,
            timeout=600,
            cwd=str(Path(repo_root)),
        )
        stdout = proc.stdout or ""
        stderr = proc.stderr or ""
        success = proc.returncode == 0

        artifacts_after = set(
            f.name for f in (Path(repo_root) / "Analysis" / "artifacts").iterdir()
            if f.is_file()
        ) if (Path(repo_root) / "Analysis" / "artifacts").exists() else set()

        new_artifacts = sorted(artifacts_after - artifacts_before)

        log.step_done("RUN", script_name,
                      f"exit={proc.returncode} | new artifacts={new_artifacts}",
                      extra={"stdout_tail": stdout[-500:],
                             "artifacts": new_artifacts})
        if not success:
            log.error(script_name, f"Script failed: {stderr[:300]}")

        return ScriptRunResult(
            success=success,
            script_path=str(script_path),
            stdout=stdout,
            stderr=stderr,
            artifacts_produced=new_artifacts,
        )

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
# CLAUDE — Evaluate output
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
If the output reveals a fundamental issue — e.g. a filter that eliminates 95%
of data, an inverted signal, a lookahead bias — propose a micro-phase.

Return JSON:
- passed (bool — false only if script crashed or produced no artifacts)
- issues (list of strings — concrete problems found)
- artifacts_validated (list of artifact names confirmed present)
- summary (2-3 sentences: what the phase found, confidence, key numbers)
- next_action (CONTINUE | RETRY | STOP | INSERT_MICRO_PHASE)
- key_insights (list of 3-5 concrete findings with numbers)
- suggested_follow_ups (list — questions for later phases)
- confidence_level (HIGH | MEDIUM | LOW)
- confidence_rationale (one sentence explaining the confidence rating)
- proposed_micro_phase (null unless INSERT_MICRO_PHASE — include phase_id,
  script_name, rationale, inline_prompt, dependencies, insert_before)
"""
    result = await runner.prompt_json(prompt, PhaseEvaluation)
    log.step_done("EVALUATE", phase_id,
                  f"passed={result.passed} confidence={result.confidence_level.value}",
                  extra={"insights": result.key_insights,
                         "next_action": result.next_action})
    return result


# ---------------------------------------------------------------------------
# Confidence gate (runs before Phase 17)
# ---------------------------------------------------------------------------

@activity.defn
async def audit_phase_confidence(
    phase_results: List[PhaseResult],
) -> ConfidenceAudit:
    """
    Count HIGH/MEDIUM/LOW confidence phases and determine if the finalization
    gate passes (≥3 HIGH confidence phases required).
    Does not block Phase 17 — just flags so the proposal can note it.
    """
    high = sum(1 for r in phase_results if r.confidence_level == ConfidenceLevel.HIGH)
    medium = sum(1 for r in phase_results if r.confidence_level == ConfidenceLevel.MEDIUM)
    low = sum(1 for r in phase_results if r.confidence_level == ConfidenceLevel.LOW)
    passes = high >= 3

    concerning = [
        r.phase_id for r in phase_results
        if r.confidence_level == ConfidenceLevel.LOW
    ]

    return ConfidenceAudit(
        total_phases_evaluated=len(phase_results),
        high_confidence_count=high,
        medium_confidence_count=medium,
        low_confidence_count=low,
        passes_finalization_gate=passes,
        rationale=(
            f"{high} HIGH-confidence phases; gate "
            f"{'PASSED' if passes else 'FAILED'} (need ≥3)."
        ),
        concerning_phases=concerning,
    )


# ---------------------------------------------------------------------------
# Build the final MLProposal (Claude)
# ---------------------------------------------------------------------------

@activity.defn
async def build_ml_proposal(
    phase_results: List[PhaseResult],
    confidence_audit: ConfidenceAudit,
    proposal_version: str = "v1",
) -> MLProposal:
    """
    Claude synthesizes all phase results into a structured config proposal.
    Each change must trace to a specific phase — no unsourced claims.
    """
    log = PipelineLogger.get()
    log.step("PROPOSAL", "finalize",
             f"[{get_runner('proposal_builder').provider_name}] building proposal "
             f"v{proposal_version} from {len(phase_results)} phases")
    runner = get_runner("proposal_builder")

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
  confidence_rationale. Be honest — don't inflate confidence.
- expected_improvement.confidence_interval must come from a bootstrap or
  Monte Carlo phase (typically Phase 14). If no such phase ran, set the
  interval to [pnl_estimate * 0.5, pnl_estimate * 1.5] and note this as
  'ESTIMATE ONLY — no bootstrap basis' in the basis field.
- bugs_to_fix should come from the bug-hunt branch (Phases 25-28).
- risks must list at least 3 items — overfitting, regime dependency, small-n
  findings, correlated changes, etc.

Return a complete MLProposal JSON object.
"""
    result = await runner.prompt_json(prompt, MLProposal)
    log.step_done("PROPOSAL", "finalize",
                  f"confidence={result.confidence_level.value} | "
                  f"weight_changes={len(result.weight_changes)} | "
                  f"bugs={len(result.bugs_to_fix)}")
    return result


# ---------------------------------------------------------------------------
# Write proposal to disk
# ---------------------------------------------------------------------------

@activity.defn
async def write_proposal_to_disk(
    proposal: MLProposal,
    output_path: str,
) -> str:
    """Serialize the MLProposal to JSON and write to disk. Returns the path."""
    log = PipelineLogger.get()
    p = Path(output_path)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(proposal.model_dump_json(indent=2), encoding="utf-8")
    log.event("PROPOSAL_SAVED", path=str(p))
    return str(p)