# Naming, Style & Hygiene (.NET)

Translated from the bootcamp coding guidelines. Python casing and tooling do not
apply; the rules below are the C# equivalents.

## Contents
1. Naming convention table
2. Formatting & analyzers (the gated baseline)
3. Project layout (defer to the architecture skill)
4. README-as-a-map
5. Comments & docstrings

---

## 1. Naming convention table

| Element                         | Convention        | Example                          |
|---------------------------------|-------------------|----------------------------------|
| Class / record / struct / enum  | PascalCase        | `BrandProfile`, `RunState`       |
| Interface                       | `I` + PascalCase  | `IMetaIntegration`               |
| Method                          | PascalCase        | `GenerateCaptionAsync`           |
| Public property                 | PascalCase        | `public Guid BrandId { get; }`   |
| Local variable / parameter      | camelCase         | `var contentItem;` `string brandId` |
| Private field                   | `_camelCase`      | `private readonly ILogger _log;` |
| Constant / `static readonly`    | PascalCase        | `const int MaxRetries = 3;`      |
| Type parameter (generic)        | `T` + PascalCase  | `TResult`, `TEntity`             |
| Async method                    | PascalCase + `Async` suffix | `LoadBrandAsync`       |
| Enum members                    | PascalCase        | `GraphPhase.AwaitingApproval`    |
| File name                       | PascalCase = type | `BrandsController.cs`            |
| Namespace                       | PascalCase dotted | `Backend.Infrastructure.Storage` |

Rules:
- **No `snake_case` anywhere.** It is the most common Python carryover; reject it.
- Boolean members read as questions: `IsActive`, `HasApproval`, `CanPublish`.
- Be descriptive: `CalculateTotalSpend()` over `Calc()`. Collections are plural:
  `assets`, `contentItems`. Methods start with a verb.
- Common acronyms stay readable: `BrandId`, `HttpClient`, `JsonOptions` (two-letter
  acronyms upper, e.g. `IOError`; longer ones PascalCased).
- One public type per file; file name matches the type.

---

## 2. Formatting & analyzers — the gated baseline

These run in pre-commit and CI (`references/ci-and-precommit.md`) and must be clean.

- **`dotnet format`** is the formatter of record (the C# equivalent of an
  auto-formatter; there is no black/isort/flake8/mypy here). CI runs
  `dotnet format --verify-no-changes` and fails on drift.
- **Roslyn analyzers** are the linter/type-checker. Enable them solution-wide and
  treat the agreed analyzer set as errors.
- **Nullable reference types ON** solution-wide. This is the static null-safety
  net; do not disable it per-file to silence warnings — fix the nullability instead.

Put these in `Directory.Build.props` so every project inherits them:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-Recommended</AnalysisLevel>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

A `.editorconfig` at the repo root pins the style rules `dotnet format` enforces
(indentation, `this.` preferences, `var` usage, naming-rule severities). Keep it
authoritative and let it settle whitespace arguments once and for all.

---

## 3. Project layout — defer to the architecture skill

The solution structure (`Api` / `Worker` / `Core` / `Infrastructure`, the test
projects, the monorepo `backend/` + `frontend/` split) is **owned by the
architecture skill**. Do not invent or re-document a different layout here.

This skill's only layout rule: **every file has one clear responsibility and lives
in the layer that owns that responsibility.** A 600-line `Program.cs` or a
controller holding business logic is a defect. Controllers stay thin (validate,
delegate, return); business logic lives in `Core`/`Infrastructure` services;
integrations live behind their interfaces.

---

## 4. README-as-a-map

The README is the front door, not a tutorial. It must contain:
- What the system does and why (one paragraph).
- Architecture overview + the diagram (pointer to the architecture skill's source).
- How to run locally (`docker compose up` is the demo target).
- **Required configuration keys by name only** — never values.
- Where the interesting code lives (the agent graph, the RLS interceptor, the
  integration interfaces).
- Known limitations and the documented hardening path.

Write it for the engineer who joins in a year and reads it once.

---

## 5. Comments & docstrings

- XML doc comments (`/// <summary>`) on public modules, services, and interface
  members — the C# equivalent of docstrings. Document `param`, `returns`, and any
  thrown domain exception.
- Inline comments explain **why**, not **what**; place them on the line above the
  code. Code should be self-explanatory; comment the non-obvious decision.
- Use `// TODO:` for planned work, `// FIXME:` for known issues, `// HACK:` for a
  workaround (and say why).
