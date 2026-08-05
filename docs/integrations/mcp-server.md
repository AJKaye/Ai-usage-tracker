# MCP Server Face

The AI Usage Tracker is itself an **MCP (Model Context Protocol) server**, so an
agent can query its own AI spend live — as MCP *tools* and *resources* — over the
same data the dashboards use (ARCHITECTURE.md §7.1).

- **Endpoint:** `POST /mcp` (JSON-RPC 2.0, protocol `2025-06-18`)
- **Auth / tenancy:** send `X-Tenant-Id` (dev) or `Authorization: Bearer <key>` (SaaS).
  The tenant is resolved server-side; a client cannot query another tenant.

## Handshake

```json
POST /mcp
{ "jsonrpc":"2.0", "id":1, "method":"initialize" }
→ { "result": { "protocolVersion":"2025-06-18",
                "capabilities": { "tools":{}, "resources":{} },
                "serverInfo": { "name":"ai-usage-tracker", "version":"0.1.0" } } }
```

## Tools (`tools/list`, `tools/call`)

| Tool | Returns (structuredContent) |
|------|------------------------------|
| `usage_summary` | spanCount, totalInputTokens, totalOutputTokens, totalEstimatedCost, currency |
| `cost_by_provider` | costByProvider map + currency |
| `recent_spans` | recent spans (arg: `limit`, default 20) |

Each tool advertises an `inputSchema` **and** an `outputSchema`, so a typed client
consumes `structuredContent` directly.

```json
POST /mcp
{ "jsonrpc":"2.0", "id":2, "method":"tools/call",
  "params": { "name":"usage_summary", "arguments":{} } }
→ { "result": {
      "content": [ { "type":"text", "text":"12 events, 0.4175 USD estimated." } ],
      "structuredContent": { "spanCount":12, "totalEstimatedCost":0.4175, "currency":"USD" },
      "isError": false } }
```

## Resources (`resources/list`, `resources/read`)

| URI | Content |
|-----|---------|
| `usage://recent-spans` | the tenant's most recent spans as JSON |

```json
{ "jsonrpc":"2.0", "id":3, "method":"resources/read",
  "params": { "uri":"usage://recent-spans" } }
```

## Errors

Standard JSON-RPC: unknown method → `-32601`; bad tool/resource params → `-32602`.
Notifications (e.g. `notifications/initialized`) get no response.

## Connecting an MCP client

Point any MCP-over-HTTP client at `https://<tracker>/mcp` with the tenant/auth header.
The server is a thin adapter over the query layer — the same numbers the dashboards
and `/v1/summary` show.
