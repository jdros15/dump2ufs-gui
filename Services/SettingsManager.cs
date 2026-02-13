using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dump2UfsGui.Services
{
    public class SettingsData
    {
        public string? Ufs2ToolPath { get; set; }
        public string? Ufs2ToolVersion { get; set; }
        public string? LastInputDir { get; set; }
        public string? LastOutputDir { get; set; }
    }

    public class UpdateCheckResult
    {
        public bool UpdateAvailable { get; set; }
        public string LatestVersion { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string AssetName { get; set; } = "";
    }

    public static class SettingsManager
    {
        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dump2ufs-gui");

        private static readonly string SettingsFile = Path.Combine(AppDataDir, "settings.json");

        public static string BundledToolDir => Path.Combine(AppDataDir, "ufs2tool");

        public static SettingsData Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    return JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
                }
            }
            catch { }
            return new SettingsData();
        }

        public static void Save(SettingsData data)
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }

        /// <summary>
        /// Finds UFS2Tool.exe: first checks bundled location, then saved path, then current dir, then PATH.
        /// </summary>
        public static string? FindUfs2Tool(SettingsData settings)
        {
            // 1. Bundled location (search recursively — zip may extract into subdirectory)
            if (Directory.Exists(BundledToolDir))
            {
                var found = Directory.GetFiles(BundledToolDir, "UFS2Tool.exe", SearchOption.AllDirectories);
                if (found.Length > 0) return found[0];
            }

            // 2. Saved path
            if (!string.IsNullOrEmpty(settings.Ufs2ToolPath) && File.Exists(settings.Ufs2ToolPath))
                return settings.Ufs2ToolPath;

            // 3. Current directory
            var currentDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UFS2Tool.exe");
            if (File.Exists(currentDir)) return currentDir;

            // 4. Same directory as the exe
            var exeDir = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "",
                "UFS2Tool.exe");
            if (File.Exists(exeDir)) return exeDir;

            // 5. PATH
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
            foreach (var dir in pathDirs)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "UFS2Tool.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Check GitHub for the latest UFS2Tool release.
        /// </summary>
        public static async Task<UpdateCheckResult> CheckForUpdateAsync(string? currentVersion, CancellationToken ct = default)
        {
            var result = new UpdateCheckResult { CurrentVersion = currentVersion ?? "unknown" };

            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("dump2ufs-gui/1.0");
                http.Timeout = TimeSpan.FromSeconds(15);

                var json = await http.GetStringAsync(
                    "https://api.github.com/repos/SvenGDK/UFS2Tool/releases/latest", ct);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tagName = root.GetProperty("tag_name").GetString() ?? "";
                result.LatestVersion = tagName;

                // Find win-x64-selfcontained asset
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (name.Contains("win-x64-selfcontained", StringComparison.OrdinalIgnoreCase))
                        {
                            result.DownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            result.AssetName = name;
                            break;
                        }
                    }
                }

                // Compare versions
                result.UpdateAvailable = !string.IsNullOrEmpty(currentVersion)
                    ? string.Compare(tagName, currentVersion, StringComparison.OrdinalIgnoreCase) != 0
                    : true;
            }
            catch { }

            return result;
        }

        /// <summary>
        /// Download and extract UFS2Tool to the bundled location.
        /// </summary>
        public static async Task DownloadUfs2ToolAsync(
            string downloadUrl,
            string version,
            Action<string>? onProgress,
            CancellationToken ct = default)
        {
            var extractDir = BundledToolDir;
            var tempZip = Path.Combine(Path.GetTempPath(), $"ufs2tool_{Guid.NewGuid():N}.zip");

            try
            {
                onProgress?.Invoke("Downloading UFS2Tool...");

                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("dump2ufs-gui/1.0");
                http.Timeout = TimeSpan.FromMinutes(10);

                using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = File.Create(tempZip);

                var buffer = new byte[81920];
                long downloaded = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read, ct);
                    downloaded += read;
                    if (totalBytes > 0)
                    {
                        var pct = (int)(downloaded * 100 / totalBytes);
                        onProgress?.Invoke($"Downloading UFS2Tool... {pct}%");
                    }
                }

                fileStream.Close();

                // Extract
                onProgress?.Invoke("Extracting UFS2Tool...");

                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, true);

                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(tempZip, extractDir);

                // Save the version
                var settings = Load();
                settings.Ufs2ToolVersion = version;
                settings.Ufs2ToolPath = FindUfs2Tool(settings);
                Save(settings);

                onProgress?.Invoke("UFS2Tool ready!");
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }
    }
}
