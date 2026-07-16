using DocVista.Core;
using DocVista.Rendering;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace DocVista.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    private CancellationTokenSource? _openCancellation;
    private ISpreadsheetWorkbook? _workbook;
    private ShellPreviewHost? _shellPreview;
    private DocumentInfo? _currentDocument;
    private bool _restoringSheet;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        RestoreWindow();
        RefreshRecentDocuments();
        Loaded += MainWindow_Loaded;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateMaximizeGlyph();
        var argument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
        if (argument is not null) await OpenDocumentAsync(argument);
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开文档",
            Filter = "支持的文档|*.pdf;*.doc;*.docx;*.ppt;*.pptx;*.xls;*.xlsx;*.csv|PDF|*.pdf|Word|*.doc;*.docx|PowerPoint|*.ppt;*.pptx|Excel 和 CSV|*.xls;*.xlsx;*.csv|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await OpenDocumentAsync(dialog.FileName);
    }

    private async Task OpenDocumentAsync(string path)
    {
        if (!File.Exists(path)) { ShowError("文件不存在或已被移动。"); return; }
        var document = DocumentInfo.FromPath(path);
        if (document.Kind == DocumentKind.Unknown) { ShowError($"暂不支持 {document.Extension} 格式。"); return; }

        _openCancellation?.Cancel();
        _openCancellation?.Dispose();
        _openCancellation = new CancellationTokenSource();
        var cancellationToken = _openCancellation.Token;
        DisposeActiveViewers();
        SetDocumentHeader(document);
        ShowState(LoadingState);
        LoadingText.Text = $"正在打开 {document.Name}…";

        try
        {
            switch (document.Kind)
            {
                case DocumentKind.Pdf:
                    await OpenPdfAsync(document.Path, cancellationToken);
                    break;
                case DocumentKind.Csv:
                    var csv = await Task.Run(() => CsvDocument.LoadAsync(document.Path, cancellationToken), cancellationToken);
                    ShowTable(csv);
                    break;
                case DocumentKind.Excel:
                    await OpenSpreadsheetAsync(document.Path, cancellationToken);
                    break;
                case DocumentKind.Word:
                    if (!await TryOpenShellPreviewAsync(document.Path, cancellationToken))
                        await OpenOfficeDocumentAsync(document.Path, cancellationToken);
                    break;
                case DocumentKind.PowerPoint:
                    if (!await TryOpenShellPreviewAsync(document.Path, cancellationToken))
                        await OpenOfficeDocumentAsync(document.Path, cancellationToken);
                    break;
                default:
                    await OpenShellPreviewAsync(document.Path, cancellationToken);
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _currentDocument = document;
            AddRecent(document.Path);
            StatusText.Text = $"已打开 · {FormatSize(document.Size)}";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            StatusText.Text = "打开失败";
        }
    }

    private async Task OpenPdfAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocVista", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(null, userData);
        await PdfViewer.EnsureCoreWebView2Async(environment);
        cancellationToken.ThrowIfCancellationRequested();
        PdfViewer.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
        PdfViewer.CoreWebView2.Settings.IsStatusBarEnabled = false;
        PdfViewer.Source = new Uri(path);
        SearchBox.Visibility = Visibility.Collapsed;
        ShowState(PdfViewer);
    }

    private async Task OpenSpreadsheetAsync(string path, CancellationToken cancellationToken)
    {
        _workbook = await Task.Run<ISpreadsheetWorkbook>(() => Path.GetExtension(path).Equals(".xls", StringComparison.OrdinalIgnoreCase)
            ? LegacyXlsWorkbook.Open(path)
            : XlsxWorkbook.Open(path), cancellationToken);
        if (_workbook.Sheets.Count == 0) throw new InvalidDataException("工作簿中没有可见工作表。");
        _restoringSheet = true;
        SheetSelector.ItemsSource = _workbook.Sheets;
        SheetSelector.DisplayMemberPath = nameof(WorkbookSheet.Name);
        SheetSelector.SelectedIndex = 0;
        SheetSelector.Visibility = Visibility.Visible;
        _restoringSheet = false;
        await LoadSheetAsync(_workbook.Sheets[0], cancellationToken);
    }

    private async Task LoadSheetAsync(WorkbookSheet sheet, CancellationToken cancellationToken)
    {
        if (_workbook is null) return;
        ShowState(LoadingState);
        LoadingText.Text = $"正在读取 {sheet.Name}…";
        var data = await Task.Run(() => _workbook.LoadSheet(sheet), cancellationToken);
        ShowTable(data);
    }

    private async Task OpenShellPreviewAsync(string path, CancellationToken cancellationToken)
    {
        _shellPreview = new ShellPreviewHost();
        ShellViewerContainer.Children.Clear();
        ShellViewerContainer.Children.Add(_shellPreview);
        ShowState(ShellViewerContainer);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _shellPreview.LoadPreview(path);
        SearchBox.Visibility = Visibility.Collapsed;
    }

    private async Task<bool> TryOpenShellPreviewAsync(string path, CancellationToken cancellationToken)
    {
        try { await OpenShellPreviewAsync(path, cancellationToken); return true; }
        catch
        {
            _shellPreview?.UnloadPreview();
            _shellPreview = null;
            ShellViewerContainer.Children.Clear();
            return false;
        }
    }

    private async Task OpenOfficeDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var document = await Task.Run(() => OfficeDocumentLoader.Load(path), cancellationToken);
        OfficePagesPanel.Children.Clear();
        for (var index = 0; index < document.Pages.Count; index++)
            OfficePagesPanel.Children.Add(CreateOfficePage(document.Pages[index], document.Mode, index + 1, document.Pages.Count));
        TableSummary.Text = document.Summary;
        SearchBox.Visibility = Visibility.Collapsed;
        ShowState(OfficeViewer);
    }

    private FrameworkElement CreateOfficePage(OfficePage page, OfficeViewMode mode, int pageNumber, int totalPages)
    {
        var content = new StackPanel();
        if (!string.IsNullOrWhiteSpace(page.Title))
            content.Children.Add(new TextBlock { Text = page.Title, FontSize = mode == OfficeViewMode.Presentation ? 26 : 20, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("OuterSpaceBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });

        foreach (var block in page.Blocks)
        {
            var text = new TextBlock
            {
                Text = block.Text,
                FontSize = block.IsHeading ? 20 : mode == OfficeViewMode.Presentation ? 16 : 13,
                FontWeight = block.IsHeading ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)FindResource(block.IsHeading ? "OuterSpaceBrush" : "TextBrush"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = mode == OfficeViewMode.Presentation ? 27 : 22,
                Margin = new Thickness(0, block.IsHeading ? 14 : 3, 0, block.IsHeading ? 8 : 5)
            };
            if (block.IsTableRow)
                content.Children.Add(new Border { Background = (Brush)FindResource("BoneSoftBrush"), BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(9, 6, 9, 6), Child = text });
            else content.Children.Add(text);
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(content, 0);
        grid.Children.Add(content);
        var number = new TextBlock { Text = $"{pageNumber} / {totalPages}", Foreground = (Brush)FindResource("MutedTextBrush"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        Grid.SetRow(number, 2);
        grid.Children.Add(number);

        return new Border
        {
            MaxWidth = mode == OfficeViewMode.Presentation ? 920 : 780,
            MinHeight = mode == OfficeViewMode.Presentation ? 480 : 820,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(mode == OfficeViewMode.Presentation ? 48 : 54),
            Margin = new Thickness(24, 10, 24, 10),
            Child = grid
        };
    }

    private void ShowTable(TableDocument document)
    {
        TableViewer.ItemsSource = document.Table.DefaultView;
        TableSummary.Text = document.Summary + (document.WasTruncated ? " · 已限制预览行数" : string.Empty);
        SearchBox.Visibility = Visibility.Visible;
        SearchInput.Text = string.Empty;
        ShowState(TableViewer);
    }

    private void ShowState(UIElement visible)
    {
        foreach (var state in new UIElement[] { EmptyState, LoadingState, ErrorState, PdfViewer, TableViewer, OfficeViewer, ShellViewerContainer })
            state.Visibility = state == visible ? Visibility.Visible : Visibility.Collapsed;

        visible.Opacity = 0;
        var transform = new TranslateTransform(0, 5);
        visible.RenderTransform = transform;
        visible.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private void ShowError(string message)
    {
        DisposeActiveViewers();
        ErrorText.Text = message;
        SearchBox.Visibility = Visibility.Collapsed;
        SheetSelector.Visibility = Visibility.Collapsed;
        TableSummary.Text = string.Empty;
        ShowState(ErrorState);
    }

    private void DisposeActiveViewers()
    {
        try { PdfViewer.CoreWebView2?.Stop(); } catch { }
        TableViewer.ItemsSource = null;
        OfficePagesPanel.Children.Clear();
        _shellPreview?.UnloadPreview();
        _shellPreview = null;
        ShellViewerContainer.Children.Clear();
        _workbook?.Dispose();
        _workbook = null;
        _restoringSheet = true;
        SheetSelector.ItemsSource = null;
        SheetSelector.Visibility = Visibility.Collapsed;
        _restoringSheet = false;
        SearchBox.Visibility = Visibility.Collapsed;
        TableSummary.Text = string.Empty;
    }

    private void SetDocumentHeader(DocumentInfo document)
    {
        DocumentTitle.Text = document.Name;
        DocumentMeta.Text = $"{document.Extension.TrimStart('.')} · {FormatSize(document.Size)}";
    }

    private void AddRecent(string path)
    {
        _settings.RecentDocuments.RemoveAll(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentDocuments.Insert(0, new RecentDocument(path, DateTimeOffset.Now));
        if (_settings.RecentDocuments.Count > 12) _settings.RecentDocuments.RemoveRange(12, _settings.RecentDocuments.Count - 12);
        RefreshRecentDocuments();
        SaveSettings();
    }

    private void RefreshRecentDocuments()
    {
        _settings.RecentDocuments.RemoveAll(item => !File.Exists(item.Path));
        RecentList.ItemsSource = _settings.RecentDocuments.Select(item => new RecentItem(item.Path)).ToList();
    }

    private async void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item) return;
        RecentList.SelectedItem = null;
        await OpenDocumentAsync(item.Path);
    }

    private async void SheetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSheet || SheetSelector.SelectedItem is not WorkbookSheet sheet) return;
        try
        {
            _openCancellation ??= new CancellationTokenSource();
            await LoadSheetAsync(sheet, _openCancellation.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { ShowError(exception.Message); }
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TableViewer.ItemsSource is not DataView view) return;
        var search = SearchInput.Text.Trim().Replace("'", "''");
        if (search.Length == 0) { view.RowFilter = string.Empty; return; }
        if (view.Table is null) return;
        var clauses = view.Table.Columns.Cast<DataColumn>()
            .Select(column => $"Convert([{column.ColumnName.Replace("]", "]]" )}], 'System.String') LIKE '%{search}%'");
        try { view.RowFilter = string.Join(" OR ", clauses); }
        catch { view.RowFilter = string.Empty; }
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e) => SearchInput.Text = string.Empty;

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) await OpenDocumentAsync(files[0]);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { OpenButton_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && SearchBox.Visibility == Visibility.Visible) { SearchInput.Focus(); SearchInput.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.F11) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; e.Handled = true; }
        else if (e.Key == Key.Escape && WindowState == WindowState.Maximized) { WindowState = WindowState.Normal; e.Handled = true; }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var updaterPath = FindUpdaterPath();
        if (updaterPath is null)
        {
            MessageBox.Show(this, "未找到 DocVista.Updater.exe。请使用 Setup 安装版本，或先构建更新程序项目。", "无法启动更新程序", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var eventName = $"Local\\DocVista.Updater.Ready.{Guid.NewGuid():N}";
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            var startInfo = new ProcessStartInfo(updaterPath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(updaterPath)! };
            startInfo.ArgumentList.Add("--ready-event");
            startInfo.ArgumentList.Add(eventName);
            var updater = Process.Start(startInfo);
            var signaled = await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(8)));
            if (signaled && updater is { HasExited: false }) Close();
            else MessageBox.Show(this, "更新程序没有成功显示，主程序将保持打开。", "无法启动更新程序", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "无法启动更新程序", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string? FindUpdaterPath()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "DocVista.Updater.exe");
        if (File.Exists(installed)) return installed;
        var development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DocVista.Updater", "bin", "Debug", "net8.0-windows", "DocVista.Updater.exe"));
        return File.Exists(development) ? development : null;
    }

    private void SidebarButton_Click(object sender, RoutedEventArgs e)
    {
        var collapse = SidebarColumn.Width.Value > 0;
        SidebarColumn.Width = collapse ? new GridLength(0) : new GridLength(236);
        _settings.SidebarCollapsed = collapse;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ActualWidth < 980) SidebarColumn.Width = new GridLength(0);
        else if (!_settings.SidebarCollapsed && SidebarColumn.Width.Value == 0) SidebarColumn.Width = new GridLength(236);
        SearchBox.Width = ActualWidth < 1080 ? 200 : 260;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void UpdateMaximizeGlyph()
    {
        if (FindName("MaximizeButton") is Button button) button.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void RestoreWindow()
    {
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        if (!double.IsNaN(_settings.WindowLeft) && _settings.WindowLeft >= SystemParameters.VirtualScreenLeft && _settings.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80) Left = _settings.WindowLeft;
        if (!double.IsNaN(_settings.WindowTop) && _settings.WindowTop >= SystemParameters.VirtualScreenTop && _settings.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80) Top = _settings.WindowTop;
        if (_settings.SidebarCollapsed) SidebarColumn.Width = new GridLength(0);
        if (_settings.IsMaximized) WindowState = WindowState.Maximized;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _openCancellation?.Cancel();
        DisposeActiveViewers();
        var bounds = RestoreBounds;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.IsMaximized = WindowState == WindowState.Maximized;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try { _settingsStore.Save(_settings); } catch (Exception exception) { Debug.WriteLine(exception); }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024:0.#} MB";
        return $"{bytes / 1024d / 1024 / 1024:0.##} GB";
    }

    private sealed record RecentItem(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
        public string Directory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    }
}
