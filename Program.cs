using System;
using System.Threading;
using Avalonia;

namespace ClashXW;

internal static class Program
{
    private const string MutexName = "ClashXW_SingleInstance_Mutex";
    private static Mutex? _mutex;

    [STAThread]
    private static void Main(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
