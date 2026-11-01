using System.IO.Compression;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using QuiverLauncher.Core.Models;
using QuiverLauncher.Core.Services;
using QuiverLauncher.Models;
using QuiverLauncher.Services;

namespace QuiverLauncher.Tests;

public class CliUpdateTests
{
    [Fact]
    public async Task Update_loads_files_for_version_only_catalog_evidence_and_keeps_the_installed_build()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuiverCliUpdateTests", Guid.NewGuid().ToString("N"));
        var previousRoot = QuiverLauncherPaths.OverrideUserDataRoot;
        var previousStore = SettingsStoreProvider.Default;
        var previousOutput = Console.Out;
        using var output = new StringWriter();
        Directory.CreateDirectory(root);
        try
        {
            Console.SetOut(output);
            QuiverLauncherPaths.OverrideUserDataRoot = root;
            var gamesFolder = Path.Combine(root, "Apps");
            var store = new FileSettingsStore(Path.Combine(root, "settings.json"));
            store.Save(new AppSettings { Platform = TestPlatforms.ForWindowsPayload, AppsPath = gamesFolder });
            SettingsStoreProvider.Default = store;
            var repository = "cli-version-only/" + Guid.NewGuid().ToString("N");
            var target = new GitHubRelease
            {
                tag_name = "v2.0.0",
                assets = [
                    new() { name = "game-v2.0.0-win64.zip", browser_download_url = "https://example.com/installed-build.zip" },
                    new() { name = "game-v2.0.0-win32.zip", browser_download_url = "https://example.com/other-build.zip" },
                ],
            };
            var newerUnverified = new GitHubRelease
            {
                tag_name = "v3.0.0",
                assets = [new() { name = "game-v3.0.0-win64.zip", browser_download_url = "https://example.com/unverified.zip" }],
            };
            byte[] archive;
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using var payload = zip.CreateEntry("game.exe").Open();
                    payload.Write([0x4D, 0x5A, 0x02]);
                }
                archive = stream.ToArray();
            }
            using var client = new HttpClient(new Handler(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                {
                    var path = request.RequestUri.AbsolutePath;
                    var json = path.EndsWith("/latest") ? JsonSerializer.Serialize(newerUnverified)
                        : path.EndsWith("/tags/v2.0.0") ? JsonSerializer.Serialize(target)
                        : path.EndsWith("/releases") ? JsonSerializer.Serialize(new[] { newerUnverified, target })
                        : throw new InvalidOperationException($"Unexpected release endpoint: {request.RequestUri}");
                    return new(HttpStatusCode.OK) { Content = new StringContent(json) };
                }
                if (request.RequestUri.AbsoluteUri == "https://example.com/installed-build.zip")
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
                throw new InvalidOperationException($"Must not download another build or release: {request.RequestUri}");
            }));
            using var manager = new GameManager(store, client, new AppCatalogService(dataDirectory: root));
            var game = new GameInfo
            {
                Name = "Catalog-only update",
                Repository = repository,
                FolderName = "Game",
                InstalledVersion = "v1.0.0",
                CatalogVerifiedVersion = target.tag_name,
                CatalogVerifiedAt = DateTimeOffset.UtcNow,
                Status = GameStatus.UpdateAvailable,
            };
            manager.Games.Add(game);
            var gamePath = game.GetInstallPath(gamesFolder);
            Directory.CreateDirectory(gamePath);
            await File.WriteAllTextAsync(Path.Combine(gamePath, "version.txt"), "v1.0.0", TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(gamePath, "game.exe"), [0x4D, 0x5A, 0x01], TestContext.Current.CancellationToken);
            await new InstalledBuild("game-v1.0.0-win64.zip", new string('0', 64)).WriteAsync(gamePath);
            GitHubApiCache.SetCache(null, repository, "v1.0.0", "", new GitHubRelease
            {
                tag_name = "v1.0.0",
                assets = [new() { name = "old-win64.zip", browser_download_url = "https://example.com/old.zip" }],
            }, persist: false);

            var exitCode = await new CLIHandler(manager).Execute(["--update"])
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            exitCode.Should().Be(0, "CLI output: {0}", output.ToString());
            File.ReadAllText(Path.Combine(gamePath, "version.txt")).Trim().Should().Be("v2.0.0");
            File.ReadAllBytes(Path.Combine(gamePath, "game.exe")).Should().Equal(0x4D, 0x5A, 0x02);
            InstalledBuild.Read(gamePath)!.FileName.Should().Be("game-v2.0.0-win64.zip");
            game.Status.Should().Be(GameStatus.Installed);
        }
        finally
        {
            Console.SetOut(previousOutput);
            SettingsStoreProvider.Default = previousStore;
            QuiverLauncherPaths.OverrideUserDataRoot = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downloads_json_hydrates_version_only_evidence_and_reports_native_choices_without_installing(bool recordedBuild)
    {
        using var fixture = new ReviewFixture();
        var game = await fixture.AddInstalledAsync("Game", recordedBuild);
        GitHubApiCache.SetCache(null, game.Repository!, "v1.0.0", "", new GitHubRelease
        {
            tag_name = "v1.0.0",
            assets = [new() { name = "old-win64.zip", browser_download_url = "https://example.com/old.zip" }],
        }, persist: false);
        var before = fixture.ReadInstalledFiles(game);

        var result = await new CLIHandler(fixture.Manager).Execute(["--downloads-json", game.InstanceKey]);

        result.Should().Be(0);
        using var document = JsonDocument.Parse(fixture.Output.ToString());
        var json = document.RootElement;
        json.GetProperty("release").GetString().Should().Be("v2.0.0");
        json.GetProperty("selected").GetString().Should().Be(recordedBuild ? fixture.Release.assets[0].name : null);
        json.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        var choices = json.GetProperty("choices").EnumerateArray().ToArray();
        choices.Select(choice => choice.GetProperty("name").GetString()).Should().Equal(
            "game-v2.0.0-win64.zip", "game-v2.0.0-win32.zip", "portable-build.zip");
        fixture.Downloads.Should().BeEmpty();
        fixture.ReadInstalledFiles(game).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downloads_json_recommends_native_files_but_selects_only_one_portable_archive(bool anotherNativeArchive)
    {
        using var fixture = new ReviewFixture(TargetOS.LinuxX64);
        fixture.UseLinuxDownloads();
        if (anotherNativeArchive)
            fixture.Release.assets = [.. fixture.Release.assets, new()
            {
                name = "game-v2.0.0-linux-alt.tar.gz", browser_download_url = "https://example.com/linux-alt.tar.gz",
            }];
        var game = await fixture.AddInstalledAsync("Game", recordedBuild: false);
        game.PreferredVersion = "v2.0.0";
        game.ReleaseAssetFilter = "*";
        await fixture.Manager.CatalogService.SaveLocalAppsAsync([game]);
        var libraryBefore = File.ReadAllBytes(fixture.Manager.CatalogService.AppsConfigPath);
        var before = fixture.ReadInstalledFiles(game);

        var result = await new CLIHandler(fixture.Manager).Execute(["--downloads-json", game.InstanceKey]);

        result.Should().Be(0, "CLI output: {0}", fixture.Output.ToString());
        using var document = JsonDocument.Parse(fixture.Output.ToString());
        var json = document.RootElement;
        json.GetProperty("selected").GetString().Should().Be(anotherNativeArchive ? null : "game-v2.0.0-linux.tar.gz");
        var choices = json.GetProperty("choices").EnumerateArray()
            .ToDictionary(choice => choice.GetProperty("name").GetString()!);
        choices["game-v2.0.0-linux.tar.gz"].GetProperty("recommended").GetBoolean().Should().BeTrue();
        choices["game-v2.0.0-linux-flatpak.zip"].GetProperty("recommended").GetBoolean().Should().BeTrue();
        choices["game-v2.0.0-win64.zip"].GetProperty("recommended").GetBoolean().Should().BeFalse();
        choices["portable-build.tar"].GetProperty("recommended").GetBoolean().Should().BeFalse();
        choices["portable-build.tar"].GetProperty("uncertain").GetBoolean().Should().BeTrue();
        if (anotherNativeArchive)
            choices["game-v2.0.0-linux-alt.tar.gz"].GetProperty("recommended").GetBoolean().Should().BeTrue();
        fixture.Downloads.Should().BeEmpty();
        fixture.ReadInstalledFiles(game).Should().BeEquivalentTo(before);
        File.ReadAllBytes(fixture.Manager.CatalogService.AppsConfigPath).Should().Equal(libraryBefore);
        File.ReadAllBytes(fixture.SettingsPath).Should().Equal(fixture.InitialSettings);
    }

    [Theory]
    [InlineData("game-v1.0.0-win64.zip", "game-v2.0.0-win64.zip")]
    [InlineData("portable-build.tar", "portable-build.tar")]
    public async Task Downloads_json_preserves_recorded_build_before_native_recommendations(string installedFile, string expectedFile)
    {
        using var fixture = new ReviewFixture(TargetOS.LinuxX64);
        fixture.UseLinuxDownloads();
        var game = await fixture.AddInstalledAsync("Game", recordedBuild: false);
        await new InstalledBuild(installedFile, new string('0', 64)).WriteAsync(game.GetInstallPath(fixture.GamesFolder));

        var result = await new CLIHandler(fixture.Manager).Execute(["--downloads-json", game.InstanceKey]);

        result.Should().Be(0, "CLI output: {0}", fixture.Output.ToString());
        using var document = JsonDocument.Parse(fixture.Output.ToString());
        document.RootElement.GetProperty("selected").GetString().Should().Be(expectedFile);
        var selected = document.RootElement.GetProperty("choices").EnumerateArray()
            .Single(choice => choice.GetProperty("name").GetString() == expectedFile);
        selected.GetProperty("recommended").GetBoolean().Should().BeFalse();
        fixture.Downloads.Should().BeEmpty();
    }

    [Theory]
    [InlineData("*linux.tar.gz", "game-v2.0.0-linux.tar.gz")]
    [InlineData("*flatpak.zip", null)]
    public async Task Downloads_json_honors_filter_without_switching_a_portable_install_to_flatpak(string filter, string? expected)
    {
        using var fixture = new ReviewFixture(TargetOS.LinuxX64);
        fixture.UseLinuxDownloads();
        var game = await fixture.AddInstalledAsync("Game", recordedBuild: false);
        game.ReleaseAssetFilter = filter;
        var before = fixture.ReadInstalledFiles(game);

        var result = await new CLIHandler(fixture.Manager).Execute(["--downloads-json", game.InstanceKey]);

        result.Should().Be(0, "CLI output: {0}", fixture.Output.ToString());
        using var document = JsonDocument.Parse(fixture.Output.ToString());
        document.RootElement.GetProperty("selected").GetString().Should().Be(expected);
        document.RootElement.GetProperty("choices").GetArrayLength().Should().Be(1);
        game.ReleaseAssetFilter.Should().Be(filter);
        fixture.ReadInstalledFiles(game).Should().BeEquivalentTo(before);
        fixture.Downloads.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_review_selection_preserves_flatpak_receipt_instead_of_portable_archives(bool portableOnly)
    {
        using var fixture = new ReviewFixture(TargetOS.LinuxX64);
        fixture.UseLinuxDownloads();
        var game = await fixture.AddInstalledAsync("Game", recordedBuild: false);
        var path = game.GetInstallPath(fixture.GamesFolder);
        FlatpakService.WriteReceipt(Path.Combine(path, FlatpakService.ReceiptFileName),
            new FlatpakReceipt("io.github.example.Game", "x86_64", "stable", new string('a', 64), "v1.0.0"));
        if (portableOnly)
            game.ReleaseAssetFilter = "*linux.tar.gz";
        var before = fixture.ReadInstalledFiles(game);
        var settings = AppSettings.Load();

        // Exercise the same selection consumer as --downloads-json, without calling the host's Flatpak process.
        CliDownloads.PrepareSelection(game, fixture.Release, settings, FlatpakService.HasReceipt(path),
            GameInstallationService.HasCompletePortableInstallation(path, game.GetInstallationOptions()));

        game.SelectedDownload?.name.Should().Be(portableOnly ? null : "game-v2.0.0-linux-flatpak.zip");
        using var document = JsonDocument.Parse(CliDownloads.Serialize(game.InstanceKey,
            GameInfo.GetPlatformIdentifier(settings), fixture.Release, game.DownloadChoices, game.SelectedDownload, null));
        document.RootElement.GetProperty("selected").GetString()
            .Should().Be(portableOnly ? null : "game-v2.0.0-linux-flatpak.zip");
        fixture.ReadInstalledFiles(game).Should().BeEquivalentTo(before);
        fixture.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_update_installs_reviewed_file_for_exact_id_and_keeps_other_equal_name_entry()
    {
        using var fixture = new ReviewFixture();
        var target = await fixture.AddInstalledAsync("Target");
        var other = await fixture.AddInstalledAsync("Other");
        target.PreferredVersion = "v2.0.0";
        target.ReleaseAssetFilter = "*.zip";
        await fixture.Manager.CatalogService.SaveLocalAppsAsync([other, target]);
        var libraryBefore = File.ReadAllBytes(fixture.Manager.CatalogService.AppsConfigPath);
        var otherBefore = fixture.ReadInstalledFiles(other);
        var requested = fixture.Release.assets[1];
        fixture.AfterDownload = () =>
        {
            // The reviewed release is now older, but the installer must not substitute v3 or win64.
            target.CatalogVerifiedVersion = "v3.0.0";
            target.CatalogVerifiedAt = DateTimeOffset.UtcNow;
        };

        var result = await new CLIHandler(fixture.Manager).Execute([
            "--update-app", target.InstanceKey, "--release", "v2.0.0", "--asset", requested.name,
        ]).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        result.Should().Be(0, "CLI output: {0}; errors: {1}", fixture.Output.ToString(), fixture.Errors.ToString());
        fixture.Downloads.Should().Equal(requested.browser_download_url);
        var targetPath = target.GetInstallPath(fixture.GamesFolder);
        File.ReadAllText(Path.Combine(targetPath, "version.txt")).Trim().Should().Be("v2.0.0");
        File.ReadAllBytes(Path.Combine(targetPath, "game.exe")).Should().Equal(0x4D, 0x5A, 0x02);
        var receipt = InstalledBuild.Read(targetPath)!;
        receipt.FileName.Should().Be(requested.name);
        receipt.Sha256.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fixture.Archive)));
        target.Status.Should().Be(GameStatus.UpdateAvailable);
        fixture.ReadInstalledFiles(other).Should().BeEquivalentTo(otherBefore);
        File.ReadAllBytes(fixture.Manager.CatalogService.AppsConfigPath).Should().Equal(libraryBefore);
        File.ReadAllBytes(fixture.SettingsPath).Should().Equal(fixture.InitialSettings);
    }

    [Theory]
    [InlineData("stale-release")]
    [InlineData("changed-tag-case")]
    [InlineData("missing-file")]
    [InlineData("wrong-file-case")]
    [InlineData("incompatible-file")]
    [InlineData("duplicate-file")]
    public async Task Explicit_update_rejects_stale_or_ambiguous_selection_without_mutation(string change)
    {
        using var fixture = new ReviewFixture();
        var game = await fixture.AddInstalledAsync("Game");
        var before = fixture.ReadInstalledFiles(game);
        var tag = change == "stale-release" ? "v1.0.0" : change == "changed-tag-case" ? "V2.0.0" : "v2.0.0";
        var name = change switch
        {
            "missing-file" => "removed-win64.zip",
            "wrong-file-case" => fixture.Release.assets[0].name.ToUpperInvariant(),
            "incompatible-file" => "game-v2.0.0-macos.zip",
            _ => fixture.Release.assets[0].name,
        };
        if (change == "duplicate-file")
            fixture.Release.assets = [.. fixture.Release.assets, new()
            {
                name = name, browser_download_url = "https://example.com/duplicate.zip",
            }];

        var result = await new CLIHandler(fixture.Manager).Execute([
            "--update-app", game.InstanceKey, "--release", tag, "--asset", name,
        ]);

        result.Should().NotBe(0);
        fixture.Downloads.Should().BeEmpty();
        fixture.ReadInstalledFiles(game).Should().BeEquivalentTo(before);
        File.ReadAllBytes(fixture.SettingsPath).Should().Equal(fixture.InitialSettings);
    }

    [Theory]
    [InlineData("unknown-id")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-file")]
    public async Task Downloads_json_rejects_ambiguous_identity_or_files_as_json_without_mutation(string change)
    {
        using var fixture = new ReviewFixture();
        var game = await fixture.AddInstalledAsync("Game");
        var before = fixture.ReadInstalledFiles(game);
        if (change == "duplicate-id")
            fixture.Manager.Games.Add(new GameInfo { Repository = game.Repository, FolderName = game.FolderName });
        if (change == "duplicate-file")
            fixture.Release.assets = [.. fixture.Release.assets, fixture.Release.assets[0]];

        var result = await new CLIHandler(fixture.Manager).Execute([
            "--downloads-json", change == "unknown-id" ? "github:missing/app:Missing" : game.InstanceKey,
        ]);

        result.Should().NotBe(0);
        using var document = JsonDocument.Parse(fixture.Output.ToString());
        document.RootElement.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace();
        document.RootElement.GetProperty("selected").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("choices").GetArrayLength().Should().Be(0);
        fixture.Downloads.Should().BeEmpty();
        fixture.ReadInstalledFiles(game).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Downloads_json_reports_empty_policy_reason_without_hiding_the_release()
    {
        using var fixture = new ReviewFixture();
        var game = await fixture.AddInstalledAsync("Game");
        game.ReleaseAssetFilter = "missing-*";

        var result = await new CLIHandler(fixture.Manager).Execute(["--downloads-json", game.InstanceKey]);

        result.Should().Be(0);
        using var document = JsonDocument.Parse(fixture.Output.ToString());
        document.RootElement.GetProperty("release").GetString().Should().Be("v2.0.0");
        document.RootElement.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace();
        document.RootElement.GetProperty("choices").GetArrayLength().Should().Be(0);
        fixture.Downloads.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_update_at_the_installed_tag_proves_or_installs_the_reviewed_file(bool sameBuild)
    {
        using var fixture = new ReviewFixture();
        var game = await fixture.AddInstalledAsync("Game");
        var path = game.GetInstallPath(fixture.GamesFolder);
        await File.WriteAllTextAsync(Path.Combine(path, "version.txt"), "v2.0.0", TestContext.Current.CancellationToken);
        await new InstalledBuild(fixture.Release.assets[0].name, new string('0', 64)).WriteAsync(path);
        var requested = fixture.Release.assets[sameBuild ? 0 : 1];

        var result = await new CLIHandler(fixture.Manager).Execute([
            "--update-app", game.InstanceKey, "--release", fixture.Release.tag_name, "--asset", requested.name,
        ]).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        result.Should().Be(0, "CLI output: {0}; errors: {1}", fixture.Output.ToString(), fixture.Errors.ToString());
        InstalledBuild.Read(path)!.FileName.Should().Be(requested.name);
        File.ReadAllText(Path.Combine(path, "version.txt")).Trim().Should().Be("v2.0.0");
        if (sameBuild)
            fixture.Downloads.Should().BeEmpty();
        else
            fixture.Downloads.Should().Equal(requested.browser_download_url);
    }

    [Fact]
    public async Task Downloads_json_does_not_repair_missing_installation_metadata()
    {
        using var fixture = new ReviewFixture();
        var game = await fixture.AddInstalledAsync("Game");
        var versionFile = Path.Combine(game.GetInstallPath(fixture.GamesFolder), "version.txt");
        File.Delete(versionFile);
        var before = fixture.ReadInstalledFiles(game);

        var result = await new CLIHandler(fixture.Manager).Execute(["--downloads-json", game.InstanceKey]);

        result.Should().Be(0);
        File.Exists(versionFile).Should().BeFalse();
        fixture.ReadInstalledFiles(game).Should().BeEquivalentTo(before);
        fixture.Downloads.Should().BeEmpty();
    }

    private sealed class ReviewFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "QuiverCliReviewTests", Guid.NewGuid().ToString("N"));
        private readonly string? _previousRoot = QuiverLauncherPaths.OverrideUserDataRoot;
        private readonly ISettingsStore _previousStore = SettingsStoreProvider.Default;
        private readonly TextWriter _previousOutput = Console.Out;
        private readonly TextWriter _previousError = Console.Error;
        private readonly string _repository = "cli-reviewed/" + Guid.NewGuid().ToString("N");
        public StringWriter Output { get; } = new();
        public StringWriter Errors { get; } = new();
        public GameManager Manager { get; }
        public string GamesFolder { get; }
        public string SettingsPath { get; }
        public byte[] InitialSettings { get; }
        public byte[] Archive { get; }
        public List<string> Downloads { get; } = [];
        public Action? AfterDownload { get; set; }
        public GitHubRelease Release { get; } = new()
        {
            tag_name = "v2.0.0",
            assets = [
                new() { name = "game-v2.0.0-win64.zip", browser_download_url = "https://example.com/win64.zip" },
                new() { name = "game-v2.0.0-win32.zip", browser_download_url = "https://example.com/win32.zip" },
                new() { name = "portable-build.zip", browser_download_url = "https://example.com/portable.zip" },
                new() { name = "game-v2.0.0-macos.zip", browser_download_url = "https://example.com/macos.zip" },
            ],
        };

        public ReviewFixture(TargetOS platform = TargetOS.Windows)
        {
            Directory.CreateDirectory(_root);
            QuiverLauncherPaths.OverrideUserDataRoot = _root;
            Console.SetOut(Output);
            Console.SetError(Errors);
            GamesFolder = Path.Combine(_root, "Apps");
            SettingsPath = Path.Combine(_root, "settings.json");
            var store = new FileSettingsStore(SettingsPath);
            store.Save(new AppSettings { Platform = platform, AppsPath = GamesFolder, ListScope = AppListScope.InstalledOnly });
            SettingsStoreProvider.Default = store;
            InitialSettings = File.ReadAllBytes(SettingsPath);
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using var payload = zip.CreateEntry("game.exe").Open();
                    payload.Write([0x4D, 0x5A, 0x02]);
                }
                Archive = stream.ToArray();
            }
            var newer = new GitHubRelease
            {
                tag_name = "v3.0.0",
                assets = [new() { name = "game-v3.0.0-win64.zip", browser_download_url = "https://example.com/newer.zip" }],
            };
            var client = new HttpClient(new Handler(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                {
                    var path = request.RequestUri.AbsolutePath;
                    var json = path.EndsWith("/latest") ? JsonSerializer.Serialize(newer)
                        : path.EndsWith("/tags/v2.0.0") ? JsonSerializer.Serialize(Release)
                        : path.EndsWith("/releases") ? JsonSerializer.Serialize(new[] { newer, Release })
                        : throw new InvalidOperationException($"Unexpected endpoint: {request.RequestUri}");
                    return new(HttpStatusCode.OK) { Content = new StringContent(json) };
                }
                Downloads.Add(request.RequestUri.AbsoluteUri);
                if (!Release.assets.Any(asset => asset.browser_download_url == request.RequestUri.AbsoluteUri))
                    throw new InvalidOperationException($"Unexpected download: {request.RequestUri}");
                AfterDownload?.Invoke();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) };
            }));
            Manager = new GameManager(store, client, new AppCatalogService(dataDirectory: _root));
        }

        public void UseLinuxDownloads() => Release.assets =
        [
            new() { name = "game-v2.0.0-linux-flatpak.zip", browser_download_url = "https://example.com/linux-flatpak.zip" },
            new() { name = "game-v2.0.0-linux.tar.gz", browser_download_url = "https://example.com/linux.tar.gz" },
            new() { name = "game-v2.0.0-win64.zip", browser_download_url = "https://example.com/win64.zip" },
            new() { name = "portable-build.tar", browser_download_url = "https://example.com/portable.tar" },
        ];

        public async Task<GameInfo> AddInstalledAsync(string folder, bool recordedBuild = true)
        {
            var game = new GameInfo
            {
                Name = "Same visible name",
                Repository = _repository,
                FolderName = folder,
                InstalledVersion = "v1.0.0",
                CatalogVerifiedVersion = "v2.0.0",
                CatalogVerifiedAt = DateTimeOffset.UtcNow,
                Status = GameStatus.UpdateAvailable,
            };
            Manager.Games.Add(game);
            var path = game.GetInstallPath(GamesFolder);
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "version.txt"), "v1.0.0", TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(path, "game.exe"), [0x4D, 0x5A, 0x01], TestContext.Current.CancellationToken);
            if (recordedBuild)
                await new InstalledBuild("game-v1.0.0-win64.zip", new string('0', 64)).WriteAsync(path);
            return game;
        }

        public Dictionary<string, byte[]> ReadInstalledFiles(GameInfo game) =>
            Directory.GetFiles(game.GetInstallPath(GamesFolder), "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(game.GetInstallPath(GamesFolder), path), File.ReadAllBytes);

        public void Dispose()
        {
            Manager.Dispose();
            Console.SetOut(_previousOutput);
            Console.SetError(_previousError);
            SettingsStoreProvider.Default = _previousStore;
            QuiverLauncherPaths.OverrideUserDataRoot = _previousRoot;
            Output.Dispose();
            Errors.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
