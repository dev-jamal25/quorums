# MAF Supervised Graph (Deterministic Stubs) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Honour `dotnet-engineering-standards` and `agent-orchestration-graph` skills throughout. Apply TDD (superpowers:test-driven-development) on every node.

**Goal:** Replace `StubOrchestrator`'s mechanism with a real Microsoft Agent Framework 1.0 supervised graph whose nodes are still deterministic stubs, and prove the existing c1/c2 durable seam (checkpoint-and-exit at the human gate → resume in a fresh job) survives the real MAF graph with no double-publish and no duplicated side effects.

**Architecture:** Keep the `IOrchestrator` seam (`RunGenerationAsync` / `RunPublishAsync`) and the two Hangfire jobs (`ExecuteRunJob` / `ResumeRunJob`) **unchanged** — they already own the durable checkpoint/exit/resume. Introduce `MafOrchestrator : IOrchestrator` (same 3-arg constructor as `StubOrchestrator`) that builds and runs a MAF `Workflow` per segment. Each agent becomes a deterministic MAF `Executor` node that returns a canned, schema-valid typed contract and records the same trace span as today. The durable human wait stays in the `AgentRun` state machine; MAF runs only intra-segment (DL-018 / DL-015).

**Tech Stack:** .NET 10, Microsoft Agent Framework 1.0 (`Microsoft.Agents.AI.Workflows`), EF Core 9 / Npgsql, Hangfire on Postgres, xUnit + Testcontainers.

---

## Context (The Why)

The current orchestrator ([backend/src/Infrastructure/Orchestration/StubOrchestrator.cs](backend/src/Infrastructure/Orchestration/StubOrchestrator.cs)) is a single deterministic class that produces fixed typed outputs and does real I/O (MinIO put, mock Meta publish) behind swappable interfaces. The c1/c2 slices proved the **durable seam**: `ExecuteRunJob` runs generation → checkpoints `RunState` to `RunCheckpoint` → sets `AwaitingApproval` → exits; approval enqueues `ResumeRunJob` which rehydrates and publishes → `Done`. Idempotency holds because the MinIO key is derived from `assetId` and the publish ref from `contentItemId`.

DL-017/DL-018 freeze the orchestration as a **Claude-supervised MAF graph**, with the explicit instruction (DL-018) to **validate, not assume** that MAF 1.0 can checkpoint-and-exit cleanly at the gate. This slice swaps the stub *mechanism* for the real MAF graph while keeping every node a deterministic stub — no LLM, no RAG, no Gemini, no cost model. The thing being proven is that **a real MAF graph slots behind the same seam and the durable round-trip still holds**. If MAF's persistence assumed in-process continuation, the banked DL-015 fallback (the state machine wraps MAF) applies — and in fact this design uses that fallback by construction: MAF never owns the durable wait.

**Intended outcome:** `MafOrchestrator` replaces `StubOrchestrator` as the registered `IOrchestrator`; all existing Durability/Trace/Publish/Isolation tests stay green now running *through* the MAF graph; one new adversarial test proves checkpoint→exit→resume round-trips with no double side effects. All CLAUDE.md gates stay green.

## The MAF seam decision (the DL-018 thing to validate)

**Path taken (by choice, not by force): the DL-015/DL-018 fallback — `AgentRun` state machine wraps MAF.** MAF 1.0 *does* ship native workflow checkpointing and human-in-the-loop support; we deliberately do **not** use them for the durable wait, because the c1/c2 seam (Postgres `RunCheckpoint` + `ExecuteRun`/`ResumeRun` jobs) is already proven and idempotent. Keeping the seam where it is means MAF's persistence model is irrelevant to durability — MAF runs only intra-segment, returns a `RunState`, and holds nothing across the gate.

- MAF runs the **generation** graph to its terminal `assembly` node and returns a `RunState` (`Phase = AwaitingApproval`). `ExecuteRunJob` then checkpoints and exits — exactly as today. MAF holds nothing across the gate.
- MAF runs the **publish** graph to its terminal `publishing` node and returns a `RunState` (`Phase = Done`). `ResumeRunJob` is a fresh process/segment that rehydrates `RunState` from `RunCheckpoint` and runs that graph.
- We do **not** use MAF's own checkpoint/human-in-the-loop request ports for the durable wait. So MAF's persistence model is irrelevant to the seam; the seam is owned by Postgres + the job state machine.

**Task 1 (spike) validates** only that MAF can run a graph to completion within a segment and that we can capture the yielded `RunState` output. That is the single MAF mechanic the design relies on. Record the confirmed API in the "MAF API cheat-sheet" below and reference it from later tasks.

### MAF API cheat-sheet (fill in during Task 1 spike, then reference)
- NuGet package id + exact version: `__________` — search `Microsoft.Agents.AI` first (Workflows appears to be a *namespace*, not its own package); pin the **latest stable 1.x** (NOT `1.0.0`) in `Directory.Packages.props`.
- Executor base + handler shape: `__________` (expected: `class X : Executor` with `[MessageHandler] ValueTask<TOut> HandleAsync(TIn, IWorkflowContext, CancellationToken)`; legacy `ReflectingExecutor<T>` is `[Obsolete]` in 1.0).
- Edge API: `WorkflowBuilder.AddEdge / AddFanOutEdge / AddFanInEdge / SetStartExecutor / Build` — confirm exact names + fan-in aggregate type delivered to the join handler (expected `IList<RunState>`).
- Run + capture-output API: `__________` (expected `InProcessExecution.RunAsync(workflow, input)` + a `WorkflowOutputEvent` / terminal-yield; confirm how a terminal node yields the final `RunState`).

## File Structure

**Create** (all under `backend/src/Infrastructure/Orchestration/Maf/`):
- `MafOrchestrator.cs` — `IOrchestrator` impl; builds + runs the two workflows; ctor `(IStorageService, IMetaIntegration, ITrace)`.
- `MafWorkflowRunner.cs` — static helper `RunToOutputAsync(workflow, RunState, ct)` (the one place that touches the MAF run/output API).
- `GenerationWorkflowFactory.cs` — builds the generation graph (entry → strategy → creative → fan-out[copywriting ∥ media] → fan-in assembly).
- `PublishWorkflowFactory.cs` — builds the publish graph (publishing terminal node).
- `Nodes/SupervisorEntryExecutor.cs` — start node; sets `Phase`, no span (Supervisor is sole writer of `Phase`/`Draft`).
- `Nodes/ContentStrategistExecutor.cs` — writes `Strategy`, records `strategy` span.
- `Nodes/CreativeDirectorExecutor.cs` — writes `Creative`, records `creative` span.
- `Nodes/CopywritingExecutor.cs` — writes `Caption`, records `copywriting` span.
- `Nodes/MediaGenerationExecutor.cs` — real MinIO put (idempotent `assetId`), writes `Media`/`Errors`, records `media`/`minio.put` span.
- `Nodes/AssemblyExecutor.cs` — Supervisor join: merges fork branches, builds `Draft`, sets `Phase = AwaitingApproval`, yields terminal output.
- `Nodes/PublishingExecutor.cs` — real mock publish (idempotent `contentItemId`), writes `Publish`/`Errors`, records `publishing`/`meta.publish` span, sets `Phase = Done`, yields terminal output.
- `Nodes/AdsOptimizationExecutor.cs` — designed-for stub (off active path), returns not-implemented marker.
- `Nodes/AnalyticsExecutor.cs` — designed-for stub (off active path), returns not-implemented marker.

**Modify:**
- `backend/Directory.Packages.props` — add the MAF `PackageVersion`.
- `backend/src/Infrastructure/Infrastructure.csproj` — add the MAF `PackageReference`.
- [backend/src/Infrastructure/Orchestration/OrchestrationServiceCollectionExtensions.cs](backend/src/Infrastructure/Orchestration/OrchestrationServiceCollectionExtensions.cs) — register `MafOrchestrator` instead of `StubOrchestrator`.
- [backend/tests/IntegrationTests/Durability/DurabilityFixture.cs](backend/tests/IntegrationTests/Durability/DurabilityFixture.cs) — `CreateExecuteRunJob` / `CreateResumeRunJob` construct `MafOrchestrator` instead of `StubOrchestrator`.

**Create (tests):**
- `backend/tests/IntegrationTests/Support/RecordingMetaIntegration.cs` — counts publish calls + records refs (for the no-double-publish assertion).
- `backend/tests/IntegrationTests/Durability/MafResumeSeamTests.cs` — the adversarial "kill the worker mid-run" proof (DB-backed, reuses `DurabilityFixture`).
- `backend/tests/IntegrationTests/Durability/MafOrchestratorIdempotencyTests.cs` — no-DB orchestrator-level proof: re-run generation → single asset; re-run publish → single distinct ref.

**Delete (after parity is green):**
- `backend/src/Infrastructure/Orchestration/StubOrchestrator.cs` — its canned outputs migrate into the MAF nodes; "replace the mechanism" ⇒ no dead orchestrator. Keep `IOrchestrator`, `RunState`, contracts, jobs, `RunStateJsonOptions` untouched.

**Unchanged (the proof that the seam survives):** `IOrchestrator`, `RunState` + all `Contracts/`, `ExecuteRunJob`, `ResumeRunJob`, `RunCheckpoint`, `RunStatus`, `GraphPhase`, `RunStateJsonOptions`, the approval controller, RLS/BrandScope, all six boundary interfaces.

## RunState ownership through the graph (DL-020)

Each node mutates **only its declared slice** via `state with { … }`; the Supervisor nodes are the **sole writers** of `Phase` and `Draft`. The message threaded along every edge is the `RunState` record itself (typed handoff, never free-form). Fork branches start from the identical post-`creative` `RunState`, so the join merges by **disjoint field ownership** (Caption from the copy branch, Media from the media branch), unions `Errors` by value and `Trace.Spans` by `SpanId`. Span set preserved exactly as today: `strategy`, `creative`, `copywriting`, `media`+`minio.put`, `publishing`+`meta.publish` (Supervisor entry/assembly nodes record no span, matching the 5-span smoke evidence and the [Tracing/TraceTests.cs](backend/tests/IntegrationTests/Tracing/TraceTests.cs) assertions).

---

## Task 0: Land this plan in the repo

Keep the plan version-controlled alongside the decision logs (the auto-generated `~/.claude/plans/…-scalable-flask.md` slug is wrong — this is .NET, not Flask).

- [x] **Step 1:** Write this plan to `docs/superpowers/plans/maf-orchestrator-seam.md` in the repo.
- [x] **Step 2:** Commit it.

```bash
git add docs/superpowers/plans/maf-orchestrator-seam.md
git commit -m "docs(maf): plan updates — package facts, reframing, concurrency, ordering"
```

- [ ] **Step 3:** STOP. Do not begin Task 1 until the user says "start".

---

## Task 1: MAF package + API spike (validate the one mechanic)

**Files:**
- Modify: `backend/Directory.Packages.props`, `backend/src/Infrastructure/Infrastructure.csproj`
- Test (throwaway, deleted at end of task): `backend/tests/IntegrationTests/Durability/MafSpikeTests.cs`

- [ ] **Step 1: Confirm the package id + version (do not assume).** Run: `dotnet package search Microsoft.Agents.AI --take 10` (search `Microsoft.Agents.AI.Workflows` only as a fallback). "MAF 1.0" means the **1.x GA line** (GA 2 April 2026) — already several minor versions in, and Workflows appears to ship as a *namespace inside* `Microsoft.Agents.AI`, not a separate package. Record the **actual id** and the **latest stable 1.x version** in the cheat-sheet above.

- [ ] **Step 2: Pin it (CPM).** Add to `Directory.Packages.props` runtime ItemGroup using the **id + latest stable 1.x version confirmed in Step 1** — do **not** hardcode `1.0.0`:

```xml
<!-- id + version from Step 1; e.g. Microsoft.Agents.AI 1.x.y (NOT 1.0.0) -->
<PackageVersion Include="Microsoft.Agents.AI" Version="<latest-stable-1.x>" />
```

Add the matching `<PackageReference Include="…" />` (same id, no version) to `Infrastructure.csproj` ItemGroup.

- [ ] **Step 3: Write a throwaway 2-node spike test** proving: build a `WorkflowBuilder` with two `Executor`s connected by `AddEdge`, run it threading a small record, capture the yielded output. Use it to confirm the exact `Executor` base ctor, the `[MessageHandler]` signature, `SetStartExecutor`, and the run/output API. Run: `dotnet test --filter FullyQualifiedName~MafSpikeTests -v minimal`. Expected: PASS, output captured.

- [ ] **Step 4: Confirm fan-in aggregate type.** Extend the spike with `AddFanOutEdge(a, [b, c])` + `AddFanInEdge(join, [b, c])` and confirm the type delivered to the join handler (record it in the cheat-sheet). Run the spike again; expected PASS.

- [ ] **Step 5: Update the cheat-sheet** in this plan with the confirmed API, then **delete `MafSpikeTests.cs`**.
  - **Reconcile every sample first:** the code in Tasks 2–5 (the `Executor` base ctor, the `[MessageHandler]` attribute, `WorkflowBuilder.AddEdge`/`AddFanOutEdge`/`AddFanInEdge`/`SetStartExecutor`/`Build`, and the run/output API) is written against **assumed** shapes. Reconcile each against the confirmed spike API before reusing it; if a name or signature has moved, **update the plan's samples in-place and continue**.
  - If Step 4 shows fan-in is not viable as written, switch the generation graph to the banked **sequential** fallback (strategy→creative→copywriting→media→assembly via `AddEdge`) and note it in the Output Summary — the rest of the plan is unaffected because the assembly merge is a no-op pass-through when nodes are sequential.

- [ ] **Step 6: Build gate.** Run: `dotnet build backend/Backend.sln -warnaserror`. Expected: PASS (no analyzer/nullable errors from the new package).

- [ ] **Step 7: Commit.**

```bash
git add backend/Directory.Packages.props backend/src/Infrastructure/Infrastructure.csproj
git commit -m "build: add Microsoft Agent Framework 1.0 workflows package + validate run/output seam"
```

---

## Task 2: Deterministic agent executor nodes (TDD, one per node)

Implement each node test-first. Each node is constructed with the deps it needs and an executor id, mutates only its slice, and records its span. Reuse the canned values verbatim from `StubOrchestrator` so behaviour and trace are identical.

**Files:**
- Create: `backend/src/Infrastructure/Orchestration/Maf/Nodes/ContentStrategistExecutor.cs` (+ the other node files)
- Test: `backend/tests/IntegrationTests/Durability/MafNodeTests.cs` (no DB; constructs nodes directly and asserts the returned `RunState` slice + span)

- [ ] **Step 1: Failing test for ContentStrategist** — asserts a node, given a base `RunState`, returns `Strategy = ContentStrategy("stub-pillar","stub-angle","stub-objective","stub-audience",null)` and appends one `strategy` span (status `ok`). Use `LocalTraceRecorder` as `ITrace`.

```csharp
[Trait("Category", "Durability")]
public sealed class MafNodeTests
{
    private static RunState Base(Guid runId, Guid brandId) => new(
        runId, brandId, GraphPhase.Strategy, null, null, null, null, null, null, null,
        new Budget(10_000, 0, 1.00m, 0m), [], new TraceRefs(string.Empty, [], []));

    [Fact]
    public async Task ContentStrategist_writes_strategy_slice_and_records_span()
    {
        var runId = Guid.NewGuid(); var brandId = Guid.NewGuid();
        var node = new ContentStrategistExecutor(new LocalTraceRecorder());

        var result = await node.RunAsync(Base(runId, brandId), CancellationToken.None);

        Assert.Equal("stub-pillar", result.Strategy!.Pillar);
        Assert.Contains(result.Trace.Spans, s => s.Node == "strategy" && s.Status == "ok");
    }
}
```

> Note: expose a thin `RunAsync(RunState, CancellationToken)` method on each node that the `[MessageHandler]` delegates to, so nodes are unit-testable without standing up a workflow. The MAF handler is a one-line wrapper around `RunAsync`.

- [ ] **Step 2: Run it, expect FAIL** (`ContentStrategistExecutor` not defined). Run: `dotnet test --filter FullyQualifiedName~MafNodeTests.ContentStrategist`.

- [ ] **Step 3: Implement `ContentStrategistExecutor`:**

```csharp
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Microsoft.Agents.AI.Workflows; // confirm namespace in Task 1

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

public sealed class ContentStrategistExecutor : Executor // base ctor id confirmed in Task 1
{
    private readonly ITrace _trace;
    public ContentStrategistExecutor(ITrace trace) : base("content-strategist") => _trace = trace;

    [MessageHandler]
    public ValueTask<RunState> HandleAsync(RunState state, IWorkflowContext ctx, CancellationToken ct)
        => new(RunAsync(state, ct));

    public async Task<RunState> RunAsync(RunState state, CancellationToken ct)
    {
        var strategy = new ContentStrategy("stub-pillar", "stub-angle", "stub-objective", "stub-audience", null);
        var now = DateTimeOffset.UtcNow;
        var trace = await _trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "strategy", null, "ok", now, now, null, ct)
            .ConfigureAwait(false);
        return state with { Strategy = strategy, Trace = trace };
    }
}
```

- [ ] **Step 4: Run it, expect PASS.**

- [ ] **Step 5: Repeat Steps 1–4 for `CreativeDirectorExecutor`** (id `creative-director`, span `creative`, writes `Creative = CreativeDirection("stub-concept", ["soft"], ["#ffffff"], "stub-brief")`).

- [ ] **Step 6: Repeat for `CopywritingExecutor`** (id `copywriting`, span `copywriting`, writes `Caption = Caption("stub-hook","stub-body",["#stub"])`).

- [ ] **Step 7: Implement + test `MediaGenerationExecutor`** (id `media-generation`). Port the MinIO block from `StubOrchestrator` verbatim: `assetId = DeterministicGuid.From(state.RunId, "asset")`, `key = StorageKeys.ForAsset(state.BrandId, assetId, "png")`, `PutAsync` the 1×1 PNG, set `Media` on success or append `ToolError("storage.put_failed", …, true)` on failure, record `media`/`minio.put` span with real start/end. Ctor `(IStorageService, ITrace)`. Test asserts: `Media.AssetId == DeterministicGuid.From(runId,"asset")`, span present; and a second `RunAsync` against a shared `InMemoryStorageService` leaves exactly one object under the asset prefix (idempotent write).

- [ ] **Step 8: Build + format gates.** Run: `dotnet build backend/Backend.sln -warnaserror` then `dotnet format backend/Backend.sln --verify-no-changes`. Expected: PASS.

- [ ] **Step 9: Commit.** `git commit -m "feat(maf): deterministic agent executor nodes with trace spans"`

---

## Task 3: Supervisor entry + assembly (fan-in merge) nodes

**Files:**
- Create: `Maf/Nodes/SupervisorEntryExecutor.cs`, `Maf/Nodes/AssemblyExecutor.cs`
- Test: add cases to `MafNodeTests.cs`

- [ ] **Step 1: Failing test for the assembly merge** — given two branch `RunState`s (one with `Caption`, one with `Media` + a shared `strategy`/`creative` span set), assert the merged state has both `Caption` and `Media`, `Phase == AwaitingApproval`, a `Draft` with `Status == "pending"`, and `Trace.Spans.Count == Trace.SpanIds.Count` with no duplicate `SpanId`.

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement `SupervisorEntryExecutor`** (id `supervisor-entry`, no span): `state with { Phase = GraphPhase.Strategy }`, delegate `HandleAsync` → strategy. (Budget pass-through; cost model out of scope.)

- [ ] **Step 4: Implement `AssemblyExecutor`** (id `assembly`). Handler receives the fan-in aggregate (type confirmed in Task 1, expected `IList<RunState>`); yields the terminal output via `IWorkflowContext`:

```csharp
public async Task<RunState> RunAsync(IReadOnlyList<RunState> branches, CancellationToken ct)
{
    var baseState = branches[0];
    var caption = branches.Select(b => b.Caption).FirstOrDefault(c => c is not null);
    var media = branches.Select(b => b.Media).FirstOrDefault(m => m is not null);

    // Branches share the pre-fork errors/spans; union de-dupes them.
    var errors = branches.SelectMany(b => b.Errors).Distinct().ToList();
    var spans = branches.SelectMany(b => b.Trace.Spans)
        .GroupBy(s => s.SpanId).Select(g => g.First())
        .OrderBy(s => s.StartedAt).ThenBy(s => s.SpanId).ToList();
    var traceId = branches.Select(b => b.Trace.TraceId).First(id => !string.IsNullOrEmpty(id));

    var draft = new ContentItemDraft(
        CaptionRef: caption!, MediaRef: media, BrandId: baseState.BrandId,
        Status: media is null ? "degraded-caption-only" : "pending");

    return baseState with
    {
        Phase = GraphPhase.AwaitingApproval,
        Caption = caption, Media = media, Draft = draft, Errors = errors,
        Trace = new TraceRefs(traceId, spans.Select(s => s.SpanId).ToList(), spans),
    };
}
```

> If Task 1 chose the **sequential** fallback, `AssemblyExecutor.RunAsync(RunState, …)` instead takes the single threaded state, sets `Draft` + `Phase`, and yields — the merge becomes a no-op.

- [ ] **Step 5: Run, expect PASS.**
- [ ] **Step 6: Build + format gates.** Expected PASS.
- [ ] **Step 7: Commit.** `git commit -m "feat(maf): supervisor entry + assembly join with deterministic merge"`

---

## Task 4: Publishing node + designed-for stub nodes

**Files:**
- Create: `Maf/Nodes/PublishingExecutor.cs`, `Maf/Nodes/AdsOptimizationExecutor.cs`, `Maf/Nodes/AnalyticsExecutor.cs`
- Test: add a `PublishingExecutor` case to `MafNodeTests.cs`

- [ ] **Step 1: Failing test for PublishingExecutor** — given a `RunState` with a `Caption`, assert it returns `Publish.ExternalRef` starting `mock://meta/`, `Publish.Status == "published"`, `Phase == Done`, and a `publishing`/`meta.publish` span. Use `MockMetaIntegration` + `LocalTraceRecorder`.

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement `PublishingExecutor`** (id `publishing`, ctor `(IMetaIntegration, ITrace)`): port `StubOrchestrator.RunPublishAsync` verbatim (build `PublishRequest` with `ContentItemId = state.RunId`, try/catch → `PublishResult` or `ToolError("meta.publish_failed", …)`, record span), set `Phase = GraphPhase.Done`, yield terminal output.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Implement the two designed-for stubs** (DL-019 "present, not exercised, not cut"). Each is a real `Executor` returning a not-implemented marker, registered but off the active edge path:

```csharp
namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>Designed-for stub (DL-019). Present in the graph wiring, off the MVP path,
/// never reached by the active spine. Returns a not-implemented marker, never throws.</summary>
public sealed class AdsOptimizationExecutor : Executor
{
    public AdsOptimizationExecutor() : base("ads-optimization") { }

    [MessageHandler]
    public ValueTask<RunState> HandleAsync(RunState state, IWorkflowContext ctx, CancellationToken ct)
        => new(state with { Errors = [.. state.Errors,
            new ToolError("ads.not_implemented", "Ads Optimization is an advanced-scope stub.", false)] });
}
```

`AnalyticsExecutor` is identical with id `analytics` and code `analytics.not_implemented`. Add a one-line unit test asserting each returns the marker (proves they are present and wired-constructible).

- [ ] **Step 6: Build + format gates.** Expected PASS.
- [ ] **Step 7: Commit.** `git commit -m "feat(maf): publishing node + Ads/Analytics designed-for stubs"`

---

## Task 5: Workflow factories + MafOrchestrator + MafWorkflowRunner

**Files:**
- Create: `Maf/GenerationWorkflowFactory.cs`, `Maf/PublishWorkflowFactory.cs`, `Maf/MafWorkflowRunner.cs`, `Maf/MafOrchestrator.cs`
- Test: `backend/tests/IntegrationTests/Durability/MafOrchestratorIdempotencyTests.cs`

- [ ] **Step 1: Implement `GenerationWorkflowFactory.Build(IStorageService, IMetaIntegration, ITrace)`** — `new`s the node instances and wires the graph (use the API confirmed in Task 1):

```csharp
var entry    = new SupervisorEntryExecutor();
var strategy = new ContentStrategistExecutor(trace);
var creative = new CreativeDirectorExecutor(trace);
var copy     = new CopywritingExecutor(trace);
var media    = new MediaGenerationExecutor(storage, trace);
var assembly = new AssemblyExecutor();
// Ads/Analytics constructed + present but NOT added to the active path (off-MVP, DL-019).

return new WorkflowBuilder()
    .SetStartExecutor(entry)
    .AddEdge(entry, strategy)
    .AddEdge(strategy, creative)
    .AddFanOutEdge(creative, [copy, media])
    .AddFanInEdge(assembly, [copy, media])
    .Build();
```

- [ ] **Step 2: Implement `PublishWorkflowFactory.Build(IMetaIntegration, ITrace)`** — start = `PublishingExecutor`, terminal yields.

- [ ] **Step 3: Implement `MafWorkflowRunner.RunToOutputAsync(workflow, RunState, ct)`** — the single place that runs the workflow and extracts the yielded terminal `RunState` (per Task 1 cheat-sheet). Throw `InvalidOperationException` if no output was produced (fail loud — never silently return the input).

- [ ] **Step 4: Implement `MafOrchestrator : IOrchestrator`** with ctor `(IStorageService storage, IMetaIntegration meta, ITrace trace)`:

```csharp
public Task<RunState> RunGenerationAsync(RunState state, CancellationToken ct = default)
    => MafWorkflowRunner.RunToOutputAsync(GenerationWorkflowFactory.Build(_storage, _meta, _trace), state, ct);

public Task<RunState> RunPublishAsync(RunState state, CancellationToken ct = default)
    => MafWorkflowRunner.RunToOutputAsync(PublishWorkflowFactory.Build(_meta, _trace), state, ct);
```

- [ ] **Step 4a: Verify trace concurrency under fan-out.** The Copywriting ∥ Media fork runs two branches in parallel, so two `ITrace.RecordAsync` calls can be in flight at once. If the DI-registered `ITrace` writes through a **shared `DbContext`**, parallel calls throw *"A second operation was started on this context before a previous operation completed."* `LocalTraceRecorder` (unit tests) sidesteps this; the **production tracer must not** rely on that. Confirm the registered `ITrace` is either functional (no shared mutable state) or resolves a **per-operation `DbContext`** inside `RecordAsync` (a scoped factory, not a captured field); fix it if it captures a shared context. Add a parallel-write test against the **production** `ITrace` implementation (not just `LocalTraceRecorder`):

```csharp
// Two concurrent RecordAsync calls on the registered tracer must both succeed.
await Task.WhenAll(
    tracer.RecordAsync(trace, runId, brandId, "copywriting", null,        "ok", t0, t1, null, ct).AsTask(),
    tracer.RecordAsync(trace, runId, brandId, "media",       "minio.put", "ok", t0, t1, null, ct).AsTask());
```

- [ ] **Step 5: Write `MafOrchestratorIdempotencyTests` (no DB):**

```csharp
[Trait("Category", "Durability")]
public sealed class MafOrchestratorIdempotencyTests
{
    [Fact]
    public async Task Generation_rerun_writes_single_asset_and_publish_rerun_keeps_one_ref()
    {
        var storage = new InMemoryStorageService();
        var meta = new RecordingMetaIntegration();
        var orch = new MafOrchestrator(storage, meta, new LocalTraceRecorder());
        var runId = Guid.NewGuid(); var brandId = Guid.NewGuid();
        var s0 = new RunState(runId, brandId, GraphPhase.Strategy, null, null, null, null, null,
            null, null, new Budget(10_000, 0, 1.00m, 0m), [], new TraceRefs("", [], []));

        var g1 = await orch.RunGenerationAsync(s0);
        var g2 = await orch.RunGenerationAsync(s0); // worker crash before checkpoint → segment re-run

        var assetKeys = (await storage.ListAsync($"brands/{brandId}/assets/")).ToList();
        Assert.Single(assetKeys);                       // idempotent MinIO write under MAF
        Assert.Equal(GraphPhase.AwaitingApproval, g1.Phase);
        Assert.NotNull(g2.Media);

        var p1 = await orch.RunPublishAsync(g1);
        var p2 = await orch.RunPublishAsync(g1);        // publish segment re-run
        Assert.Equal(p1.Publish!.ExternalRef, p2.Publish!.ExternalRef); // deterministic ref
        Assert.Single(meta.PublishedRefs.Distinct());   // no second distinct post
    }
}
```

- [ ] **Step 6: Implement `RecordingMetaIntegration`** in Support — wraps `MockMetaIntegration`, exposes `IReadOnlyList<string?> PublishedRefs` and `int PublishCount`, appends on each call.

- [ ] **Step 7: Run the test, expect PASS.** Run: `dotnet test --filter FullyQualifiedName~MafOrchestratorIdempotencyTests`.
- [ ] **Step 8: Build + format gates.** Expected PASS.
- [ ] **Step 9: Commit.** `git commit -m "feat(maf): workflow factories + MafOrchestrator behind IOrchestrator"`

---

## Task 6: Swap DI + fixture to MafOrchestrator; delete StubOrchestrator; prove parity

This is the seam-survival proof: the **existing** Durability/Trace/Publish tests now run through the MAF graph and must stay green unchanged.

**Files:**
- Modify: [OrchestrationServiceCollectionExtensions.cs](backend/src/Infrastructure/Orchestration/OrchestrationServiceCollectionExtensions.cs), [DurabilityFixture.cs](backend/tests/IntegrationTests/Durability/DurabilityFixture.cs)
- Delete: `backend/src/Infrastructure/Orchestration/StubOrchestrator.cs`

> **Verify-then-delete.** Keep `StubOrchestrator.cs` until parity is proven, so a failing run can be diffed against it without a `git restore`. Delete only after every gate below is green.

- [ ] **Step 1: Register MAF orchestrator.** In `OrchestrationServiceCollectionExtensions`, change `services.AddScoped<IOrchestrator, StubOrchestrator>()` → `services.AddScoped<IOrchestrator, MafOrchestrator>()`.

- [ ] **Step 2: Switch the fixture.** In `DurabilityFixture.CreateExecuteRunJob` and `CreateResumeRunJob`, replace both `new StubOrchestrator(…)` with `new MafOrchestrator(new InMemoryStorageService(), new MockMetaIntegration(), new LocalTraceRecorder())`. (Ctor signature is identical, so this is a two-line change.)

- [ ] **Step 3: Run the existing durable + trace + publish suites — they must pass unchanged through the MAF graph:**

Run: `dotnet test backend/Backend.sln --filter "Category=Durability|Category=Trace|Category=Publish"`
Expected: all green — in particular `ResumeRun_in_fresh_scope_reconstructs_from_checkpoint_and_reaches_done`, `ResumeRun_twice_is_idempotent_no_duplicate_checkpoint`, and `Completed_run_has_one_continuous_trace_with_node_and_tool_spans` (asserts `strategy`, `media`+`minio.put`, `publishing`+`meta.publish` spans, `Spans.Count == SpanIds.Count`, all `ok`).

- [ ] **Step 4: Run the isolation gate** (data-access touched ⇒ mandatory). Run: `dotnet test backend/Backend.sln --filter Category=Isolation`. Expected: PASS (zero cross-brand leakage).

- [ ] **Step 5: Build + format gates.** Run: `dotnet build backend/Backend.sln -warnaserror` then `dotnet format backend/Backend.sln --verify-no-changes`. Expected PASS.

- [ ] **Step 6: Only after Steps 3–5 are all green — delete `StubOrchestrator.cs`.** Confirm no remaining references: `grep -rn StubOrchestrator backend/` returns nothing. Re-run the build gate to confirm nothing else referenced it.

- [ ] **Step 7: Commit.** `git commit -m "feat(maf): replace StubOrchestrator with MAF graph; existing seam tests green through MAF"`

---

## Task 7: Adversarial proof test — kill the worker mid-run (ship it)

**Files:**
- Create: `backend/tests/IntegrationTests/Durability/MafResumeSeamTests.cs`

- [ ] **Step 1: Write the DB-backed adversarial test** (reuses `DurabilityFixture`, real Postgres). It simulates a worker kill by re-invoking each segment job (Hangfire retry semantics) and asserts no duplicated side effects:

```csharp
[Trait("Category", "Durability")]
public sealed class MafResumeSeamTests : IClassFixture<DurabilityFixture>
{
    private readonly DurabilityFixture _fixture;
    public MafResumeSeamTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MafGraph_checkpoint_exit_resume_roundtrips_with_no_duplicate_sideeffects()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        // Segment 1: ExecuteRun runs the MAF generation graph → checkpoint → AwaitingApproval.
        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb) { await execJob.ExecuteAsync(runId, _fixture.BrandA); }

        // Kill + Hangfire retry of segment 1: re-run ExecuteRun → guarded no-op (already AwaitingApproval).
        var (exec2Db, exec2Job) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (exec2Db) { await exec2Job.ExecuteAsync(runId, _fixture.BrandA); }

        var afterGen = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.NotNull(afterGen);
        Assert.Equal(GraphPhase.AwaitingApproval, afterGen!.Phase);
        Assert.NotNull(afterGen.Media);
        Assert.NotNull(afterGen.Draft);
        Assert.Contains(afterGen.Trace.Spans, s => s.Node == "media" && s.Tool == "minio.put");

        await _fixture.ApproveRunAsync(runId, _fixture.BrandA);

        // Segment 2: ResumeRun rehydrates from checkpoint → mock publish → Done.
        var (resDb, resJob) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (resDb) { await resJob.ExecuteAsync(runId, _fixture.BrandA); }

        // Kill + retry of segment 2: re-run ResumeRun → guarded no-op (already Done).
        var (res2Db, res2Job) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (res2Db) { await res2Job.ExecuteAsync(runId, _fixture.BrandA); }

        var final = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.Equal(GraphPhase.Done, final!.Phase);
        Assert.StartsWith("mock://meta/", final.Publish!.ExternalRef!);
        Assert.Empty(final.Errors);
        Assert.Equal(final.Trace.Spans.Count, final.Trace.SpanIds.Count); // one continuous trace

        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();
            var run = await readDb.AgentRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.Done, run.Status);
            var checkpoints = await readDb.RunCheckpoints.AsNoTracking()
                .Where(c => c.AgentRunId == runId).ToListAsync();
            Assert.Single(checkpoints); // exactly one checkpoint across all four job invocations
        }
    }
}
```

- [ ] **Step 2: Run it, expect PASS.** Run: `dotnet test --filter FullyQualifiedName~MafResumeSeamTests -v minimal`.
- [ ] **Step 3: Build + format gates.** Expected PASS.
- [ ] **Step 4: Commit.** `git commit -m "test(maf): adversarial worker-kill resume seam — no double publish, no duplicate asset"`

---

## Task 8: Full verification + Output Summary

- [ ] **Step 1: Types.** Run: `dotnet build backend/Backend.sln -warnaserror`. Expected: PASS.
- [ ] **Step 2: Format.** Run: `dotnet format backend/Backend.sln --verify-no-changes`. Expected: PASS.
- [ ] **Step 3: Full suite.** Run: `dotnet test backend/Backend.sln`. Expected: all green (existing + new MAF tests).
- [ ] **Step 4: Mandatory gates.** Run: `dotnet test backend/Backend.sln --filter Category=Isolation` and `--filter Category=Durability`. Expected: PASS.
- [ ] **Step 5: Secrets hygiene.** Run `gitleaks detect` (or the repo's configured invocation). Expected: clean.
- [ ] **Step 6: Fill in the Output Summary below**, then **commit** the summary (append to CLAUDE.md "Slice" notes if that is the repo convention).

---

## Verification (end-to-end)

| Check | Command | Expected |
|---|---|---|
| Types | `dotnet build backend/Backend.sln -warnaserror` | green |
| Format | `dotnet format backend/Backend.sln --verify-no-changes` | no changes |
| Full tests | `dotnet test backend/Backend.sln` | all green |
| Durable resume | `dotnet test --filter Category=Durability` | green (incl. new `MafResumeSeamTests`) |
| Isolation | `dotnet test --filter Category=Isolation` | zero leakage |
| Trace seam | `dotnet test --filter Category=Trace` | one continuous trace, node + tool spans |
| MAF path | manual | generation graph reaches `assembly` and returns; publish graph reaches `publishing` and returns; MAF holds nothing across the gate |

**Optional full-stack smoke (matches c2 evidence):** `docker compose up --build`, then `POST /brands` → `POST /runs` (X-Brand-Id) → after ExecuteRun `GET /runs/{id}` shows `status=2`; `POST /runs/{id}/approval {"approve"}` → `status=4`; `GET /runs/{id}/trace` shows one trace id with `strategy, creative, copywriting, media:minio.put, publishing:meta.publish` spans; exactly one object under the brand asset prefix.

---

## OUTPUT SUMMARY (fill in during/after implementation)

> A reviewer should be able to read this section alone and know exactly what changed and that the seam held.

**Slice: MAF supervised graph (deterministic stubs) — real graph behind the unchanged durable seam (DATE, commit `____`)**

- **MAF seam path taken:** DL-015/DL-018 fallback **chosen (not forced)** — durable wait owned by the `AgentRun` state machine to reuse the proven c1/c2 seam; MAF 1.0's native checkpointing/HITL was deliberately **not** used here. MAF runs only intra-segment, returns a `RunState`, holds nothing across the gate. (Confirm: the generation workflow terminates at `assembly`; `ExecuteRunJob` still does the checkpoint/exit; `ResumeRunJob` rehydrates and runs the publish workflow.)
- **Fork topology shipped:** real fan-out/fan-in (Copywriting ∥ Media → join at assembly) — OR sequential fallback if the Task 1 spike found fan-in unviable (state which, and why).
- **MAF package pinned:** `Microsoft.Agents.AI __._._` (latest stable 1.x; Workflows namespace) — CPM.
- **Nodes:** Supervisor entry, Content Strategist, Creative Director, Copywriting, Media Generation, Assembly (Supervisor join), Publishing — all deterministic stubs; Ads Optimization + Analytics present as off-path designed-for stubs (DL-019).
- **What was replaced:** `StubOrchestrator` deleted; `MafOrchestrator : IOrchestrator` registered in its place (identical ctor). `IOrchestrator`, `RunState`, contracts, both Hangfire jobs, `RunCheckpoint`, `RunStateJsonOptions` **unchanged** — the proof that the seam survives.
- **Trace parity:** 5 spans preserved — `strategy, creative, copywriting, media:minio.put, publishing:meta.publish`; one continuous trace id across the ExecuteRun→ResumeRun seam.
- **Files created:** _list `Maf/` + test files._  **Files modified:** _DI ext, fixture, csproj, props._  **Files deleted:** `StubOrchestrator.cs`.
- **Gate evidence (paste actual output):**
  - `dotnet build -warnaserror` → ____
  - `dotnet format --verify-no-changes` → ____
  - `dotnet test` → ____ passed / 0 failed
  - `Category=Durability` (incl. `MafResumeSeamTests`) → ____
  - `Category=Isolation` → ____
  - `Category=Trace` → ____
- **Adversarial proof result:** ExecuteRun ×2 (kill+retry) → 1 checkpoint, 1 asset, `AwaitingApproval`; approve; ResumeRun ×2 (kill+retry) → `Done`, same deterministic `mock://meta/…` ref, no second publish, `Spans.Count == SpanIds.Count`.

## Out of scope (later slices, do not build here)

Real agent prompts, structured-output schemas + forced-tool enforcement, the cost model / `Budget` gate, `PlatformConstraints` validators, RAG / `IRetrievalService`, live Gemini, live Meta. Stubs only — this slice is the seam.

The stub `Strategy`/`Caption`/etc. contracts used here are the **c1/c2 shapes** (single strategy, string objective, no `Grounding`, no Supervisor-selection node). The DL-028 multi-field schemas, the multi-angle Strategist → Supervisor-selection segment, the cost gate, and `PlatformConstraints` extend this graph in the **generation-pipeline** slice — they are the next slice's deliveries, **not regressions** from this plan.
