# Quickstart — download → run → works

The zero-infra path. No .NET, no Docker, no database, no admin.

1. **Download** the single file for your platform from the release (or CI artifacts):
   `usage-tracker` (`.exe` on Windows) for win/linux/macOS × x64/arm64.
2. **Verify** (optional but recommended): `sha256sum -c SHA256SUMS`; if a
   `SHA256SUMS.sig` is present, verify it against the published release public key.
3. **Run it:**
   ```bash
   ./usage-tracker            # Windows: double-click, or  usage-tracker.exe
   ```
   In `solo` mode it opens the dashboard in an app-mode window at
   `http://127.0.0.1:5000` (set `USAGETRACKER__NO_WINDOW=1` to run headless and just
   browse there). Data persists to `usage-tracker.db` next to the exe.
4. **Send it some usage** — open the **Integrations** tab in the UI and copy a snippet,
   or:
   ```bash
   curl -X POST http://127.0.0.1:5000/v1/ingest -H 'Content-Type: application/json' \
     -H 'X-Tenant-Id: demo' \
     -d '{"gen_ai.provider.name":"anthropic","gen_ai.response.model":"claude-opus-5",
          "gen_ai.usage.input_tokens":1000,"gen_ai.usage.output_tokens":500,"kind":"llm"}'
   ```
5. **See it:** refresh the dashboard — spend, allocation, efficiency, governance.

Back up any time with `GET /v1/export` (a portable JSON bundle); restore with
`POST /v1/import`. That same export is how you migrate to the server tier later.

Platform health/uptime/throughput: `GET /v1/platform/stats`.
