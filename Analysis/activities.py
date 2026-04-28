import os
import subprocess
from temporalio import activity
from google.genai import Client
from models import QuantReport, CodePatch

# --- THE VAULT ---
CLAUDE_TOKENS = {
    "ARCHITECT": "sk-ant-sid-CLAUDE-ACCOUNT-1...",
    "RISK_MANAGER": "sk-ant-sid-CLAUDE-ACCOUNT-2..."
}

GEMINI_KEYS = {
    "DATA_MINER": os.environ.get("GEMINI_KEY_1"),
    "COMPILER": os.environ.get("GEMINI_KEY_2"),
    "FALLBACK": os.environ.get("GEMINI_KEY_3")
}

# --- ACTIVITY: GEMINI DATA MINING ---
@activity.define
async def mine_trade_data(trades_csv: str) -> str:
    """Uses Gemini 1 to parse the raw data into a summarized state."""
    client = Client(api_key=GEMINI_KEYS["DATA_MINER"])
    
    try:
        response = client.models.generate_content(
            model="gemini-2.5-pro",
            contents=[f"Summarize these failures: {trades_csv}"]
        )
        return response.text
    except Exception as e: # Catch rate limits
        activity.logger.warning("Gemini 1 Rate Limited. Swapping to Fallback Key...")
        client_fallback = Client(api_key=GEMINI_KEYS["FALLBACK"])
        response = client_fallback.models.generate_content(...)
        return response.text

# --- ACTIVITY: CLAUDE ARCHITECTURE ---
@activity.define
async def generate_alpha_patch(summary: str) -> str:
    """Uses Claude 1 to write the C# logic."""
    os.environ['CLAUDE_TOKEN'] = CLAUDE_TOKENS["ARCHITECT"]
    
    cmd = ["claude", "prompt", f"Write a C# patch for NinjaTrader based on this summary: {summary}"]
    result = subprocess.run(cmd, capture_output=True, text=True)
    return result.stdout

# --- ACTIVITY: CLAUDE RISK REVIEW ---
@activity.define
async def risk_team_review(proposed_cs_code: str) -> bool:
    """Uses Claude 2 to brutally audit the code."""
    os.environ['CLAUDE_TOKEN'] = CLAUDE_TOKENS["RISK_MANAGER"]
    
    cmd = ["claude", "prompt", f"Audit this code. Reject it if it increases trade frequency over 2 per week. Code: {proposed_cs_code}"]
    result = subprocess.run(cmd, capture_output=True, text=True)
    
    return "APPROVED" in result.stdout.upper()