using System.IO;
using System.Reflection;
using System.Diagnostics;
using ClashXW.Services;
using Xunit;

namespace ClashXW.Tests.Services;

public sealed class ClashProcessServiceTests
{
    [Fact]
    public void BuildSafePaths_IncludesDashboardDirectory()
    {
        var result = ClashProcessService.BuildSafePaths("C:/existing", "C:/config", "C:/dashboard");

        Assert.Contains("C:/config", result);
        Assert.Contains("C:/dashboard", result);
    }

    [Fact]
    public void BuildSafePaths_UsesPlatformPathSeparator()
    {
        var result = ClashProcessService.BuildSafePaths("C:/existing", "C:/config", "C:/dashboard");

        Assert.Contains(Path.PathSeparator, result);
    }

    [Fact]
    public void Stop_DoesNotThrow_WhenTrackedProcessAlreadyExited()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);
        process!.WaitForExit();

        using var service = new ClashProcessService("unused");
        typeof(ClashProcessService)
            .GetField("_clashProcess", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, process);

        var ex = Record.Exception(service.Stop);
        Assert.Null(ex);
    }
}
