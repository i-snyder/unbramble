# AGENTS.md

UnBramble is a Windows CLI that builds a dependency graph of a Unity project — GUID/asset references (prefabs, scenes, materials, shaders, Addressables, ...) plus real C# semantic analysis (Roslyn) — so an agent can ask "what does this touch / what touches this" instead of grepping and missing Unity's hidden reference forms.

Keep docs terse, direct, and useful to someone encountering the repository for the first time.

## Start Here

- `README.md` — first-time user overview, release setup, and feature summary.
- `CONTRIBUTING.md` — contribution scope, privacy rules, and verification requirements.
- `docs/installing.md` — install, upgrade, uninstall, and manual ZIP instructions.
- `docs/building.md` — source-build instructions.
- `docs/releasing.md` — maintainer release workflow.
- `docs/comparison.md` — sourced technical comparison with adjacent Unity tools.
- `docs/architecture.md` — full design, invariants, known gaps. Read before touching the scanner, parser, store, or liveness code.
- `docs/validation-runbook.md` — validation procedure against a real Unity project and the per-project adoption gates.
- `src/UnBramble.Cli/`, `src/UnBramble.Core/`, `src/UnBramble.Tests/` — CLI entry points/verbs, core scanning/parsing/store/query logic, and the xUnit test suite, respectively.
- `scripts/verify-all.ps1` — the authoritative full verification sequence (build, tests, publish, smoke tests).

Note: `src/UnBramble.Cli/AgentInstructionsSetup.cs` writes a *different* `AGENTS.md` block — into the *consuming Unity projects* UnBramble indexes, telling agents there to use `who-uses`/`uses`/etc. instead of grep. That's unrelated to this file.

## Memory Routing

Record durable project knowledge in checked-in docs, not per-machine auto-memory. When unsure, ask.

- Project facts and decisions -> the nearest relevant checked-in doc (usually `docs/architecture.md`)
- Reusable artifacts -> none tracked yet (no `ref/`)
- Personal, machine-local, and cross-project preferences -> local memory only

No `docs/GOTCHAS.md` or `docs/TOOLS-REGISTER.md` exist yet — don't point at them until they do.

## Writing Style

Terse, direct tone throughout (README, architecture, runbook already follow this). Use natural contractions in prose. Don't preserve private project names, paths, customer/vendor details, or field-test narratives when the technical lesson can stand on its own.

No hard-wrapped lines in Markdown: one line per paragraph/list item, let the renderer soft-wrap. Never manually break prose at a column width.

README is a first-time end-user overview — positioning, release setup, and a compact command map. Contributor and implementation details belong in `CONTRIBUTING.md` and `docs/`.

## Agent Files

`AGENTS.md` (this file) is canonical. `CLAUDE.md` is a tiny shim that imports it — don't add content there.

## Verification

Use the repository wrapper for ordinary verification. It selects a complete x64 .NET 10 installation and neutralizes stale `DOTNET_ROOT` values:

```powershell
./scripts/verify-all.ps1 -SkipPublish
```

580 tests as of this writing. Run `./scripts/verify-all.ps1` locally before pushing changes to scanning, parsing, storage, monitoring, liveness, publishing, or smoke-test behavior.

## Public Release

[`docs/releasing.md`](docs/releasing.md) is the sole release and repository-visibility checklist.

Dependabot is intentionally not configured. Don't add `.github/dependabot.yml`, automated security fixes, or dependency-update PRs unless the maintainer explicitly asks for them. The Verify workflow is manual-only for now; run it from GitHub's Actions page or with `gh workflow run Verify`. It checks all direct and transitive NuGet dependencies for known vulnerabilities.

## Git

Commits are fine, but NEVER push unless the user explicitly asks. Maintainer-directed work may push directly to `main`; external contributions use pull requests once the contribution policy allows them.
