# Security

## Reporting a vulnerability

Use GitHub private vulnerability reporting for security reports. Don't put exploit details, private Unity project content, filesystem layouts, or security-sensitive logs in a public issue.

If private reporting isn't available, open a minimal issue asking the maintainer to establish a private contact channel. Include no vulnerability details in that issue.

## Scope worth treating carefully

UnBramble recursively reads Unity projects, follows configured junctions/symlinks, starts a background watcher, writes `.unbramble/` state, and can optionally request administrator approval to add Windows Defender exclusions. Reports involving path traversal, unintended writes, command or PowerShell injection, unsafe link handling, stale-index answers, privilege boundaries, or exclusions applied without affirmative consent are security-sensitive.

The Defender feature prints every proposed process/path exclusion and requires an explicit `y`/`yes` before showing UAC. `unbramble defender remove` removes only entries recorded by UnBramble.

## Support window

Security fixes target the latest release and the current `main` branch. Older releases are unsupported unless a release note says otherwise.
