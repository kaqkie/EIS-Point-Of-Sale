using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using IOPath = System.IO.Path;

namespace PointOfSale.App.Services;

public interface IApplicationUpdateService
{
    Version CurrentVersion { get; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    Task<bool> TryApplyStagedUpdateOnStartupAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Pings an internal HTTPS update feed, downloads packages in the background, and stages them
/// for application on the next restart (cashiers keep working during retail hours).
/// </summary>
public sealed class ApplicationUpdateService : IApplicationUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ApplicationUpdateOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApplicationUpdateService> _logger;

    public ApplicationUpdateService(
        IOptions<ApplicationUpdateOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ApplicationUpdateService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.FeedUrl))
        {
            return UpdateCheckResult.Disabled();
        }

        try
        {
            var client = CreateClient();
            using var response = await client.GetAsync(_options.FeedUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var manifest = await JsonSerializer.DeserializeAsync<UpdateFeedManifest>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                return UpdateCheckResult.Failed("Update feed returned an invalid manifest.");
            }

            if (!Version.TryParse(NormalizeVersion(manifest.Version), out var remoteVersion))
            {
                return UpdateCheckResult.Failed($"Unable to parse remote version '{manifest.Version}'.");
            }

            if (remoteVersion <= CurrentVersion)
            {
                return UpdateCheckResult.UpToDate(CurrentVersion);
            }

            if (!string.IsNullOrWhiteSpace(manifest.MinSupportedVersion) &&
                Version.TryParse(NormalizeVersion(manifest.MinSupportedVersion), out var minSupported) &&
                CurrentVersion < minSupported)
            {
                return UpdateCheckResult.Failed(
                    $"Installed version {CurrentVersion} is below the minimum supported {minSupported}. Manual upgrade required.");
            }

            _logger.LogInformation(
                "Update available: {Current} → {Remote}. Staging silent download.",
                CurrentVersion,
                remoteVersion);

            await StagePackageAsync(manifest, remoteVersion, cancellationToken).ConfigureAwait(false);
            return UpdateCheckResult.UpdateStaged(CurrentVersion, remoteVersion, manifest.ReleaseNotes, manifest.Mandatory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Update check failed against {FeedUrl}.", _options.FeedUrl);
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    public Task<bool> TryApplyStagedUpdateOnStartupAsync(CancellationToken cancellationToken = default)
    {
        var stagingRoot = GetStagingRoot();
        var markerPath = IOPath.Combine(stagingRoot, "pending-update.json");
        if (!File.Exists(markerPath))
        {
            return Task.FromResult(false);
        }

        try
        {
            var marker = JsonSerializer.Deserialize<PendingUpdateMarker>(
                File.ReadAllText(markerPath),
                JsonOptions);
            if (marker is null || string.IsNullOrWhiteSpace(marker.ExtractedPath) ||
                !Directory.Exists(marker.ExtractedPath))
            {
                return Task.FromResult(false);
            }

            var installDir = AppContext.BaseDirectory.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            var updaterScript = IOPath.Combine(stagingRoot, "apply-update.cmd");
            var exePath = IOPath.Combine(installDir, "AlbertRetailTerminal.exe");
            var script = $"""
                @echo off
                rem Albert Retail Terminal staged update applicator
                timeout /t 2 /nobreak >nul
                xcopy /E /Y /I /Q "{marker.ExtractedPath}\*" "{installDir}\"
                del /F /Q "{markerPath}" 2>nul
                start "" "{exePath}"
                """;

            File.WriteAllText(updaterScript, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterScript,
                UseShellExecute = true,
                WorkingDirectory = stagingRoot,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            _logger.LogInformation(
                "Applying staged update {Version}. Application will restart.",
                marker.Version);

            // Exit current process so files can be overwritten.
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                System.Windows.Application.Current.Shutdown(0));

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply staged update.");
            return Task.FromResult(false);
        }
    }

    private async Task StagePackageAsync(
        UpdateFeedManifest manifest,
        Version remoteVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifest.PackageUrl))
        {
            throw new InvalidOperationException("Update manifest is missing packageUrl.");
        }

        var stagingRoot = GetStagingRoot();
        Directory.CreateDirectory(stagingRoot);

        var packagePath = IOPath.Combine(stagingRoot, $"art-{remoteVersion}.zip");
        var extractPath = IOPath.Combine(stagingRoot, $"extract-{remoteVersion}");

        var client = CreateClient();
        await using (var remote = await client.GetStreamAsync(manifest.PackageUrl, cancellationToken).ConfigureAwait(false))
        await using (var file = File.Create(packagePath))
        {
            await remote.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            var hash = await ComputeSha256HexAsync(packagePath, cancellationToken).ConfigureAwait(false);
            if (!hash.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(packagePath);
                throw new InvalidOperationException("Downloaded update package failed SHA-256 verification.");
            }
        }

        if (Directory.Exists(extractPath))
        {
            Directory.Delete(extractPath, recursive: true);
        }

        ZipFile.ExtractToDirectory(packagePath, extractPath, overwriteFiles: true);

        var marker = new PendingUpdateMarker
        {
            Version = remoteVersion.ToString(),
            ExtractedPath = extractPath,
            PackagePath = packagePath,
            SchemaVersion = manifest.SchemaVersion,
            StagedUtc = DateTime.UtcNow
        };

        await File.WriteAllTextAsync(
                IOPath.Combine(stagingRoot, "pending-update.json"),
                JsonSerializer.Serialize(marker, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Update {Version} staged at {Path}.", remoteVersion, extractPath);
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(nameof(ApplicationUpdateService));
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.HttpTimeoutSeconds));
        if (!string.IsNullOrWhiteSpace(_options.FeedAuthorizationHeader))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", _options.FeedAuthorizationHeader);
        }

        return client;
    }

    private static string GetStagingRoot() =>
        IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlbertRetailTerminal",
            "Updates");

    private static string NormalizeVersion(string version) =>
        version.Count(c => c == '.') >= 3 ? version : version.TrimEnd('.') + ".0";

    private static async Task<string> ComputeSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}

public sealed class ApplicationUpdateBackgroundService : BackgroundService
{
    private readonly IApplicationUpdateService _updateService;
    private readonly ApplicationUpdateOptions _options;
    private readonly ILogger<ApplicationUpdateBackgroundService> _logger;

    public ApplicationUpdateBackgroundService(
        IApplicationUpdateService updateService,
        IOptions<ApplicationUpdateOptions> options,
        ILogger<ApplicationUpdateBackgroundService> logger)
    {
        _updateService = updateService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        // Initial delay so checkout UI is available before the first network check.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(15, _options.CheckIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _updateService.CheckForUpdatesAsync(stoppingToken).ConfigureAwait(false);
                if (result.UpdateAvailable)
                {
                    _logger.LogInformation(
                        "Staged update {Version} ready (mandatory={Mandatory}). Will apply on next restart.",
                        result.AvailableVersion,
                        result.Mandatory);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Background update poll failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

public sealed class UpdateFeedManifest
{
    public string Version { get; set; } = string.Empty;
    public string? MinSupportedVersion { get; set; }
    public int SchemaVersion { get; set; }
    public string? Channel { get; set; }
    public string? ReleaseNotes { get; set; }
    public string PackageUrl { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public bool Mandatory { get; set; }
    public DateTime? PublishedUtc { get; set; }
}

public sealed class PendingUpdateMarker
{
    public string Version { get; set; } = string.Empty;
    public string ExtractedPath { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public DateTime StagedUtc { get; set; }
}

public sealed class UpdateCheckResult
{
    public bool Enabled { get; init; }
    public bool UpdateAvailable { get; init; }
    public bool Staged { get; init; }
    public bool Mandatory { get; init; }
    public Version? CurrentVersion { get; init; }
    public Version? AvailableVersion { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? Error { get; init; }

    public static UpdateCheckResult Disabled() => new() { Enabled = false };

    public static UpdateCheckResult UpToDate(Version current) =>
        new() { Enabled = true, CurrentVersion = current };

    public static UpdateCheckResult UpdateStaged(Version current, Version available, string? notes, bool mandatory) =>
        new()
        {
            Enabled = true,
            UpdateAvailable = true,
            Staged = true,
            Mandatory = mandatory,
            CurrentVersion = current,
            AvailableVersion = available,
            ReleaseNotes = notes
        };

    public static UpdateCheckResult Failed(string error) =>
        new() { Enabled = true, Error = error };
}
