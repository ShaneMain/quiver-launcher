using System.Text.Json;
using FluentAssertions;
using QuiverLauncher.Core.Models;
using QuiverLauncher.Models;

namespace QuiverLauncher.Tests;

public class CliLibraryTests
{
    [Fact]
    public void Serialize_preserves_consumer_identity_artwork_and_release_versions()
    {
        var game = new GameInfo
        {
            Name = "Pokémon \"Red\"",
            Repository = "Owner/Project",
            RepositorySource = "gitlab",
            FolderName = "Red-Portable",
            GameIconUrl = "https://catalog.example/art/red.png?size=256&variant=1",
            InstalledVersion = "v1.2.3+portable",
            LatestVersion = "v2.0.0-rc.1",
            Status = GameStatus.UpdateAvailable,
        };

        using var document = JsonDocument.Parse(CliLibrary.Serialize([game]));
        var root = document.RootElement;
        root.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("schema_version", "apps");
        root.GetProperty("schema_version").GetInt32().Should().Be(1);
        root.GetProperty("apps").GetArrayLength().Should().Be(1);
        var app = root.GetProperty("apps")[0];
        app.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "id", "name", "repository", "icon_url", "status", "installed_version", "latest_version");
        app.GetProperty("id").GetString().Should().Be("gitlab:Owner/Project:Red-Portable");
        app.GetProperty("name").GetString().Should().Be(game.Name);
        app.GetProperty("repository").GetString().Should().Be("Owner/Project");
        app.GetProperty("icon_url").GetString().Should().Be(game.GameIconUrl);
        app.GetProperty("installed_version").GetString().Should().Be("v1.2.3+portable");
        app.GetProperty("latest_version").GetString().Should().Be("v2.0.0-rc.1");
        app.GetProperty("status").GetString().Should().Be("update_available");
    }

    [Fact]
    public void Serialize_keeps_equal_names_with_distinct_repositories_folders_and_hosts()
    {
        var games = new[]
        {
            App("Same name", "owner/shared", "FolderA"),
            App("Same name", "owner/shared", "FolderB"),
            App("Same name", "other/project", "FolderA"),
            App("Same name", "owner/shared", "FolderA", "gitlab"),
        };

        using var document = JsonDocument.Parse(CliLibrary.Serialize(games));
        var apps = document.RootElement.GetProperty("apps").EnumerateArray().ToArray();
        apps.Should().HaveCount(4);
        apps.Select(app => app.GetProperty("id").GetString()).Should().BeEquivalentTo(
            "github:owner/shared:FolderA", "github:owner/shared:FolderB",
            "github:other/project:FolderA", "gitlab:owner/shared:FolderA");
        apps.Select(app => app.GetProperty("name").GetString()).Should().OnlyContain(name => name == "Same name");
        foreach (var game in games)
        {
            var app = apps.Single(entry => entry.GetProperty("id").GetString() == game.InstanceKey);
            app.GetProperty("repository").GetString().Should().Be(game.Repository);
            CliLibrary.FindById(games, app.GetProperty("id").GetString()!).Should().BeSameAs(game);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Serialize_normalizes_missing_metadata_without_losing_manual_app_identity(string? blank)
    {
        var game = new GameInfo
        {
            Name = blank,
            Repository = blank,
            FolderName = "MyManualApp",
            GameIconUrl = blank,
            InstalledVersion = blank,
            LatestVersion = blank,
            Status = GameStatus.NotInstalled,
        };

        using var document = JsonDocument.Parse(CliLibrary.Serialize([game]));
        var app = document.RootElement.GetProperty("apps")[0];
        app.GetProperty("id").GetString().Should().Be("manual:MyManualApp");
        foreach (var field in new[] { "repository", "icon_url", "installed_version", "latest_version" })
            app.GetProperty(field).ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(GameStatus.Installed, "installed")]
    [InlineData(GameStatus.UpdateAvailable, "update_available")]
    [InlineData(GameStatus.NotInstalled, "not_installed")]
    [InlineData(GameStatus.Downloading, "unavailable")]
    [InlineData(GameStatus.Installing, "unavailable")]
    [InlineData(GameStatus.Updating, "unavailable")]
    [InlineData(GameStatus.NeedsExecutable, "unavailable")]
    [InlineData((GameStatus)999, "unavailable")]
    public void Serialize_maps_only_actionable_native_states_directly(GameStatus status, string expected)
    {
        var game = App("App", "owner/project", "AppFolder");
        game.Status = status;

        using var document = JsonDocument.Parse(CliLibrary.Serialize([game]));
        document.RootElement.GetProperty("apps")[0].GetProperty("status").GetString().Should().Be(expected);
    }

    [Fact]
    public void Serialize_orders_names_and_ties_deterministically_without_changing_ids()
    {
        var games = new[]
        {
            App("Zulu", "owner/zulu", "Zulu"),
            App("alpha", "owner/alpha", "AlphaB"),
            App("Beta", "owner/beta", "Beta"),
            App("alpha", "owner/alpha", "AlphaA"),
        };

        var json = CliLibrary.Serialize(games);
        CliLibrary.Serialize(games.Reverse()).Should().Be(json);
        using var document = JsonDocument.Parse(json);
        var apps = document.RootElement.GetProperty("apps").EnumerateArray().ToArray();
        apps.Select(app => app.GetProperty("name").GetString()).Should().Equal("alpha", "alpha", "Beta", "Zulu");
        apps.Select(app => app.GetProperty("id").GetString()).Should().Equal(
            "github:owner/alpha:AlphaA", "github:owner/alpha:AlphaB",
            "github:owner/beta:Beta", "github:owner/zulu:Zulu");
    }

    [Fact]
    public void Serialize_empty_library_is_a_valid_empty_inventory()
    {
        using var document = JsonDocument.Parse(CliLibrary.Serialize([]));

        document.RootElement.GetProperty("schema_version").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("apps").ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetProperty("apps").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("owner/project", "AppFolder")]
    [InlineData("OWNER/PROJECT", "APPFOLDER")]
    public void Duplicate_ids_prevent_both_inventory_and_targeted_lookup(string repository, string folder)
    {
        var games = new[]
        {
            App("First", "owner/project", "AppFolder"),
            App("Different name", repository, folder),
        };

        Action serialize = () => CliLibrary.Serialize(games);
        Action find = () => CliLibrary.FindById(games, "github:owner/project:AppFolder");

        serialize.Should().Throw<ArgumentException>().WithParameterName("games");
        find.Should().Throw<ArgumentException>().WithParameterName("games");
    }

    [Fact]
    public void FindById_rejects_duplicate_snapshot_even_when_requested_app_is_unique()
    {
        var target = App("Target", "owner/target", "Target");
        var games = new[]
        {
            target,
            App("Other", "owner/other", "Other"),
            App("Duplicate", "OWNER/OTHER", "OTHER"),
        };

        Action find = () => CliLibrary.FindById(games, target.InstanceKey);

        find.Should().Throw<ArgumentException>().WithParameterName("games");
    }

    [Fact]
    public void FindById_uses_full_case_insensitive_identity_not_enumeration_order()
    {
        var first = App("Same", "owner/project", "FolderA");
        var target = App("Same", "owner/project", "FolderB");
        var games = new[] { first, target };

        CliLibrary.FindById(games, "GITHUB:OWNER/PROJECT:FOLDERB").Should().BeSameAs(target);
        CliLibrary.FindById(games.Reverse(), "github:owner/project:FolderB").Should().BeSameAs(target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("Visible name")]
    [InlineData("FolderA")]
    [InlineData("owner/project")]
    [InlineData("github:owner/project")]
    [InlineData("github:owner/project:Folder")]
    [InlineData("github:owner/project:FolderAB")]
    [InlineData("gitlab:owner/project:FolderA")]
    [InlineData(" github:owner/project:FolderA ")]
    public void FindById_rejects_empty_unknown_or_partial_targets(string? id)
    {
        var game = App("Visible name", "owner/project", "FolderA");

        Action find = () => CliLibrary.FindById([game], id!);

        find.Should().Throw<ArgumentException>().WithParameterName("id");
    }

    [Fact]
    public void FindById_rejects_unknown_id_in_empty_library()
    {
        Action find = () => CliLibrary.FindById([], "manual:Missing");

        find.Should().Throw<ArgumentException>().WithParameterName("id");
    }

    private static GameInfo App(string name, string repository, string folder, string? source = null) => new()
    {
        Name = name,
        Repository = repository,
        RepositorySource = source,
        FolderName = folder,
    };
}
