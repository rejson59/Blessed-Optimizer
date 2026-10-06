using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlessedOptimizer.Services;

public sealed record UpdateCheckResult(
    bool HasUpdate,
    string Message,
    Version? LatestVersion = null,
    string? TagName = null,
    string? InstallerUrl = null,
    string? ChecksumsUrl = null);

public static class UpdateService
{
    private const string Repository = "rejson59/Blessed-Optimizer";
    private const string InstallerAssetName = "BlessedOptimizer-Setup.exe";
    private const string ChecksumsAssetName = "SHA256SUMS.txt";
    private const long MaximumAssetSize = 350L * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();

    public static async Task<UpdateCheckResult> CheckLatestAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://github.com/{Repository}/releases/latest/download/BlessedOptimizer-release.json");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(false, "Nie ma jeszcze opublikowanego wydania. Aktualizacje sprawdzę ponownie przy następnym uruchomieniu.");
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (release is null || !Version.TryParse((release.TagName ?? "").TrimStart('v', 'V'), out var latestVersion))
                return new UpdateCheckResult(false, "Nie udało się odczytać wersji wydania.");

            var current = Normalize(currentVersion);
            var latest = Normalize(latestVersion);
            if (latest <= current)
                return new UpdateCheckResult(false, $"Masz aktualną wersję {current}.", latest, release.TagName);

            var installer = release.Assets?.FirstOrDefault(asset => asset.Name == InstallerAssetName)?.BrowserDownloadUrl;
            var checksums = release.Assets?.FirstOrDefault(asset => asset.Name == ChecksumsAssetName)?.BrowserDownloadUrl;
            if (string.IsNullOrWhiteSpace(installer) || string.IsNullOrWhiteSpace(checksums))
                return new UpdateCheckResult(false, "Wydanie jest dostępne, ale brakuje pliku instalatora lub sum kontrolnych.", latest, release.TagName);

            return new UpdateCheckResult(true, $"Dostępna jest wersja {latest}.", latest, release.TagName, installer, checksums);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(false, "Sprawdzanie aktualizacji przerwano.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return new UpdateCheckResult(false, "Nie udało się sprawdzić aktualizacji. Sprawdź połączenie i spróbuj ponownie.");
        }
    }

    public static async Task<(string InstallerPath, string Sha256)> DownloadAndVerifyAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!update.HasUpdate || string.IsNullOrWhiteSpace(update.InstallerUrl) || string.IsNullOrWhiteSpace(update.ChecksumsUrl))
            throw new InvalidOperationException("Brak danych do pobrania aktualizacji.");
        if (!Uri.TryCreate(update.InstallerUrl, UriKind.Absolute, out var installerUri) || installerUri.Host != "github.com" && installerUri.Host != "objects.githubusercontent.com")
            throw new InvalidOperationException("Adres instalatora nie pochodzi z GitHub Releases.");
        if (!Uri.TryCreate(update.ChecksumsUrl, UriKind.Absolute, out var checksumsUri) || checksumsUri.Host != "github.com" && checksumsUri.Host != "objects.githubusercontent.com")
            throw new InvalidOperationException("Adres sum kontrolnych nie pochodzi z GitHub Releases.");

        var checksums = await Http.GetStringAsync(checksumsUri, cancellationToken).ConfigureAwait(false);
        var expectedHash = ReadExpectedHash(checksums, InstallerAssetName);
        var stagedPath = Path.Combine(Path.GetTempPath(), $"BlessedOptimizer-update-{update.LatestVersion}.exe");
        TryDelete(stagedPath);

        try
        {
            using var response = await Http.GetAsync(installerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumAssetSize)
                throw new InvalidDataException("Plik aktualizacji jest większy niż oczekiwano.");

            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
            {
                var buffer = new byte[1024 * 128];
                long received = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > MaximumAssetSize)
                        throw new InvalidDataException("Plik aktualizacji przekroczył dozwolony rozmiar.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    if (total is > 0) progress?.Report(Math.Clamp(received * 100d / total.Value, 0, 100));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var file = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            var expectedBytes = Convert.FromHexString(expectedHash);
            var actualBytes = Convert.FromHexString(actualHash);
            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
            {
                TryDelete(stagedPath);
                throw new CryptographicException("Suma kontrolna aktualizacji się nie zgadza. Plik nie zostanie uruchomiony.");
            }

            progress?.Report(100);
            return (stagedPath, actualHash);
        }
        catch
        {
            TryDelete(stagedPath);
            throw;
        }
    }

    public static void StartReplacementHelper(string stagedInstallerPath, string expectedSha256)
    {
        if (!InstallService.IsCurrentProcessInstalled())
            throw new InvalidOperationException("Automatyczna aktualizacja działa tylko dla wersji zainstalowanej w profilu użytkownika.");
        var currentExe = Environment.ProcessPath ?? throw new InvalidOperationException("Nie udało się określić ścieżki programu.");
        var helperPath = Path.Combine(Path.GetTempPath(), $"BlessedUpdater-{Guid.NewGuid():N}.exe");
        File.Copy(currentExe, helperPath, overwrite: false);
        try
        {
            var startInfo = new ProcessStartInfo(helperPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--apply-update");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(stagedInstallerPath);
            startInfo.ArgumentList.Add(InstallService.InstalledExecutablePath);
            startInfo.ArgumentList.Add(expectedSha256);
            var helperProcess = Process.Start(startInfo);
            if (helperProcess is null)
                throw new InvalidOperationException("Nie udało się uruchomić pomocnika aktualizacji.");
            helperProcess.Dispose();
        }
        catch
        {
            TryDelete(helperPath);
            throw;
        }
    }

    public static void RunReplacementHelper(string[] args)
    {
        if (args.Length != 5 || !int.TryParse(args[1], out var parentPid))
            return;

        var stagedPath = args[2];
        var targetPath = args[3];
        var expectedSha = args[4];
        var helperPath = Environment.ProcessPath;
        var backupPath = targetPath + ".previous";
        try
        {
            var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!PathsEqual(targetPath, InstallService.InstalledExecutablePath) ||
                string.IsNullOrWhiteSpace(helperPath) || !Path.GetFileName(helperPath).StartsWith("BlessedUpdater-", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(stagedPath).StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(stagedPath).StartsWith("BlessedOptimizer-update-", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(stagedPath) || expectedSha.Length != 64)
                throw new InvalidOperationException("Nieprawidłowe parametry pomocnika aktualizacji.");

            try
            {
                using var parent = Process.GetProcessById(parentPid);
                if (!PathsEqual(parent.MainModule?.FileName ?? "", targetPath))
                    throw new InvalidOperationException("Proces instalacji nie pasuje do bieżącej aplikacji.");
                if (!parent.WaitForExit(60_000))
                    throw new TimeoutException("Blessed Optimizer nie został zamknięty na czas.");
            }
            catch (ArgumentException)
            {
                // The main app already exited before the helper could inspect it.
            }

            using (var downloaded = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actual = Convert.ToHexString(SHA256.HashData(downloaded));
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedSha), Convert.FromHexString(actual)))
                    throw new CryptographicException("Weryfikacja pliku aktualizacji nie powiodła się.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            TryDelete(backupPath);
            if (File.Exists(targetPath))
                File.Move(targetPath, backupPath);
            try
            {
                File.Move(stagedPath, targetPath, overwrite: true);
                Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true, Arguments = "--skip-update-check" });
                TryDelete(backupPath);
                ScheduleHelperDeletion(helperPath);
            }
            catch
            {
                TryDelete(targetPath);
                if (File.Exists(backupPath)) File.Move(backupPath, targetPath, overwrite: true);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or CryptographicException or ArgumentException or FormatException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            System.Windows.MessageBox.Show(
                $"Aktualizacja nie została zastosowana. Twoja obecna instalacja pozostała bez zmian.\n\n{ex.Message}",
                "Blessed Optimizer — aktualizacja",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            TryDelete(stagedPath);
            if (!string.IsNullOrWhiteSpace(helperPath)) ScheduleHelperDeletion(helperPath);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BlessedOptimizer/1.0.0 (+https://github.com/rejson59/Blessed-Optimizer)");
        return client;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static string ReadExpectedHash(string checksumText, string assetName)
    {
        foreach (var line in checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[^1].TrimStart('*'), assetName, StringComparison.Ordinal) &&
                parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                return parts[0].ToUpperInvariant();
        }
        throw new InvalidDataException("W pliku SHA256SUMS.txt nie znaleziono sumy kontrolnej instalatora.");
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ScheduleHelperDeletion(string? helperPath)
    {
        if (string.IsNullOrWhiteSpace(helperPath)) return;
        try { MoveFileEx(helperPath, null, MoveFileDelayUntilReboot); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private const uint MoveFileDelayUntilReboot = 0x00000004;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);

    public sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    public sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
