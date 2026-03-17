using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ClashXW.Services;

namespace ClashXW;

public partial class App : Application
{
    private TrayController? _trayController;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUiUnhandledException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _trayController = new TrayController(this, desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnUiUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled UI exception", e.Exception);

        if (e.Exception is InvalidOperationException invalidOperation
            && invalidOperation.Message.Contains("Unable to create child window for native control host", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback path: keep tray app alive if embedded WebView host fails on this platform.
            e.Handled = true;
        }
    }
}
