using System.Globalization;
using System.Net;
using System.Text;

namespace DNSManagerPro.Core.Diagnostics;

public static class DiagnosticReport
{
    public static string Build(DateTime startedUtc, DiagnosticOverall overall, IReadOnlyList<DiagnosticStep> steps, IReadOnlyList<DiagnosticFinding> findings)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);
        L("DNS Manager Pro - diagnostic report");
        L(string.Create(CultureInfo.InvariantCulture, $"Run: {startedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} (local time)"));
        L($"Overall: {overall}");
        L("This report stays on this computer. It contains local network details; review it before sharing.");
        L();
        L("CHECKS PERFORMED");
        for (var i = 0; i < steps.Count; i++)
        {
            L($"{i + 1}. [{steps[i].Status.ToString().ToUpperInvariant()}] {steps[i].Title}");
            L($"     {steps[i].Detail}");
        }

        L();
        L("DETECTED PROBLEMS (observed by the tests above)");
        var problems = findings.Where(f => f.Severity != FindingSeverity.Info).ToList();
        if (problems.Count == 0)
        {
            L("  None.");
        }

        foreach (var f in problems)
        {
            L($"  - [{f.Severity}] {f.Detected}");
        }

        L();
        L("POSSIBLE CAUSES (not confirmed; these are common explanations for what was detected)");
        var any = false;
        foreach (var f in problems.Where(f => f.PossibleCauses.Count > 0))
        {
            any = true;
            L($"  For: {f.Detected}");
            foreach (var c in f.PossibleCauses)
            {
                L($"    * {c}");
            }

            if (f.Suggestion is not null)
            {
                L($"    Suggested next step: {f.Suggestion}");
            }
        }

        if (!any)
        {
            L("  Not applicable.");
        }

        var notes = findings.Where(f => f.Severity == FindingSeverity.Info).ToList();
        if (notes.Count > 0)
        {
            L();
            L("NOTES");
            foreach (var n in notes)
            {
                L($"  - {n.Detected}" + (n.Suggestion is null ? string.Empty : $" ({n.Suggestion})"));
            }
        }

        return sb.ToString();
    }
}

