# Contributing

UnBramble is maintained with limited time. Bug reports, documentation corrections, and focused fixes are welcome, but there's no guaranteed response time or roadmap commitment.

## Before you start

- Use the bug-report form for reproducible defects.
- Open a proposal before implementing a feature, new reference form, architectural change, or large refactor.
- Keep changes focused and read [architecture.md](docs/architecture.md) before changing scanning, parsing, storage, freshness, or liveness behavior.
- Report vulnerabilities through [SECURITY.md](SECURITY.md), never a public issue.

## Protect project data

Don't submit proprietary Unity assets, credentials, real project or company names, absolute machine paths, or unredacted logs. Reproduce parser and graph issues with the smallest synthetic fixture or serialization fragment that demonstrates the behavior.

## Verify your change

Run this before opening a pull request:

```powershell
./scripts/verify-all.ps1 -SkipPublish
```

Run the complete `./scripts/verify-all.ps1` sequence for changes to scanning, parsing, storage, monitoring, liveness, publishing, or smoke tests. Add a focused regression test for behavior changes.

In the pull request, link the relevant issue, explain the correctness case and user-visible behavior, and record verification results. Update nearby documentation when a guarantee changes. You're responsible for understanding everything you submit, including AI-assisted work.

Contributions are licensed under the repository's [MIT License](LICENSE).
