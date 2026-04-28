import json
import datetime
from pathlib import Path

def main():
    repo_root = Path(".")
    artifacts_dir = repo_root / "Analysis/artifacts"
    
    config_path = artifacts_dir / "config_proposal.json"
    tm_path = artifacts_dir / "trade_mgmt_proposal.json"
    
    final_params = {}
    
    if config_path.exists():
        with open(config_path, 'r') as f:
            final_params.update(json.load(f))
            
    if tm_path.exists():
        with open(tm_path, 'r') as f:
            final_params.update(json.load(f))
            
    final_params['OPT_TIMESTAMP'] = datetime.datetime.now().isoformat()
    
    report_md = artifacts_dir / "final_proposal.md"
    with open(report_md, 'w', encoding='utf-8') as f:
        f.write("# Final Proposal\n\n")
        
        f.write("### 1. Recommended OPTIMIZED block\n")
        f.write("```json\n")
        f.write(json.dumps(final_params, indent=2) + "\n")
        f.write("```\n\n")
        
        f.write("### 2. Rollout checklist\n")
        f.write("- [ ] Run proposal on last 30 days of paper-trading data\n")
        f.write("- [ ] Verify replay match > 95%\n")
        f.write("- [ ] Hand off to dev for StrategyConfig.cs copy\n")
        
    print(f"Generated {report_md}")

if __name__ == '__main__':
    main()
