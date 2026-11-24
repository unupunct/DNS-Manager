# Security and privacy

## What the app does and does not do

- **Local only.** No account, no telemetry, no analytics, no update checks, no cloud service. The only network traffic is what you ask for: DNS queries (benchmark, diagnostics, verification after a change, optional monitoring) and optional DNS-over-HTTPS queries to the provider you selected. The *Project page* button opens your browser only when you click it.
- **Local data.** Database, settings and logs are stored under `%LocalAppData%\DNSManagerPro`. Nothing is uploaded. Exports and backups are files you choose.
- **No secrets handled.** The app asks for no passwords, API keys or tokens, so none are stored or logged.

## Privileges

- Runs as a standard user. Only the actions that need it (changing DNS, reading/flushing the DNS cache) require administrator rights.
- When elevation is needed, the app asks first and relaunches itself through the normal Windows UAC prompt with an *intent* argument that only selects an existing provider or profile by id. The review dialog is shown again; nothing is applied without clicking *Apply changes* (and, by default, confirming a second time).
- "Start with Windows" is a per-user registry value, not a service or scheduled task, and needs no administrator rights.

## Safe changes

Every DNS change: preview, backup first, apply, verify by read-back, test with a real query, flush cache, and automatic rollback if anything fails. Backups and results are recorded in History.

## Input validation and execution

- IP addresses, URLs, host names, profile and settings files are validated; imports are size-limited (profiles 1 MB, settings 256 KB) and cannot inject identifiers.
- **No arbitrary shell commands.** External programs (`netsh`, `ipconfig`, `explorer`, a fixed PowerShell script) are started without a shell, from absolute system paths, with argument lists built only from parsed values (interface index, `IPAddress`, validated https DoH templates). User text is never concatenated into a command line or script.
- Database access uses parameterised SQL. Database restore checks integrity, schema version, and required tables before replacing anything, and keeps a copy of the current database.

## Distribution

Release binaries are not code-signed. Verify downloads with the published SHA-256 hashes. Build from source if you prefer; the build is reproducible with the .NET 10 SDK and, for the installer, WiX v5.

## Reporting a vulnerability

Please report security issues privately through the repository's *Security* tab (GitHub private vulnerability reporting) rather than a public issue. Include the version (*About, Copy version details*) and steps to reproduce.

## Known limitations

- Windows has no built-in setting for DNS over TLS or QUIC; those endpoints are stored for reference only.
- DNS-over-HTTPS registration uses the Windows 11 `netsh dns` encryption feature; it is not available on Windows 10.
- Other software (VPNs, security suites, group policy) may override DNS settings after the app applies them.
