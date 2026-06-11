# CI, Pre-commit, Security & Dependencies (.NET)

The gated baseline. Everything here either runs in CI on every push/PR or blocks
a commit locally. Translated to the .NET toolchain — no black/isort/flake8/mypy.

## Contents
1. CI gates (GitHub Actions)
2. Branch ruleset (admins bound too)
3. Pre-commit hooks
4. Secrets hygiene & ignores
5. Dependency management (central pinning)

---

## 1. CI gates — GitHub Actions

Required checks on every push and pull request: build, test, format check,
analyzers, secret scan. All run on mocks (no live Meta/Gemini/embeddings).

```yaml
# .github/workflows/ci.yml
name: ci
on: [push, pull_request]

jobs:
  build-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - name: Restore
        run: dotnet restore
      - name: Format check
        run: dotnet format --verify-no-changes          # fails on style drift
      - name: Build (analyzers, warnings-as-errors)
        run: dotnet build --no-restore -c Release        # Roslyn analyzers gate here
      - name: Test
        run: dotnet test --no-build -c Release           # xUnit + Testcontainers

  secret-scan:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - name: gitleaks
        uses: gitleaks/gitleaks-action@v2
```

`TreatWarningsAsErrors` (set in `Directory.Build.props`, see
`references/naming-and-style.md`) makes the analyzer gate real during build.

---

## 2. Branch ruleset — admins are bound too

A gate anyone can bypass is not a gate. The protected-branch ruleset on
`main` (and `develop`) must:
- Require the `build-test` and `secret-scan` checks to pass before merge.
- Require a pull request + at least one review; no direct pushes.
- **Include administrators** in the ruleset (do not exempt admins).
- Require branches to be up to date and conversations resolved before merge.

---

## 3. Pre-commit hooks

Catch problems before they reach CI. Mirror the CI gates locally.

```yaml
# .pre-commit-config.yaml
repos:
  - repo: https://github.com/gitleaks/gitleaks
    rev: v8.18.0
    hooks:
      - id: gitleaks

  - repo: local
    hooks:
      - id: dotnet-format
        name: dotnet format
        entry: dotnet format --verify-no-changes
        language: system
        pass_filenames: false
        types: [c#]
```

---

## 4. Secrets hygiene & ignores

- **No secret in code, image, DB, or logs — ever.** gitleaks blocks commits and
  PRs. Any secret ever committed is considered compromised and must be rotated.
- Secrets load through the Options pattern / the architecture skill's secrets
  provider (Vault KV + Transit). Document every key by name in
  `appsettings.Example.json` / `.env.example`; commit no real values.

`.gitignore` (essentials):

```gitignore
# secrets & local config
.env
.env.*
appsettings.*.local.json
*.pem
*.key
# build output
bin/
obj/
*.user
# IDE / OS
.vs/
.idea/
.DS_Store
# test output
TestResults/
coverage*.xml
```

`.dockerignore` (keep build context lean and secrets out of images):

```dockerignore
.git
.gitignore
.env
.env.*
**/bin/
**/obj/
**/.vs/
**/.idea/
tests/
*.md
docker-compose*.yml
TestResults/
```

Without `.dockerignore`, the whole repo (secrets, test data, IDE config) ships to
the Docker daemon as build context and can leak into images.

---

## 5. Dependency management — central pinning

- **Central Package Management**: pin every version once in
  `Directory.Packages.props`; project files reference packages without versions.
  No floating versions.

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="FluentValidation.AspNetCore" Version="11.3.0" />
    <PackageVersion Include="Polly.Extensions.Http" Version="3.0.0" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.2" />
    <PackageVersion Include="Pgvector.EntityFrameworkCore" Version="0.2.0" />
    <PackageVersion Include="Hangfire.PostgreSql" Version="1.20.10" />
    <PackageVersion Include="VaultSharp" Version="1.17.5.1" />
    <PackageVersion Include="Minio" Version="6.0.3" />
    <PackageVersion Include="Serilog.AspNetCore" Version="8.0.3" />
    <!-- test-only packages referenced only by test projects -->
    <PackageVersion Include="xunit" Version="2.9.2" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.1.0" />
    <PackageVersion Include="NSubstitute" Version="5.3.0" />
  </ItemGroup>
</Project>
```

- **Separate dev/test from runtime** — test packages (xUnit, Testcontainers,
  mocking) are referenced only by test projects, never by `Api`/`Worker`.
- **Scan for vulnerabilities** in CI: `dotnet list package --vulnerable
  --include-transitive` (fail the job on findings).
- **Minimize count** — don't add a package for what the base class library
  already does. Review each new dependency: maintained? known CVEs?

> Versions above are illustrative placeholders — pin to the actual current
> releases at setup time; the **discipline** (central, pinned, scanned, minimal)
> is the rule, not these exact numbers.
