using System;
using System.IO;
using System.Text.Json;

namespace Dump2UfsGui.Services
{
    public class GameInfo
    {
        public string TitleId { get; set; } = "";
        public string TitleName { get; set; } = "";
        public string DefaultLanguage { get; set; } = "";
        public string AutoLabel { get; set; } = "";
        public string SuggestedOutputName { get; set; } = "";
    }

    public static class GameDumpValidator
    {
        public static bool IsValidDump(string folderPath)
        {
            var paramPath = Path.Combine(folderPath, "sce_sys", "param.json");
            return File.Exists(paramPath);
        }

        public static GameInfo ParseGameInfo(string folderPath)
        {
            var paramPath = Path.Combine(folderPath, "sce_sys", "param.json");
            if (!File.Exists(paramPath))
                throw new FileNotFoundException("sce_sys/param.json not found in the selected folder.");

            var json = File.ReadAllText(paramPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var info = new GameInfo();

            // Parse titleId
            if (root.TryGetProperty("titleId", out var titleIdElem))
                info.TitleId = titleIdElem.GetString() ?? "";

            if (string.IsNullOrEmpty(info.TitleId))
                throw new InvalidDataException("Failed to parse titleId from param.json");

            // Parse defaultLanguage
            string defaultLang = "en-US";
            if (root.TryGetProperty("localizedParameters", out var localParams))
            {
                if (localParams.TryGetProperty("defaultLanguage", out var defLangElem))
                {
                    var lang = defLangElem.GetString();
                    if (!string.IsNullOrEmpty(lang))
                        defaultLang = lang;
                }

                info.DefaultLanguage = defaultLang;

                // Parse titleName
                if (localParams.TryGetProperty(defaultLang, out var langObj))
                {
                    if (langObj.TryGetProperty("titleName", out var nameElem))
                        info.TitleName = nameElem.GetString() ?? "";
                }
            }

            if (string.IsNullOrEmpty(info.TitleName))
                throw new InvalidDataException("Failed to parse titleName from param.json");

            // Generate auto-label (same logic as PowerShell script)
            var titleNameClean = System.Text.RegularExpressions.Regex.Replace(info.TitleName, @"[^A-Za-z0-9]", "");
            var titleIdClean = System.Text.RegularExpressions.Regex.Replace(info.TitleId, @"[^A-Za-z0-9]", "");

            var idPart = titleIdClean.Length > 5
                ? titleIdClean.Substring(titleIdClean.Length - 5)
                : titleIdClean;
            var namePart = titleNameClean.Length > 11
                ? titleNameClean.Substring(0, 11)
                : titleNameClean;

            info.AutoLabel = $"{idPart}{namePart}";
            info.SuggestedOutputName = $"{info.TitleId}.ffpkg";

            return info;
        }
    }
}
