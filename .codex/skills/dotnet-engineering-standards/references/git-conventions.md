# Git Conventions

Branch / commit / PR conventions for this repo. These are enforced socially and,
where possible, by the branch ruleset and CI in `references/ci-and-precommit.md`.
Language-neutral — they apply identically to the .NET codebase.

## Contents
1. Branch naming
2. Commit messages (Conventional Commits)
3. Pull requests

---

## 1. Branch naming

Pattern: `type/short-description` — lowercase, hyphen-separated, 2–4 words.

| Prefix      | Purpose               | Example                          |
|-------------|-----------------------|----------------------------------|
| `feature/`  | New functionality     | `feature/run-approval-gate`      |
| `bugfix/`   | Bug fix               | `bugfix/rls-context-reset`       |
| `hotfix/`   | Urgent production fix | `hotfix/worker-crash-on-resume`  |
| `refactor/` | Restructuring         | `refactor/extract-media-tool`    |
| `docs/`     | Documentation only    | `docs/architecture-readme`       |
| `test/`     | Tests only            | `test/publishing-failure-paths`  |
| `chore/`    | Tooling/maintenance   | `chore/pin-dependencies`         |

Rules:
- Lowercase + hyphens only — **no underscores, no spaces, no capitals**.
- Include a ticket id when one exists: `feature/AIE-42-approval-gate`.
- **Never commit directly to `main` or `develop`.**
- Delete merged branches promptly.

---

## 2. Commit messages (Conventional Commits)

Format: `type(scope): imperative summary`

| Type       | When |
|------------|------|
| `feat`     | a new feature |
| `fix`      | a bug fix |
| `docs`     | documentation only |
| `style`    | formatting, no logic change |
| `refactor` | neither fixes nor adds a feature |
| `test`     | adding/correcting tests |
| `chore`    | build/deps/tooling |
| `perf`     | performance improvement |
| `security` | security fix or hardening |

Examples:

```
feat(runs): add human-approval gate before publish
fix(rls): reset brand context at transaction commit
test(publishing): cover retry-then-fail-item path
chore(deps): pin package versions centrally
```

Rules:
- Imperative mood ("add", not "added"/"adds").
- Summary under 72 chars, capitalized first letter, no trailing period.
- Optional body after a blank line for the *why*.

---

## 3. Pull requests

**Title:** `[TYPE] imperative description`

```
[FEATURE] Add human-approval gate before publish
[BUGFIX] Reset RLS brand context at commit
```

**Description template (required):**

```markdown
## Summary
What this PR does and why.

## Changes
- key change 1
- key change 2

## Testing
- how it was tested
- new tests added

## Checklist
- [ ] Follows the .NET engineering standards (this skill)
- [ ] Self-reviewed
- [ ] Tests added/updated and passing in CI
- [ ] Docs/README updated if needed
- [ ] No secrets or credentials in code, image, DB, or logs
- [ ] dotnet format + analyzers clean
```

**Practices:**
- Keep PRs small and focused — aim under ~400 changed lines.
- One concern per PR; don't mix a feature, a refactor, and a bug fix.
- At least one reviewer; resolve every comment before merge.
- **Squash-merge** to keep `main` history clean.
- Link related issues ("Closes #42").
