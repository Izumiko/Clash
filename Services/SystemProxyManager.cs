using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ClashXW.Services;

public static class SystemProxyManager
{
    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static void SetProxy(string proxyAddress)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsProxy(proxyAddress);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            SetLinuxProxy(proxyAddress);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            SetMacProxy(proxyAddress);
            return;
        }

        throw new PlatformNotSupportedException("System proxy is not supported on this platform.");
    }

    public static void DisableProxy()
    {
        if (OperatingSystem.IsWindows())
        {
            DisableWindowsProxy();
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            DisableLinuxProxy();
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            DisableMacProxy();
            return;
        }

        throw new PlatformNotSupportedException("System proxy is not supported on this platform.");
    }

    public static bool IsProxyEnabled(string expectedProxyServer)
    {
        if (OperatingSystem.IsWindows())
        {
            return IsWindowsProxyEnabled(expectedProxyServer);
        }

        if (OperatingSystem.IsLinux())
        {
            return IsLinuxProxyEnabled(expectedProxyServer);
        }

        if (OperatingSystem.IsMacOS())
        {
            return IsMacProxyEnabled(expectedProxyServer);
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsProxyEnabled(string expectedProxyServer)
    {
        using var registry = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
        if (registry == null)
        {
            return false;
        }

        var proxyEnabledObj = registry.GetValue("ProxyEnable");
        var proxyEnabled = proxyEnabledObj != null ? Convert.ToInt32(proxyEnabledObj, CultureInfo.InvariantCulture) : 0;

        return proxyEnabled == 1
               && registry.GetValue("ProxyServer") is string currentProxyServer
               && string.Equals(currentProxyServer, expectedProxyServer, StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsProxy(string proxyAddress)
    {
        using var registry = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
        if (registry == null)
        {
            throw new InvalidOperationException("Cannot open Internet Settings registry key.");
        }

        registry.SetValue("ProxyEnable", 1);
        registry.SetValue("ProxyServer", proxyAddress);
        registry.SetValue("ProxyOverride", "<local>");

        NotifyWindowsSystemOfChange();
    }

    [SupportedOSPlatform("windows")]
    private static void DisableWindowsProxy()
    {
        using var registry = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
        if (registry == null)
        {
            throw new InvalidOperationException("Cannot open Internet Settings registry key.");
        }

        registry.SetValue("ProxyEnable", 0);
        NotifyWindowsSystemOfChange();
    }

    [SupportedOSPlatform("windows")]
    private static void NotifyWindowsSystemOfChange()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    private static void SetLinuxProxy(string proxyAddress)
    {
        var (host, port) = ParseHostAndPort(proxyAddress);
        var gnomeSuccess = TrySetGnomeProxy(host, port);
        var kdeSuccess = TrySetKdeProxy(host, port);
        var xfceSuccess = TrySetXfceProxy(host, port);

        if (!gnomeSuccess && !kdeSuccess && !xfceSuccess)
        {
            throw new InvalidOperationException("Unable to configure proxy for GNOME/KDE/XFCE.");
        }
    }

    private static void DisableLinuxProxy()
    {
        var gnomeSuccess = Run("gsettings", "set org.gnome.system.proxy mode 'none'");
        var kdeSuccess = Run("kwriteconfig5", "--file kioslaverc --group 'Proxy Settings' --key ProxyType 0");
        var xfceSuccess = Run("xfconf-query", "-c xfce4-settings-editor -p /system/proxy/mode -s 0");

        if (!gnomeSuccess && !kdeSuccess && !xfceSuccess)
        {
            throw new InvalidOperationException("Unable to disable proxy for GNOME/KDE/XFCE.");
        }
    }

    private static bool IsLinuxProxyEnabled(string expectedProxyServer)
    {
        var (expectedHost, expectedPort) = ParseHostAndPort(expectedProxyServer);

        var gnomeMode = RunWithOutput("gsettings", "get org.gnome.system.proxy mode");
        var gnomeHost = RunWithOutput("gsettings", "get org.gnome.system.proxy.http host");
        var gnomePort = RunWithOutput("gsettings", "get org.gnome.system.proxy.http port");
        if (gnomeMode.Success
            && gnomeHost.Success
            && gnomePort.Success
            && gnomeMode.Output.Contains("manual", StringComparison.OrdinalIgnoreCase)
            && TrimQuoted(gnomeHost.Output).Equals(expectedHost, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(gnomePort.Output.Trim(), out var parsedPort)
            && parsedPort == expectedPort)
        {
            return true;
        }

        var kdeProxyType = RunWithOutput("kreadconfig5", "--file kioslaverc --group 'Proxy Settings' --key ProxyType");
        var kdeHttpProxy = RunWithOutput("kreadconfig5", "--file kioslaverc --group 'Proxy Settings' --key httpProxy");
        if (kdeProxyType.Success
            && kdeHttpProxy.Success
            && kdeProxyType.Output.Trim() == "1"
            && kdeHttpProxy.Output.Contains($"{expectedHost} {expectedPort}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool TrySetGnomeProxy(string host, int port)
    {
        var ok = true;
        ok &= Run("gsettings", "set org.gnome.system.proxy mode 'manual'");
        ok &= Run("gsettings", $"set org.gnome.system.proxy.http host '{EscapeSingleQuotes(host)}'");
        ok &= Run("gsettings", $"set org.gnome.system.proxy.http port {port}");
        ok &= Run("gsettings", $"set org.gnome.system.proxy.https host '{EscapeSingleQuotes(host)}'");
        ok &= Run("gsettings", $"set org.gnome.system.proxy.https port {port}");
        ok &= Run("gsettings", $"set org.gnome.system.proxy.socks host '{EscapeSingleQuotes(host)}'");
        ok &= Run("gsettings", $"set org.gnome.system.proxy.socks port {port}");
        return ok;
    }

    private static bool TrySetKdeProxy(string host, int port)
    {
        var proxy = $"http://{host} {port}";
        var ok = true;
        ok &= Run("kwriteconfig5", "--file kioslaverc --group 'Proxy Settings' --key ProxyType 1");
        ok &= Run("kwriteconfig5", $"--file kioslaverc --group 'Proxy Settings' --key httpProxy '{EscapeSingleQuotes(proxy)}'");
        ok &= Run("kwriteconfig5", $"--file kioslaverc --group 'Proxy Settings' --key httpsProxy '{EscapeSingleQuotes(proxy)}'");
        ok &= Run("kwriteconfig5", $"--file kioslaverc --group 'Proxy Settings' --key socksProxy '{EscapeSingleQuotes(proxy)}'");
        _ = Run("qdbus", "org.kde.KLauncher5 /KLauncher reparseConfiguration");
        _ = Run("qdbus", "org.kde.kded5 /kded org.kde.kded5.reconfigure");
        return ok;
    }

    private static bool TrySetXfceProxy(string host, int port)
    {
        var ok = true;
        ok &= Run("xfconf-query", "-c xfce4-settings-editor -p /system/proxy/mode -s 1");
        ok &= Run("xfconf-query", $"-c xfce4-settings-editor -p /system/proxy/http/host -s '{EscapeSingleQuotes(host)}'");
        ok &= Run("xfconf-query", $"-c xfce4-settings-editor -p /system/proxy/http/port -s {port}");
        return ok;
    }

    private static void SetMacProxy(string proxyAddress)
    {
        var (host, port) = ParseHostAndPort(proxyAddress);
        var services = GetMacNetworkServices();
        if (services.Count == 0)
        {
            throw new InvalidOperationException("No macOS network services available.");
        }

        var anySuccess = false;
        foreach (var service in services)
        {
            anySuccess |= RunWithElevation("networksetup", $"-setwebproxy '{EscapeSingleQuotes(service)}' {host} {port}");
            anySuccess |= RunWithElevation("networksetup", $"-setsecurewebproxy '{EscapeSingleQuotes(service)}' {host} {port}");
            anySuccess |= RunWithElevation("networksetup", $"-setsocksfirewallproxy '{EscapeSingleQuotes(service)}' {host} {port}");
        }

        if (!anySuccess)
        {
            throw new InvalidOperationException("Failed to set macOS proxy settings.");
        }
    }

    private static void DisableMacProxy()
    {
        var services = GetMacNetworkServices();
        if (services.Count == 0)
        {
            throw new InvalidOperationException("No macOS network services available.");
        }

        var anySuccess = false;
        foreach (var service in services)
        {
            anySuccess |= RunWithElevation("networksetup", $"-setwebproxystate '{EscapeSingleQuotes(service)}' off");
            anySuccess |= RunWithElevation("networksetup", $"-setsecurewebproxystate '{EscapeSingleQuotes(service)}' off");
            anySuccess |= RunWithElevation("networksetup", $"-setsocksfirewallproxystate '{EscapeSingleQuotes(service)}' off");
        }

        if (!anySuccess)
        {
            throw new InvalidOperationException("Failed to disable macOS proxy settings.");
        }
    }

    private static bool IsMacProxyEnabled(string expectedProxyServer)
    {
        var (host, port) = ParseHostAndPort(expectedProxyServer);
        foreach (var service in GetMacNetworkServices())
        {
            var web = RunWithOutput("networksetup", $"-getwebproxy '{EscapeSingleQuotes(service)}'");
            if (!web.Success)
            {
                continue;
            }

            var output = web.Output;
            var enabled = output.Contains("Enabled: Yes", StringComparison.OrdinalIgnoreCase);
            var hostOk = output.Contains($"Server: {host}", StringComparison.OrdinalIgnoreCase);
            var portOk = output.Contains($"Port: {port}", StringComparison.OrdinalIgnoreCase);

            if (enabled && hostOk && portOk)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> GetMacNetworkServices()
    {
        var result = RunWithOutput("networksetup", "-listallnetworkservices");
        if (!result.Success)
        {
            return new List<string>();
        }

        var lines = result.Output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !x.StartsWith("An asterisk", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.StartsWith("*", StringComparison.Ordinal))
            .ToList();

        return lines;
    }

    private static (string host, int port) ParseHostAndPort(string proxyAddress)
    {
        var value = proxyAddress.StartsWith("socks=", StringComparison.OrdinalIgnoreCase)
            ? proxyAddress.Substring("socks=".Length)
            : proxyAddress;

        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
        {
            throw new ArgumentException($"Invalid proxy address format: {proxyAddress}");
        }

        return (parts[0], port);
    }

    private static bool Run(string fileName, string arguments)
    {
        var result = RunWithOutput(fileName, arguments);
        return result.Success;
    }

    private static CommandResult RunWithOutput(string fileName, string arguments)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                return new CommandResult(true, output);
            }

            Logger.Warn($"Command failed: {fileName} {arguments}. {error}");
            return new CommandResult(false, output + error);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Command failed: {fileName} {arguments}. {ex.Message}");
            return new CommandResult(false, ex.Message);
        }
    }

    private static bool RunWithElevation(string fileName, string arguments)
    {
        var direct = Run(fileName, arguments);
        if (direct)
        {
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            return Run("pkexec", $"{fileName} {arguments}");
        }

        if (OperatingSystem.IsMacOS())
        {
            var escaped = EscapeDoubleQuotes($"{fileName} {arguments}");
            return Run("osascript", $"-e \"do shell script \\\"{escaped}\\\" with administrator privileges\"");
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                process?.WaitForExit();
                return process?.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Elevated command failed: {fileName} {arguments}. {ex.Message}");
                return false;
            }
        }

        return false;
    }

    private static string TrimQuoted(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    private static string EscapeSingleQuotes(string input)
    {
        return input.Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private static string EscapeDoubleQuotes(string input)
    {
        return input.Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private readonly record struct CommandResult(bool Success, string Output);
}
