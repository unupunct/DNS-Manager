using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DNSManagerPro.Core.History;

public enum TimelineKind
{
    ConfigChange,
    Benchmark,
    Diagnostic,
    Monitoring,
}

public enum TimelineStatus
{
    Success,
    Info,
    Warning,
    Failed,
}

/// <summary>Identifies the source row of a timeline entry so it can be deleted.</summary>
public readonly record struct TimelineKey(TimelineKind Kind, string Id);

public sealed record TimelineEntry(
    TimelineKey Key,
    DateTime TimestampUtc,
    string Title,
    string Detail,
    TimelineStatus Status,
    string? Adapter = null)
{
    public TimelineKind Kind => Key.Kind;

    public bool IsFailure => Status == TimelineStatus.Failed;
}

public enum TimeWindow
{
    Day,
    Week,
    Month,
    All,
}

public sealed record TimelineFilter(TimelineKind? Kind, bool FailuresOnly, TimeWindow Window, string Search)
{
    public static TimelineFilter None { get; } = new(null, false, TimeWindow.All, string.Empty);

    public IEnumerable<TimelineEntry> Apply(IEnumerable<TimelineEntry> source, DateTime nowUtc)
    {
        var cutoff = Window switch
        {
            TimeWindow.Day => nowUtc.AddDays(-1),
            TimeWindow.Week => nowUtc.AddDays(-7),
            TimeWindow.Month => nowUtc.AddDays(-30),
            _ => DateTime.MinValue,
        };
        var q = Search.Trim();
        return source
            .Where(e => (Kind is null || e.Kind == Kind) && (!FailuresOnly || e.IsFailure) && e.TimestampUtc >= cutoff
                && (q.Length == 0 || e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (e.Adapter?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)))
            .OrderByDescending(e => e.TimestampUtc);
    }
}

public static class TimelineExporter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string Csv(IEnumerable<TimelineEntry> entries)
    {
        var sb = new StringBuilder("TimestampUtc,Kind,Status,Adapter,Title,Detail\r\n");
        foreach (var e in entries)
        {
            sb.AppendJoin(',', e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture), e.Kind, e.Status,
                Benchmark.BenchmarkExporter.Esc(e.Adapter), Benchmark.BenchmarkExporter.Esc(e.Title), Benchmark.BenchmarkExporter.Esc(e.Detail)).Append("\r\n");
        }

        return sb.ToString();
    }

    public static string JsonText(IEnumerable<TimelineEntry> entries) => JsonSerializer.Serialize(
        entries.Select(e => new { e.TimestampUtc, Kind = e.Kind.ToString(), Status = e.Status.ToString(), e.Adapter, e.Title, e.Detail }), Json);
}
