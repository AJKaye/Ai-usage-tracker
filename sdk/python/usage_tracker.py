"""AI Usage Tracker — thin Python SDK (stdlib only, no dependencies).

Convenience over the tracker's HTTP API so a new surface integrates in a few lines.
The raw /v1 endpoints are always sufficient; this just wraps the common actions.

    from usage_tracker import UsageTracker
    ut = UsageTracker("http://localhost:5000", tenant="acme")
    ut.ingest(provider="anthropic", model="claude-opus-5", input_tokens=1000, output_tokens=500)
    ut.send_usage_event("com.uipath.ai.units", "orchestrator/robot-7",
                        {"provider": "uipath", "granularity": "credit",
                         "units_consumed": 2, "unit_type": "ai_unit"})
    ut.post_score("span-123", "helpfulness", 0.92, source="ragas")
    print(ut.summary())
"""

from __future__ import annotations

import json
import uuid
import urllib.request
import urllib.error
from typing import Any


class UsageTracker:
    def __init__(self, base_url: str, tenant: str | None = None, api_key: str | None = None) -> None:
        self.base_url = base_url.rstrip("/")
        self.tenant = tenant
        self.api_key = api_key

    def _headers(self) -> dict[str, str]:
        h = {"Content-Type": "application/json"}
        if self.tenant:
            h["X-Tenant-Id"] = self.tenant
        if self.api_key:
            h["Authorization"] = f"Bearer {self.api_key}"
        return h

    def _request(self, method: str, path: str, body: dict[str, Any] | None = None) -> Any:
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.base_url + path, data=data, headers=self._headers(), method=method)
        try:
            with urllib.request.urlopen(req) as resp:
                raw = resp.read()
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as e:
            raise RuntimeError(f"{path} -> HTTP {e.code}: {e.read().decode(errors='replace')}") from e

    def ingest(self, provider: str, model: str | None = None, *,
               input_tokens: int | None = None, output_tokens: int | None = None,
               cache_read_input_tokens: int | None = None,
               cache_creation_input_tokens: int | None = None,
               reasoning_output_tokens: int | None = None,
               span_id: str | None = None, trace_id: str | None = None,
               kind: str = "llm") -> Any:
        """Ingest one gen_ai.* usage event (flat-JSON path)."""
        body = {
            "gen_ai.provider.name": provider,
            "gen_ai.response.model": model,
            "gen_ai.usage.input_tokens": input_tokens,
            "gen_ai.usage.output_tokens": output_tokens,
            "gen_ai.usage.cache_read.input_tokens": cache_read_input_tokens,
            "gen_ai.usage.cache_creation.input_tokens": cache_creation_input_tokens,
            "gen_ai.usage.reasoning.output_tokens": reasoning_output_tokens,
            "span_id": span_id,
            "trace_id": trace_id,
            "kind": kind,
        }
        return self._request("POST", "/v1/ingest", {k: v for k, v in body.items() if v is not None})

    def send_usage_event(self, event_type: str, source: str, data: dict[str, Any]) -> Any:
        """Send a coarse usage event (RPA units, seats, premium requests) as a CloudEvent."""
        return self._request("POST", "/v1/events", {
            "specversion": "1.0", "type": event_type, "source": source,
            "id": str(uuid.uuid4()), "data": data,
        })

    def post_score(self, target_id: str, name: str,
                   value: float | str | bool, source: str | None = None) -> Any:
        """Attach an externally-computed eval score to a span/trace."""
        body: dict[str, Any] = {"target_id": target_id, "name": name, "source": source}
        if isinstance(value, bool):
            body["boolean"] = value
        elif isinstance(value, (int, float)):
            body["numeric"] = value
        else:
            body["category"] = value
        return self._request("POST", "/v1/scores", body)

    def summary(self) -> dict[str, Any]:
        """The rolled-up usage/cost summary for the tenant."""
        return self._request("GET", "/v1/summary")
