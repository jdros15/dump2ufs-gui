using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Dump2UfsGui.Services
{
    public class GitHubRelease
    {
        public string tag_name { get; set; } = "";
        public string name { get; set; } = "";
        public List<GitHubAsset> assets { get; set; } = new();
    }

    public class GitHubAsset
    {
        public string name { get; set; } = "";
        public string browser_download_url { get; set; } = "";
    }

    public class UpdateService
    {
        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dump2ufs-gui");

        public static string InternalToolDir => Path.Combine(AppDataDir, "internal_tool", "v3.0");
        public static string UpdatedToolDir => Path.Combine(AppDataDir, "updated_tool");
        
        private const string RepoOwner = "SvenGDK";
        private const string RepoName = "UFS2Tool";

        public static async Task InitializeAsync()
        {
            if (!VerifyIntegratedToolHealth())
            {
                await ExtractInternalToolAsync();
            }
        }

        public static bool VerifyIntegratedToolHealth()
        {
            var exePath = FindExecutablePath(InternalToolDir);
            if (string.IsNullOrEmpty(exePath)) return false;

            var dir = Path.GetDirectoryName(exePath)!;

            // Critical files from the self-contained package
            string[] criticalFiles = {
                "UFS2Tool.dll",
                "UFS2Tool.runtimeconfig.json",
                "coreclr.dll",
                "hostfxr.dll",
                "hostpolicy.dll",
                "System.Private.CoreLib.dll"
            };

            foreach (var file in criticalFiles)
            {
                if (!File.Exists(Path.Combine(dir, file)))
                {
                    return false;
                }
            }

            return true;
        }

        private static async Task ExtractInternalToolAsync()
        {
            await Task.Run(() =>
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = "Dump2UfsGui.ufs2tool_v3.zip";

                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) throw new Exception($"Resource {resourceName} not found.");

                if (Directory.Exists(InternalToolDir))
                    Directory.Delete(InternalToolDir, true);

                Directory.CreateDirectory(InternalToolDir);

                using var archive = new ZipArchive(stream);
                archive.ExtractToDirectory(InternalToolDir);
            });
        }

        public static string GetEffectiveToolPath()
        {
            // 1. Check updated tool (verify health)
            var updatedPath = FindExecutablePath(UpdatedToolDir);
            if (!string.IsNullOrEmpty(updatedPath) && VerifyUpdateHealth())
            {
                return updatedPath;
            }

            // 2. Check internal tool (verify health)
            var internalPath = FindExecutablePath(InternalToolDir);
            if (!string.IsNullOrEmpty(internalPath) && VerifyIntegratedToolHealth())
            {
                return internalPath;
            }

            return "";
        }

        public static bool VerifyUpdateHealth()
        {
            var exePath = FindExecutablePath(UpdatedToolDir);
            if (string.IsNullOrEmpty(exePath)) return false;

            var dir = Path.GetDirectoryName(exePath)!;

            // Updated tool depends on common DLLs usually found in self-contained apps, 
            // but we check for at least the core binaries.
            string[] criticalFiles = {
                "UFS2Tool.dll",
                "UFS2Tool.runtimeconfig.json"
            };

            foreach (var file in criticalFiles)
            {
                if (!File.Exists(Path.Combine(dir, file)))
                {
                    return false;
                }
            }

            return true;
        }

        private static string FindExecutablePath(string rootDir)
        {
            if (!Directory.Exists(rootDir)) return "";

            try
            {
                var files = Directory.GetFiles(rootDir, "UFS2Tool.exe", SearchOption.AllDirectories);
                return files.FirstOrDefault() ?? "";
            }
            catch
            {
                return "";
            }
        }

        public static bool IsUsingUpdate()
        {
            return VerifyUpdateHealth();
        }

        public static async Task<(bool HasUpdate, string NewVersion, string DownloadUrl)> CheckForUpdateAsync(string currentVersion)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "dump2ufs-gui-updater");
                
                var release = await client.GetFromJsonAsync<GitHubRelease>(
                    $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");

                if (release == null) return (false, "", "");

                if (release.tag_name != currentVersion)
                {
                    var asset = release.assets.FirstOrDefault(a => a.name.Contains("win-x64") && a.name.EndsWith(".zip"));
                    if (asset != null)
                    {
                        return (true, release.tag_name, asset.browser_download_url);
                    }
                }
            }
            catch { }
            return (false, "", "");
        }

        public static async Task DownloadAndInstallUpdateAsync(string url, IProgress<double>? progress = null)
        {
            using var client = new HttpClient();
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var canReportProgress = totalBytes != -1 && progress != null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();
            
            var buffer = new byte[81920];
            var totalRead = 0L;
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await ms.WriteAsync(buffer, 0, read);
                totalRead += read;
                if (canReportProgress) progress!.Report((double)totalRead / totalBytes);
            }

            // Extract to updated tool dir
            if (Directory.Exists(UpdatedToolDir))
                Directory.Delete(UpdatedToolDir, true);
            
            Directory.CreateDirectory(UpdatedToolDir);
            
            ms.Position = 0;
            using var archive = new ZipArchive(ms);
            archive.ExtractToDirectory(UpdatedToolDir);
        }

        public static void UninstallUpdate()
        {
            if (Directory.Exists(UpdatedToolDir))
                Directory.Delete(UpdatedToolDir, true);
        }

    }
}
