# DNS Manager Pro

Windows desktop app (WPF, .NET 10) to switch DNS providers, benchmark resolvers, manage custom DNS profiles and troubleshoot DNS. Local-only: no account, no telemetry, no cloud.

> Status: **Phase 4 of 8 complete** (shell, dashboard, provider library, adapter manager, safe DNS apply, benchmark). Applying DNS, benchmarking, profiles, diagnostics, history and settings are in later phases - see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Build and run

Requires Windows 10/11 and the .NET 10 SDK.

```powershell
dotnet build DNSManagerPro.slnx
dotnet test src/DNSManagerPro.Tests
dotnet run --project src/DNSManagerPro.App
```

## What works today

- Collapsible sidebar with all ten sections and a global status bar (connectivity, active adapter, DNS, privilege, background activity).
- Dashboard populated from the real system: active adapter, IPv4/IPv6 DNS servers, automatic vs manual mode (per IP family), provider recognition, gateway, addresses, and a live direct-UDP DNS latency chart.
- Flush DNS cache (needs administrator rights; the app reports this clearly instead of failing silently).

- **DNS Providers**: seven built-in providers (Cloudflare, Google, Quad9, OpenDNS, AdGuard, Control D, NextDNS template) stored in SQLite. Add, edit, delete custom providers, restore built-ins, view privacy/security/filtering details, and run a quick real latency check per provider. Strict IPv4/IPv6/URL validation. 
- **Network Adapters**: all real adapters (NDIS filter bindings hidden), per-adapter IP/gateway/DHCP and DNS servers with automatic-vs-manual mode per IP family.

- **Apply DNS safely**: *Quick Apply* (providers), *Restore automatic DNS* (dashboard/adapters) and *Restore previous settings* (adapters) open a review dialog: pick the adapter, choose per IP family (IPv4 and IPv6 are independent) to leave unchanged / use the provider / go automatic, and see the exact before -> after preview and warnings. Applying then: saves a backup first, changes the settings, verifies they read back correctly, checks the new server answers a real DNS query, flushes the cache, and **rolls back automatically** if any step fails.
- **Permissions**: changing DNS needs administrator rights. The app starts as a normal user; when you apply it explains why and offers *Restart as administrator* (Windows UAC prompt), reopening the same review. Nothing is changed without pressing *Apply changes*.

- **DNS Benchmark**: direct queries (never the OS resolver) over UDP, optional TCP and optional DNS-over-HTTPS, against any mix of built-in/custom providers, your current DNS and custom IPs. Configurable domains, repetitions, timeout, retries, parallel limit, A/AAAA, and **warm vs cold cache** handling. Live progress with Stop (partial results are kept). Per server: min, mean, median, max, P95/P99, standard deviation, success %, timeout %, failed count, ranked in a sortable table with notes (low sample count, unreliable, within noise). Charts: average latency, min/median/max range, reliability, and median over time across runs. Export raw CSV, summary CSV and JSON. *Switch to fastest* / *Use...* open the normal review-before-apply dialog. Results are saved in SQLite and feed the dashboard.
- **Honest conclusions**: the app never calls a provider "objectively fastest". Servers within max(2 ms, 10%) of the leader are reported as indistinguishable, low sample counts are flagged, and the page explains caching and network variability.

The dashboard's "last benchmark" fields stay empty until the benchmark engine ships (Phase 4).



