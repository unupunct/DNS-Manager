using System.Net;

namespace DNSManagerPro.Core.Diagnostics;

public enum LookupType
{
    A,
    AAAA,
    MX,
    TXT,
    NS,
    CNAME,
    SOA,
    PTR,
}

public sealed record DnsRecordInfo(string Type, string Name, int Ttl, string Data);

/// <summary>One direct DNS query to one server and everything it returned.</summary>
public sealed record LookupResult(
    string Domain,
    IPAddress Server,
    string ServerName,
    LookupType Type,
    double Milliseconds,
    string ResponseCode,
    IReadOnlyList<DnsRecordInfo> Records,
    string? Error,
    bool TimedOut)
{
    /// <summary>A DNS response arrived (whatever its code).</summary>
    public bool Answered => Error is null && !TimedOut;

    public bool Success => Answered && ResponseCode == "NoError";
}

public sealed record DnsCacheEntry(string Name, string Type, string Data, int TimeToLive, string Status);

public sealed record PingResult(bool Success, double Milliseconds, string Status);

public sealed record OsResolution(string Host, bool Success, double Milliseconds, IReadOnlyList<string> Addresses, string? Error);

public enum DiagnosticStatus
{
    Pass,
    Warning,
    Fail,
    Skipped,
    Info,
}

public sealed record DiagnosticStep(string Title, DiagnosticStatus Status, string Detail);

public enum FindingSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>What the tests actually observed (Detected) is kept apart from what might explain it (PossibleCauses, never confirmed).</summary>
public sealed record DiagnosticFinding(FindingSeverity Severity, string Detected, IReadOnlyList<string> PossibleCauses, string? Suggestion = null);

public enum DiagnosticOverall
{
    Healthy,
    Warnings,
    Problems,
}

public sealed record DiagnosticRun(
    Guid Id,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    IReadOnlyList<DiagnosticStep> Steps,
    IReadOnlyList<DiagnosticFinding> Findings,
    DiagnosticOverall Overall,
    string ReportText);

public sealed record DnsServerCheck(IPAddress Server, bool IsConfigured, bool Answered, double Milliseconds, string ResponseCode, string? Error);

public sealed record AdapterFacts(string Name, bool IsUp, bool HasIPv4, bool HasGateway, bool DnsManual, IReadOnlyList<IPAddress> DnsServers);

/// <summary>Raw observations collected by the workflow; the rules turn them into findings.</summary>
public sealed record DiagnosticFacts(
    AdapterFacts? Adapter,
    PingResult? Gateway,
    IReadOnlyList<DnsServerCheck> Configured,
    IReadOnlyList<DnsServerCheck> Alternatives,
    IReadOnlyList<OsResolution> OsResolutions);
