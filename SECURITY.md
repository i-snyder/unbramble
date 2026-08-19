# Security

## Reporting a vulnerability

Once GitHub private vulnerability reporting is enabled for the public repository, use it for security reports. Do not put exploit details, private Unity project content, filesystem layouts, or security-sensitive logs in a public issue.

If private reporting is unavailable, open a minimal issue asking the maintainer to establish a private contact channel. Include no vulnerability details in that issue.

## Scope worth treating carefully

UnBramble recursively reads Unity projects, follows configured junctions/symlinks, starts a background watcher, writes `.unbramble/` state, and can optionally request administrator approval to add Windows Defender exclusions. Reports involving path traversal, unintended writes, command or PowerShell injection, unsafe link handling, stale-index answers, privilege boundaries, or exclusions applied without affirmative consent are security-sensitive.

The Defender feature prints every proposed process/path exclusion and requires an explicit `y`/`yes` before showing UAC. `unbramble defender remove` removes only entries recorded by UnBramble.

## Support window

This is an early pre-release. Security fixes target the latest commit on `main`; no older-version support window is promised yet.
