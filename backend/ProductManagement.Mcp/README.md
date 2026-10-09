# ProductManagement.Mcp

An [MCP](https://modelcontextprotocol.io) server that lets AI assistants (Claude Code, Claude Desktop, VS Code
Copilot, ...) work with the Product Management system: search the catalogue, find low-stock products and record
stock movements. Generated with `dotnet new mcpserver` (official C# SDK) and runs over **stdio**.

## Tools

| Tool                           | What it does                                              | Changes data |
| ------------------------------ | --------------------------------------------------------- | ------------ |
| `search_products`              | Products whose name contains a text (case-insensitive)    | no           |
| `get_product`                  | One product by id, with current stock                     | no           |
| `list_products`                | All products, paged                                       | no           |
| `list_products_by_stock_level` | Products with stock in a range, lowest first              | no           |
| `list_low_stock`               | Products with stock ≤ 10 (restocking candidates)          | no           |
| `list_categories`              | Product categories                                        | no           |
| `add_stock`                    | Record a delivery (adds units)                            | yes          |
| `remove_stock`                 | Record a sale/write-off; refused if not enough stock      | yes          |

Tools carry MCP annotations (`readOnly`, `idempotent`, ...) so clients can ask for confirmation before changes.

## Security model

The server is a **client of the REST API**, never of the database. It signs in to Keycloak with its own identity
(OAuth 2.0 client credentials, client `product-management-mcp`) whose service account has only the `stock-clerk`
role. An assistant can therefore read the catalogue and move stock, but cannot create, edit or delete products, and
every call is subject to the API's validation, RBAC, rate limits and audit logs, exactly like any other client.

## Run

With the cluster running and the gateway forwarded to `localhost:8080`, the repository's `.mcp.json` (Claude Code)
and `.vscode/mcp.json` (VS Code) already register it. Manually:

```bash
ProductApi__ClientSecret=mcp-local-dev-secret dotnet run --project backend/ProductManagement.Mcp
```

| Setting (environment variable) | Default                                                                       |
| ------------------------------ | ----------------------------------------------------------------------------- |
| `ProductApi__BaseUrl`          | `http://localhost:8080/`                                                      |
| `ProductApi__TokenEndpoint`    | `http://localhost:8080/auth/realms/product-management/protocol/openid-connect/token` |
| `ProductApi__ClientId`         | `product-management-mcp`                                                      |
| `ProductApi__ClientSecret`     | *(required)*                                                                  |
