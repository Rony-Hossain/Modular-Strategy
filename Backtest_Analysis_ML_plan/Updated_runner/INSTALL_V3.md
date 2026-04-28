# INSTALL_V3 — Production drop for the autonomous ML pipeline

This bundle implements all six optimizations we discussed:

1. **API-based Claude runner** with prompt caching (no more CLI subprocess)
2. **Gemini-drafts → Claude-edits planning** (your idea, ~70% cheaper than full Claude planning)
3. **Local pre-validation gate** — catches the [INPUT]-line bug + 6 other failure modes before Claude review burns tokens
4. **Smart insights filtering** — relevance-scored, not chronological
5. **`skip_planning` flag** on bug-hunt phases (templated audits don't need the brain)
6. **`skip_review_if_short`** — short scripts that pass local validation skip Claude review

Plus the role registry + sandbox-disabled worker from earlier in the session.

---

## Step 1 — env vars

Open `.env` in the orchestrator dir. Add or update:

```
ANTHROPIC_API_KEY=sk-ant-api03-...
ANTHROPIC_MODEL=claude-sonnet-4-6        # default; roles.yaml can override per role
GEMINI_API_KEY=...                       # Tier 1 paid recommended for full runs
```

Get the Anthropic key at **console.anthropic.com → Settings → API Keys**. Set a $20 spend cap on the account ("Limits" tab) so a runaway loop can't bill you out.

For Gemini, **upgrade to Tier 1 paid** at aistudio.google.com. Free tier is too restrictive for `gemini-2.5-pro` — you'll hit the 50/day cap on a single full pipeline run.

Verify in PowerShell:
```powershell
cd D:\Ninjatrader-Modular-Startegy\quant_orchestrator
.\venv\Scripts\activate
python -c "import os; from dotenv import load_dotenv; load_dotenv(); print('ANTHROPIC:', os.getenv('ANTHROPIC_API_KEY','MISSING')[:20]); print('GEMINI:', os.getenv('GEMINI_API_KEY','MISSING')[:20])"
```

You should see the first 20 chars of each. If either says `MISSING`, the .env line is missing or has a typo.

---

## Step 2 — install the anthropic SDK

```powershell
cd D:\Ninjatrader-Modular-Startegy\quant_orchestrator
.\venv\Scripts\activate
pip install anthropic
```

Verify:
```powershell
python -c "import anthropic; print(anthropic.__version__)"
```

Should print 0.30+ or similar.

---

## Step 3 — drop the v3 files

Files in this bundle, with their target paths:

| Drop file              | Target path                                                                |
|------------------------|----------------------------------------------------------------------------|
| `base_runner.py`       | `quant_orchestrator/app/base_runner.py`                                    |
| `claude_runner.py`     | `quant_orchestrator/app/claude_runner.py` (replaces CLI version)           |
| `gemini_runner.py`     | `quant_orchestrator/app/gemini_runner.py`                                  |
| `mock_runner.py`       | `quant_orchestrator/app/mock_runner.py`                                    |
| `roles.py`             | `quant_orchestrator/app/roles.py`                                          |
| `roles.yaml`           | `quant_orchestrator/app/roles.yaml`                                        |
| `script_validator.py`  | `quant_orchestrator/app/script_validator.py` **(NEW)**                     |
| `insights_filter.py`   | `quant_orchestrator/app/insights_filter.py` **(NEW)**                      |
| `ml_models.py`         | `quant_orchestrator/app/ml_models.py`                                      |
| `ml_activities.py`     | `quant_orchestrator/app/ml_activities.py`                                  |
| `ml_workflow.py`       | `quant_orchestrator/app/ml_workflow.py`                                    |
| `main_worker.py`       | `quant_orchestrator/app/main_worker.py`                                    |

---

## Step 4 — verify the install

After copying, run these PowerShell checks. **Don't proceed until all return as expected.**

```powershell
# 1. New files exist
Test-Path D:\Ninjatrader-Modular-Startegy\quant_orchestrator\app\script_validator.py
Test-Path D:\Ninjatrader-Modular-Startegy\quant_orchestrator\app\insights_filter.py
# Both should be True

# 2. main_worker has the v3 banner
Select-String -Path D:\Ninjatrader-Modular-Startegy\quant_orchestrator\app\main_worker.py -Pattern "v3|UnsandboxedWorkflowRunner|fast_plan_ml_phase"
# Should return at least 4 hits

# 3. roles.yaml uses the new model IDs
Select-String -Path D:\Ninjatrader-Modular-Startegy\quant_orchestrator\app\roles.yaml -Pattern "claude-sonnet-4-6|claude-opus-4-7|gemini-2.5-pro"
# Should return at least 5 hits

# 4. ml_activities has the new fast_plan + validate
Select-String -Path D:\Ninjatrader-Modular-Startegy\quant_orchestrator\app\ml_activities.py -Pattern "fast_plan_ml_phase|validate_generated_script|_gemini_draft_plan"
# Should return at least 6 hits

# 5. ml_workflow has the validator gate logic
Select-String -Path D:\Ninjatrader-Modular-Startegy\quant_orchestrator\app\ml_workflow.py -Pattern "validate_generated_script|skip_planning|skip_review_if_short"
# Should return at least 4 hits

# 6. claude_runner is API-based (not CLI)
Select-String -Path D:\Ninjatrader-Modular-Startegy\quant_orchestrator\app\claude_runner.py -Pattern "AsyncAnthropic|cache_control|tool_use"
# Should return at least 3 hits — proves it's the API version
```

If any check fails, that file isn't installed correctly. Re-copy it.

---

## Step 5 — terminate stale workflows

Open `http://localhost:8233` (Temporal UI). Workflows tab → terminate every "Running" workflow. They're stale from yesterday and will keep retrying when the worker comes up if you don't kill them.

---

## Step 6 — start the worker

```powershell
cd D:\Ninjatrader-Modular-Startegy\quant_orchestrator
.\venv\Scripts\activate
python -u -m app.main_worker
```

You should see this banner — if you don't, the new `main_worker.py` isn't in place:

```
============================================================
ML Pipeline Worker — starting (v3)
  Task queue:        quant-task-queue
  Sandbox:           DISABLED (UnsandboxedWorkflowRunner)
  Data converter:    pydantic_data_converter
  Health check:      provider_health_check (multi-provider)
  Planner mode:      Gemini drafts → Claude edits
  Local validator:   ENABLED (script_validator)
============================================================
Waiting for workflows... (Ctrl+C to stop)
```

---

## Step 7 — first run

In a second terminal:

```powershell
cd D:\Ninjatrader-Modular-Startegy\quant_orchestrator
.\venv\Scripts\activate
python run_ml_pipeline.py --phases 25 --force
```

**Run this exactly once.** Multiple runs queue multiple workflows, like yesterday.

In the worker terminal you should see:

```
[HEALTH_CHECK] gemini=True | claude=True
[PHASE 25] START — 25_zone_hygiene.py
[PLAN-FAST] 25 — templated planning for 25_zone_hygiene.py     ← skip_planning short-circuit
[PLAN-FAST] 25 — templated prompt ready (~2500 chars)
[WRITE] 25 — Gemini writing 25_zone_hygiene.py
[WRITE] 25 — DONE (~60s) — Script written
                                                                ← validator gate runs silently
                                                                ← if validation fails: WRITE retries with feedback
                                                                ← if validation passes + skip_review_if_short: review skipped
[RUN] 25_zone_hygiene.py — DONE — SUCCESS — N artifacts
[EVALUATE] 25 — [claude] evaluating 25_zone_hygiene.py output
[EVALUATE] 25 — DONE (~60s) — PASSED [HIGH/MEDIUM]: <findings>
```

For Phase 25 specifically (skip_planning + skip_review_if_short are both ON), the log won't show the Claude editor or Claude reviewer. It WILL show Claude on the evaluator since that's not skipped.

---

## Step 8 — what to watch for

**Good signals:**
- Banner shows v3
- Health check shows BOTH `gemini=True` AND `claude=True`
- Phase 25 shows `[PLAN-FAST]`, not `[PLAN]` (proves skip_planning works)
- `[VALIDATOR]` rejection messages followed by `WRITE` retry (proves the gate works)
- Claude calls log as `[claude]` with model ID

**Bad signals:**
- `[FALLBACK]` on Claude — usually means API key is wrong/exhausted
- `429` errors — Gemini quota cap, upgrade to Tier 1 paid
- `RestrictedWorkflowAccessError: __builtins__.open` — sandbox wasn't disabled, main_worker.py didn't update
- No banner — the new main_worker.py didn't install

---

## Cost expectations

A full 11-phase run on this config:

- 7 phases use the full Gemini-drafts/Claude-edits planner (Sonnet 4.6): ~$0.10/phase
- 4 phases use fast_plan + skip review (no Claude calls): ~$0.005/phase
- Reviewer (Sonnet 4.6, all 7 non-skip phases): ~$0.05/phase
- Evaluator (Sonnet 4.6, all 11 phases): ~$0.04/phase
- Proposal builder (Opus 4.7 with thinking, runs once): ~$0.50

**Estimated total per full run: $1.50 - $2.50**

The first run will be at the high end since prompt caching writes the cache. Subsequent runs within 5 minutes hit cached prefixes and drop ~30%.

---

## What to flip later

Things tuned conservatively for tonight that you may want to revisit:

1. **`planner.thinking: false`** in roles.yaml. Setting `true` adds extended thinking on the editor critique — catches subtler issues, costs ~2x. Worth trying once you've validated the basic flow works.

2. **`skip_review_if_short` length threshold**. Currently 4000 chars (~80-100 lines). If you find short scripts slipping through with bugs the local validator missed, lower it.

3. **Insights filter `max_chars`**. Default 3000 in `claude_plan_ml_phase`. Drop to 2000 for tighter context, raise to 5000 if Claude is missing important prior findings.

4. **Reviewer fallback config**. None set right now. If you want graceful degradation when Claude is down, add a `fallback: { provider: gemini, model: gemini-2.5-pro }` block to the reviewer in roles.yaml.

5. **Claude model on reviewer**. Currently Sonnet 4.6. If reviewer accuracy disappoints (approves bad scripts), bump to `claude-opus-4-7` — significantly more expensive but the most load-bearing call.

---

## If something breaks

Paste me the worker terminal output starting from the banner. The v3 logging is verbose enough that I can usually diagnose from a single failure.

Common issues and their causes:

| Symptom                          | Likely cause                             |
|----------------------------------|------------------------------------------|
| `ANTHROPIC_API_KEY not set`      | .env not loaded; check working dir       |
| `Restricted...__builtins__.open` | main_worker.py wasn't replaced           |
| `[FALLBACK] role=planner...`     | Either Claude or Gemini Pro is unhappy   |
| Validator never approves         | Gemini struggling with the format spec   |
| `429 RESOURCE_EXHAUSTED` (Gemini)| Free tier; upgrade to Tier 1             |
| `429 rate_limit_error` (Anthropic)| Spend cap or org rate limit, check console |
