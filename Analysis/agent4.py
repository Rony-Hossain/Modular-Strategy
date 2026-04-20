# -*- coding: utf-8 -*-
import os, glob, re

def patch_file(filepath):
    with open(filepath, 'r', encoding='utf-8-sig') as f:
        text = f.read()

    modified = False

    # 1. Dual-parse SignalId
    if 'def extract_match_key(signal_id):' in text:
        old_fn = '''def extract_match_key(signal_id):
    """Robust matching: Invariant (SetId, barIndex) handles both yyyyMMdd and yyyyMMdd_HHmmss."""
    if not signal_id or ':' not in signal_id: return None
    parts = signal_id.split(':')
    if len(parts) < 3: return None
    return (parts[0], parts[-1])'''
        new_fn = '''def extract_match_key(signal_id):
    if not signal_id or ':' not in signal_id: return None
    parts = signal_id.split(':')
    if len(parts) == 4: return (parts[0], parts[1], parts[2], parts[3])
    elif len(parts) == 3: return (parts[0], 'Unknown', parts[1], parts[2])
    return None'''
        if old_fn in text:
            text = text.replace(old_fn, new_fn)
            modified = True

    # 2. Add EVAL columns & exclude filtering to CSV reading loops
    # Look for: r = next(reader) or for r in reader:
    # Actually, look for 'elif tag == \'EVAL\':' or similar, or 'reader = csv.DictReader'
    if 'reader = csv.DictReader' in text:
        if 'PARTIAL_ON_EXCEPTION' not in text:
            text = re.sub(
                r'(for r in reader:\s*\n(\s+))(try:\s*\n\s+)?(tag = r\[\'Tag\'\])',
                r'\1\3\4\n\2if r.get("LogStatus") == "PARTIAL_ON_EXCEPTION" or r.get("LabelQuality") == "TRUNCATED_DATA_END": continue\n\2if r.get("LabelQuality") == "TRUNCATED_SESSION": r["Warning"] = "TRUNCATED_SESSION"',
                text
            )
            # Also read new columns in EVAL
            text = re.sub(
                r'(elif tag == \'EVAL\':\s*\n\s+.*?)(signals\[\(bar, cid\)\] = \{)',
                r'\1r["filter_reason"] = r.get("FilterReason"); r["raw_score"] = r.get("RawScore"); r["final_score"] = r.get("FinalScore"); r["confluence_multiplier"] = r.get("ConfMult"); r["rank_score"] = r.get("RankScore"); r["num_candidates_on_bar"] = r.get("NumCands"); r["log_status"] = r.get("LogStatus"); r["label_quality"] = r.get("LabelQuality"); r["entry_timing"] = r.get("EntryTiming"); r["SIM_PNL_OPTIMISTIC"] = r.get("SIM_PNL_OPTIMISTIC"); \n\2',
                text
            )
            modified = True

    if modified:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(text)

files = glob.glob('*.py')
for f in files:
    if f not in ['agent4.py', 'agent2.py', 'agent1.py', 'agent3_fix.py', 'agent2_script.py', 'agent2_host.py', 'best_scenarios.py']:
        try:
            patch_file(f)
        except Exception as e:
            print(f"Failed {f}: {e}")

# Patch best_scenarios.py
bs_path = 'best_scenarios.py'
if os.path.exists(bs_path):
    with open(bs_path, 'r', encoding='utf-8-sig') as f:
        bs = f.read()
    if 'EXPLORATORY — NO STATISTICAL VALIDITY' not in bs:
        bs = bs.replace('def discover_best_scenarios():', 'def discover_best_scenarios():\n    print("\\n======================================================\\nEXPLORATORY — NO STATISTICAL VALIDITY\\n======================================================")')
        with open(bs_path, 'w', encoding='utf-8') as f:
            f.write(bs)

print("Agent 4 patches applied.")
