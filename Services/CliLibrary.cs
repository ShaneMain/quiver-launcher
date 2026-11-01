using System.Text.Json;
using System.Text.Json.Serialization;
using QuiverLauncher.Core.Models;
using QuiverLauncher.Models;

namespace QuiverLauncher;

internal static class CliLibrary
{
    internal static string Serialize(IEnumerable<GameInfo> games)
    {
        ArgumentNullException.ThrowIfNull(games);

        var apps = new List<LibraryApp>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            var id = game.InstanceKey;
            AddUniqueId(ids, id);
            apps.Add(new LibraryApp(
                id,
                string.IsNullOrWhiteSpace(game.Name) ? "Unknown" : game.Name,
                NullIfBlank(game.Repository),
                NullIfBlank(game.GameIconUrl),
                game.Status switch
                {
                    GameStatus.Installed => "installed",
                    GameStatus.UpdateAvailable => "update_available",
                    GameStatus.NotInstalled => "not_installed",
                    _ => "unavailable",
                },
                NullIfBlank(game.InstalledVersion),
                NullIfBlank(game.LatestVersion)));
        }

        apps.Sort(static (left, right) =>
        {
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (comparison != 0)
                return comparison;
            comparison = StringComparer.Ordinal.Compare(left.Name, right.Name);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Id, right.Id);
        });

        return JsonSerializer.Serialize(new { schema_version = 1, apps });
    }

    internal static GameInfo FindById(IEnumerable<GameInfo> games, string id)
    {
        ArgumentNullException.ThrowIfNull(games);
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("An exact app ID is required.", nameof(id));

        GameInfo? match = null;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            var instanceKey = game.InstanceKey;
            // Validate the entire snapshot, even if the requested app was already found.
            AddUniqueId(ids, instanceKey);
            if (string.Equals(instanceKey, id, StringComparison.OrdinalIgnoreCase))
                match = game;
        }

        return match ?? throw new ArgumentException($"No app has ID '{id}'.", nameof(id));
    }

    private static void AddUniqueId(HashSet<string> ids, string id)
    {
        if (!ids.Add(id))
            throw new ArgumentException($"The library contains duplicate app ID '{id}'.", "games");
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private readonly record struct LibraryApp(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("repository")] string? Repository,
        [property: JsonPropertyName("icon_url")] string? IconUrl,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("installed_version")] string? InstalledVersion,
        [property: JsonPropertyName("latest_version")] string? LatestVersion);
}
