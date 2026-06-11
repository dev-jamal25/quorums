# Right / Wrong Patterns (.NET)

Concrete C# enforcement examples for the standards in `SKILL.md`. Every "wrong"
here is something that passes a manual smoke test and fails under load, in CI, or
in review. Read the section you're working in.

## Contents
1. Async all the way down
2. Dependency injection + lifetimes
3. Configuration — the Options pattern
4. Boundary validation — DTOs + FluentValidation
5. Errors, resilience & failure isolation
6. Structured logging

---

## 1. Async all the way down

**WRONG — sync-over-async and blocking I/O on the request path:**

```csharp
[HttpPost("runs")]
public IActionResult StartRun(StartRunRequest request)
{
    var brand = _db.Brands.First(b => b.Id == request.BrandId);   // blocking EF
    var resp  = _http.GetAsync(MetaUrl).Result;                   // .Result -> deadlock risk
    Thread.Sleep(200);                                            // blocks a thread-pool thread
    return Ok(brand);
}
```

**RIGHT — async end to end, parallel where independent:**

```csharp
[HttpPost("runs")]
public async Task<IActionResult> StartRunAsync(
    StartRunRequest request, CancellationToken ct)
{
    var brand = await _db.Brands
        .FirstOrDefaultAsync(b => b.Id == request.BrandId, ct);

    // independent calls run concurrently, not one-after-the-other
    var profileTask   = _profiles.LoadAsync(request.BrandId, ct);
    var knowledgeTask = _knowledge.CountAsync(request.BrandId, ct);
    await Task.WhenAll(profileTask, knowledgeTask);
    var profile   = await profileTask;
    var knowledge = await knowledgeTask;

    await Task.Delay(TimeSpan.FromMilliseconds(200), ct); // never Thread.Sleep
    return Accepted();
}
```

Rules:
- Controllers, services, jobs, and tools that touch I/O are `async Task` /
  `async Task<T>`. Flow a `CancellationToken` through.
- Never `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`.
- HTTP comes from `IHttpClientFactory` (see §2), never `new HttpClient()`.
- CPU-bound work (large parsing, in-process inference) goes to a job/`Task.Run`,
  off the request path.

---

## 2. Dependency injection + lifetimes

**WRONG — statics, hand-built clients, locator pattern:**

```csharp
public static class Clients
{
    public static readonly HttpClient Http = new();          // static, untestable
}

public class RunsController : ControllerBase
{
    public async Task<IActionResult> Post()
    {
        var db = new AppDbContext();                          // new-ed in handler
        var meta = ServiceLocator.Get<IMetaIntegration>();    // service locator
        ...
    }
}
```

**RIGHT — constructor injection + explicit lifetimes in `Program.cs`:**

```csharp
public sealed class RunsController(
    AppDbContext db,
    IMetaIntegration meta,
    ILogger<RunsController> log) : ControllerBase
{
    // db, meta, log are injected; the class declares exactly what it touches
}
```

```csharp
// Program.cs
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs)); // Scoped
builder.Services.AddHttpClient<IMediaGenerationTool, GeminiMediaTool>(); // typed client
builder.Services.AddSingleton<IStorageService, MinioStorage>();   // shared client
builder.Services.AddScoped<IBrandContext, BrandContext>();        // per-request
builder.Services.AddScoped<IMetaIntegration, MockMetaIntegration>();
```

Lifetime rule of thumb:
- **Singleton** — HTTP/SDK clients, loaded models, integration clients, config.
- **Scoped** — `DbContext`, request/brand context, per-request services.
- **Transient** — lightweight stateless helpers.

Startup singletons that need warm-up come up through `IHostedService` and dispose
on shutdown — never lazy-loaded inside a request.

---

## 3. Configuration — the Options pattern

**WRONG — scattered raw env reads, silent `null`:**

```csharp
var key = Environment.GetEnvironmentVariable("ANTHROPIC_KEY"); // null if missing
var model = "claude-haiku";                                    // hardcoded in N places
```

**RIGHT — typed options, fail-fast at startup:**

```csharp
public sealed class GenerationOptions
{
    [Required] public string AnthropicKey { get; init; } = default!;
    [Required] public string GeminiKey    { get; init; } = default!;
    public string CheapModel { get; init; } = "claude-haiku";
    public int    MediaBudgetCents { get; init; } = 200;
}

// Program.cs
builder.Services
    .AddOptions<GenerationOptions>()
    .Bind(builder.Configuration.GetSection("Generation"))
    .ValidateDataAnnotations()
    .ValidateOnStart();   // missing/invalid required config => app refuses to boot
```

```csharp
// consume via injection, never re-read the environment
public sealed class CaptionService(IOptions<GenerationOptions> options)
{
    private readonly GenerationOptions _cfg = options.Value;
}
```

The **secrets mechanism** — Vault KV feeding these options, Vault Transit for
per-brand tokens — is owned by the **architecture skill**. Bind from it; do not
restate or re-implement it here. Document every key by name in
`appsettings.Example.json` / `.env.example`; commit no real values.

---

## 4. Boundary validation — DTOs + FluentValidation

**WRONG — defensive checks smeared through the method, entity bound to the wire:**

```csharp
public IActionResult Create(Brand brand) // binds the domain entity directly
{
    if (brand is null) return BadRequest();
    if (string.IsNullOrEmpty(brand.Name)) return BadRequest();
    if (brand.Voice is null) brand.Voice = "";   // guessing at fixes
    ...
}
```

**RIGHT — typed DTO + validator; trust types inside:**

```csharp
public sealed record CreateBrandRequest(string Name, string Brief, string Voice);

public sealed class CreateBrandRequestValidator : AbstractValidator<CreateBrandRequest>
{
    public CreateBrandRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Brief).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.Voice).NotEmpty();
    }
}

[ApiController]                       // invalid model => 400 ProblemDetails automatically
[Route("brands")]
public sealed class BrandsController(IBrandService brands) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> CreateAsync(
        CreateBrandRequest request, CancellationToken ct)
    {
        var brand = await brands.CreateAsync(request, ct); // request already valid
        return CreatedAtAction(nameof(GetAsync), new { id = brand.Id }, brand);
    }
}
```

- Validate **once**, at the edge. The service interior assumes valid, non-null input.
- Nullable reference types ON makes "could be null" a compile-time concern, not a
  runtime guess.
- LLM/tool structured outputs deserialize into typed records and are validated
  before use — no ad-hoc string scraping.

---

## 5. Errors, resilience & failure isolation

### Layer 1 — timeout on every external call

```csharp
builder.Services.AddHttpClient<IMediaGenerationTool, GeminiMediaTool>(c =>
    c.Timeout = TimeSpan.FromSeconds(30));
```

### Layer 2 — Polly retry, transient only

```csharp
builder.Services.AddHttpClient<IMediaGenerationTool, GeminiMediaTool>()
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()                       // 5xx, 408, network
        .WaitAndRetryAsync(3, attempt =>
            TimeSpan.FromSeconds(Math.Pow(2, attempt)))); // 2s, 4s, 8s
```

Never retry a `4xx` — it fails the same way every time.

### Layer 3 — failure isolation inside the agent loop

**WRONG — exception crashes the run:**

```csharp
public async Task<MediaAssetRef> GenerateAsync(CreativeDirection d, CancellationToken ct)
    => await _gemini.GenerateAsync(d, ct); // throws on exhausted retries -> graph dies
```

**RIGHT — structured `ToolError` returned into the loop:**

```csharp
public async Task<Result<MediaAssetRef>> GenerateAsync(
    CreativeDirection d, CancellationToken ct)
{
    try
    {
        var asset = await _gemini.GenerateAsync(d, ct);
        return Result.Ok(asset);
    }
    catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests)
    {
        return Result.Fail(new ToolError("media.rate_limited", ex.Message, Retryable: true));
    }
    catch (TaskCanceledException ex)
    {
        return Result.Fail(new ToolError("media.timeout", ex.Message, Retryable: true));
    }
}
```

The **supervisor** adjudicates the `ToolError` (retry / degrade to caption-only /
fail the item) per the orchestration skill's policy. Exceptions never cross the
graph boundary. Catch **specific** types; let genuinely unexpected exceptions
propagate — never a bare `catch (Exception) { }` swallow.

### HTTP error surface

```csharp
// Program.cs — RFC 7807 ProblemDetails, correct status codes, no leaked internals
builder.Services.AddProblemDetails();
app.UseExceptionHandler();   // maps unhandled -> 500 ProblemDetails, no stack trace
```

Never return `200` with an error body; never expose a stack trace, SQL, file
path, or secret to the caller.

---

## 6. Structured logging

**WRONG — unstructured, leaks data, wrong tool:**

```csharp
Console.WriteLine($"published for {brand.Name} token={token}"); // PII + secret + Console
```

**RIGHT — structured `ILogger` with named fields, no secrets:**

```csharp
public sealed class PublishingService(ILogger<PublishingService> log)
{
    public async Task PublishAsync(Guid runId, Guid brandId, CancellationToken ct)
    {
        log.LogInformation("publish.start {RunId} {BrandId}", runId, brandId);
        try
        {
            // ...
            log.LogInformation("publish.success {RunId}", runId);
        }
        catch (MetaIntegrationException ex)
        {
            log.LogError(ex, "publish.failure {RunId} {BrandId}", runId, brandId);
            throw; // let the caller map it; don't swallow
        }
    }
}
```

- `ILogger<T>` / Serilog with **named structured properties**, never string
  interpolation into the message template.
- Levels: Debug (diagnostics) / Information (events) / Warning (recoverable) /
  Error (operation failed) / Critical (unusable).
- **Never** log tokens, secrets, connection strings, or PII. Log ids
  (`RunId`, `BrandId`, correlation id) so a run is one query away at 3 a.m.
