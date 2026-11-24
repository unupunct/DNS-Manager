using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DNSManagerPro.Core.Providers;

public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Ok { get; } = new(true, null);

    public static ValidationResult Fail(string error) => new(false, error);
}

/// <summary>Strict validation of anything that will be shown, saved or later applied to the system.</summary>
public static partial class DnsValidator
{
    public static ValidationResult ValidateIPv4(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValidationResult.Fail("Address is empty.");
        }

        var s = text.Trim();
        var parts = s.Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit)))
        {
            return ValidationResult.Fail($"'{s}' is not a valid IPv4 address (expected a.b.c.d).");
        }

        if (!IPAddress.TryParse(s, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return ValidationResult.Fail($"'{s}' is not a valid IPv4 address (octets must be 0-255).");
        }

        var b = ip.GetAddressBytes();
        if (b.All(x => x == 0))
        {
            return ValidationResult.Fail("0.0.0.0 cannot be used as a DNS server.");
        }

        if (b.All(x => x == 255))
        {
            return ValidationResult.Fail("255.255.255.255 (broadcast) cannot be used as a DNS server.");
        }

        if (b[0] >= 224)
        {
            return ValidationResult.Fail($"{s} is a multicast/reserved address, not a DNS server.");
        }

        return ValidationResult.Ok;
    }

    public static ValidationResult ValidateIPv6(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValidationResult.Fail("Address is empty.");
        }

        var s = text.Trim();
        if (s.Contains('%', StringComparison.Ordinal))
        {
            return ValidationResult.Fail("Scoped IPv6 addresses (with %) are not supported.");
        }

        if (!s.Contains(':', StringComparison.Ordinal) || !IPAddress.TryParse(s, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return ValidationResult.Fail($"'{s}' is not a valid IPv6 address.");
        }

        if (ip.Equals(IPAddress.IPv6Any))
        {
            return ValidationResult.Fail(":: (unspecified) cannot be used as a DNS server.");
        }

        if (ip.IsIPv6Multicast)
        {
            return ValidationResult.Fail("Multicast addresses cannot be used as a DNS server.");
        }

        if (ip.IsIPv6LinkLocal)
        {
            return ValidationResult.Fail("Link-local (fe80::) addresses need a scope and are not supported.");
        }

        return ValidationResult.Ok;
    }

    public static ValidationResult ValidateHttpsUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValidationResult.Ok;
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
        {
            return ValidationResult.Fail("DoH URL must be a valid https:// address.");
        }

        return ValidationResult.Ok;
    }

    public static ValidationResult ValidateHostName(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValidationResult.Ok;
        }

        var s = text.Trim();
        if (s.Length > 253 || !HostRegex().IsMatch(s))
        {
            return ValidationResult.Fail($"{what} must be a valid host name (letters, digits, dots, hyphens).");
        }

        return ValidationResult.Ok;
    }

    public static ValidationResult ValidateWebsite(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValidationResult.Ok;
        }

        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? ValidationResult.Ok
            : ValidationResult.Fail("Website must be an http(s) address.");
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostRegex();

    /// <summary>Validates a whole provider; returns all problems found (empty = valid).</summary>
    public static IReadOnlyList<string> ValidateProvider(DnsProvider p)
    {
        var errors = new List<string>();
        void Add(ValidationResult r)
        {
            if (!r.IsValid)
            {
                errors.Add(r.Error!);
            }
        }

        if (string.IsNullOrWhiteSpace(p.Name))
        {
            errors.Add("Name is required.");
        }
        else if (p.Name.Trim().Length > 60)
        {
            errors.Add("Name must be 60 characters or fewer.");
        }

        if (!string.IsNullOrWhiteSpace(p.IPv4Primary))
        {
            Add(ValidateIPv4(p.IPv4Primary));
        }

        if (!string.IsNullOrWhiteSpace(p.IPv4Secondary))
        {
            Add(ValidateIPv4(p.IPv4Secondary));
            if (string.IsNullOrWhiteSpace(p.IPv4Primary))
            {
                errors.Add("IPv4 secondary requires an IPv4 primary.");
            }
            else if (string.Equals(p.IPv4Primary.Trim(), p.IPv4Secondary.Trim(), StringComparison.Ordinal))
            {
                errors.Add("IPv4 primary and secondary are identical.");
            }
        }

        if (!string.IsNullOrWhiteSpace(p.IPv6Primary))
        {
            Add(ValidateIPv6(p.IPv6Primary));
        }

        if (!string.IsNullOrWhiteSpace(p.IPv6Secondary))
        {
            Add(ValidateIPv6(p.IPv6Secondary));
            if (string.IsNullOrWhiteSpace(p.IPv6Primary))
            {
                errors.Add("IPv6 secondary requires an IPv6 primary.");
            }
            else if (IPAddress.TryParse(p.IPv6Primary, out var a) && IPAddress.TryParse(p.IPv6Secondary, out var b) && a.Equals(b))
            {
                errors.Add("IPv6 primary and secondary are identical.");
            }
        }

        Add(ValidateWebsite(p.Website));
        Add(ValidateHttpsUrl(p.DohUrl));
        Add(ValidateHostName(p.DotHost, "DoT host"));
        Add(ValidateHostName(p.DoqHost, "DoQ host"));
        return errors;
    }
}
