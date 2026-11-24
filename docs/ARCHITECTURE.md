# Architecture

```
src/
  DNSManagerPro.Core            models, interfaces, built-in provider data (no OS or UI dependencies)
  DNSManagerPro.Windows         Windows adapter discovery, privilege check, cache flush
  DNSManagerPro.Networking      direct DNS queries (DnsClient, UDP)
  DNSManagerPro.Data            SQLite persistence (Phase 5)
  DNSManagerPro.Infrastructure  app paths, file logging
  DNSManagerPro.App             WPF shell, MVVM view models, theme
  DNSManagerPro.Tests           xUnit
```

Rules: the App depends on interfaces from Core; Windows-specific code lives only in `Windows`; the DNS engine never references UI types.

## Phase 1 decisions

- **Automatic vs manual DNS** is read from the per-interface `NameServer` registry value Windows uses (readable without admin), per IP family.
- **Latency** on the dashboard is a direct UDP query (not the OS resolver), explicitly labelled a single, possibly cache-warm sample.
- **Theme**: custom resource dictionaries (`Theme/`) rather than a third-party UI kit, to keep control of the look and avoid dependency churn.
- Analyzer warnings are errors; `CA1848/CA1873` (logger delegates) are disabled as noise.

## Roadmap
2 providers/adapters/config read - 3 apply, backup, rollback, verify - 4 benchmark + charts + export - 5 custom DNS, profiles, SQLite - 6 diagnostics, history, monitoring - 7 settings, hardening, tests - 8 performance, docs, installer, release.

## Phase 2 decisions

- **Persistence**: SQLite via Microsoft.Data.Sqlite, schema versioned with PRAGMA user_version migrations (SqliteDatabase). Providers and detected adapters are tables; later phases add migrations. Built-ins are seeded once so user edits survive restarts; *Restore built-ins* resets them.
- **Validation** lives in Core.Providers.DnsValidator and is enforced both in the editor (live) and in the repository (defence in depth).
- **Encrypted DNS**: DoH/DoT/DoQ endpoints are stored as provider metadata only. Applying server IPs never claims to enable them.
- **Adapters**: NDIS filter bindings (QoS, WFP, Npcap...) are excluded; WAN Miniport/Bluetooth are classified Virtual and hidden unless *Show all adapters* is ticked.


## Phase 3 decisions

- **Engine**: Core.Configuration.DnsChangeService = validate -> require elevation -> read adapter fresh -> persist backup (DNSHistory, *before* changing) -> apply via IDnsConfigurator -> verify read-back (retries) -> probe the new server with a real query -> flush cache -> mark success; any failure triggers automatic rollback to the snapshot. It depends only on interfaces, so it is unit-tested with an in-memory fake OS (nothing touches the real network in tests).
- **OS change**: 
etsh interface ipv4|ipv6 set|add dnsservers with an argument *list* (no shell) built only from an integer interface index and parsed IPAddress values. Chosen over WMI (IPv4 only) and PowerShell (startup cost, quoting surface).
- **Elevation**: the app runs as a standard user; ElevationService relaunches with the UAC unas verb and a command-line intent (--apply-provider <id>, --restore-automatic, --restore-previous <adapterId>) so the review reopens. Only one adapter is ever changed per operation; there is no "all adapters" action.
- **History schema** (migration v2): DNSHistory(AdapterId, AdapterName, PreviousConfiguration JSON, NewConfiguration, Operation, Result, Message, Timestamp). Restore-previous uses the latest Success row for the adapter.

## Phase 4 decisions

- **Engine** (Core.Benchmark): `BenchmarkRunner` interleaves work (rotating server order each round, capped parallelism), optional warm-up queries (Warm mode) or random sub-domain names (Cold mode, NXDOMAIN counts as answered), retries only after a failure, cancellation returns the partial run. It depends on `IDnsQueryClient` only, so it is tested with fakes.
- **Transports** (`Networking.DnsQueryClient`): UDP/TCP through DnsClient with no client cache; DoH implemented on the RFC 8484 wire format (`DnsWire`) over a pooled HTTP/2 connection (handshake excluded after warm-up). Socket-level failures (e.g. TCP/53 blocked by a firewall) are reported with their real reason instead of a generic error.
- **Statistics**: sample standard deviation; percentiles by linear interpolation; ranking by median then P95; a server needs >= 50% answered queries to be ranked; ties use `WithinNoise` = max(2 ms, 10%) measured against the leader.
- **Persistence** (migration v3): `BenchmarkRuns` + `BenchmarkResults` (raw samples, cascade delete). Warm-up queries are not stored.
- **Export**: RFC 4180 CSV with spreadsheet-formula neutralisation; JSON includes raw samples, summary, conclusion and a limitations statement.
- **Charts** are small custom WPF controls (`FillBar`, `RangeBar`, `MultiLineChart`) instead of a charting dependency.

## Phase 5 decisions

- **Custom DNS vs Profiles**: both pages work on one store (`DnsProfile` in `DNSProfiles`, migration v4). *Custom DNS* is the editor, *DNS Profiles* is the gallery (apply / duplicate / export / import / filter). `ProfileService` is the single access point and raises `Changed` so both stay in sync; `PendingEditId` / `StartNew` hand intent from one page to the other.
- **Automatic profiles**: `IsAutomatic` profiles carry no addresses and restore DHCP DNS through the normal review dialog.
- **Import hardening** (`ProfileExchange`): format + version header, 1 MB and 500-profile caps, every entry validated with the same rules as the editor, provider ids and adapter ids dropped, fresh ids, name collisions get a numeric suffix. Never throws on bad input.
- **Multi-adapter apply**: `ApplyDnsViewModel` builds one plan per ticked adapter and runs them sequentially through `DnsChangeService` (backup / verify / rollback per adapter). It stops at the first failure and reports which adapters keep the new settings. Only the preselected adapter is ticked by default.
- **Encrypted DNS**: `IEncryptedDnsService` (`netsh dns add encryption` with an argument list) runs only after plain DNS was applied and verified, is opt-in, and its failure never rolls back working plain DNS. Search-domain suffix lists are intentionally not managed (global OS setting, out of scope).

## Phase 6 decisions

- **Rules engine** (`DiagnosticRules`) is a pure function over collected facts, so every finding is unit-tested. A finding has *Detected* (what the tests observed) and *PossibleCauses* (common explanations, explicitly unconfirmed) so the UI and report never present guesses as facts. The workflow (`DiagnosticsWorkflow`) only gathers facts through interfaces (adapters, direct lookups, ICMP ping, OS resolver).
- **Why compare direct queries with the OS resolver**: a failure only in the Windows resolver while the server answers directly points at local causes (cache, DNS Client service, hosts/VPN), which is the most common support case.
- **Cache inspection** uses a fixed `Get-DnsClientCache` PowerShell command. Windows returns nothing (not an error) to non-administrators, which would look like an empty cache, so the app refuses with a clear message instead.
- **Propagation** does not call differing answers wrong: CDN / geo-DNS variation is normal and the summary says so.
- **History** is a read-only union (`SqliteTimelineRepository`) over `DNSHistory`, `BenchmarkRuns`, `DiagnosticRuns` (migration v5) and `MonitorEvents` (v5); deleting removes the underlying rows and warns that deleting configuration entries also removes their restore backups.
- **Monitoring**: `MonitorStateMachine` is pure (thresholded failures, transition-only events, silent adoption of the app's own changes); `MonitorService` runs it on a `PeriodicTimer` (one adapter read + one UDP query per tick) and is configured through `SettingsService` (atomic JSON file, clamped values, corrupt file set aside). Notifications use a tray icon balloon (Windows toast on 10/11).

## Phase 7 decisions

- **Settings model**: `AppSettings` is one immutable record; `Sanitized()` clamps every number, re-validates benchmark domains with `DnsValidator`, drops unknown enum values and control characters in the adapter id. It runs on load, on every update and on import, so a hand-edited or imported file can never put the app in an invalid state. Enums are stored by name. Files over 256 KB are refused before parsing.
- **Confirmation setting**: the apply dialog always shows the full preview. With "second confirmation" on (default) the first click only *arms* the button ("Yes, apply these changes"); changing the adapter selection disarms it. Done inside the dialog, not as a nested window, so it cannot appear behind the modal.
- **Settings that change behaviour** are applied where they act: `ApplyFlow` passes `ApplyDnsOptions` (confirmation, IPv6 preference, pre-ticked DoH) and the default adapter; `BenchmarkViewModel` follows changed benchmark defaults; `MainWindow` hides on minimize when "minimize to tray" is on; `App` honours "start minimized" and the `--minimized` flag (ignored when a UAC-restart intent is pending, so a requested action is never hidden).
- **Start with Windows**: per-user `HKCU\...\Run` value only (no admin, no service). The command is `"<exe>" [--minimized]`; paths containing quotes or control characters are rejected. Registry failure reverts the checkbox and shows the reason.
- **Data tools** (`DatabaseMaintenance`): backup uses `VACUUM INTO` to a temp file then an atomic move (a consistent copy even while the app is open); restore first *validates* the file read-only (integrity check, `user_version` between 1 and the current schema, required tables) and refuses newer-version or foreign databases, keeps a `before-restore-*.db` copy under `backups\`, swaps the file, re-runs migrations, then restarts the app so no view holds stale data. Clearing history is one transaction and keeps providers and profiles.
- **Hardening review** (no change needed unless listed): every external process is started with `UseShellExecute=false`, an absolute System32 path and an argument list; netsh arguments come only from an interface index and parsed `IPAddress` values, DoH templates are validated as https URLs first; no user text reaches PowerShell (fixed script). Elevation-restart intents only select an *existing* provider/profile by id and still open the review dialog. Added: settings size cap, import/restore validation, registry-command quoting guard. Logs rotate at 14 days and contain no credentials (the app handles none).

## Phase 8 decisions

- **About page**: `AboutViewModel` shows the version (from `AssemblyInformationalVersion`, build metadata stripped), runtime, copyright, principles and third-party licences. The placeholder page was removed. The data folder is displayed as `%LocalAppData%\DNSManagerPro` so screenshots do not expose the user name.
- **Version and icon**: version comes from `Directory.Build.props`; the executable is `DNSManagerPro.exe` with `assets/app.ico`, generated by `scripts/make-icon.ps1` (no external artwork).
- **Release build** (`scripts/build-release.ps1`): runs the tests, publishes self-contained `win-x64` with ReadyToRun (faster start, no .NET install needed; WPF cannot be trimmed), then builds the portable zip, the MSI and `SHA256SUMS.txt` into `dist\`.
- **Installer** (`installer/Product.wxs`, WiX v5): per-machine MSI under Program Files, Start Menu shortcut, optional desktop shortcut (feature, off by default), major-upgrade support, license page. The file list is generated from the publish folder at build time. Uninstall removes the per-user startup entry of the uninstalling user but deliberately keeps `%LocalAppData%\DNSManagerPro` (user data).
- **Performance**: startup work is already limited to DI and migrations; benchmark and diagnostics run off the UI thread; ReadyToRun reduces JIT cost at launch.
