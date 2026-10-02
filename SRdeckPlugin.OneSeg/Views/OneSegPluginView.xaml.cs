using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.WPF;
using Microsoft.Web.WebView2.Core;
using SRdeckPlugin.OneSeg.Models;
using SRdeckPlugin.OneSeg.ViewModels;

namespace SRdeckPlugin.OneSeg.Views;

public partial class OneSegPluginView : UserControl
{
    private const string BmlOrigin = "https://oneseg-bml.local";
    private OneSegViewModel? viewModel;
    private bool bmlInitializing;
    private bool bmlReady;
    private int renderedRevision = -1;
    private int bmlRenderRequest;
    private Window? fullScreenWindow;

    public OneSegPluginView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as OneSegViewModel);
        await EnsureBmlViewerAsync();
        await RenderBmlAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        fullScreenWindow?.Close();
        AttachViewModel(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachViewModel(e.NewValue as OneSegViewModel);
        if (fullScreenWindow is not null) fullScreenWindow.DataContext = e.NewValue;
        _ = ScheduleBmlRenderAsync();
    }

    private void VideoSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (fullScreenWindow is not null) return;

        var hitArea = new Grid { Background = new SolidColorBrush(Color.FromArgb(2, 0, 0, 0)) };
        hitArea.ContextMenu = new ContextMenu();
        hitArea.ContextMenuOpening += StationContextMenuOpening;
        var video = new VideoView { Content = hitArea };
        var captionText = new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        captionText.SetBinding(TextBlock.TextProperty, new Binding(nameof(OneSegViewModel.Caption)));
        var caption = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(176, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = captionText
        };
        caption.SetBinding(VisibilityProperty, new Binding(nameof(OneSegViewModel.CaptionVisibility)));
        hitArea.Children.Add(caption);
        hitArea.SizeChanged += (_, args) =>
        {
            double fontSize = Math.Clamp(args.NewSize.Height * 0.045, 20, 96);
            captionText.FontSize = fontSize;
            caption.Padding = new Thickness(fontSize * 0.4, fontSize * 0.16,
                fontSize * 0.4, fontSize * 0.16);
            caption.MaxWidth = args.NewSize.Width * 0.84;
            caption.Margin = new Thickness(16, 0, 16, args.NewSize.Height * 0.09);
        };

        var window = new Window
        {
            Owner = Window.GetWindow(this),
            Content = video,
            DataContext = DataContext,
            Background = Brushes.Black,
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Topmost = true
        };
        hitArea.MouseLeftButtonUp += (_, _) => window.Close();
        window.MouseLeftButtonUp += (_, _) => window.Close();
        PreProcessInputEventHandler exitOnEscape = (_, args) =>
        {
            if (args.StagingItem.Input is KeyEventArgs key &&
                key.RoutedEvent == Keyboard.PreviewKeyDownEvent && key.Key == Key.Escape)
            {
                key.Handled = true;
                window.Close();
            }
        };
        window.Closed += (_, _) =>
        {
            InputManager.Current.PreProcessInput -= exitOnEscape;
            BindingOperations.ClearBinding(video, VideoView.MediaPlayerProperty);
            BindingOperations.SetBinding(VideoSurface, VideoView.MediaPlayerProperty,
                new Binding(nameof(OneSegViewModel.MediaPlayer)));
            fullScreenWindow = null;
            viewModel?.VideoOutputChanged?.Invoke();
        };

        fullScreenWindow = window;
        window.Show();
        InputManager.Current.PreProcessInput += exitOnEscape;
        BindingOperations.ClearBinding(VideoSurface, VideoView.MediaPlayerProperty);
        BindingOperations.SetBinding(video, VideoView.MediaPlayerProperty,
            new Binding(nameof(OneSegViewModel.MediaPlayer)));
        viewModel?.VideoOutputChanged?.Invoke();
    }

    private void StationContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu }) return;
        menu.Items.Clear();
        if (viewModel is null || viewModel.Stations.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "局がありません", IsEnabled = false });
            return;
        }

        foreach (OneSegStation station in viewModel.Stations)
        {
            var item = new MenuItem
            {
                Header = station.DisplayName,
                Tag = station,
                IsCheckable = true,
                IsChecked = station.ServiceId == viewModel.SelectedStation?.ServiceId &&
                    station.ChannelNumber == viewModel.SelectedStation?.ChannelNumber
            };
            item.Click += StationMenuItem_Click;
            menu.Items.Add(item);
        }
    }

    private void StationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: OneSegStation station } && viewModel is not null)
            viewModel.SelectedStation = station;
    }

    private void AttachViewModel(OneSegViewModel? value)
    {
        if (viewModel is not null) viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel = value;
        if (viewModel is not null) viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OneSegViewModel.BmlRevision)) _ = ScheduleBmlRenderAsync();
    }

    private async Task ScheduleBmlRenderAsync()
    {
        int request = ++bmlRenderRequest;
        await Task.Delay(250);
        if (request == bmlRenderRequest) await RenderBmlAsync();
    }

    private async Task EnsureBmlViewerAsync()
    {
        if (bmlReady || bmlInitializing) return;
        bmlInitializing = true;
        try
        {
            await BmlWebView.EnsureCoreWebView2Async();
            CoreWebView2 core = BmlWebView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            BmlWebView.NavigationCompleted += OnNavigationCompleted;
            bmlReady = true;
        }
        catch (Exception exception)
        {
            ShowBmlPlaceholder($"BMLビューを開始できません。\nWebView2 Runtimeを確認してください。\n{exception.Message}");
            viewModel?.ReportBmlViewerStatus($"BML表示失敗: {exception.Message}");
        }
        finally { bmlInitializing = false; }
    }

    private async Task RenderBmlAsync()
    {
        OneSegViewModel? current = viewModel;
        if (current is null || !IsLoaded) return;
        await EnsureBmlViewerAsync();
        if (!bmlReady || current.BmlRevision == renderedRevision) return;
        renderedRevision = current.BmlRevision;
        if (string.IsNullOrWhiteSpace(current.BmlEntryPath))
        {
            ShowBmlPlaceholder("BML文書を受信待ち");
            return;
        }
        string path = string.Join('/', current.BmlEntryPath.Split('/').Select(Uri.EscapeDataString));
        ShowBmlPlaceholder($"BMLを読み込み中…\n{current.BmlEntryPath}");
        BmlWebView.CoreWebView2.Navigate($"{BmlOrigin}/{path}?revision={current.BmlRevision}");
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out Uri? uri)) return;
        if (!uri.Host.Equals("oneseg-bml.local", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.Scheme is "data" or "blob" or "about") return;
            e.Response = BmlWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(), 403, "Blocked", "Content-Type: text/plain");
            return;
        }
        string path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (viewModel?.TryGetBmlResource(path, out OneSegBmlResource? resource) != true || resource is null)
        {
            e.Response = BmlWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream("BML resource not found"u8.ToArray()), 404, "Not Found",
                "Content-Type: text/plain; charset=utf-8\r\nCache-Control: no-store");
            return;
        }
        bool document = resource.ContentType.Contains("bml", StringComparison.OrdinalIgnoreCase) ||
            resource.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
            resource.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(resource.Path).Equals(".bml", StringComparison.OrdinalIgnoreCase);
        byte[] content = document ? BmlHtmlAdapter.Transform(resource) : resource.Content;
        string contentType = document ? "text/html; charset=utf-8" : resource.ContentType;
        string security = document
            ? "\r\nContent-Security-Policy: default-src 'self' data: blob:; img-src 'self' data: blob:; media-src 'self' data: blob:; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'"
            : "";
        e.Response = BmlWebView.CoreWebView2.Environment.CreateWebResourceResponse(
            new MemoryStream(content, writable: false), 200, "OK",
            $"Content-Type: {contentType}\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: {BmlOrigin}{security}");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) &&
            (uri.Host.Equals("oneseg-bml.local", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme is "about" or "data")) return;
        e.Cancel = true;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            BmlPlaceholder.Visibility = Visibility.Collapsed;
            if (viewModel is not null)
                viewModel.ReportBmlViewerStatus($"BML表示中: {viewModel.BmlEntryPath}");
        }
        else ShowBmlPlaceholder($"BMLを表示できませんでした。\n{e.WebErrorStatus}");
    }

    private async void BmlRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!bmlReady || sender is not Button { Tag: string key }) return;
        try { await BmlWebView.ExecuteScriptAsync(BmlHtmlAdapter.RemoteScript(key)); }
        catch (InvalidOperationException) { }
    }

    private void ShowBmlPlaceholder(string value)
    {
        BmlPlaceholderText.Text = value;
        BmlPlaceholder.Visibility = Visibility.Visible;
    }
}
