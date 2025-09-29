using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Traycer.Update
{
    internal sealed class UpdateService : IDisposable
    {
        private const string CacheFileName = "update-cache.json";
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(1);
        private static readonly HttpClient Client = CreateClient();

        private readonly string _cachePath;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _cts = new();

        private UpdateCache _cache;
        private UpdateAvailability? _latest;
        private System.Threading.Timer? _timer;
        private bool _disposed;

        public event EventHandler<UpdateAvailabilityChangedEventArgs>? UpdateAvailabilityChanged;

        public UpdateService()
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConstants.AppName);
            Directory.CreateDirectory(baseDir);
            _cachePath = Path.Combine(baseDir, CacheFileName);
            _cache = LoadCache();

            if (!string.IsNullOrWhiteSpace(_cache.TagName)
                && !string.IsNullOrWhiteSpace(_cache.LatestInstallerUrl)
                && !string.IsNullOrWhiteSpace(_cache.LatestAssetName)
                && Version.TryParse(_cache.LatestVersion, out var cachedVersion)
                && cachedVersion > AppVersion.SemanticVersion
                && Uri.TryCreate(_cache.ReleasePageUrl, UriKind.Absolute, out var releasePage)
                && Uri.TryCreate(_cache.LatestInstallerUrl, UriKind.Absolute, out var installerUri))
            {
                _latest = new UpdateAvailability(
                    _cache.TagName!,
                    cachedVersion,
                    installerUri,
                    _cache.LatestAssetName!,
                    releasePage);
            }
        }

        public UpdateAvailability? LatestUpdate => Volatile.Read(ref _latest);

        public void Start()
        {
            ThrowIfDisposed();
            _timer ??= new System.Threading.Timer(async _ => await SafeCheckAsync(false), null, UpdateInterval, UpdateInterval);
            _ = SafeCheckAsync(false);
        }

        public Task<UpdateAvailability?> ForceCheckAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return CheckForUpdatesInternalAsync(true, cancellationToken);
        }

        public Task<UpdateAvailability?> CheckIfDueAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return CheckForUpdatesInternalAsync(false, cancellationToken);
        }

        public Task<bool> TryLaunchWingetUpgradeAsync(UpdateAvailability update, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            _ = update ?? throw new ArgumentNullException(nameof(update));
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var args = $"upgrade {AppConstants.PackageIdentifier} --silent --silent-with-progress --accept-package-agreements --accept-source-agreements --force";
                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = args,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = Process.Start(psi);
                return Task.FromResult(process != null);
            }
            catch (Win32Exception ex)
            {
                Debug.WriteLine($"Winget upgrade failed: {ex.NativeErrorCode} {ex.Message}");
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Winget upgrade failed: {ex}");
                return Task.FromResult(false);
            }
        }

        public async Task<bool> TryLaunchInstallerUpgradeAsync(UpdateAvailability update, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            _ = update ?? throw new ArgumentNullException(nameof(update));

            var tempDir = Path.Combine(Path.GetTempPath(), AppConstants.AppName);
            Directory.CreateDirectory(tempDir);

            var destination = Path.Combine(tempDir, update.AssetName);
            try
            {
                await DownloadInstallerAsync(update, destination, cancellationToken).ConfigureAwait(false);

                var psi = new ProcessStartInfo
                {
                    FileName = destination,
                    Arguments = "/VERYSILENT /NORESTART",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using var process = Process.Start(psi);
                return process != null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Installer upgrade failed: {ex}");
                return false;
            }
        }

        private async Task DownloadInstallerAsync(UpdateAvailability update, string destination, CancellationToken cancellationToken)
        {
            using var response = await Client.GetAsync(update.InstallerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        private async Task<UpdateAvailability?> SafeCheckAsync(bool force)
        {
            try
            {
                return await CheckForUpdatesInternalAsync(force, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return _latest;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Update check failed: {ex}");
                return _latest;
            }
        }

        private async Task<UpdateAvailability?> CheckForUpdatesInternalAsync(bool force, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!force && DateTimeOffset.UtcNow - _cache.LastChecked < UpdateInterval)
                {
                    return _latest;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, AppConstants.LatestReleaseApi);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.UserAgent.ParseAdd("Traycer-Updater/1.0");
                if (!string.IsNullOrWhiteSpace(_cache.ETag) && EntityTagHeaderValue.TryParse(_cache.ETag, out var etag))
                {
                    request.Headers.IfNoneMatch.Add(etag);
                }

                using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    _cache.LastChecked = DateTimeOffset.UtcNow;
                    SaveCache();
                    return _latest;
                }

                _cache.LastChecked = DateTimeOffset.UtcNow;
                _cache.ETag = response.Headers.ETag?.ToString();

                if (!response.IsSuccessStatusCode)
                {
                    SaveCache();
                    return _latest;
                }

                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
                var parsed = ParseRelease(document.RootElement);

                if (parsed is not null)
                {
                    _cache.LatestVersion = parsed.Version.ToString();
                    _cache.LatestInstallerUrl = parsed.InstallerUri.ToString();
                    _cache.LatestAssetName = parsed.AssetName ?? string.Empty;
                    _cache.TagName = parsed.TagName;
                    _cache.ReleasePageUrl = parsed.ReleasePage.ToString();
                }

                SaveCache();

                var previous = _latest;
                if (parsed is not null && parsed.Version > AppVersion.SemanticVersion)
                {
                    _latest = parsed;
                }
                else
                {
                    _latest = null;
                }

                if (!UpdateAvailability.AreEqual(previous, _latest))
                {
                    UpdateAvailabilityChanged?.Invoke(this, new UpdateAvailabilityChangedEventArgs(previous, _latest));
                }

                return _latest;
            }
            finally
            {
                _gate.Release();
            }
        }

#pragma warning disable CS8600, CS8601
        private UpdateAvailability? ParseRelease(JsonElement root)
        {
            if (!TryGetString(root, "tag_name", out var tag) || tag is null)
            {
                return null;
            }

            if (!TryNormalizeVersion(tag, out var parsedVersion, out var normalized))
            {
                return null;
            }

            string? assetName = null;
            string? downloadUrl = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!TryGetString(asset, "name", out var name) || !TryGetString(asset, "browser_download_url", out var url))
                    {
                        continue;
                    }

                    if (string.Equals(name, $"{AppConstants.InstallerAssetPrefix}{normalized}{AppConstants.InstallerAssetSuffix}", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        downloadUrl = url;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                return null;
            }

            string assetNameValue = assetName!;
            string downloadUrlValue = downloadUrl!;

            if (!Uri.TryCreate(downloadUrlValue, UriKind.Absolute, out var installerUri))
            {
                return null;
            }

            Uri releasePage;
            if (!TryGetString(root, "html_url", out var htmlUrl) || !Uri.TryCreate(htmlUrl, UriKind.Absolute, out releasePage))
            {
                releasePage = new Uri(AppConstants.ReleasesPage, $"tag/{tag}");
            }

            return new UpdateAvailability(tag, parsedVersion, installerUri, assetNameValue, releasePage);
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string? value)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return true;
            }

            value = null;
            return false;
        }

        private static bool TryNormalizeVersion(string? tag, out Version version, out string normalized)
        {
            normalized = string.Empty;
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            var trimmed = tag.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[1..];
            }

            normalized = trimmed;
            return Version.TryParse(trimmed, out version);
        }

        private UpdateCache LoadCache()
        {
            if (!File.Exists(_cachePath))
            {
                return new UpdateCache();
            }

            try
            {
                var json = File.ReadAllText(_cachePath);
                return JsonSerializer.Deserialize<UpdateCache>(json) ?? new UpdateCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load update cache: {ex}");
                return new UpdateCache();
            }
        }

        private void SaveCache()
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache);
                File.WriteAllText(_cachePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save update cache: {ex}");
            }
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Traycer-Updater/1.0");
            return client;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(UpdateService));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            _timer?.Dispose();
            _gate.Dispose();
            _cts.Dispose();
        }

        private sealed class UpdateCache
        {
            public DateTimeOffset LastChecked { get; set; }
            public string? ETag { get; set; }
            public string? LatestVersion { get; set; }
            public string? LatestInstallerUrl { get; set; }
            public string? LatestAssetName { get; set; }
            public string? TagName { get; set; }
            public string? ReleasePageUrl { get; set; }
        }
    }

    internal sealed record UpdateAvailability(string TagName, Version Version, Uri InstallerUri, string AssetName, Uri ReleasePage)
    {
        public string VersionString => Version.ToString();
        public string DisplayVersion => $"v{VersionString}";
        public string InstallerUrl => InstallerUri.ToString();

        public static bool AreEqual(UpdateAvailability? left, UpdateAvailability? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null)
            {
                return false;
            }

            return string.Equals(left.TagName, right.TagName, StringComparison.OrdinalIgnoreCase)
                && left.Version == right.Version
                && string.Equals(left.InstallerUrl, right.InstallerUrl, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class UpdateAvailabilityChangedEventArgs : EventArgs
    {
        public UpdateAvailabilityChangedEventArgs(UpdateAvailability? previous, UpdateAvailability? current)
        {
            Previous = previous;
            Current = current;
        }

        public UpdateAvailability? Previous { get; }
        public UpdateAvailability? Current { get; }
    }
}























