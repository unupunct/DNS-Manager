# User guide

## The window

The sidebar lists the ten sections; the button at its bottom collapses it. The status bar always shows connectivity, the active adapter, its DNS servers (and whether they are automatic or manual), the privilege level, and any background activity.

## Dashboard

Shows the real state of your active adapter: DNS servers for IPv4 and IPv6, automatic vs manual mode, the provider it recognises, gateway and addresses, and a live DNS latency chart. From here you can *restore automatic DNS* or flush the DNS cache (administrator needed).

## DNS Providers

A library of well-known resolvers (Cloudflare, Google, Quad9, OpenDNS, AdGuard, Control D, and a NextDNS template). Open one to see its addresses, privacy notes, and filtering. You can add, edit and delete your own, restore the built-ins, and run a quick latency check. *Quick apply* opens the review dialog (see below).

## Applying DNS safely

Every change goes through the same steps:

1. **Review**: choose the adapter(s) and, separately for IPv4 and IPv6, whether to leave unchanged, use the provider's servers, or go automatic. You see the exact *before, after* preview and warnings.
2. **Confirm**: with the default setting, the first click only arms the button; click again to apply.
3. **Apply**: your current settings are saved as a backup first, the change is made, read back to confirm, tested with a real DNS query, and the Windows cache is flushed.
4. **Rollback**: if any step fails, the previous settings are restored automatically and the failure is shown.

*Restore previous settings* on the Adapters page reverts to a saved backup. If the app is not elevated it offers to restart as administrator and reopens the same review.

Plain DNS server addresses do not encrypt anything. On Windows 11, a provider with a DNS-over-HTTPS address offers an optional extra step that registers it with Windows. DoT and DoQ endpoints are stored for reference only because Windows has no setting for them.

## DNS Benchmark

Queries each chosen server directly (not through the Windows resolver) for your list of domains, over UDP and optionally TCP and DoH. You can set repetitions, timeout, retries, parallelism, record types, and warm vs cold cache. *Stop* keeps partial results. The table gives min, mean, median, max, P95, P99, standard deviation, success and timeout rates. Charts show averages, ranges, reliability and the trend across runs. Results export as CSV or JSON.

The app does not claim an "objectively fastest" provider. Servers within the larger of 2 ms or 10% of the leader are shown as indistinguishable, low sample counts are flagged, and caching and network variation are explained. *Switch to fastest* only opens the normal review dialog.

## Custom DNS and Profiles

*Custom DNS* is an editor with live validation for IPv4, IPv6, DoH URLs and DoT/DoQ hosts. Save, save as a copy, save and apply, or apply without saving. *DNS Profiles* keeps named setups by category (Default, Gaming, Privacy, Security, Family, Custom) with search, duplicate, delete, and JSON import/export. Imported profiles are validated, size limited, and get new ids.

## Network Adapters

Lists real adapters with addresses, gateway, DHCP state and DNS servers. Virtual filter bindings are hidden. From here you can restore automatic DNS or restore a previous backup per adapter.

## DNS Diagnostics

- **Automated check**: connectivity, gateway, each configured DNS server, Windows name resolution, and comparison with public resolvers. Findings separate *detected* problems (observed) from *possible* causes (not confirmed). The report can be saved.
- **Lookup**: A, AAAA, MX, TXT, NS, CNAME, SOA and reverse queries against your DNS, a provider, or a custom server.
- **Propagation**: asks every resolver the same question and groups the answers. Differences are often normal (CDNs).
- **DNS cache**: inspect and flush the Windows cache (administrator needed).
- **Monitoring** (optional, off by default): periodically reads adapter DNS settings and runs one query; reports outside changes, failures and recovery once per transition.

## History

One timeline of configuration changes (including failed and rolled-back ones), benchmarks, diagnostics and monitoring events. Filter, search, delete and export.

## Settings

General (start with Windows, start minimized, tray, confirmation), benchmark defaults, monitoring, network defaults (default adapter, IPv6 handling, prefer DoH), and data tools: back up or restore the database, export or import settings, clear history, reset settings, open the data or log folder. Restore validates the file first, keeps a copy of your current data, then restarts the app.

## Troubleshooting

- **"Administrator rights needed"**: use *Restart as administrator* in the dialog. Nothing is changed until you apply.
- **DNS did not change after applying**: the app verifies by reading settings back and sending a query; if it reports a rollback, the message says which step failed. Some VPNs and security software override DNS and will undo changes.
- **Internet stopped working after a change**: choose *Restore automatic DNS* on the Dashboard, or run `ipconfig /flushdns` and try again.
- **Logs**: *Settings, Data and privacy, Open log folder*. Logs contain no passwords or tokens (the app handles none).
