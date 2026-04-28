"""
ML Step 3 — Apply proposed weights to StrategyConfig.cs

Reads weight_proposal.json and patches the constants in StrategyConfig.cs.
Creates a backup before modifying.

Usage: python Analysis/ml/03_apply_weights.py [--dry-run]
"""

import json
import re
import shutil
import sys
from pathlib import Path

ML_DIR    = Path(__file__).resolve().parent
REPO_ROOT = ML_DIR.parent.parent
CONFIG    = REPO_ROOT / "ModularStrategy" / "StrategyConfig.cs"
PROPOSAL  = ML_DIR / "weight_proposal.json"


def main():
    dry_run = "--dry-run" in sys.argv

    if not PROPOSAL.exists():
        print(f"ERROR: {PROPOSAL} not found. Run 02_train_weight_model.py first.")
        sys.exit(1)

    with open(PROPOSAL) as f:
        proposal = json.load(f)

    if not CONFIG.exists():
        print(f"ERROR: {CONFIG} not found.")
        sys.exit(1)

    content = CONFIG.read_text(encoding="utf-8")
    original = content

    changes = []
    for key, info in proposal.items():
        current = info["current"]
        proposed = info["proposed"]
        if current == proposed:
            continue

        # Match pattern: public const int KEY = VALUE;
        pattern = rf"(public\s+const\s+int\s+{key}\s*=\s*){current}(\s*;)"
        match = re.search(pattern, content)
        if match:
            content = re.sub(pattern, rf"\g<1>{proposed}\2", content)
            changes.append((key, current, proposed, info["reason"]))
        else:
            print(f"  WARNING: Could not find {key} = {current} in StrategyConfig.cs")

    if not changes:
        print("[DONE] No weight changes proposed. StrategyConfig.cs unchanged.")
        return

    print(f"\n{'[DRY RUN] ' if dry_run else ''}Weight changes to apply:\n")
    print(f"  {'Key':<30} {'Current':>8} {'Proposed':>8}  Reason")
    print(f"  {'─'*30} {'─'*8} {'─'*8}  {'─'*40}")
    for key, cur, prop, reason in changes:
        print(f"  {key:<30} {cur:>8} {prop:>8}  {reason}")

    if dry_run:
        print(f"\n[DRY RUN] No files modified. Remove --dry-run to apply.")
        return

    # Backup
    backup = CONFIG.with_suffix(".cs.bak")
    shutil.copy2(CONFIG, backup)
    print(f"\n[BACKUP] → {backup}")

    CONFIG.write_text(content, encoding="utf-8")
    print(f"[APPLIED] {len(changes)} weight(s) updated in {CONFIG}")
    print(f"\nNext: re-run backtest and compare results.")


if __name__ == "__main__":
    main()
