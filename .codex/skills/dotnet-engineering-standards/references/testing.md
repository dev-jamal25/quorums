# Testing (.NET)

The C# translation of the bootcamp testing standard. **xUnit**, not pytest.
Critical-path tests that run in seconds and fail loudly — not coverage theater.

## Contents
1. What to test (three tiers)
2. Tier 1 — validators
3. Tier 2 — services with mocked integrations
4. Tier 3 — one happy path end-to-end
5. Conventions
6. Coverage stance

---

## 1. What to test (three tiers)

| Tier | Target | Tooling |
|------|--------|---------|
| 1 | DTO / FluentValidation validators (valid + invalid) | xUnit |
| 2 | Services & agent tools, external calls mocked | xUnit + a mocking lib (e.g. NSubstitute / Moq) |
| 3 | One happy path through the whole API | xUnit + `WebApplicationFactory` + Testcontainers (Postgres) |

CI runs **on mocks only** — no live Meta, Gemini, or embedding calls.

---

## 2. Tier 1 — validators (cheap, high value)

If a tool/DTO validator is wrong, the whole agent is wrong. Test both directions.

```csharp
public sealed class CreateBrandRequestValidatorTests
{
    private readonly CreateBrandRequestValidator _validator = new();

    [Fact]
    public void Rejects_empty_name()
    {
        var result = _validator.Validate(new CreateBrandRequest("", "brief", "voice"));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Accepts_valid_request()
    {
        var result = _validator.Validate(
            new CreateBrandRequest("Roaster Co", "specialty coffee", "warm, direct"));
        Assert.True(result.IsValid);
    }
}
```

---

## 3. Tier 2 — services with mocked integrations

Test the logic without hitting the network. Cover the happy path **and** the
structured-error path.

```csharp
public sealed class MediaToolTests
{
    [Fact]
    public async Task Returns_tool_error_on_timeout()
    {
        var gemini = Substitute.For<IGeminiClient>();
        gemini.GenerateAsync(Arg.Any<CreativeDirection>(), Arg.Any<CancellationToken>())
              .Returns<Task<MediaAssetRef>>(_ => throw new TaskCanceledException());

        var tool = new GeminiMediaTool(gemini);

        var result = await tool.GenerateAsync(SampleDirection(), CancellationToken.None);

        Assert.True(result.IsFailed);
        Assert.True(result.Error.Retryable);          // degrade, don't crash
    }
}
```

Mock Meta, Gemini, and embeddings behind their interfaces
(`IMetaIntegration`, `IMediaGenerationTool`, `IEmbeddingProvider`).

---

## 4. Tier 3 — one happy path end-to-end

`WebApplicationFactory` boots the API in-memory; Testcontainers gives a real
throwaway Postgres so RLS and migrations are exercised for real.

```csharp
public sealed class RunHappyPathTests
    : IClassFixture<PostgresWebAppFactory>   // spins up a Postgres container
{
    private readonly HttpClient _client;
    public RunHappyPathTests(PostgresWebAppFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Onboard_run_approve_completes()
    {
        var brand = await _client.PostAsJsonAsync("/brands",
            new CreateBrandRequest("Roaster Co", "specialty coffee", "warm"));
        brand.EnsureSuccessStatusCode();

        // start run -> 202, approve -> resume -> done, all integrations mocked
        // assert AgentRun reaches Done and a ContentItem exists
    }
}
```

The two-brand **RLS leakage test** (seed two brands, prove no cross-brand read,
storage-prefix cross, or token-decrypt cross) lives with the isolation work owned
by the architecture skill — this skill requires that it **exists and runs in CI**.

---

## 5. Conventions

- **Arrange–Act–Assert** in every test; one behavior per test where practical.
- Test names describe behavior: `Rejects_empty_name`,
  `Returns_tool_error_on_timeout`.
- Test classes `*Tests`; fixtures named for what they provide
  (`PostgresWebAppFactory`, `SampleBrand`).
- Cover **error paths**, not just happy paths: invalid input raises the right
  validation failure; transient failure yields a retryable `ToolError`.
- Don't test framework internals or pass-through wrappers; test business logic,
  boundaries, and failure handling.

---

## 6. Coverage stance

- Aim for meaningful coverage of **critical paths** (onboarding, the run loop,
  approval/publish, isolation), not a global percentage.
- A test that does not run in CI does not exist — wire it into the workflow in
  `references/ci-and-precommit.md`.
