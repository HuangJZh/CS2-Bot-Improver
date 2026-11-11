using System.Net;

namespace InventorySimulator;

// Local customization: fail closed outside this account's insecure listen server.
internal static class LocalOnlyPolicy
{
    internal static bool AllowsLaunch(string processName, IEnumerable<string> arguments)
    {
        var args = arguments.ToArray();
        return processName.Equals("cs2", StringComparison.OrdinalIgnoreCase)
            && args.Contains("-insecure", StringComparer.OrdinalIgnoreCase)
            && !args.Contains("-dedicated", StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsLoopback(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;
        if (address.Equals("loopback", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("loopback:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(address, out var ip))
            return IPAddress.IsLoopback(ip);
        return IPEndPoint.TryParse(address, out var endpoint)
            && IPAddress.IsLoopback(endpoint.Address);
    }

    internal static bool AllowsPlayer(bool launchAllowed, bool localMapActive,
        ulong configuredAccount, ulong playerAccount, bool isBot, string? address)
        => launchAllowed && localMapActive && configuredAccount != 0
            && playerAccount == configuredAccount && !isBot && IsLoopback(address);
}
