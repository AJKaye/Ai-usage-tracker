# AI Usage Tracker — Python SDK

A thin, **stdlib-only** client (no `pip install` needed — uses `urllib`). The raw
`/v1` endpoints are always sufficient; this wraps the common onboarding actions.

```python
from usage_tracker import UsageTracker

ut = UsageTracker("http://localhost:5000", tenant="acme")

# 1. Ingest a model call's usage
ut.ingest(provider="anthropic", model="claude-opus-5", input_tokens=1000, output_tokens=500)

# 2. Send a coarse usage event (e.g. an RPA "AI unit")
ut.send_usage_event("com.uipath.ai.units", "orchestrator/robot-7",
                    {"provider": "uipath", "granularity": "credit",
                     "units_consumed": 2, "unit_type": "ai_unit"})

# 3. Attach an eval score
ut.post_score("span-123", "helpfulness", 0.92, source="ragas")

# 4. Read the rolled-up spend
print(ut.summary())
```

**Auth:** pass `tenant=` for dev/self-host, or `api_key=` for authenticated/SaaS
deployments (the key resolves the tenant server-side).

For deep tracing, point any OpenTelemetry `gen_ai.*` exporter at `POST /v1/traces` —
no SDK needed (see `docs/integrations/`).
