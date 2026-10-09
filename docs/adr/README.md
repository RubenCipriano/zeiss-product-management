# Architecture Decision Records

Short records of the decisions that shape this codebase: the context, the decision, and what it costs.

| #    | Decision                                                                     |
| ---- | ---------------------------------------------------------------------------- |
| 0001 | [Clean architecture with a CQRS-lite split](0001-clean-architecture.md)      |
| 0002 | [Product identifiers from a PostgreSQL sequence](0002-product-id-generation.md) |
| 0003 | [Atomic, conditional stock updates](0003-atomic-stock-updates.md)            |
| 0004 | [Optimistic concurrency with `xmin`, ETag and If-Match](0004-optimistic-concurrency.md) |
| 0005 | [Result pattern and RFC 9457 problem details](0005-error-handling.md)        |
| 0006 | [PostgreSQL as the database](0006-postgresql.md)                             |
| 0007 | [Frontend state with signals and `rxResource`](0007-frontend-state.md)       |
| 0008 | [Redis read cache with generation-based invalidation](0008-redis-cache.md)  |
| 0009 | [API-issued JWTs for registered consumers, permission-based RBAC](0009-authentication-authorization.md) |
| 0010 | [Rate limiting in two layers](0010-rate-limiting.md)                         |
| 0011 | [Kubernetes deployment, one manifest per project](0011-kubernetes-deployment.md) |
| 0012 | [nginx gateway in front of the application](0012-gateway.md)                 |
| 0013 | [UI with Tailwind CSS and Angular CDK](0013-ui-tailwind-cdk.md)              |
| 0014 | [MCP server for AI assistants](0014-mcp-server.md)                          |
| 0015 | [Load testing with k6, inside the cluster](0015-load-testing.md)            |
