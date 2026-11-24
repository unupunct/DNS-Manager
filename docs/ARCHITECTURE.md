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
