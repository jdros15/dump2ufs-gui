using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Dump2UfsGui.Services
{
    public class UpdateService
    {
        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dump2ufs-gui");

        public static string InternalToolDir => Path.Combine(AppDataDir, "internal_tool", "v4.0");
        
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
                var resourceName = "Dump2UfsGui.ufs2tool_v4.zip";

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
            // Only use internal tool (verify health)
            var internalPath = FindExecutablePath(InternalToolDir);
            if (!string.IsNullOrEmpty(internalPath) && VerifyIntegratedToolHealth())
            {
                return internalPath;
            }

            return "";
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
    }
}
