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
