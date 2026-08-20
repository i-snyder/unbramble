# Security

Report vulnerabilities through GitHub private vulnerability reporting. If that isn't available, open a minimal public issue asking for a private contact channel without including vulnerability details.

Never publish private Unity project content, filesystem layouts, credentials, exploit details, or security-sensitive logs.

Security-sensitive areas include path traversal, unintended writes, command or PowerShell injection, unsafe link handling, stale-index answers, privilege boundaries, and Windows Defender exclusions. Defender changes require explicit consent, show every proposed exclusion, and can be removed with `unbramble defender remove`.

Security fixes target the latest release and current `main`.
