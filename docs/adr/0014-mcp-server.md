# 0014 — MCP server for AI assistants

**Status:** accepted

## Context

AI assistants (Claude, Copilot) can operate systems through the Model Context Protocol. Exposing the product
catalogue lets an assistant answer "what is running low?" or record a delivery, but an AI client must not bypass the
rules that apply to humans.

## Decision

* `backend/ProductManagement.Mcp`, generated with `dotnet new mcpserver` (official C# SDK), **stdio** transport so
  Claude Code, Claude Desktop and VS Code can launch it locally (`.mcp.json`, `.vscode/mcp.json`).
* It is a **client of the REST API, not of the database**. Every tool call goes through the gateway, authentication,
  RBAC, validation, rate limits and the audit trail, exactly like the UI.
* It authenticates as **its own service account** (client credentials) with only the `stock-clerk` role. Tokens are
  cached until 30 s before expiry, and concurrent tool calls share one token request.
* Eight tools with descriptions written for a model to read, and MCP annotations (`readOnly`, `idempotent`,
  `destructive`) so clients can ask the user before changes. API errors become tool errors carrying the API's
  message and `errorCode`, e.g. "only 3 available [product.insufficient_stock]", so the model can react.
* It has its own response records rather than referencing the API's assemblies: it depends only on the HTTP
  contract and could live in another repository.

`.mcp.json` also registers development MCP servers: Kubernetes (read-only), PostgreSQL (restricted mode) and
Playwright.

## Consequences

* An assistant can never do more than a stock clerk. Giving it more power is a role change in Keycloak, not a code
  change.
* For remote use (a shared MCP endpoint), the template's `--transport remote` variant would host it over HTTP with
  OAuth on behalf of the user, instead of a shared service account.
