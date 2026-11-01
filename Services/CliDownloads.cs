using System.Text.Json;
using QuiverLauncher.Core.Models;
using QuiverLauncher.Core.Services;
using QuiverLauncher.Models;
using QuiverLauncher.Services;

namespace QuiverLauncher;

internal static class CliDownloads
{
    internal static void PrepareSelection(GameInfo game, GitHubRelease release, AppSettings settings,
        bool flatpak, bool portable)
    {
        GameDownloadService.TrySelectPlatformDownload(game, release, settings);
        bool PreservesFormat(GitHubAsset asset) =>
            flatpak ? GameInstallationService.IsFlatpakAsset(asset.name) :
            !portable || !GameInstallationService.IsFlatpakAsset(asset.name);

        // Keep the native saved-build/automatic choice, but never change installation format automatically.
        if (game.SelectedDownload != null && PreservesFormat(game.SelectedDownload))
            return;
        game.SelectedDownload = null;
        var platform = GameInfo.GetPlatformIdentifier(settings);
        GitHubAsset? recommended = null;
        foreach (var asset in game.DownloadChoices!.Eligible)
        {
            if (!GameInfo.MatchesPlatform(asset.name, platform) || !PreservesFormat(asset))
                continue;
            if (recommended != null)
                return;
            recommended = asset;
        }
        game.SelectedDownload = recommended;
    }

    internal static string Serialize(string gameId, string platform, GitHubRelease? release,
        DownloadAssetSelection? choices, GitHubAsset? selected, string? reason) =>
        JsonSerializer.Serialize(new
        {
            schema_version = 1,
            game_id = gameId,
            release = release?.tag_name,
            platform,
            selected = selected?.name,
            choices = (choices?.Eligible.Select(asset => new
                { name = asset.name, size = 0L, uncertain = false, recommended = GameInfo.MatchesPlatform(asset.name, platform) }) ?? [])
                .Concat(choices?.Uncertain.Select(asset => new
                    { name = asset.name, size = 0L, uncertain = true, recommended = false }) ?? []),
            reason,
        });
}
