from pydantic import BaseModel, Field
from typing import Dict, Optional
from datetime import datetime

class QuantReport(BaseModel):
    """
    The performance report generated after a NinjaTrader backtest.
    This is passed to Gemini to determine if a fix is needed.
    """
    strategy_id: str
    net_profit: float
    profit_factor: float
    sharpe_ratio: float
    max_drawdown: float
    win_rate: float
    total_trades: int
    session_perf_map: Dict[str, float] = Field(description="Per-Set performance to isolate failing logic")
    timestamp: datetime = Field(default_factory=datetime.now)

class CodePatch(BaseModel):
    """
    The strict instruction set returned by the LLM (or internal logic) 
    dictating exactly how to modify the C# source code.
    """
    file_path: str       # e.g., "StrategyConfig.cs"
    change_type: str     # e.g., "REPLACE_CONSTANT"
    target_class: str    # Used by our Regex as the exact variable name (e.g., "VWAP_REVERSION_SD_THRESHOLD")
    old_content: Optional[str] = None 
    new_content: str     # The new value to inject
    reasoning: str       # The LLM's explanation for the quant audit trail