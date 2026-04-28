# Install Notes — Role Registry / Multi-Provider Pipeline

## What this delivers

A role-based LLM provider system. Activities ask for `get_runner('planner')`
and get back whatever's configured in `roles.yaml`. Swap providers without
touching code.

Tonight's config: every role uses Gemini (Claude is bypassed due to quota).
To restore Claude later, edit `roles.yaml` — no code changes needed.

## Files to drop into `app/` (10 files)

**New files:**
- `base_runner.py` — abstract LLMRunner interface
- `mock_runner.py` — for offline tests
- `roles.py` — registry / factory / fallback wrapper
- `roles.yaml` — provider config (live operational knob)

**Replacements:**
- `claude_runner.py` — now subclass of LLMRunner; better error messages
- `gemini_runner.py` — now implements the full LLMRunner interface;
  uses `thinking_level` (Gemini 3.x) or `thinking_budget` (Gemini 2.x)
- `activities.py` — adds `provider_health_check` activity
- `ml_activities.py` — every ClaudeRunner/_gemini call replaced with
  `get_runner(role)`
- `ml_workflow.py` — health check now uses `provider_health_check`
- `main_worker.py` — registers the new activity

The workflow code itself (priority queue, parallel dispatch, micro-phases,
proposal generation) is unchanged. The provider layer is below activities;
the workflow doesn't know any of this exists.

## Install

```powershell
cd D:\Ninjatrader-Modular-Startegy\quant_orchestrator
.\venv\Scripts\activate

# Install pyyaml (the registry reads roles.yaml)
pip install pyyaml

# Make sure pyarrow is installed (still needed for parquet sampling)
pip install pyarrow pandas
```

Drop the 10 files into `app/`. The `roles.yaml` goes there too — same
directory as `roles.py` (the loader uses its sibling by default).

## Set up Gemini API key

The runner reads `GEMINI_API_KEY` from environment first, then falls back
to `Settings.GEMINI_KEYS["COMPILER"]` (your existing config). Either works.

If you want the new env var explicitly:
```
# in your .env
GEMINI_API_KEY=your-api-key-here
```

If you'd rather keep using your existing `GEMINI_KEY_2` (which is
`Settings.GEMINI_KEYS["COMPILER"]`), no change needed.

## Run

Three terminals as before:
```powershell
# Terminal 1
temporal server start-dev

# Terminal 2
python -u -m app.main_worker

# Terminal 3
python run_ml_pipeline.py --phases 25 --force
```

You should see in terminal 2 (the worker):

```
[HEALTH_CHECK] gemini=True
[PHASE 25] START — 25_zone_hygiene.py
[PLAN] 25 — [gemini] planning 25_zone_hygiene.py
[PLAN] 25 — DONE (XXs) — Prompt built ...
[WRITE] 25 — Gemini writing 25_zone_hygiene.py
[WRITE] 25 — DONE (XXs) — Script written (...)
[REVIEW] 25 — [gemini] reviewing 25_zone_hygiene.py (XXX chars)
[REVIEW] 25 — DONE (XXs) — APPROVED: ...
[RUN] 25 — Saving and executing 25_zone_hygiene.py
[RUN] 25 — DONE (XXs) — SUCCESS — N artifacts
[EVALUATE] 25 — [gemini] evaluating 25_zone_hygiene.py output
[EVALUATE] 25 — DONE (XXs) — PASSED [HIGH]: ...
[PHASE 25] completed
```

Note the `[gemini]` tags in the log — that's how you can verify which
provider actually ran the call. After Claude quota returns and you flip
those roles to claude in roles.yaml, those tags will say `[claude]`.

## Swap to Claude when quota returns

Edit `roles.yaml`. For each role you want on Claude, replace the gemini
block with:

```yaml
planner:
  provider: claude
  timeout_seconds: 420
```

Restart the worker. Done. No code changes.

## Disable a role to fail-fast in tests

```yaml
proposal_builder:
  enabled: false
  # rest of config ignored when disabled
```

Calls to `get_runner('proposal_builder')` will raise immediately with a
clear error — useful for forcing the workflow to skip the proposal stage
or for running partial pipelines.

## Key debugging fact

The worker log lines now include `[provider_name]` for every Claude-or-
Gemini-bound activity:

```
[PLAN] 25 — [gemini] planning 25_zone_hygiene.py
[REVIEW] 25 — [gemini] reviewing ... (with `[gemini->fallback(gemini)]`
                                       if a fallback wrapper is in play)
```

If something fails, this immediately tells you which provider was called
without diving into the JSONL log. The fallback wrapper also logs to
stdout when it triggers a fallback so you'll see:

```
[FALLBACK] role=planner primary=gemini failed: RuntimeError: ... — trying fallback
```

## What I didn't change

- `ml_workflow.py` scheduler logic (priority queue, parallel groups,
  micro-phases) is unchanged.
- The QuantRepairWorkflowV2 (your C# repair loop) still uses
  `claude_health_check` and `ClaudeRunner` directly. That workflow is
  hard-wired to Claude on purpose — it doesn't need the role system.
  When you're ready, we can migrate it too.
- `pipeline_logger.py` is unchanged.
- `ml_models.py` is unchanged.
- `gemini-3.1-pro-preview` is the configured model for thinking roles,
  per Google's current docs. If you want to use a different model just
  change the `model:` line in roles.yaml.
