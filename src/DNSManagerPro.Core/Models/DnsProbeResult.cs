using System.Net;

namespace DNSManagerPro.Core.Models;

/// <summary>Result of a single direct DNS query to a specific server.</summary>
public sealed record DnsProbeResult(
    IPAddress Server,
    string Domain,
    bool Success,
    double ResponseTimeMs,
    string ResponseCode,
    string? Error);
