using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Velopack;

namespace DocVista.Updater;

public partial class MainWindow : Window
{
    private UpdateManager? _manager;
    private UpdateInfo? _availableUpdate;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SignalReadyEvent();
        await CheckForUpdatesAsync();
    }

    private static void SignalReadyEvent()
    {
        var arguments = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(arguments, argument => argument.Equals("--ready-event", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= arguments.Length) return;
        try { using var ready = EventWaitHandle.OpenExisting(arguments[index + 1]); ready.Set(); }
        catch { }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_busy) return;
        _busy = true;
        PrimaryButton.IsEnabled = false;
        DownloadProgress.IsIndeterminate = true;
        StatusText.Text = "正在检查更新…";
        DetailText.Text = string.Empty;

        try
        {
            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "未知";
            VersionText.Text = $"当前版本 {assemblyVersion}";
            var feedUrl = ResolveFeedUrl();
            if (feedUrl is null)
            {
                ShowIdle("尚未配置更新地址", "请在打包时设置 UpdateFeedUrl，或设置 DOCVISTA_UPDATE_FEED 环境变量。");
                return;
            }

            _manager = new UpdateManager(feedUrl);
            var version = _manager.CurrentVersion?.ToString() ?? assemblyVersion;
            VersionText.Text = $"当前版本 {version}";
            if (!_manager.IsInstalled)
            {
                ShowIdle("当前不是安装版本", "独立更新程序需要从 DocVista 安装目录运行。");
                return;
            }

            _availableUpdate = await _manager.CheckForUpdatesAsync();
            if (_availableUpdate is null)
            {
                ShowIdle("已是最新版本", "没有可用更新。", "重新检查");
                return;
            }

            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
            StatusText.Text = $"发现新版本 {_availableUpdate.TargetFullRelease.Version}";
            DetailText.Text = "更新将使用差分包优先下载，完成后自动安装并重新启动 DocVista。";
            PrimaryButton.Content = "下载并安装";
            PrimaryButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            ShowIdle("检查更新失败", exception.Message, "重试");
        }
        finally { _busy = false; }
    }

    private async Task DownloadAndInstallAsync()
    {
        if (_manager is null || _availableUpdate is null || _busy) return;
        _busy = true;
        PrimaryButton.IsEnabled = false;
        DownloadProgress.IsIndeterminate = false;
        StatusText.Text = "正在下载更新…";
        try
        {
            await _manager.DownloadUpdatesAsync(_availableUpdate, progress => Dispatcher.Invoke(() =>
            {
                DownloadProgress.Value = progress;
                DetailText.Text = $"已下载 {progress}%";
            }));
            StatusText.Text = "正在安装更新…";
            DetailText.Text = "DocVista 将在安装完成后重新启动。";
            _manager.ApplyUpdatesAndRestart(_availableUpdate.TargetFullRelease);
        }
        catch (Exception exception)
        {
            ShowIdle("更新失败", exception.Message, "重试");
            _busy = false;
        }
    }

    private void ShowIdle(string status, string detail, string action = "检查更新")
    {
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;
        StatusText.Text = status;
        DetailText.Text = detail;
        PrimaryButton.Content = action;
        PrimaryButton.IsEnabled = true;
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) await CheckForUpdatesAsync();
        else await DownloadAndInstallAsync();
    }

    private static string? ResolveFeedUrl()
    {
        var environment = Environment.GetEnvironmentVariable("DOCVISTA_UPDATE_FEED");
        if (Uri.TryCreate(environment, UriKind.Absolute, out _)) return environment;
        var configurationPath = Path.Combine(AppContext.BaseDirectory, "update-feed.txt");
        if (!File.Exists(configurationPath)) return null;
        var configured = File.ReadAllText(configurationPath).Trim().TrimStart('\uFEFF');
        return Uri.TryCreate(configured, UriKind.Absolute, out _) ? configured : null;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
