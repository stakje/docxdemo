using DocVista.Core;
using System.Windows;
using System.Windows.Input;

namespace DocVista.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ZoomStepCombo.ItemsSource = new[] { 5, 10, 15, 20, 25, 50 }.Select(value => $"{value}%").ToList();
        OfficeModeCombo.ItemsSource = new[] { "自动（优先系统高保真预览）", "兼容视图（统一缩放）" };
        LoadValues(settings);
    }

    private void LoadValues(AppSettings settings)
    {
        DefaultZoomSlider.Value = settings.DefaultZoomPercent;
        ZoomStepCombo.SelectedIndex = Array.IndexOf(new[] { 5, 10, 15, 20, 25, 50 }, settings.ZoomStepPercent);
        if (ZoomStepCombo.SelectedIndex < 0) ZoomStepCombo.SelectedIndex = 1;
        RememberZoomCheck.IsChecked = settings.RememberZoom;
        OfficeModeCombo.SelectedIndex = settings.OfficeDisplayPreference == OfficeDisplayPreference.Auto ? 0 : 1;
        RecentLimitSlider.Value = settings.RecentDocumentLimit;
        RowHeightSlider.Value = settings.SpreadsheetRowHeight;
        AnimationsCheck.IsChecked = settings.AnimationsEnabled;
        RefreshLabels();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.DefaultZoomPercent = (int)DefaultZoomSlider.Value;
        _settings.ZoomStepPercent = new[] { 5, 10, 15, 20, 25, 50 }[Math.Max(0, ZoomStepCombo.SelectedIndex)];
        _settings.RememberZoom = RememberZoomCheck.IsChecked == true;
        _settings.OfficeDisplayPreference = OfficeModeCombo.SelectedIndex == 1 ? OfficeDisplayPreference.Compatibility : OfficeDisplayPreference.Auto;
        _settings.RecentDocumentLimit = (int)RecentLimitSlider.Value;
        _settings.SpreadsheetRowHeight = RowHeightSlider.Value;
        _settings.AnimationsEnabled = AnimationsCheck.IsChecked == true;
        _settings.Normalize();
        DialogResult = true;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => LoadValues(new AppSettings());
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void DefaultZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RefreshLabels();
    private void RecentLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RefreshLabels();
    private void RowHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RefreshLabels();

    private void RefreshLabels()
    {
        if (DefaultZoomValue is null || RecentLimitValue is null || RowHeightValue is null) return;
        DefaultZoomValue.Text = $"{DefaultZoomSlider.Value:0}%";
        RecentLimitValue.Text = $"{RecentLimitSlider.Value:0} 条";
        RowHeightValue.Text = $"{RowHeightSlider.Value:0} px";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
