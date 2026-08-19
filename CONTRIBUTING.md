# Contributing

UnBramble is maintained with limited time. Bug reports, documentation corrections, and focused fixes are welcome. There's no guaranteed response time, roadmap commitment, or obligation to accept a proposed change.

## Before starting

- Use the bug-report form for reproducible defects.
- Open a proposal before implementing a feature, new reference form, architectural change, or large refactor. Unsolicited large pull requests may be declined even when well implemented.
- Keep changes focused. A smaller change with a clear correctness case is easier to review and maintain.
- Follow [SECURITY.md](SECURITY.md) for vulnerabilities. Never put security details in a public issue.

## Protect project data

Don't submit proprietary Unity assets, credentials, real project or company names, absolute machine paths, or unredacted logs. Reproduce parser and graph issues with the smallest synthetic fixture or serialization fragment that demonstrates the behavior.

## Build and test

Follow [docs/building.md](docs/building.md) for the development environment.

Before opening a pull request:

```powershell
./scripts/verify-all.ps1 -SkipPublish
```

Run the complete `./scripts/verify-all.ps1` sequence when changing scanning, parsing, storage, monitoring, liveness, publishing, or smoke-test behavior. Add a focused regression test for behavior changes. Read [docs/architecture.md](docs/architecture.md) before changing the scanner, parser, store, graph walks, freshness, or liveness logic.

## Pull requests

- Link the bug or proposal discussed before the change.
- Explain the correctness case and any user-visible behavior.
- Record the verification commands and results.
- Update the nearest relevant documentation when behavior or guarantees change.
- You're responsible for reviewing and understanding everything you submit, including AI-assisted work.

The maintainer may ask for changes or close a pull request that doesn't fit the project's scope, risk tolerance, or maintenance budget.

## License

By submitting a contribution, you agree to license it under the repository's [MIT License](LICENSE) and confirm you hold the right to do so.
