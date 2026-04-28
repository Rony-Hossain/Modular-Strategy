# Signal Generation Notes

### TL;DR
Net score mismatch is the critical blocker. 16k EVAL orphans suggest silent filtering.

### Critical
1. **Net score mismatch**: StrategyLogger.cs arithmetic logic needs fix.

### High-impact candidates
## [issue-001] [LOGGING] Silent Filtering
**Estimated $ leak:** N/A
**Suggested investigation:** SignalGenerator.cs early returns.

### Tuning candidates
- None currently confident enough to recommend weight changes.
