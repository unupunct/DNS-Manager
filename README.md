# DNS Manager Pro

Windows desktop app (WPF, .NET 10) to switch DNS providers, benchmark resolvers, manage custom DNS profiles and troubleshoot DNS. Local-only: no account, no telemetry, no cloud.

> Status: **Phase 7 of 8 complete** (shell, dashboard, provider library, adapter manager, safe DNS apply, benchmark, custom DNS and profiles, diagnostics, history, monitoring, settings, data backup/restore). Applying DNS, benchmarking, profiles, diagnostics, history and settings are in later phases - see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

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

- **Custom DNS**: a full-page editor with live validation (IPv4/IPv6 addresses, DoH URL, DoT/DoQ hosts), start-from-provider shortcuts, optional adapter association, tags and notes. *Save*, *Save as new copy*, *Save and apply*, or *Apply without saving*. Plain DNS server IPs and encrypted-DNS endpoints are kept clearly separate: applying IPs never claims to enable encryption.
- **DNS Profiles**: create, edit, duplicate, delete, filter and search named profiles (Default / Gaming / Privacy / Security / Family / Custom), templates to start from, **export/import as JSON** (imports are size-limited, validated and given fresh ids; machine-specific data is not exported), created / last-applied dates, and adapter association. Everything is stored in SQLite.
- **Apply to chosen adapters**: the review dialog lets you tick one or more adapters; each gets its own backup, verification and rollback, and unticked adapters are never touched.
- **Encrypted DNS on Windows 11**: for providers/profiles with a DoH URL the dialog offers an optional step that registers it with Windows (`netsh dns add encryption`, auto-upgrade, plain-DNS fallback allowed). Windows has no setting for DoT or DoQ, so those endpoints are stored for reference only.

- **DNS Diagnostics**: *Automated check* walks through connectivity, default gateway, each configured DNS server, Windows name resolution, comparison with public resolvers, analysis and a saveable report; findings separate **detected problems** (observed) from **possible causes** (not confirmed). *Lookup*: A, AAAA, MX, TXT, NS, CNAME, SOA and reverse (PTR) queries against your DNS, any provider or a custom server, with response time, response code, records and errors. *Propagation*: asks every resolver the same question and groups the answers (and explains that CDNs legitimately differ). *DNS cache*: inspect and flush the Windows cache (both need administrator rights; the app says so and offers a restart).
- **History**: one timeline of DNS configuration changes (before/after, including failed and rolled-back ones), benchmarks, diagnostics and monitoring events; filter by type, time window or failures only, search, select and delete, export CSV/JSON.
- **Monitoring** (optional, off by default): every 1-60 minutes it reads the adapter's DNS settings and sends one DNS query. Reports changes made outside the app, resolution failures (after two in a row, to ignore packet loss) and recovery, once per transition, with optional desktop notifications. Changes made by the app itself are not reported as outside changes.

The dashboard's "last benchmark" fields stay empty until the benchmark engine ships (Phase 4).





