"""
Data models for the autonomous ML pipeline.

Stage 1 enhancements:
- ConfidenceLevel enum for phase-level confidence tracking
- MicroPhaseProposal for Claude-initiated deep-dive investigations
- MLProposal for structured Phase 17 output
- ConfidenceAudit for the finalization gate
- PhaseSpec now supports parallel_group and confidence gating
- Evaluation extended with confidence_level and proposed_micro_phase
"""

from datetime import datetime, timezone
from enum import Enum
from typing import Any, Dict, List, Literal, Optional
from pydantic import BaseModel, Field


# ---------------------------------------------------------------------------
# Enums
# ---------------------------------------------------------------------------

class ConfidenceLevel(str, Enum):
    HIGH = "HIGH"
    MEDIUM = "MEDIUM"
    LOW = "LOW"


class PhaseStatus(str, Enum):
    PENDING = "PENDING"
    COMPLETED = "COMPLETED"
    FAILED = "FAILED"
    SKIPPED = "SKIPPED"


# ---------------------------------------------------------------------------
# Phase specification (the "registry" schema)
# ---------------------------------------------------------------------------

class MLPhaseSpec(BaseModel):
    """One phase of the ML pipeline to build."""
    phase_id: str
    script_name: str
    plan_file: str
    dependencies: List[str] = []

    # Phases with the same non-None parallel_group can run in parallel.
    # Phases with parallel_group=None run sequentially (one at a time).
    parallel_group: Optional[str] = None

    # Default minimum sample size n for findings. Carried into the planning
    # prompt and review prompt so sub-threshold findings get flagged.
    min_sample_size: int = 30

    # If True, a confidence audit runs before this phase; the phase still
    # executes if the gate fails but is marked LOW_CONFIDENCE in the proposal.
    requires_confidence_gate: bool = False

    # If True, this is a Claude-inserted micro-phase with inline prompt.
    is_micro_phase: bool = False
    inline_prompt: Optional[str] = None


class MicroPhaseProposal(BaseModel):
    """Claude can propose an unplanned micro-phase in response to anomalies."""
    phase_id: str                  # e.g. "13_a"
    script_name: str               # e.g. "13_a_threshold_sensitivity.py"
    rationale: str                 # WHY this micro-phase is needed
    inline_prompt: str             # full analytical request for Gemini
    dependencies: List[str] = []   # typically the phase that discovered the anomaly
    insert_before: Optional[str] = None  # phase_id that should be blocked until this runs


# ---------------------------------------------------------------------------
# Planning + execution flow
# ---------------------------------------------------------------------------

class MLPhaseContext(BaseModel):
    """Everything Claude needs to plan a phase."""
    phase_id: str
    script_name: str
    plan_prompt: str                    # raw plan doc OR inline prompt
    schema_content: str
    existing_artifacts: List[str]
    strategy_config_snippet: str
    repo_root: str                      # passed through so prompts aren't hardcoded
    previous_phase_summary: Optional[str] = None
    retry_error: Optional[str] = None
    accumulated_insights: Optional[str] = None
    min_sample_size: int = 30
    is_micro_phase: bool = False        # when True, plan_prompt IS the full request


class GeminiScriptRequest(BaseModel):
    """What Gemini receives to write a script."""
    prompt: str
    phase_id: str
    script_name: str


class ScriptReviewDecision(BaseModel):
    """Claude's review of Gemini's script before running."""
    approved: bool
    verdict: str
    risk_flags: List[str]
    notes: str


class ScriptRunResult(BaseModel):
    """Result of saving and executing a Python script."""
    success: bool
    script_path: str
    stdout: str
    stderr: str
    artifacts_produced: List[str]


class PhaseEvaluation(BaseModel):
    """Claude's evaluation of a completed phase."""
    passed: bool
    issues: List[str]
    artifacts_validated: List[str]
    summary: str
    next_action: Literal["CONTINUE", "RETRY", "STOP", "INSERT_MICRO_PHASE"]
    key_insights: List[str] = []
    suggested_follow_ups: List[str] = []

    # NEW: confidence assessment for downstream gating
    confidence_level: ConfidenceLevel = ConfidenceLevel.MEDIUM
    confidence_rationale: str = ""

    # NEW: Claude can propose a targeted deep-dive before moving on
    proposed_micro_phase: Optional[MicroPhaseProposal] = None


class PhaseResult(BaseModel):
    """A completed phase entry for workflow history (structured)."""
    phase_id: str
    status: PhaseStatus
    script_name: str
    artifacts: List[str] = []
    evaluation_summary: str = ""
    key_insights: List[str] = []
    suggested_follow_ups: List[str] = []
    confidence_level: ConfidenceLevel = ConfidenceLevel.MEDIUM
    confidence_rationale: str = ""
    issues: List[str] = []


# ---------------------------------------------------------------------------
# Final proposal (Phase 17 output)
# ---------------------------------------------------------------------------

class WeightChange(BaseModel):
    source_name: str
    old_value: float
    new_value: float
    basis: str                          # "Phase 13 finding: SMF_Impulse ρ=-0.23 ..."


class VetoChange(BaseModel):
    signal_name: str
    action: Literal["ADD", "REMOVE", "MODIFY"]
    basis: str


class ThresholdChange(BaseModel):
    parameter: str                      # e.g. "PER_SOURCE_THRESHOLDS.ORB"
    old_value: float
    new_value: float
    basis: str


class BugReport(BaseModel):
    id: str                             # e.g. "BUG-001"
    severity: Literal["HIGH", "MEDIUM", "LOW"]
    description: str
    phase_source: str                   # e.g. "Phase 26"


class ExpectedImprovement(BaseModel):
    pnl_delta_estimate: float
    win_rate_delta: float
    # Pydantic v2 uses min_length / max_length for list constraints
    confidence_interval: List[float] = Field(..., min_length=2, max_length=2)
    basis: str                          # e.g. "Phase 14 bootstrap, n=10000"


class MLProposal(BaseModel):
    """The final config proposal produced by Phase 17."""
    proposal_version: str
    generated_at: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    confidence_level: ConfidenceLevel
    confidence_rationale: str
    weight_changes: List[WeightChange] = []
    veto_changes: List[VetoChange] = []
    threshold_changes: List[ThresholdChange] = []
    trade_management_changes: Dict[str, Any] = {}
    bugs_to_fix: List[BugReport] = []
    expected_improvement: Optional[ExpectedImprovement] = None
    risks: List[str] = []
    recommended_next_backtest: Dict[str, Any] = {}


# ---------------------------------------------------------------------------
# Confidence audit (gate before finalization)
# ---------------------------------------------------------------------------

class ConfidenceAudit(BaseModel):
    total_phases_evaluated: int
    high_confidence_count: int
    medium_confidence_count: int
    low_confidence_count: int
    # Gate passes when >= 3 HIGH confidence phases (per role doc)
    passes_finalization_gate: bool
    rationale: str
    # Flagged phases that pulled confidence down
    concerning_phases: List[str] = []


# ---------------------------------------------------------------------------
# Phase registry — data-driven, enhanced with parallel_group
# ---------------------------------------------------------------------------
#
# Parallelization strategy:
# - Phase 11 (replay harness) must run first; everything else depends on it
# - After Phase 11, the optimization branch (13→14) and trade-mgmt branch
#   (15→16) can proceed in parallel — grouped as "post_baseline"
# - The bug-hunt branch (25, 26, 27) has no dependencies and runs fully in
#   parallel as "bug_hunt"
# - Phase 17 requires a confidence gate and depends on everything upstream
#
# Note: parallel_group only enables parallelism if dependencies allow it.

ML_PHASES: List[MLPhaseSpec] = [
    # --- Foundation (sequential) ---
    MLPhaseSpec(
        phase_id="11",
        script_name="11_replay_harness.py",
        plan_file="gemini_prompts_part2.md",
        dependencies=[],
    ),
    MLPhaseSpec(
        phase_id="12",
        script_name="12_baseline_validation.py",
        plan_file="gemini_prompts_part2.md",
        dependencies=["11"],
    ),

    # --- Optimization branch (threshold -> stress test) ---
    MLPhaseSpec(
        phase_id="13",
        script_name="13_optimize_thresholds.py",
        plan_file="gemini_prompts_part2.md",
        dependencies=["11", "12"],
    ),
    MLPhaseSpec(
        phase_id="14",
        script_name="14_stress_test.py",
        plan_file="gemini_prompts_part2.md",
        dependencies=["11", "13"],
    ),

    # --- Trade management branch ---
    MLPhaseSpec(
        phase_id="15",
        script_name="15_trade_mgmt_diagnostic.py",
        plan_file="gemini_prompts_part2.md",
        dependencies=["11"],
    ),
    MLPhaseSpec(
        phase_id="16",
        script_name="16_optimize_trade_mgmt.py",
        plan_file="gemini_prompts_part2.md",
        dependencies=["11", "15"],
    ),

    # --- Bug hunt branch (fully parallel — no upstream deps) ---
    MLPhaseSpec(
        phase_id="25",
        script_name="25_zone_hygiene.py",
        plan_file="gemini_prompts_part4_bug_hunt.md",
        dependencies=[],
        parallel_group="bug_hunt",
    ),
    MLPhaseSpec(
        phase_id="26",
        script_name="26_ta_decision_audit.py",
        plan_file="gemini_prompts_part4_bug_hunt.md",
        dependencies=[],
        parallel_group="bug_hunt",
    ),
    MLPhaseSpec(
        phase_id="27",
        script_name="27_funnel_orphan_audit.py",
        plan_file="gemini_prompts_part4_bug_hunt.md",
        dependencies=[],
        parallel_group="bug_hunt",
    ),
    MLPhaseSpec(
        phase_id="28",
        script_name="28_bug_list.py",
        plan_file="gemini_prompts_part4_bug_hunt.md",
        dependencies=["25", "26", "27"],
    ),

    # --- Finalization (confidence-gated) ---
    MLPhaseSpec(
        phase_id="17",
        script_name="17_finalize_proposal.py",
        plan_file="gemini_prompts_part2.md",
        dependencies=["13", "14", "16", "28"],
        requires_confidence_gate=True,
    ),
]
