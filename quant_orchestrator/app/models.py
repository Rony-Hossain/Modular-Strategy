from datetime import datetime, timezone
from typing import Dict, Optional, Literal, List
from pydantic import BaseModel, Field


class QuantReport(BaseModel):
    strategy_id: str
    net_profit: float
    profit_factor: float
    sharpe_ratio: float
    max_drawdown: float
    win_rate: float
    total_trades: int
    session_perf_map: Dict[str, float]
    timestamp: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))


class DiagnosisAndInstructions(BaseModel):
    problem: str
    priority_metric: str
    target_file: str
    target_class: str
    instruction: str
    constraints: List[str]
    max_trade_increase_pct: float


class CodePatch(BaseModel):
    file_path: str
    change_type: Literal[
        "REPLACE_CONSTANT",
        "REPLACE_BLOCK",
        "INSERT_METHOD",
        "DELETE_BLOCK",
        "ADD_GUARD",
    ]
    target_class: str
    old_content: Optional[str] = None
    new_content: str
    reasoning: str


class ReviewDecision(BaseModel):
    approved: bool
    verdict: str
    risk_flags: List[str]
    notes: str


class PatchApplyResult(BaseModel):
    success: bool
    file_path: str
    backup_path: Optional[str] = None
    details: str


class BuildResult(BaseModel):
    success: bool
    stdout: str
    stderr: str


class BacktestResult(BaseModel):
    success: bool
    report: Optional[QuantReport] = None
    raw_output_path: Optional[str] = None
    notes: str


class ComparisonDecision(BaseModel):
    improved: bool
    should_continue: bool
    summary: str
    reasons: List[str]
    next_action: Literal["ACCEPT", "RETRY", "STOP"]