using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using ClashXW.Models;

namespace ClashXW.Services;

public sealed class TrayController : IDisposable
{
    private readonly Application _application;
    private readonly IClassicDesktopStyleApplicationLifetime _desktopLifetime;
    private readonly TrayIcon _trayIcon;
    private readonly DispatcherTimer? _refreshTimer;
    private readonly DashboardAssetManager _dashboardAssetManager;

    private ClashApiService? _apiService;
    private readonly ClashProcessService _clashProcessService;
    private readonly string _clashExecutablePath;
    private string _currentConfigPath;
    private string? _currentDashboardDirectory;

    private ClashConfig? _cachedConfigs;
    private ProxiesResponse? _cachedProxies;

    private bool _isSystemProxyEnabled;
    private bool _isTunEnabled;
    private bool _isRefreshing;
    private bool _disposed;
    private string? _coreStartError;
    private string? _lastRefreshError;

    public TrayController(Application application, IClassicDesktopStyleApplicationLifetime desktopLifetime)
    {
        _application = application;
        _desktopLifetime = desktopLifetime;

        ConfigManager.EnsureDefaultConfigExists();

        _clashExecutablePath = ResolveClashExecutablePath();
        _currentConfigPath = ConfigManager.GetCurrentConfigPath();
        _dashboardAssetManager = new DashboardAssetManager(
            ResolveBundledDashboardPath(),
            Path.Combine(ConfigManager.AppDataDir, "Dashboard"),
            Path.Combine(ConfigManager.AppDataDir, "dashboard-state.json"),
            ConfigManager.DefaultDashboardUpdateUrl);
        EnsureDashboardConfigured(_currentConfigPath);
        _clashProcessService = new ClashProcessService(_clashExecutablePath);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "ClashXW",
            IsVisible = true,
            Icon = LoadTrayIcon(tunEnabled: false, proxyEnabled: false)
        };
        _trayIcon.Clicked += OnTrayIconClicked;

        var trayIcons = new TrayIcons();
        trayIcons.Add(_trayIcon);
        TrayIcon.SetIcons(_application, trayIcons);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();

        StartClashCore();
        InitializeApiService();
        _trayIcon.Menu = BuildMenu();

        _ = RefreshStateAndMenuAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTimerTick;
        }
        _trayIcon.Clicked -= OnTrayIconClicked;

        if (_cachedConfigs != null)
        {
            var proxyAddress = GetProxyAddress(_cachedConfigs);
            if (proxyAddress != null && SystemProxyManager.IsProxyEnabled(proxyAddress))
            {
                try
                {
                    SystemProxyManager.DisableProxy();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to disable proxy on exit: {ex.Message}");
                }
            }
        }

        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();

        _clashProcessService.Dispose();
    }

    private static string ResolveClashExecutablePath()
    {
        var fileName = OperatingSystem.IsWindows() ? "clash.exe" : "clash";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ClashAssets", fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ClashAssets", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ClashAssets", fileName))
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string ResolveBundledDashboardPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "DashboardAssets"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "DashboardAssets")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DashboardAssets"))
        };

        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    private static WindowIcon LoadTrayIcon(bool tunEnabled, bool proxyEnabled)
    {
        // Option A icon matrix placeholder: keep current icon assets and map state to normal/TUN icon.
        var iconPath = tunEnabled ? "avares://ClashXW/Resources/icon_tun.ico" : "avares://ClashXW/Resources/icon.ico";
        if (!proxyEnabled)
        {
            iconPath = "avares://ClashXW/Resources/icon.ico";
        }

        using var iconStream = AssetLoader.Open(new Uri(iconPath));
        return new WindowIcon(iconStream);
    }

    private void StartClashCore()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_currentDashboardDirectory))
            {
                _clashProcessService.Start(_currentConfigPath, _currentDashboardDirectory);
            }
            else
            {
                _clashProcessService.Start(_currentConfigPath);
            }
            _coreStartError = null;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start Clash process", ex);
            _coreStartError = ex.Message;
            // Keep the tray app alive so users can still edit config and retry.
        }
    }

    private void InitializeApiService()
    {
        var apiDetails = ConfigManager.ReadApiDetails(_currentConfigPath);
        if (apiDetails == null)
        {
            _apiService = null;
            Logger.Warn($"Failed to read API details from {_currentConfigPath}");
            _lastRefreshError = "Failed to parse external-controller from config";
            return;
        }

        _apiService = new ClashApiService(apiDetails.BaseUrl, apiDetails.Secret);
    }

    private void EnsureDashboardConfigured(string configPath)
    {
        try
        {
            var updateUrl = ConfigManager.ReadDashboardUpdateUrl(configPath) ?? ConfigManager.DefaultDashboardUpdateUrl;
            var dashboardState = _dashboardAssetManager.EnsureDashboardAssets(updateUrl);
            _currentDashboardDirectory = dashboardState.DashboardDirectory;
            var changed = ConfigManager.EnsureDashboardConfig(configPath, dashboardState.DashboardDirectory, dashboardState.UpdateUrl);

            if (changed)
            {
                Logger.Info($"Synced dashboard config for {configPath}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to sync dashboard assets: {ex.Message}");
        }
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        await RefreshStateAndMenuAsync();
    }

    private async void OnTrayIconClicked(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        await RefreshStateAndMenuAsync();
    }

    private async Task RefreshStateAndMenuAsync()
    {
        if (_disposed || _isRefreshing || _apiService == null)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var configsTask = _apiService.GetConfigsAsync();
            var proxiesTask = _apiService.GetProxiesAsync();
            await Task.WhenAll(configsTask, proxiesTask);

            _cachedConfigs = await configsTask;
            _cachedProxies = await proxiesTask;
            _lastRefreshError = null;

            var newTunState = _cachedConfigs?.Tun?.Enable ?? false;
            var tunChanged = _isTunEnabled != newTunState;
            _isTunEnabled = newTunState;

            var proxyAddress = _cachedConfigs != null ? GetProxyAddress(_cachedConfigs) : null;
            var newProxyState = proxyAddress != null && SystemProxyManager.IsProxyEnabled(proxyAddress);
            var proxyChanged = _isSystemProxyEnabled != newProxyState;
            _isSystemProxyEnabled = newProxyState;

            if (tunChanged || proxyChanged)
            {
                _trayIcon.Icon = LoadTrayIcon(_isTunEnabled, _isSystemProxyEnabled);
            }

            if (_disposed)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    _trayIcon.Menu = BuildMenu();
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to refresh tray state: {ex.Message}");
            _lastRefreshError = ex.Message;
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        BuildModeSubMenu(menu);
        menu.Add(new NativeMenuItemSeparator());

        if (_coreStartError != null)
        {
            menu.Add(new NativeMenuItem($"Core start failed: {_coreStartError}")
            {
                IsEnabled = false
            });
            menu.Add(new NativeMenuItemSeparator());
        }

        if (_apiService == null)
        {
            menu.Add(new NativeMenuItem("API unavailable: check external-controller in config")
            {
                IsEnabled = false
            });
            menu.Add(new NativeMenuItemSeparator());
        }

        if (_cachedProxies?.Proxies == null && _lastRefreshError != null)
        {
            menu.Add(new NativeMenuItem($"Proxy list unavailable: {_lastRefreshError}")
            {
                IsEnabled = false
            });
            menu.Add(new NativeMenuItemSeparator());
        }

        if (_cachedProxies?.Proxies != null)
        {
            BuildProxyGroupMenus(menu, _cachedProxies);
            menu.Add(new NativeMenuItemSeparator());
        }

        var systemProxyItem = new NativeMenuItem("Set System Proxy")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _isSystemProxyEnabled,
            Gesture = new KeyGesture(Key.S, KeyModifiers.Control)
        };
        systemProxyItem.Click += async (_, _) => await OnSystemProxyToggleAsync(!_isSystemProxyEnabled);
        menu.Add(systemProxyItem);

        var tunItem = new NativeMenuItem("TUN Mode")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _isTunEnabled,
            Gesture = new KeyGesture(Key.E, KeyModifiers.Control)
        };
        tunItem.Click += async (_, _) => await OnTunModeToggleAsync(!_isTunEnabled);
        menu.Add(tunItem);

        menu.Add(new NativeMenuItemSeparator());

        var dashboardItem = new NativeMenuItem("Open Dashboard")
        {
            Gesture = new KeyGesture(Key.D, KeyModifiers.Control)
        };
        dashboardItem.Click += (_, _) => OnOpenDashboard();
        menu.Add(dashboardItem);

        var latencyItem = new NativeMenuItem("Test Latency");
        latencyItem.Click += async (_, _) => await OnTestLatencyAsync();
        menu.Add(latencyItem);

        BuildConfigSubMenu(menu);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += CreateDeferredHandler(
            Exit,
            action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));
        menu.Add(exitItem);

        return menu;
    }

    private void BuildModeSubMenu(NativeMenu menu)
    {
        var currentMode = _cachedConfigs?.Mode?.ToLowerInvariant() ?? "rule";
        var modeDisplay = currentMode.Length > 0
            ? char.ToUpperInvariant(currentMode[0]) + currentMode[1..]
            : "Rule";

        var modeSubMenu = new NativeMenu();
        modeSubMenu.Add(BuildModeItem("Rule", "rule", currentMode, Key.R));
        modeSubMenu.Add(BuildModeItem("Direct", "direct", currentMode, Key.D));
        modeSubMenu.Add(BuildModeItem("Global", "global", currentMode, Key.G));

        menu.Add(new NativeMenuItem($"Mode ({modeDisplay})")
        {
            Menu = modeSubMenu
        });
    }

    private NativeMenuItem BuildModeItem(string header, string mode, string currentMode, Key gestureKey)
    {
        var item = new NativeMenuItem(header)
        {
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = currentMode.Equals(mode, StringComparison.OrdinalIgnoreCase),
            Gesture = new KeyGesture(gestureKey, KeyModifiers.Alt)
        };

        item.Click += async (_, _) => await OnModeSelectedAsync(mode);
        return item;
    }

    private void BuildProxyGroupMenus(NativeMenu menu, ProxiesResponse proxies)
    {
        var orderedGroups = proxies.Proxies.TryGetValue("GLOBAL", out var globalGroup) && globalGroup.All != null
            ? globalGroup.All
                .Select(name => proxies.Proxies.TryGetValue(name, out var node) ? node : null)
                .Where(node => node != null)
                .Cast<ProxyNode>()
                .ToList()
            : proxies.Proxies.Values.ToList();

        foreach (var group in orderedGroups.Where(p => p.All is { Count: > 0 }))
        {
            BuildProxyGroupSubMenu(menu, group, proxies.Proxies);
        }
    }

    private void BuildProxyGroupSubMenu(NativeMenu menu, ProxyNode group, Dictionary<string, ProxyNode> allProxies)
    {
        var groupLatency = GetLatestLatency(group);
        var title = groupLatency.HasValue
            ? $"{group.Name} ({group.Now}) {groupLatency.Value}ms"
            : $"{group.Name} ({group.Now})";

        var groupMenu = new NativeMenu();

        var testLatencyItem = new NativeMenuItem("Test Latency");
        testLatencyItem.Click += async (_, _) => await OnTestGroupLatencyAsync(group.Name);
        groupMenu.Add(testLatencyItem);
        groupMenu.Add(new NativeMenuItemSeparator());

        foreach (var nodeName in group.All ?? Array.Empty<string>())
        {
            var isSelected = nodeName.Equals(group.Now, StringComparison.OrdinalIgnoreCase);
            var latency = GetNodeLatency(nodeName, allProxies);
            var nodeTitle = latency.HasValue ? $"{nodeName} {latency.Value}ms" : nodeName;

            var nodeItem = new NativeMenuItem(nodeTitle)
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = isSelected
            };
            nodeItem.Click += async (_, _) => await OnProxyNodeSelectedAsync(group.Name, nodeName);
            groupMenu.Add(nodeItem);
        }

        menu.Add(new NativeMenuItem(title)
        {
            Menu = groupMenu
        });
    }

    private void BuildConfigSubMenu(NativeMenu menu)
    {
        var configMenu = new NativeMenu();

        foreach (var configPath in ConfigManager.GetAvailableConfigs())
        {
            var fileName = Path.GetFileName(configPath);
            var item = new NativeMenuItem(fileName)
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = string.Equals(configPath, _currentConfigPath, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += async (_, _) => await OnConfigSelectedAsync(configPath);
            configMenu.Add(item);
        }

        configMenu.Add(new NativeMenuItemSeparator());

        var reloadItem = new NativeMenuItem("Reload Config")
        {
            Gesture = new KeyGesture(Key.R, KeyModifiers.Control)
        };
        reloadItem.Click += async (_, _) => await OnReloadConfigAsync();
        configMenu.Add(reloadItem);

        var editItem = new NativeMenuItem("Edit Config");
        editItem.Click += (_, _) => OnEditConfig();
        configMenu.Add(editItem);

        var openFolderItem = new NativeMenuItem("Open Config Folder")
        {
            Gesture = new KeyGesture(Key.O, KeyModifiers.Control)
        };
        openFolderItem.Click += (_, _) => OnOpenConfigFolder();
        configMenu.Add(openFolderItem);

        menu.Add(new NativeMenuItem("Configuration")
        {
            Menu = configMenu
        });
    }

    private async Task OnModeSelectedAsync(string mode)
    {
        if (_apiService == null)
        {
            return;
        }

        try
        {
            await _apiService.UpdateModeAsync(mode);
            await RefreshStateAndMenuAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to set mode '{mode}': {ex.Message}");
        }
    }

    private async Task OnProxyNodeSelectedAsync(string groupName, string nodeName)
    {
        if (_apiService == null)
        {
            return;
        }

        try
        {
            await _apiService.SelectProxyNodeAsync(groupName, nodeName);
            await RefreshStateAndMenuAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to select proxy node '{groupName}/{nodeName}': {ex.Message}");
        }
    }

    private async Task OnTestGroupLatencyAsync(string groupName)
    {
        if (_apiService == null)
        {
            return;
        }

        try
        {
            var group = _cachedProxies?.Proxies?.GetValueOrDefault(groupName);
            if (group == null)
            {
                return;
            }

            if (IsAutoGroup(group))
            {
                await _apiService.TestGroupLatencyAsync(groupName);
            }
            else
            {
                var tasks = new List<Task>();
                foreach (var nodeName in group.All ?? Array.Empty<string>())
                {
                    tasks.Add(_apiService.TestProxyLatencyAsync(nodeName));
                }

                await Task.WhenAll(tasks);
            }

            await RefreshStateAndMenuAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to test latency for group '{groupName}': {ex.Message}");
        }
    }

    private async Task OnSystemProxyToggleAsync(bool enable)
    {
        if (_apiService == null)
        {
            return;
        }

        try
        {
            var configs = await _apiService.GetConfigsAsync();
            if (configs == null)
            {
                return;
            }

            var proxyAddress = GetProxyAddress(configs);
            if (proxyAddress == null)
            {
                Logger.Warn("Proxy port is missing from Clash config.");
                return;
            }

            if (enable)
            {
                SystemProxyManager.SetProxy(proxyAddress);
            }
            else
            {
                SystemProxyManager.DisableProxy();
            }

            _isSystemProxyEnabled = enable;
            _trayIcon.Icon = LoadTrayIcon(_isTunEnabled, _isSystemProxyEnabled);
            await RefreshStateAndMenuAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to toggle system proxy: {ex.Message}");
        }
    }

    private async Task OnTunModeToggleAsync(bool enable)
    {
        if (_apiService == null)
        {
            return;
        }

        try
        {
            await _apiService.UpdateTunModeAsync(enable);
            var configs = await _apiService.GetConfigsAsync();
            var actualTunState = configs?.Tun?.Enable ?? false;
            _isTunEnabled = actualTunState;
            _trayIcon.Icon = LoadTrayIcon(_isTunEnabled, _isSystemProxyEnabled);
            await RefreshStateAndMenuAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to toggle TUN mode: {ex.Message}");
        }
    }

    private void OnOpenDashboard()
    {
        var dashboardUri = ResolveDashboardUri();
        OpenInExternalBrowser(dashboardUri);
    }

    private static void OpenInExternalBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString())
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to open dashboard in browser: {ex.Message}");
        }
    }

    private Uri ResolveDashboardUri()
    {
        var apiDetails = ConfigManager.ReadApiDetails(_currentConfigPath);
        if (apiDetails != null && Uri.TryCreate(apiDetails.DashboardUrl, UriKind.Absolute, out var remoteUri))
        {
            return remoteUri;
        }

        return new Uri("about:blank");
    }

    private async Task OnTestLatencyAsync()
    {
        if (_apiService == null || _cachedProxies?.Proxies == null)
        {
            return;
        }

        try
        {
            var tasks = new List<Task>();
            foreach (var proxy in _cachedProxies.Proxies.Values)
            {
                if (proxy.All is { Count: > 0 })
                {
                    if (IsAutoGroup(proxy))
                    {
                        tasks.Add(_apiService.TestGroupLatencyAsync(proxy.Name));
                    }
                    else
                    {
                        foreach (var nodeName in proxy.All)
                        {
                            tasks.Add(_apiService.TestProxyLatencyAsync(nodeName));
                        }
                    }
                }
                else
                {
                    tasks.Add(_apiService.TestProxyLatencyAsync(proxy.Name));
                }
            }

            await Task.WhenAll(tasks);
            await RefreshStateAndMenuAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to test latency: {ex.Message}");
        }
    }

    private async Task OnConfigSelectedAsync(string newPath)
    {
        if (_apiService == null)
        {
            return;
        }

        try
        {
            EnsureDashboardConfigured(newPath);
            await _apiService.ReloadConfigAsync(newPath);
            _currentConfigPath = newPath;
            ConfigManager.SetCurrentConfigPath(newPath);
            InitializeApiService();
            await RefreshStateAndMenuAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to switch config: {ex.Message}");
        }
    }

    private async Task OnReloadConfigAsync()
    {
        if (_apiService == null)
        {
            return;
        }

        try
        {
            EnsureDashboardConfigured(_currentConfigPath);
            await _apiService.ReloadConfigAsync(_currentConfigPath);
            await RefreshStateAndMenuAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to reload config: {ex.Message}");
        }
    }

    private void OnEditConfig()
    {
        if (string.IsNullOrEmpty(_currentConfigPath) || !File.Exists(_currentConfigPath))
        {
            Logger.Warn($"Config path not found: {_currentConfigPath}");
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("notepad.exe", _currentConfigPath) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{_currentConfigPath}\"") { UseShellExecute = false });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", $"-e \"{_currentConfigPath}\"") { UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to edit config: {ex.Message}");
        }
    }

    private void OnOpenConfigFolder()
    {
        if (string.IsNullOrEmpty(_currentConfigPath))
        {
            return;
        }

        var folder = Path.GetDirectoryName(_currentConfigPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{folder}\"") { UseShellExecute = false });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", $"\"{folder}\"") { UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to open config folder: {ex.Message}");
        }
    }

    private void Exit()
    {
        Dispose();
        _desktopLifetime.Shutdown();
    }

    private static bool IsAutoGroup(ProxyNode proxy)
    {
        return proxy.Type.Equals("Fallback", StringComparison.OrdinalIgnoreCase)
               || proxy.Type.Equals("URLTest", StringComparison.OrdinalIgnoreCase);
    }

    private static int? GetLatestLatency(ProxyNode proxy)
    {
        return proxy.History?.LastOrDefault()?.Delay;
    }

    private static int? GetNodeLatency(string nodeName, Dictionary<string, ProxyNode> allProxies)
    {
        return allProxies.TryGetValue(nodeName, out var proxy) ? GetLatestLatency(proxy) : null;
    }

    private static string? GetProxyAddress(ClashConfig configs)
    {
        var mixedPort = configs.MixedPort;
        if (mixedPort is > 0)
        {
            return $"127.0.0.1:{mixedPort.Value}";
        }

        var socksPort = configs.SocksPort;
        if (socksPort is > 0)
        {
            return $"socks=127.0.0.1:{socksPort.Value}";
        }

        return null;
    }

    public static EventHandler CreateDeferredHandler(Action action, Action<Action> defer)
    {
        return (_, _) => defer(action);
    }
}
