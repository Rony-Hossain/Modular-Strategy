import os
import re
from pathlib import Path
from google.genai import Client
from dotenv import load_dotenv

load_dotenv()
client = Client(api_key=os.environ.get("GEMINI_API_KEY"))

# Paths
BASE_DIR = Path(r"D:\Ninjatrader-Modular-Startegy")
ANALYSIS_DIR = BASE_DIR / "Analysis"

def generate_blueprint(code_path):
    """Extracts structural signatures. Handles UTF-16/UTF-8 encoding issues."""
    blueprint = []
    if not code_path.exists(): 
        return "Codebase file not found."
    
    # Try UTF-16 first (common for PS redirects), fallback to UTF-8
    try:
        f = open(code_path, 'r', encoding='utf-16')
        lines = f.readlines()
    except (UnicodeDecodeError, UnicodeError):
        f = open(code_path, 'r', encoding='utf-8', errors='ignore')
        lines = f.readlines()
    finally:
        f.close()

    print(f"Successfully read {len(lines)} lines from architecture file.")

    for line in lines:
        # Capture File Headers, Namespaces, Classes, and Methods
        if "// --- FILE:" in line or any(x in line for x in ["namespace ", "class ", "public ", "protected "]):
            # Skip common noise like 'using' statements or property getters
            if "using " in line or "{ get;" in line:
                continue
            
            # Clean the line: remove code after the start of a method
            clean_line = re.sub(r'\{.*', '{', line).strip()
            if clean_line:
                blueprint.append(clean_line)
    
    return "\n".join(blueprint)

# 1. Create the lightweight blueprint
print("Generating codebase blueprint...")
code_blueprint = generate_blueprint(ANALYSIS_DIR / "flattened_strategy_code" / "Flattened_Strategy_Architecture.txt")

# 2. Run the request
print("Sending blueprint to Gemini 3 Flash...")

prompt = f"""
Act as a Senior Quant Architect. I have a 55-file NinjaTrader C# system. 
Below is the ARCHITECTURAL BLUEPRINT (Signatures only).

BLUEPRINT:
{code_blueprint[:900000]} 

TASK:
Write a Python Temporal.io orchestration pipeline:
1. models.py: Pydantic models (QuantReport & CodePatch).
2. activities.py: Skeletons for run_backtest, evaluate_performance, and apply_patch.
3. workflow.py: The main loop (Backtest -> Evaluate -> Fix -> Patch).

Ensure 'activities.py' uses the file paths from the // --- FILE: markers in the blueprint.
"""

try:
    response = client.models.generate_content(
        model="gemini-3-flash-preview",
        contents=prompt
    )

    with open("temporal_architecture_output.txt", "w", encoding="utf-8") as f:
        f.write(response.text)
    
    print("SUCCESS: Architecture generated in temporal_architecture_output.txt")

except Exception as e:
    print(f"FAILED: {e}")