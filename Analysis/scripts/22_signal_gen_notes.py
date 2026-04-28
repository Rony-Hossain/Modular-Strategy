import pandas as pd
from pathlib import Path

def main():
    repo_root = Path(".")
    artifacts_dir = repo_root / "Analysis/artifacts"
    
    report_md = artifacts_dir / "signal_generation_notes.md"
    with open(report_md, 'w', encoding='utf-8') as f:
        f.write("# Signal Generation Notes\n\n")
        
        f.write("### TL;DR\n")
        f.write("Net score mismatch is the critical blocker. 16k EVAL orphans suggest silent filtering.\n\n")
        
        f.write("### Critical\n")
        f.write("1. **Net score mismatch**: StrategyLogger.cs arithmetic logic needs fix.\n\n")
        
        f.write("### High-impact candidates\n")
        f.write("## [issue-001] [LOGGING] Silent Filtering\n")
        f.write("**Estimated $ leak:** N/A\n")
        f.write("**Suggested investigation:** SignalGenerator.cs early returns.\n\n")
        
        f.write("### Tuning candidates\n")
        f.write("- None currently confident enough to recommend weight changes.\n")
        
    print("Generated signal_generation_notes.md")

if __name__ == '__main__':
    main()
