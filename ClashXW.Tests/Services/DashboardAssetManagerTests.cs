using System;
using System.IO;
using ClashXW.Models;
using ClashXW.Services;
using Xunit;

namespace ClashXW.Tests.Services;

public sealed class DashboardAssetManagerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _stateFilePath;
    private readonly string _bundledAssetsPath;
    private readonly string _installRootPath;

    public DashboardAssetManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ClashXW.Tests", Guid.NewGuid().ToString("N"));
        _stateFilePath = Path.Combine(_tempDirectory, "dashboard-state.json");
        _bundledAssetsPath = Path.Combine(_tempDirectory, "bundled");
        _installRootPath = Path.Combine(_tempDirectory, "installed");

        Directory.CreateDirectory(_bundledAssetsPath);
        File.WriteAllText(Path.Combine(_bundledAssetsPath, "index.html"), "<html>dashboard</html>");
    }

    [Fact]
    public void SaveState_ThenLoadState_RoundTripsDashboardMetadata()
    {
        var manager = new DashboardAssetManager(_bundledAssetsPath, _installRootPath, _stateFilePath, "https://example.com/dashboard.zip");
        var state = new DashboardState("1.0.0", Path.Combine(_installRootPath, "1.0.0"), "https://example.com/dashboard.zip", "etag-1");

        manager.SaveState(state);

        var loaded = manager.LoadState();
        Assert.Equal(state.Version, loaded?.Version);
        Assert.Equal(state.UpdateUrl, loaded?.UpdateUrl);
    }

    [Fact]
    public void EnsureDashboardAssets_CopiesBundledAssetsAndUsesConfiguredUrl()
    {
        var manager = new DashboardAssetManager(_bundledAssetsPath, _installRootPath, _stateFilePath, "https://example.com/dashboard.zip");

        var state = manager.EnsureDashboardAssets();

        Assert.True(Directory.Exists(state.DashboardDirectory));
        Assert.True(File.Exists(Path.Combine(state.DashboardDirectory, "index.html")));
        Assert.Equal("https://example.com/dashboard.zip", state.UpdateUrl);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
