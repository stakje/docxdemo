using Velopack;
using System.IO;

namespace DocVista.App;

public sealed record UpdateCheckResult(bool Available, string Message, UpdateInfo? Update = null);

public sealed class UpdateService
{
    public async Task<UpdateCheckResult> CheckAsync()
    {
        var feedUrl = ResolveFeedUrl();
        if (feedUrl is null) return new UpdateCheckResult(false, "尚未配置更新源");
        var manager = new UpdateManager(feedUrl);
        if (!manager.IsInstalled) return new UpdateCheckResult(false, "开发版本不执行在线更新");
        var update = await manager.CheckForUpdatesAsync();
        return update is null
            ? new UpdateCheckResult(false, "当前已是最新版本")
            : new UpdateCheckResult(true, $"发现版本 {update.TargetFullRelease.Version}", update);
    }

    public async Task DownloadAndRestartAsync(UpdateInfo update, Action<int> progress, CancellationToken cancellationToken = default)
    {
        var feedUrl = ResolveFeedUrl() ?? throw new InvalidOperationException("尚未配置更新源");
        var manager = new UpdateManager(feedUrl);
        await manager.DownloadUpdatesAsync(update, progress, cancellationToken);
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }

    private static string? ResolveFeedUrl()
    {
        var environment = Environment.GetEnvironmentVariable("DOCVISTA_UPDATE_FEED");
        if (Uri.TryCreate(environment, UriKind.Absolute, out _)) return environment;
        var configurationPath = Path.Combine(AppContext.BaseDirectory, "update-feed.txt");
        if (!File.Exists(configurationPath)) return null;
        var configured = File.ReadAllText(configurationPath).Trim();
        return Uri.TryCreate(configured, UriKind.Absolute, out _) ? configured : null;
    }
}
