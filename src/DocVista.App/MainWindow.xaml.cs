using DocVista.Core;
using DocVista.Rendering;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    private int _zoomPercent = 100;
    private string _displayMode = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        RestoreWindow();
        ApplySettings();
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
        if (!_settings.RememberZoom) SetZoom(_settings.DefaultZoomPercent, persist: false);
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
                    if (_settings.OfficeDisplayPreference == OfficeDisplayPreference.Auto && await TryOpenShellPreviewAsync(document.Path, cancellationToken)) break;
                    await OpenSpreadsheetAsync(document.Path, cancellationToken);
                    break;
                case DocumentKind.Word:
                    if (_settings.OfficeDisplayPreference == OfficeDisplayPreference.Compatibility || !await TryOpenShellPreviewAsync(document.Path, cancellationToken))
                        await OpenOfficeDocumentAsync(document.Path, cancellationToken);
                    break;
                case DocumentKind.PowerPoint:
                    if (_settings.OfficeDisplayPreference == OfficeDisplayPreference.Compatibility || !await TryOpenShellPreviewAsync(document.Path, cancellationToken))
                        await OpenOfficeDocumentAsync(document.Path, cancellationToken);
                    break;
                default:
                    await OpenShellPreviewAsync(document.Path, cancellationToken);
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _currentDocument = document;
            AddRecent(document.Path);
            StatusText.Text = $"已打开 · {FormatSize(document.Size)} · {_displayMode}";
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
        PdfViewer.ZoomFactor = _zoomPercent / 100d;
        PdfViewer.Source = new Uri(path);
        _displayMode = "PDF";
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
        _displayMode = "数据视图";
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
        ApplyZoomToShellFromDefault();
        _displayMode = "系统高保真预览";
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
        _displayMode = document.Mode == OfficeViewMode.CompatibilityText ? "兼容文本视图" : "兼容视图";
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
            if (block.ImageData is { Length: > 0 })
            {
                content.Children.Add(CreateDocumentImage(block));
                continue;
            }

            var fontSize = block.IsHeading ? 20 : block.FontSize > 0 ? Math.Clamp(block.FontSize * 96d / 72d, 11, 28) : mode == OfficeViewMode.Presentation ? 16 : 13;
            var text = new TextBlock
            {
                Text = block.Text,
                FontSize = fontSize,
                FontWeight = block.IsHeading || block.IsBold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle = block.IsItalic ? FontStyles.Italic : FontStyles.Normal,
                Foreground = (Brush)FindResource(block.IsHeading ? "OuterSpaceBrush" : "TextBrush"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = block.Alignment switch
                {
                    OfficeTextAlignment.Center => TextAlignment.Center,
                    OfficeTextAlignment.Right => TextAlignment.Right,
                    OfficeTextAlignment.Justify => TextAlignment.Justify,
                    _ => TextAlignment.Left
                },
                LineHeight = mode == OfficeViewMode.Presentation ? 27 : 22,
                Margin = new Thickness(block.LeftIndent + block.FirstLineIndent, Math.Max(block.SpaceBefore, block.IsHeading ? 14 : 3), 0, Math.Max(block.SpaceAfter, block.IsHeading ? 8 : 5))
            };
            if (block.IsTableRow && block.Cells is { Count: > 0 })
                content.Children.Add(CreateTableRow(block.Cells));
            else if (block.IsTableRow)
                content.Children.Add(new Border { Background = (Brush)FindResource("BoneSoftBrush"), BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(9, 6, 9, 6), Child = text });
            else content.Children.Add(text);
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(content, 0);
        grid.Children.Add(content);
        var number = new TextBlock { Text = $"{pageNumber} / {totalPages}", Visibility = totalPages > 1 ? Visibility.Visible : Visibility.Collapsed, Foreground = (Brush)FindResource("MutedTextBrush"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        Grid.SetRow(number, 2);
        grid.Children.Add(number);

        return new Border
        {
            MaxWidth = mode == OfficeViewMode.Presentation ? 920 : 780,
            MinHeight = mode == OfficeViewMode.Presentation ? 480 : totalPages > 1 ? 820 : 420,
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

    private FrameworkElement CreateDocumentImage(OfficeTextBlock block)
    {
        using var stream = new MemoryStream(block.ImageData!, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        var width = block.ImageWidth > 0 ? block.ImageWidth * 96d / 72d : bitmap.PixelWidth;
        var height = block.ImageHeight > 0 ? block.ImageHeight * 96d / 72d : bitmap.PixelHeight;
        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxWidth = 660,
            Width = Math.Clamp(width, 24, 660),
            Height = Math.Clamp(height, 24, 900),
            HorizontalAlignment = block.Alignment switch
            {
                OfficeTextAlignment.Center => HorizontalAlignment.Center,
                OfficeTextAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left
            }
        };
        return new Border { Margin = new Thickness(0, 8, 0, 10), Child = image };
    }

    private FrameworkElement CreateTableRow(IReadOnlyList<string> cells)
    {
        var grid = new Grid { Background = (Brush)FindResource("BoneSoftBrush") };
        for (var index = 0; index < cells.Count; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 72 });
            var cell = new Border
            {
                BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(index == 0 ? 1 : 0, 0, 1, 1),
                Padding = new Thickness(8, 6, 8, 6),
                Child = new TextBlock { Text = cells[index], TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 19 }
            };
            Grid.SetColumn(cell, index);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private void ShowTable(TableDocument document)
    {
        TableViewer.ItemsSource = document.Table.DefaultView;
        TableViewer.RowHeight = _settings.SpreadsheetRowHeight;
        TableSummary.Text = document.Summary + (document.WasTruncated ? " · 已限制预览行数" : string.Empty);
        _displayMode = "数据视图";
        SearchBox.Visibility = Visibility.Visible;
        SearchInput.Text = string.Empty;
        ShowState(TableViewer);
    }

    private void ShowState(UIElement visible)
    {
        foreach (var state in new UIElement[] { EmptyState, LoadingState, ErrorState, PdfViewer, TableViewer, OfficeViewer, ShellViewerContainer })
            state.Visibility = state == visible ? Visibility.Visible : Visibility.Collapsed;

        if (!_settings.AnimationsEnabled)
        {
            visible.BeginAnimation(OpacityProperty, null);
            visible.Opacity = 1;
            visible.RenderTransform = Transform.Identity;
            return;
        }

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
        if (_settings.RecentDocuments.Count > _settings.RecentDocumentLimit)
            _settings.RecentDocuments.RemoveRange(_settings.RecentDocumentLimit, _settings.RecentDocuments.Count - _settings.RecentDocumentLimit);
        RefreshRecentDocuments();
        SaveSettings();
    }

    private void RefreshRecentDocuments()
    {
        _settings.RecentDocuments.RemoveAll(item => !File.Exists(item.Path));
        RecentList.ItemsSource = _settings.RecentDocuments.Select(item => new RecentItem(item.Path)).ToList();
    }

    private void RemoveRecentButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RemoveRecent(sender);
        e.Handled = true;
    }

    private void RemoveRecentButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveRecent(sender);
        e.Handled = true;
    }

    private void RemoveRecent(object sender)
    {
        if (sender is not Button { Tag: string path }) return;
        _settings.RecentDocuments.RemoveAll(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        RecentList.SelectedItem = null;
        RefreshRecentDocuments();
        SaveSettings();
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
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Add or Key.OemPlus) { ChangeZoom(_settings.ZoomStepPercent); e.Handled = true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Subtract or Key.OemMinus) { ChangeZoom(-_settings.ZoomStepPercent); e.Handled = true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.D0 or Key.NumPad0) { SetZoom(100); e.Handled = true; }
        else if (e.Key == Key.F11) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; e.Handled = true; }
        else if (e.Key == Key.Escape && WindowState == WindowState.Maximized) { WindowState = WindowState.Normal; e.Handled = true; }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        ChangeZoom(e.Delta > 0 ? _settings.ZoomStepPercent : -_settings.ZoomStepPercent);
        e.Handled = true;
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => ChangeZoom(-_settings.ZoomStepPercent);
    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => ChangeZoom(_settings.ZoomStepPercent);
    private void ZoomResetButton_Click(object sender, RoutedEventArgs e) => SetZoom(100);
    private void ChangeZoom(int delta) => SetZoom(_zoomPercent + delta);

    private void SetZoom(int percent, bool persist = true)
    {
        percent = Math.Clamp(percent, 50, 200);
        var previous = _zoomPercent;
        _zoomPercent = percent;
        var scale = percent / 100d;
        TableScaleTransform.ScaleX = TableScaleTransform.ScaleY = scale;
        OfficeScaleTransform.ScaleX = OfficeScaleTransform.ScaleY = scale;
        try { PdfViewer.ZoomFactor = scale; } catch { }
        if (ShellViewerContainer.Visibility == Visibility.Visible && _shellPreview is not null && previous != percent)
        {
            var step = Math.Max(1, _settings.ZoomStepPercent);
            var steps = Math.Max(1, (int)Math.Round(Math.Abs(percent - previous) / (double)step));
            _shellPreview.TryAdjustZoom(Math.Sign(percent - previous), steps);
        }
        ZoomResetButton.Content = $"{percent}%";
        if (persist && _settings.RememberZoom)
        {
            _settings.CurrentZoomPercent = percent;
            SaveSettings();
        }
    }

    private void ApplyZoomToShellFromDefault()
    {
        if (_shellPreview is null || _zoomPercent == 100) return;
        var steps = Math.Max(1, (int)Math.Round(Math.Abs(_zoomPercent - 100) / (double)Math.Max(1, _settings.ZoomStepPercent)));
        _shellPreview.TryAdjustZoom(Math.Sign(_zoomPercent - 100), steps);
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var previousPreference = _settings.OfficeDisplayPreference;
            var dialog = new SettingsWindow(_settings) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            ApplySettings();
            RefreshRecentDocuments();
            SaveSettings();
            if (_currentDocument is not null && previousPreference != _settings.OfficeDisplayPreference)
                await OpenDocumentAsync(_currentDocument.Path);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplySettings()
    {
        _settings.Normalize();
        TableViewer.RowHeight = _settings.SpreadsheetRowHeight;
        var targetZoom = _settings.RememberZoom ? _settings.CurrentZoomPercent : _settings.DefaultZoomPercent;
        SetZoom(targetZoom, persist: false);
        if (_settings.RecentDocuments.Count > _settings.RecentDocumentLimit)
            _settings.RecentDocuments.RemoveRange(_settings.RecentDocumentLimit, _settings.RecentDocuments.Count - _settings.RecentDocumentLimit);
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        var uninstallerPath = FindUninstallerPath();
        if (uninstallerPath is null)
        {
            MessageBox.Show(this, "当前运行的不是 Setup 安装版本，未找到系统卸载程序。", "无法卸载 DocVista", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            this,
            "确定要卸载 DocVista 吗？\n\n卸载程序将移除应用和快捷方式，不会删除你的文档。",
            "卸载 DocVista",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            var startInfo = new ProcessStartInfo(uninstallerPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(uninstallerPath)!
            };
            startInfo.ArgumentList.Add("--uninstall");
            var uninstaller = Process.Start(startInfo);
            if (uninstaller is null) throw new InvalidOperationException("系统卸载程序没有成功启动。");
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法卸载 DocVista", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? FindUninstallerPath()
    {
        var installRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe"));
        if (File.Exists(installRootPath)) return installRootPath;

        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DocVista";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(keyPath);
            var command = key?.GetValue("UninstallString") as string;
            var registeredPath = ExtractExecutablePath(command);
            if (registeredPath is not null && File.Exists(registeredPath)) return registeredPath;
        }

        return null;
    }

    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command[0] == '"')
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? command[1..closingQuote] : null;
        }

        var executableEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0 ? command[..(executableEnd + 4)].Trim() : null;
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
