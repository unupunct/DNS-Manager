using System.Security.Principal;
using DNSManagerPro.Core.Abstractions;

namespace DNSManagerPro.Windows.Services;

public sealed class WindowsPrivilegeService : IPrivilegeService
{
    public bool IsElevated { get; } = Detect();

    private static bool Detect()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
