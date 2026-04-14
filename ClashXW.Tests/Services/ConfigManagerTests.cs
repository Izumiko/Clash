using System;
using System.IO;
using ClashXW.Services;
using Xunit;

namespace ClashXW.Tests.Services;

public sealed class ConfigManagerTests : IDisposable
{
    private readonly string _tempDirectory;

    public ConfigManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ClashXW.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void EnsureDashboardConfig_RewritesExternalUiAndExternalUiUrl()
    {
        var configPath = Path.Combine(_tempDirectory, "config.yaml");
        File.WriteAllText(configPath, "external-controller: 127.0.0.1:9090\nexternal-ui: old-ui\nexternal-ui-url: https://old.example/ui.zip\n");

        ConfigManager.EnsureDashboardConfig(configPath, @"C:\dashboard\current", "https://example.com/dashboard.zip");

        var updated = File.ReadAllText(configPath);
        Assert.Contains("external-ui: C:/dashboard/current", updated);
        Assert.Contains("external-ui-url: https://example.com/dashboard.zip", updated);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
