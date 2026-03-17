using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ClashXW.Views;

public partial class DashboardWindow : Window
{
    private NativeWebView? _webView;

    public DashboardWindow()
        : this(new Uri("about:blank"))
    {
    }

    public DashboardWindow(Uri uri)
    {
        InitializeComponent();
        Navigate(uri);
    }

    public void Navigate(Uri uri)
    {
        _webView ??= this.FindControl<NativeWebView>("DashboardWebView");
        if (_webView != null)
        {
            _webView.Source = uri;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
