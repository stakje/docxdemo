using DocVista.Core;
using DocVista.Rendering;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
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
    private readonly SemaphoreSlim _sheetLoadGate = new(1, 1);
    private CancellationTokenSource? _openCancellation;
    private CancellationTokenSource? _sheetCancellation;
    private ISpreadsheetWorkbook? _workbook;
    private ShellPreviewHost? _shellPreview;
    private Task<CoreWebView2Environment>? _pdfEnvironmentTask;
    private Task? _pdfInitializationTask;
    private WebView2? _pdfViewer;
    private CoreWebView2? _pdfCore;
    private string? _activePdfPath;
    private long _activePdfGeneration;
    private long _openGeneration;
    private bool _pdfEngineFailed;
    private bool _pdfRecoveryInProgress;
    private OfficeDocument? _officeRenderDocument;
    private StackPanel? _officeRenderContent;
    private DispatcherOperation? _officeRenderOperation;
    private long _officeRenderGeneration;
    private int _officeRenderPageIndex;
    private int _officeRenderBlockIndex;
    private DocumentInfo? _currentDocument;
    private bool _restoringSheet;
    private int _zoomPercent = 100;
    private string _displayMode = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        CreatePdfViewer();
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
        if (argument is not null)
        {
            await OpenDocumentAsync(argument);
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _ = WarmPdfViewerAsync()));
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
        var openGeneration = ++_openGeneration;
        _openCancellation?.Cancel();
        if (!File.Exists(path)) { ShowError("文件不存在或已被移动。"); return; }
        var document = DocumentInfo.FromPath(path);
        if (document.Kind == DocumentKind.Unknown) { ShowError($"暂不支持 {document.Extension} 格式。"); return; }

        _openCancellation?.Dispose();
        _openCancellation = new CancellationTokenSource();
        var cancellationToken = _openCancellation.Token;
        _currentDocument = null;
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
                    await OpenPdfAsync(document.Path, openGeneration, cancellationToken);
                    break;
                case DocumentKind.Csv:
                    var csv = await Task.Run(() => CsvDocument.LoadAsync(document.Path, cancellationToken), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (openGeneration != _openGeneration) throw new OperationCanceledException(cancellationToken);
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
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            StatusText.Text = "打开失败";
        }
    }

    private async Task OpenPdfAsync(string path, long openGeneration, CancellationToken cancellationToken)
    {
        await Task.Run(() => PdfDocumentSource.Validate(path), cancellationToken);
        try
        {
            await EnsurePdfViewerAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (WebView2RuntimeNotFoundException exception)
        {
            throw new InvalidOperationException("系统缺少 Microsoft Edge WebView2 Runtime，无法显示 PDF。请安装或修复 WebView2 Runtime 后重试。", exception);
        }
        catch (Exception firstFailure)
        {
            Debug.WriteLine($"PDF 引擎初始化失败，正在重建查看器：{firstFailure}");
            try { await RecreatePdfViewerAsync(cancellationToken); }
            catch (WebView2RuntimeNotFoundException exception)
            {
                throw new InvalidOperationException("系统缺少 Microsoft Edge WebView2 Runtime，无法显示 PDF。请安装或修复 WebView2 Runtime 后重试。", exception);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"PDF 渲染引擎初始化失败：{exception.Message}", exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        try { await NavigatePdfAsync(path, cancellationToken); }
        catch (PdfProcessFailedException firstFailure)
        {
            Debug.WriteLine($"PDF 导航期间渲染进程退出，正在重建：{firstFailure}");
            await RecreatePdfViewerAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (openGeneration != _openGeneration) throw new OperationCanceledException(cancellationToken);
            await NavigatePdfAsync(path, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (openGeneration != _openGeneration) throw new OperationCanceledException(cancellationToken);
        ShowState(PdfViewerContainer);
        _pdfViewer?.Focus();
        _activePdfPath = path;
        _activePdfGeneration = openGeneration;
        _displayMode = "PDF";
        SearchBox.Visibility = Visibility.Collapsed;
    }

    private void CreatePdfViewer()
    {
        _pdfEngineFailed = false;
        _pdfViewer = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        PdfViewerContainer.Children.Clear();
        PdfViewerContainer.Children.Add(_pdfViewer);
    }

    private async Task WarmPdfViewerAsync()
    {
        try { await EnsurePdfViewerAsync(CancellationToken.None); }
        catch (Exception exception) { Debug.WriteLine($"PDF 查看器预热失败：{exception}"); }
    }

    private async Task EnsurePdfViewerAsync(CancellationToken cancellationToken)
    {
        if (_pdfEngineFailed)
        {
            DisposePdfViewer();
            CreatePdfViewer();
            _pdfInitializationTask = null;
        }
        _pdfInitializationTask ??= InitializePdfViewerCoreAsync();
        var initialization = _pdfInitializationTask;
        try
        {
            await initialization.WaitAsync(cancellationToken);
        }
        catch
        {
            if (initialization.IsFaulted && ReferenceEquals(_pdfInitializationTask, initialization))
                _pdfInitializationTask = null;
            throw;
        }
    }

    private async Task InitializePdfViewerCoreAsync()
    {
        if (_pdfViewer is null) CreatePdfViewer();
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocVista", "WebView2");
        _pdfEnvironmentTask ??= CoreWebView2Environment.CreateAsync(null, userData);
        CoreWebView2Environment environment;
        try { environment = await _pdfEnvironmentTask; }
        catch
        {
            _pdfEnvironmentTask = null;
            throw;
        }

        await _pdfViewer!.EnsureCoreWebView2Async(environment);
        _pdfCore = _pdfViewer.CoreWebView2;
        var settings = _pdfCore.Settings;
        settings.AreBrowserAcceleratorKeysEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;
        settings.IsStatusBarEnabled = false;
        _pdfCore.ProcessFailed -= PdfViewer_ProcessFailed;
        _pdfCore.ProcessFailed += PdfViewer_ProcessFailed;
        _pdfViewer.ZoomFactor = _zoomPercent / 100d;
    }

    private async Task NavigatePdfAsync(string path, CancellationToken cancellationToken)
    {
        var viewer = _pdfViewer ?? throw new InvalidOperationException("PDF 查看器尚未初始化");
        var core = viewer.CoreWebView2 ?? throw new InvalidOperationException("PDF 渲染引擎尚未就绪");
        var completion = new TaskCompletionSource<(bool IsSuccess, CoreWebView2WebErrorStatus Status)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        EventHandler<CoreWebView2NavigationStartingEventArgs>? starting = null;
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? completed = null;
        EventHandler<CoreWebView2ProcessFailedEventArgs>? processFailed = null;
        starting = (_, args) => navigationId ??= args.NavigationId;
        completed = (_, args) =>
        {
            if (navigationId is not null && args.NavigationId == navigationId.Value)
                completion.TrySetResult((args.IsSuccess, args.WebErrorStatus));
        };
        processFailed = (_, args) =>
        {
            if (IsFatalPdfProcessFailure(args.ProcessFailedKind))
                completion.TrySetException(new PdfProcessFailedException(args.ProcessFailedKind));
        };
        core.NavigationStarting += starting;
        core.NavigationCompleted += completed;
        core.ProcessFailed += processFailed;

        try
        {
            core.Navigate(PdfDocumentSource.CreateUri(path).AbsoluteUri);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
            if (!result.IsSuccess)
                throw new InvalidOperationException($"PDF 导航失败：{result.Status}");
        }
        catch (OperationCanceledException)
        {
            try { core.Stop(); } catch { }
            throw;
        }
        catch (TimeoutException exception)
        {
            try { core.Stop(); } catch { }
            throw new TimeoutException("PDF 加载超时，请确认文件未损坏且当前用户有读取权限。", exception);
        }
        finally
        {
            try
            {
                core.NavigationStarting -= starting;
                core.NavigationCompleted -= completed;
                core.ProcessFailed -= processFailed;
            }
            catch { }
        }
    }

    private async Task RecreatePdfViewerAsync(CancellationToken cancellationToken)
    {
        DisposePdfViewer();
        CreatePdfViewer();
        _pdfInitializationTask = null;
        await EnsurePdfViewerAsync(cancellationToken);
    }

    private async void PdfViewer_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Debug.WriteLine($"WebView2 PDF 进程异常：{e.ProcessFailedKind}");
        if (!ReferenceEquals(sender, _pdfCore) || !IsFatalPdfProcessFailure(e.ProcessFailedKind)) return;
        _pdfEngineFailed = true;
        _pdfInitializationTask = null;
        if (_pdfRecoveryInProgress || PdfViewerContainer.Visibility != Visibility.Visible || _activePdfPath is null || _activePdfGeneration == 0) return;
        _pdfRecoveryInProgress = true;
        var path = _activePdfPath;
        var generation = _activePdfGeneration;
        var recoveryToken = _openCancellation?.Token ?? CancellationToken.None;
        try
        {
            StatusText.Text = "PDF 渲染进程正在恢复…";
            await RecreatePdfViewerAsync(recoveryToken);
            if (!IsCurrentPdf(path, generation, recoveryToken)) return;
            await NavigatePdfAsync(path, recoveryToken);
            if (!IsCurrentPdf(path, generation, recoveryToken)) return;
            ShowState(PdfViewerContainer);
            _activePdfPath = path;
            StatusText.Text = $"已恢复 · PDF · {FormatSize(new FileInfo(path).Length)}";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrentPdf(path, generation, recoveryToken)) return;
            ShowError($"PDF 渲染进程异常，自动恢复失败：{exception.Message}");
            StatusText.Text = "PDF 打开失败";
        }
        finally { _pdfRecoveryInProgress = false; }
    }

    private bool IsCurrentPdf(string path, long generation, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && generation == _openGeneration && generation == _activePdfGeneration &&
        string.Equals(path, _activePdfPath, StringComparison.OrdinalIgnoreCase);

    private static bool IsFatalPdfProcessFailure(CoreWebView2ProcessFailedKind kind) => kind is
        CoreWebView2ProcessFailedKind.BrowserProcessExited or
        CoreWebView2ProcessFailedKind.RenderProcessExited;

    private void DisposePdfViewer()
    {
        if (_pdfViewer is null) return;
        try { if (_pdfCore is not null) _pdfCore.ProcessFailed -= PdfViewer_ProcessFailed; }
        catch { }
        try { _pdfViewer.Dispose(); } catch { }
        PdfViewerContainer.Children.Clear();
        _pdfViewer = null;
        _pdfCore = null;
    }

    private async Task OpenSpreadsheetAsync(string path, CancellationToken cancellationToken)
    {
        ShowState(LoadingState);
        LoadingText.Text = $"正在读取 {Path.GetFileName(path)}…";
        ISpreadsheetWorkbook? openedWorkbook = null;
        try
        {
            openedWorkbook = await Task.Run<ISpreadsheetWorkbook>(() => Path.GetExtension(path).Equals(".xls", StringComparison.OrdinalIgnoreCase)
                ? LegacyXlsWorkbook.Open(path, cancellationToken)
                : XlsxWorkbook.Open(path, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _workbook = openedWorkbook;
            openedWorkbook = null;
        }
        finally { openedWorkbook?.Dispose(); }

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
        var workbook = _workbook;
        if (workbook is null) return;
        _sheetCancellation?.Cancel();
        _sheetCancellation?.Dispose();
        var sheetCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sheetCancellation = sheetCancellation;
        var sheetToken = sheetCancellation.Token;
        ShowState(LoadingState);
        LoadingText.Text = $"正在读取 {sheet.Name}…";
        try
        {
            await _sheetLoadGate.WaitAsync(sheetToken);
            TableDocument data;
            try { data = await Task.Run(() => workbook.LoadSheet(sheet, sheetToken), CancellationToken.None); }
            finally { _sheetLoadGate.Release(); }
            sheetToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(_workbook, workbook)) ShowTable(data);
        }
        finally
        {
            if (ReferenceEquals(_sheetCancellation, sheetCancellation)) _sheetCancellation = null;
            sheetCancellation.Dispose();
        }
    }

    private async Task OpenShellPreviewAsync(string path, CancellationToken cancellationToken)
    {
        var preview = new ShellPreviewHost();
        _shellPreview = preview;
        ShellViewerContainer.Children.Clear();
        ShellViewerContainer.Children.Add(preview);
        try
        {
            ShowState(ShellViewerContainer);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_shellPreview, preview)) throw new OperationCanceledException();
            preview.LoadPreview(path);
            preview.Focus();
            ApplyZoomToShellFromDefault(preview);
            _displayMode = "系统高保真预览";
            SearchBox.Visibility = Visibility.Collapsed;
        }
        catch
        {
            ReleaseShellPreview(preview);
            throw;
        }
    }

    private async Task<bool> TryOpenShellPreviewAsync(string path, CancellationToken cancellationToken)
    {
        try { await OpenShellPreviewAsync(path, cancellationToken); return true; }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private void ReleaseShellPreview(ShellPreviewHost preview)
    {
        try { preview.UnloadPreview(); } catch { }
        if (!ReferenceEquals(_shellPreview, preview)) return;
        _shellPreview = null;
        ShellViewerContainer.Children.Remove(preview);
    }

    private async Task OpenOfficeDocumentAsync(string path, CancellationToken cancellationToken)
    {
        ShowState(LoadingState);
        LoadingText.Text = $"正在读取 {Path.GetFileName(path)}…";
        var document = await Task.Run(() => OfficeDocumentLoader.Load(path, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ResetOfficeRenderer();
        _officeRenderDocument = document;
        _officeRenderGeneration = _openGeneration;
        TableSummary.Text = document.Summary;
        _displayMode = document.Mode == OfficeViewMode.CompatibilityText ? "兼容文本视图" : "兼容视图";
        SearchBox.Visibility = Visibility.Collapsed;
        ShowState(OfficeViewer);
        OfficeViewer.ScrollToTop();
        RenderOfficeBatch();
        ScheduleOfficeRenderIfNeeded();
    }

    private StackPanel CreateOfficePage(OfficePage page, OfficeViewMode mode, int pageNumber, int totalPages)
    {
        var content = new StackPanel();
        if (!string.IsNullOrWhiteSpace(page.Title))
        {
            var title = CreateSelectableText(page.Title);
            title.FontSize = mode == OfficeViewMode.Presentation ? 26 : 20;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = (Brush)FindResource("OuterSpaceBrush");
            title.Margin = new Thickness(0, 0, 0, 18);
            content.Children.Add(title);
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

        var pageSurface = new Border
        {
            MaxWidth = mode == OfficeViewMode.Presentation ? 920 : 780,
            MinHeight = mode == OfficeViewMode.Presentation ? 480 : totalPages > 1 ? 820 : 420,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = (Brush)FindResource("DocumentSurfaceBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(mode == OfficeViewMode.Presentation ? 48 : 54),
            Margin = new Thickness(24, 10, 24, 10),
            Child = grid
        };
        OfficePagesPanel.Children.Add(pageSurface);
        return content;
    }

    private FrameworkElement CreateOfficeBlock(OfficeTextBlock block, OfficeViewMode mode)
    {
        if (block.ImageData is { Length: > 0 }) return CreateDocumentImage(block);

        var fontSize = block.IsHeading ? 20 : block.FontSize > 0 ? Math.Clamp(block.FontSize * 96d / 72d, 11, 28) : mode == OfficeViewMode.Presentation ? 16 : 13;
        var text = CreateSelectableText(block.Text);
        text.FontSize = fontSize;
        text.FontWeight = block.IsHeading || block.IsBold ? FontWeights.SemiBold : FontWeights.Normal;
        text.FontStyle = block.IsItalic ? FontStyles.Italic : FontStyles.Normal;
        text.Foreground = (Brush)FindResource(block.IsHeading ? "OuterSpaceBrush" : "TextBrush");
        text.TextAlignment = block.Alignment switch
        {
            OfficeTextAlignment.Center => TextAlignment.Center,
            OfficeTextAlignment.Right => TextAlignment.Right,
            OfficeTextAlignment.Justify => TextAlignment.Justify,
            _ => TextAlignment.Left
        };
        TextBlock.SetLineHeight(text, mode == OfficeViewMode.Presentation ? 27 : 22);
        TextBlock.SetLineStackingStrategy(text, LineStackingStrategy.BlockLineHeight);
        text.Margin = new Thickness(block.LeftIndent + block.FirstLineIndent, Math.Max(block.SpaceBefore, block.IsHeading ? 14 : 3), 0, Math.Max(block.SpaceAfter, block.IsHeading ? 8 : 5));
        if (block.IsTableRow && block.Cells is { Count: > 0 }) return CreateTableRow(block.Cells);
        if (block.IsTableRow)
            return new Border { Background = (Brush)FindResource("DocumentSurfaceBrush"), BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(9, 6, 9, 6), Child = text };
        return text;
    }

    private void RenderOfficeBatch()
    {
        const int batchBudget = 56;
        var document = _officeRenderDocument;
        if (document is null) return;
        var remaining = batchBudget;

        while (remaining > 0 && _officeRenderPageIndex < document.Pages.Count)
        {
            var page = document.Pages[_officeRenderPageIndex];
            if (_officeRenderContent is null)
            {
                _officeRenderContent = CreateOfficePage(page, document.Mode, _officeRenderPageIndex + 1, document.Pages.Count);
                remaining -= 6;
            }

            if (_officeRenderBlockIndex >= page.Blocks.Count)
            {
                _officeRenderPageIndex++;
                _officeRenderBlockIndex = 0;
                _officeRenderContent = null;
                continue;
            }

            var block = page.Blocks[_officeRenderBlockIndex++];
            _officeRenderContent.Children.Add(CreateOfficeBlock(block, document.Mode));
            remaining -= block.ImageData is { Length: > 0 } ? 8 : 1;
        }

        if (_officeRenderPageIndex >= document.Pages.Count)
        {
            _officeRenderDocument = null;
            _officeRenderContent = null;
        }
    }

    private void OfficeViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) => ScheduleOfficeRenderIfNeeded();

    private void ScheduleOfficeRenderIfNeeded()
    {
        if (_officeRenderDocument is null || OfficeViewer.Visibility != Visibility.Visible) return;
        var renderAhead = Math.Max(720, OfficeViewer.ViewportHeight * 1.5);
        if (OfficeViewer.ExtentHeight > 0 && OfficeViewer.VerticalOffset + OfficeViewer.ViewportHeight + renderAhead < OfficeViewer.ExtentHeight) return;
        if (_officeRenderOperation is { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing }) return;

        _officeRenderOperation = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _officeRenderOperation = null;
            var generation = _officeRenderGeneration;
            if (generation == 0 || generation != _openGeneration) return;
            try
            {
                RenderOfficeBatch();
                ScheduleOfficeRenderIfNeeded();
            }
            catch (Exception exception)
            {
                if (generation != _openGeneration) return;
                ShowError($"文档后续内容渲染失败：{exception.Message}");
                StatusText.Text = "打开失败";
            }
        }));
    }

    private void ResetOfficeRenderer()
    {
        if (_officeRenderOperation?.Status == DispatcherOperationStatus.Pending) _officeRenderOperation.Abort();
        _officeRenderOperation = null;
        _officeRenderDocument = null;
        _officeRenderContent = null;
        _officeRenderGeneration = 0;
        _officeRenderPageIndex = 0;
        _officeRenderBlockIndex = 0;
        OfficePagesPanel.Children.Clear();
    }

    private FrameworkElement CreateDocumentImage(OfficeTextBlock block)
    {
        using var stream = new MemoryStream(block.ImageData!, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (block.ImageWidth > 0)
            bitmap.DecodePixelWidth = (int)Math.Clamp(Math.Ceiling(block.ImageWidth * 96d / 72d * 1.5), 64, 1320);
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
        var grid = new Grid { Background = (Brush)FindResource("DocumentSurfaceBrush") };
        for (var index = 0; index < cells.Count; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 72 });
            var cell = new Border
            {
                BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(index == 0 ? 1 : 0, 0, 1, 1),
                Padding = new Thickness(8, 6, 8, 6),
                Child = CreateSelectableTableCell(cells[index])
            };
            Grid.SetColumn(cell, index);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private TextBox CreateSelectableText(string value)
    {
        return new TextBox
        {
            Text = value,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            IsTabStop = false,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FocusVisualStyle = null,
            Cursor = Cursors.IBeam,
            SelectionBrush = (Brush)FindResource("AccentBrush"),
            SelectionOpacity = 0.3,
            IsInactiveSelectionHighlightEnabled = true
        };
    }

    private TextBox CreateSelectableTableCell(string value)
    {
        var text = CreateSelectableText(value);
        text.FontSize = 12;
        TextBlock.SetLineHeight(text, 19);
        TextBlock.SetLineStackingStrategy(text, LineStackingStrategy.BlockLineHeight);
        return text;
    }

    private void ShowTable(TableDocument document)
    {
        TableViewer.ItemsSource = document.Table.DefaultView;
        ApplyTableZoom();
        TableSummary.Text = document.Summary + (document.WasTruncated ? " · 已限制预览行数" : string.Empty);
        _displayMode = "数据视图";
        SearchBox.Visibility = Visibility.Visible;
        SearchInput.Text = string.Empty;
        ShowState(TableViewer);
    }

    private void ShowState(UIElement visible)
    {
        foreach (var state in new UIElement[] { EmptyState, LoadingState, ErrorState, PdfViewerContainer, TableViewer, OfficeViewer, ShellViewerContainer })
            state.Visibility = state == visible ? Visibility.Visible : Visibility.Collapsed;

        if (!_settings.AnimationsEnabled || visible == PdfViewerContainer || visible == ShellViewerContainer)
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
        _currentDocument = null;
        DisposeActiveViewers();
        ErrorText.Text = message;
        SearchBox.Visibility = Visibility.Collapsed;
        SheetSelector.Visibility = Visibility.Collapsed;
        TableSummary.Text = string.Empty;
        ShowState(ErrorState);
    }

    private void DisposeActiveViewers()
    {
        _activePdfPath = null;
        _activePdfGeneration = 0;
        try { _pdfViewer?.CoreWebView2?.Stop(); } catch { }
        _sheetCancellation?.Cancel();
        _sheetCancellation?.Dispose();
        _sheetCancellation = null;
        TableViewer.ItemsSource = null;
        ResetOfficeRenderer();
        var shellPreview = _shellPreview;
        _shellPreview = null;
        if (shellPreview is not null)
        {
            try { shellPreview.UnloadPreview(); } catch { }
        }
        ShellViewerContainer.Children.Clear();
        var workbook = _workbook;
        _workbook = null;
        if (workbook is not null) QueueWorkbookDisposal(workbook);
        _restoringSheet = true;
        SheetSelector.ItemsSource = null;
        SheetSelector.Visibility = Visibility.Collapsed;
        _restoringSheet = false;
        SearchBox.Visibility = Visibility.Collapsed;
        TableSummary.Text = string.Empty;
    }

    private void QueueWorkbookDisposal(ISpreadsheetWorkbook workbook)
    {
        if (_sheetLoadGate.Wait(0))
        {
            try { workbook.Dispose(); }
            finally { _sheetLoadGate.Release(); }
            return;
        }
        _ = DisposeWorkbookAfterReadsAsync(workbook);
    }

    private async Task DisposeWorkbookAfterReadsAsync(ISpreadsheetWorkbook workbook)
    {
        await _sheetLoadGate.WaitAsync();
        try { workbook.Dispose(); }
        catch (Exception exception) { Debug.WriteLine(exception); }
        finally { _sheetLoadGate.Release(); }
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
        catch (OperationCanceledException) { }
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
        ApplyTableZoom();
        OfficeScaleTransform.ScaleX = OfficeScaleTransform.ScaleY = scale;
        try { if (_pdfViewer is not null) _pdfViewer.ZoomFactor = scale; } catch { }
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

    private void ApplyTableZoom()
    {
        var scale = _zoomPercent / 100d;
        TableViewer.FontSize = 12 * scale;
        TableViewer.RowHeight = _settings.SpreadsheetRowHeight * scale;
        TableViewer.ColumnHeaderHeight = 34 * scale;
        TableViewer.RowHeaderWidth = 46 * scale;
    }

    private void ApplyZoomToShellFromDefault(ShellPreviewHost preview)
    {
        if (_zoomPercent == 100) return;
        var steps = Math.Max(1, (int)Math.Round(Math.Abs(_zoomPercent - 100) / (double)Math.Max(1, _settings.ZoomStepPercent)));
        preview.TryAdjustZoom(Math.Sign(_zoomPercent - 100), steps);
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
        DisposePdfViewer();
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

    private sealed class PdfProcessFailedException(CoreWebView2ProcessFailedKind kind)
        : InvalidOperationException($"PDF 渲染进程异常：{kind}");
}
