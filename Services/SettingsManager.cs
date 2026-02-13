using System;
using System.IO;
using System.Text.Json;

namespace Dump2UfsGui.Services
{
    public class SettingsData
    {
        public string? Ufs2ToolPath { get; set; }
        public string? Ufs2ToolVersion { get; set; }
        public string? LastInputDir { get; set; }
        public string? LastOutputDir { get; set; }
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
    }
}
