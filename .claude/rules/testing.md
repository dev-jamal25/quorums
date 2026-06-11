---
paths:
  - "backend/tests/**/*.cs"
---

# Testing

<!-- Loads when Claude touches the test projects. The two named tests below are the project's acceptance gates. -->

## Shape
- xUnit, Arrange-Act-Assert. Unit tests mock integrations. Integration tests use Testcontainers (Postgres) + `WebApplicationFactory`.
- CI NEVER hits live Meta / Gemini / Anthropic. Use the `IMetaIntegration` mock and fakes. No network in CI.

## The two gates that must always stay green
- **Isolation** — tag `[Trait("Category","Isolation")]`. Seed two brands; assert zero cross-brand leakage across query results, storage key, and token decrypt. This is the multi-tenant contract. Runnable via `dotnet test --filter Category=Isolation`.
- **Durable resume** — checkpoint a run, kill/restart the worker, approve, assert `ResumeRun` completes exactly once with no duplicate asset or double publish.

## Coverage discipline
- Test the RLS interceptor directly: scope is set per transaction and reset after.
- Cover critical paths, not line count. Don't assert on log strings. Don't test framework behavior.
