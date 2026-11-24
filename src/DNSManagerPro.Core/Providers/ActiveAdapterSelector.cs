using DNSManagerPro.Core.Models;

namespace DNSManagerPro.Core.Providers;

/// <summary>Chooses the adapter that is most likely carrying the user's traffic.</summary>
public static class ActiveAdapterSelector
{
    public static NetworkAdapterInfo? Select(IEnumerable<NetworkAdapterInfo> adapters)
    {
        return adapters
            .Where(a => a.IsUp && a.Kind != AdapterKind.Virtual)
            .OrderByDescending(a => a.HasGateway)
            .ThenByDescending(a => a.IPv4Addresses.Count > 0)
            .ThenByDescending(a => a.SpeedBitsPerSecond)
            .FirstOrDefault();
    }
}
