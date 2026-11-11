using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace InventorySimulator;

internal static class LocalOnly
{
    private static bool _launchAllowed;
    private static bool _localMapActive;
    private static ulong _account;

    internal static bool Initialize(BasePlugin plugin)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(plugin.ModuleDirectory, "local-only.json")));
            _account = ulong.Parse(json.RootElement.GetProperty("PlayerSteamId").GetString()!);
            _launchAllowed = json.RootElement.GetProperty("Enabled").GetBoolean()
                && _account != 0 && OperatingSystem.IsWindows()
                && LocalOnlyPolicy.AllowsLaunch(Process.GetCurrentProcess().ProcessName,
                    GetNativeArguments());
        }
        catch (Exception error)
        {
            _launchAllowed = false;
            plugin.Logger.LogError(error, "[LocalOnly] Invalid local-only.json; disabled.");
        }
        plugin.Logger.LogInformation(_launchAllowed
            ? "[LocalOnly] Insecure client detected; cosmetics are restricted to the configured local host."
            : "[LocalOnly] Disabled: requires cs2 -insecure, without -dedicated.");
        return _launchAllowed;
    }

    internal static void MapStarted() => _localMapActive = true;
    internal static void MapEnded() => _localMapActive = false;

    internal static bool CanApply(CCSPlayerController? player)
    {
        if (!_launchAllowed || !_localMapActive || player is not { IsValid: true })
            return false;
        try
        {
            return LocalOnlyPolicy.AllowsPlayer(_launchAllowed, _localMapActive,
                _account, player.SteamID, player.IsBot, player.IpAddress);
        }
        catch
        {
            return false;
        }
    }

    // The client inventory hook can also run while connected to a remote server.
    // Require a live local server and its loopback host before replacing any item.
    internal static bool IsActiveSession()
    {
        if (!_launchAllowed || !_localMapActive)
            return false;
        try { return Utilities.GetPlayers().Any(CanApply); }
        catch { return false; }
    }

    private static string[] GetNativeArguments()
    {
        var pointer = CommandLineToArgvW(GetCommandLineW(), out var count);
        if (pointer == IntPtr.Zero)
            return [];
        try
        {
            var args = new string[count];
            for (var i = 0; i < count; i++)
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally { LocalFree(pointer); }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCommandLineW();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(IntPtr commandLine, out int argc);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
