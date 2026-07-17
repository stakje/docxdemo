using System.Text.Json;

namespace DocVista.Core;

public enum OfficeDisplayPreference { Auto, Compatibility }

public sealed class AppSettings
{
    public double WindowWidth { get; set; } = 1240;
    public double WindowHeight { get; set; } = 780;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool IsMaximized { get; set; }
    public bool SidebarCollapsed { get; set; }
    public int DefaultZoomPercent { get; set; } = 100;
    public int CurrentZoomPercent { get; set; } = 100;
    public int ZoomStepPercent { get; set; } = 10;
    public bool RememberZoom { get; set; } = true;
    public int RecentDocumentLimit { get; set; } = 12;
    public double SpreadsheetRowHeight { get; set; } = 30;
    public bool AnimationsEnabled { get; set; } = true;
    public OfficeDisplayPreference OfficeDisplayPreference { get; set; } = OfficeDisplayPreference.Auto;
    public List<RecentDocument> RecentDocuments { get; set; } = [];

    public void Normalize()
    {
        DefaultZoomPercent = Math.Clamp(DefaultZoomPercent, 50, 200);
        CurrentZoomPercent = Math.Clamp(CurrentZoomPercent, 50, 200);
        ZoomStepPercent = Math.Clamp(ZoomStepPercent, 5, 50);
        RecentDocumentLimit = Math.Clamp(RecentDocumentLimit, 3, 30);
        SpreadsheetRowHeight = Math.Clamp(SpreadsheetRowHeight, 24, 44);
    }
}

public sealed record RecentDocument(string Path, DateTimeOffset OpenedAt);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public SettingsStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocVista");
        Directory.CreateDirectory(root);
        _settingsPath = Path.Combine(root, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _settingsPath, true);
    }
}
