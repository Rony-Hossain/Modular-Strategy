from pathlib import Path
from typing import Optional

from app.config import Settings
from app.models import (
    QuantReport,
    DiagnosisAndInstructions,
    CodePatch,
    ReviewDecision,
    PatchApplyResult,
    BuildResult,
    BacktestResult,
    ComparisonDecision,
)
from app.claude_runner import ClaudeRunner
from app.gemini_runner import GeminiRunner
from app.patcher import apply_patch, restore_backup
from app.build_runner import build_project
from app.backtest_runner import load_backtest_report, trigger_and_wait_for_backtest
from temporalio import activity


def _gemini(role: str) -> GeminiRunner:
    api_key = Settings.GEMINI_KEYS[role]
    if not api_key:
        raise RuntimeError(f"Missing Gemini key for role: {role}")
    return GeminiRunner(api_key)


# ---------------------------------------------------------------------------
# Source code reader
# ---------------------------------------------------------------------------

@activity.defn
async def read_source_file(repo_root: str, relative_path: str) -> str:
    file_path = Path(repo_root) / relative_path
    if not file_path.exists():
        return ""
    return file_path.read_text(encoding="utf-8")


# ---------------------------------------------------------------------------
# Health check
# ---------------------------------------------------------------------------

@activity.defn
async def claude_health_check() -> bool:
    """Legacy health check — only pings Claude. Used by QuantRepairWorkflowV2
    which is hard-wired to Claude. The ML pipeline uses provider_health_check
    instead (which pings whatever roles.yaml has configured)."""
    runner = ClaudeRunner(timeout_seconds=30)
    return await runner.health_check()


@activity.defn
async def provider_health_check() -> dict:
    """
    Ping every distinct provider used by the ML pipeline's roles.yaml.
    Returns a dict {provider: bool} so callers can see which one failed.
    The workflow treats "all healthy" as success.
    """
    from app.roles import list_active_providers, _build_runner
    results: dict = {}
    for provider in list_active_providers():
        try:
            # Build a minimal runner with a short timeout for the ping
            runner = _build_runner({
                "provider": provider,
                "timeout_seconds": 30,
            })
            results[provider] = await runner.health_check()
        except Exception as e:
            results[provider] = False
            results[f"{provider}_error"] = str(e)[:200]
    return results


# ---------------------------------------------------------------------------
# CLAUDE TOUCH 1 — Diagnose the report and write Gemini's instructions
# Now receives iteration history so it won't repeat failed approaches.
# ---------------------------------------------------------------------------

@activity.defn
async def claude_diagnose_and_instruct(
    report: QuantReport,
    iteration_history: list,
) -> DiagnosisAndInstructions:
    runner = ClaudeRunner()

    history_block = ""
    if iteration_history:
        history_block = "\n\nPrevious iterations (do NOT repeat these approaches):\n"
        for h in iteration_history:
            status = h.get("status", "unknown")
            instr = h.get("instructions", {})
            problem = instr.get("problem", "N/A")
            history_block += f"- [{status}] {problem}\n"

    prompt = f"""You are a quant strategy doctor. Be brief and surgical.

Backtest report:
{report.model_dump_json(indent=2)}
{history_block}
Identify the single most important problem. Then write precise instructions
for a junior engineer (Gemini) to fix it in the C# NinjaTrader codebase.

Rules:
- One problem only
- One file, one class, one change
- No overtrading — never loosen entry filters
- Prefer tighter thresholds, guards, or session filters

Return JSON:
- problem (one sentence)
- priority_metric (the metric to improve)
- target_file (e.g. StrategyConfig.cs)
- target_class
- instruction (direct order to Gemini: what to change and exactly how)
- constraints (list of hard rules)
- max_trade_increase_pct (float, max % trade count is allowed to grow)
"""
    return await runner.prompt_json(prompt, DiagnosisAndInstructions)


# ---------------------------------------------------------------------------
# GEMINI — Execute the patch based on Claude's instructions
# Now receives the actual source code so it can write exact old_content.
# Optional feedback from a rejected review for retry attempts.
# ---------------------------------------------------------------------------

@activity.defn
async def gemini_execute_patch(
    report: QuantReport,
    instructions: DiagnosisAndInstructions,
    source_code: str,
    feedback: Optional[str],
) -> CodePatch:
    feedback_block = ""
    if feedback:
        feedback_block = f"""
IMPORTANT — Your previous attempt was rejected. Fix these issues:
{feedback}
"""

    source_block = ""
    if source_code:
        source_block = f"""
Current file content ({instructions.target_file}):
```csharp
{source_code}
```
"""

    prompt = f"""You are a senior C# NinjaTrader engineer executing a precise fix.

Your orders from the lead architect:
Problem: {instructions.problem}
Target file: {instructions.target_file}
Target class: {instructions.target_class}
Instruction: {instructions.instruction}

Hard constraints you must not violate:
{chr(10).join(f'- {c}' for c in instructions.constraints)}
{source_block}
Backtest context (for reference only):
{report.model_dump_json(indent=2)}
{feedback_block}
Write the smallest safe code patch that follows the instruction exactly.
For REPLACE operations, old_content must be an EXACT substring from the file above.

Return JSON:
- file_path (relative path to the .cs file)
- change_type (REPLACE_CONSTANT | REPLACE_BLOCK | INSERT_METHOD | DELETE_BLOCK | ADD_GUARD)
- target_class
- old_content (the exact string to replace, copied from the file — required for REPLACE/DELETE)
- new_content (the replacement)
- reasoning (one sentence)
"""
    try:
        return await _gemini("COMPILER").prompt_json(prompt, CodePatch)
    except Exception:
        activity.logger.warning("Primary Gemini failed. Using fallback.")
        return await _gemini("FALLBACK").prompt_json(prompt, CodePatch)


# ---------------------------------------------------------------------------
# CLAUDE TOUCH 2 — Review Gemini's patch
# ---------------------------------------------------------------------------

@activity.defn
async def claude_review_patch(
    instructions: DiagnosisAndInstructions,
    patch: CodePatch,
) -> ReviewDecision:
    runner = ClaudeRunner()

    prompt = f"""You wrote these instructions:
{instructions.model_dump_json(indent=2)}

Gemini produced this patch:
{patch.model_dump_json(indent=2)}

Did Gemini follow your instructions correctly?
Reject if: vague, unsafe, violates constraints, or increases trade frequency.

Return JSON:
- approved (bool)
- verdict (one sentence)
- risk_flags (list)
- notes (one sentence)
"""
    return await runner.prompt_json(prompt, ReviewDecision)


# ---------------------------------------------------------------------------
# Deterministic activities — no AI needed
# ---------------------------------------------------------------------------

@activity.defn
async def apply_code_patch(patch: CodePatch, repo_root: str) -> PatchApplyResult:
    return apply_patch(patch, repo_root)


@activity.defn
async def rollback_code_patch(file_path: str, backup_path: str) -> PatchApplyResult:
    return restore_backup(file_path, backup_path)


@activity.defn
async def compile_strategy(repo_root: str, build_cmd: list[str]) -> BuildResult:
    return build_project(build_cmd, repo_root)


@activity.defn
async def load_new_backtest_report(report_json_path: str) -> BacktestResult:
    return load_backtest_report(report_json_path)


@activity.defn
async def trigger_backtest_and_load(report_json_path: str) -> BacktestResult:
    """End-to-end: click Run in Strategy Analyzer, wait for export, load report."""
    return trigger_and_wait_for_backtest(report_json_path)


# ---------------------------------------------------------------------------
# CLAUDE TOUCH 3 — Compare before/after and decide
# ---------------------------------------------------------------------------

@activity.defn
async def claude_compare_and_decide(
    before: QuantReport,
    after: QuantReport,
    instructions: DiagnosisAndInstructions,
) -> ComparisonDecision:
    runner = ClaudeRunner()

    prompt = f"""You targeted: {instructions.problem}
Priority metric: {instructions.priority_metric}

Before: profit_factor={before.profit_factor}, sharpe={before.sharpe_ratio}, \
net_profit={before.net_profit}, drawdown={before.max_drawdown}, \
trades={before.total_trades}, win_rate={before.win_rate}

After:  profit_factor={after.profit_factor}, sharpe={after.sharpe_ratio}, \
net_profit={after.net_profit}, drawdown={after.max_drawdown}, \
trades={after.total_trades}, win_rate={after.win_rate}

Hard reject if trades grew more than {instructions.max_trade_increase_pct}%.

Did the priority metric improve? Is this worth keeping?

Return JSON:
- improved (bool)
- should_continue (bool)
- summary (one sentence)
- reasons (list)
- next_action (ACCEPT | RETRY | STOP)
"""
    return await runner.prompt_json(prompt, ComparisonDecision)
