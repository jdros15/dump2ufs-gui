using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Dump2UfsGui.Services
{
    public class BlockSizeResult
    {
        public int BlockSize { get; set; }
        public int FragmentSize { get; set; }
        public long ImageSize { get; set; }
    }

    public class ConversionProgress
    {
        public string Stage { get; set; } = "";
        public string Detail { get; set; } = "";
        public int PercentComplete { get; set; }
        public bool IsError { get; set; }
    }

    public class ConversionResult
    {
        public bool Success { get; set; }
        public string OutputPath { get; set; } = "";
        public long FileSize { get; set; }
        public int OptimalBlockSize { get; set; }
        public int OptimalFragmentSize { get; set; }
        public string ErrorMessage { get; set; } = "";
    }

    public class Ufs2Converter
    {
        private static readonly int[] BlockSizes = { 4096, 8192, 16384, 32768, 65536 };

        public event Action<ConversionProgress>? OnProgress;
        public event Action<string>? OnLog;

        private readonly string _ufs2ToolPath;

        public Ufs2Converter(string ufs2ToolPath)
        {
            _ufs2ToolPath = ufs2ToolPath;
        }

        public async Task<ConversionResult> ConvertAsync(
            string inputPath,
            string outputPath,
            string label,
            string titleId,
            bool enableCompatibility = false,
            CancellationToken cancellationToken = default)
        {
            var result = new ConversionResult { OutputPath = outputPath };

            try
            {
                // Phase 1: Test block sizes
                Log("=== Starting conversion ===");
                Log($"Input: {inputPath}");
                Log($"Output: {outputPath}");
                Log($"Label: {label}");
                Log("");

                ReportProgress("Optimizing", "Scanning source directory...", 2);

                long inputDirSize = 0;
                try
                {
                    inputDirSize = await Task.Run(() =>
                    {
                        long size = 0;
                        var files = Directory.GetFiles(inputPath, "*", SearchOption.AllDirectories);
                        foreach (var f in files) size += new FileInfo(f).Length;
                        return size;
                    }, cancellationToken);
                    Log($"Input directory size: {FormatSize(inputDirSize)}");
                }
                catch { }

                ReportProgress("Optimizing", "Testing block sizes for optimal space efficiency...", 5);

                // For large games (> 32GB), smaller block sizes often cause UFS2Tool to run out of memory 
                // while building the inode table (the "Memory stream is not expandable" error).
                // We skip 4K and 8K blocks for large games to ensure stability.
                var testableBlockSizes = new List<int>(BlockSizes);
                if (inputDirSize > 30L * 1024 * 1024 * 1024) // > 30 GB
                {
                    Log("⚠️ Large game detected (> 30GB). Skipping 4K and 8K block sizes for stability.");
                    testableBlockSizes.RemoveAll(b => b <= 8192);
                }

                BlockSizeResult? bestResult = null;
                var tempFile = Path.Combine(Path.GetTempPath(), $"ufs2tool_test_{Guid.NewGuid():N}.img");

                for (int i = 0; i < testableBlockSizes.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var b = testableBlockSizes[i];
                    var f = b / 8;
                    
                    var startPercent = 5 + (int)(i / (float)testableBlockSizes.Count * 50);
                    var endPercent = 5 + (int)((i + 0.8) / (float)testableBlockSizes.Count * 50);

                    ReportProgress("Optimizing", $"Testing block size {b} ({i + 1}/{testableBlockSizes.Count})...", startPercent);

                    // Clean up temp file
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);

                    var args = $"makefs -b 0 -o bsize={b},fsize={f},minfree=0,version=2,optimization=space -s {b} \"{tempFile}\" \"{inputPath}\"";
                    var output = await RunUfs2ToolAsync(args, cancellationToken);
                    
                    ReportProgress("Optimizing", $"Finished testing block size {b}...", endPercent);

                    // Try to parse image size from output
                    // UFS2Tool format variations: 
                    // 1. "Image size (6 833 565 696 bytes)"
                    // 2. "... size of 6833565696 ..."
                    // 3. "image size 6833565696 too large" (when using -s)
                    
                    var sizeMatch = Regex.Match(output, @"(?:Image size \(|size of |image size )([\d\s\u00A0,.]+)", RegexOptions.IgnoreCase);
                    bool parsed = false;

                    if (sizeMatch.Success)
                    {
                        var sizeStr = Regex.Replace(sizeMatch.Groups[1].Value, @"[\s\u00A0,.]", "");
                        if (long.TryParse(sizeStr, out var size) && size > 0)
                        {
                            Log($"  Block {b}, Fragment {f} → {FormatSize(size)}");
                            parsed = true;

                            if (bestResult == null || size < bestResult.ImageSize)
                            {
                                bestResult = new BlockSizeResult
                                {
                                    BlockSize = b,
                                    FragmentSize = f,
                                    ImageSize = size
                                };
                            }
                        }
                    }

                    if (!parsed)
                    {
                        Log($"  Block {b}: Could not parse size from output.");
                        // Only log first two lines of output to avoid cluttering, plus any line containing "error" or "size"
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int lineIdx = 0; lineIdx < Math.Min(lines.Length, 3); lineIdx++)
                            Log($"    > {lines[lineIdx].Trim()}");
                        foreach (var line in lines)
                        {
                            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("size", StringComparison.OrdinalIgnoreCase))
                                if (!Array.Exists(lines, l => l == line && Array.IndexOf(lines, l) < 3))
                                    Log($"    > {line.Trim()}");
                        }
                    }
                }

                // Clean up temp file
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }

                if (bestResult == null)
                {
                    // Fallback for extremely large games: if optimization fails, use 64K blocks as a safe default
                    // instead of erroring out entirely, as 64K is most stable for large games.
                    Log("⚠️ Warning: Failed to determine optimal block size via testing.");
                    Log("   Falling back to safe default: Block 65536, Fragment 8192.");
                    bestResult = new BlockSizeResult
                    {
                        BlockSize = 65536,
                        FragmentSize = 8192,
                        ImageSize = inputDirSize // Estimate
                    };
                }

                Log("");
                Log($"Working with: Block {bestResult.BlockSize}, Fragment {bestResult.FragmentSize}");
                Log("");

                result.OptimalBlockSize = bestResult.BlockSize;
                result.OptimalFragmentSize = bestResult.FragmentSize;

                // Phase 2: Create the actual image
                cancellationToken.ThrowIfCancellationRequested();

                // If output file exists, try to delete it first to avoid lock issues
                if (File.Exists(outputPath))
                {
                    try { File.Delete(outputPath); }
                    catch (Exception ex) { Log($"⚠️ Note: Could not delete existing file: {ex.Message}"); }
                }

                ReportProgress("Creating", $"Building UFS2 image ({FormatSize(bestResult.ImageSize)})...", 60);

                var labelOpt = !string.IsNullOrEmpty(label) ? $",label={label}" : "";
                var makefsOptions = $"bsize={bestResult.BlockSize},fsize={bestResult.FragmentSize},minfree=0,version=2,optimization=space{labelOpt}";
                var makefsArgs = $"makefs -b 0 -o {makefsOptions} \"{outputPath}\" \"{inputPath}\"";

                Log($"Running: UFS2Tool.exe {makefsArgs}");
                Log("");

                var makefsOutput = await RunUfs2ToolWithProgressAsync(makefsArgs, cancellationToken);

                // Verify output
                if (File.Exists(outputPath))
                {
                    var fi = new FileInfo(outputPath);
                    result.FileSize = fi.Length;
                    result.Success = true;

                    ReportProgress("Complete", $"Successfully created {FormatSize(fi.Length)} image!", 100);
                    Log("");
                    Log($"   File size: {FormatSize(fi.Length)}");
                    
                    if (enableCompatibility && outputPath.EndsWith(".ffpkg", StringComparison.OrdinalIgnoreCase))
                    {
                        ReportProgress("Finalizing", "Applying EchoStretch compatibility trailer...", 98);
                        await WrapAsFfpkgAsync(outputPath, titleId, inputPath, cancellationToken);
                        fi = new FileInfo(outputPath);
                        result.FileSize = fi.Length;
                        Log($"   Compatibility trailer applied. New size: {FormatSize(fi.Length)}");
                    }
                }
                else
                {
                    throw new Exception($"UFS2Tool completed but output file was not created.\n\nOutput:\n{makefsOutput}");
                }
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "Conversion was cancelled.";
                Log("❌ Conversion cancelled by user.");
                ReportProgress("Cancelled", "Conversion was cancelled.", 0, true);
                
                // Cleanup incomplete file
                CleanupIncompleteFile(outputPath);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                Log($"❌ Error: {ex.Message}");
                ReportProgress("Error", ex.Message, 0, true);
                
                // Cleanup incomplete file on failure
                CleanupIncompleteFile(outputPath);
            }

            return result;
        }

        private void CleanupIncompleteFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                // Give it a tiny bit of time for the process to actually release the handle
                if (File.Exists(path))
                {
                    Log("🧹 Cleaning up incomplete output file...");
                    // Try a few times in case of slow handle release
                    for (int i = 0; i < 3; i++)
                    {
                        try { File.Delete(path); break; }
                        catch { Thread.Sleep(200); }
                    }
                }
            }
            catch { }
        }

        private async Task<string> RunUfs2ToolAsync(string arguments, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ufs2ToolPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Environment = { ["__COMPAT_LAYER"] = "RunAsInvoker" }
            };

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(ct);

                return await stdoutTask + "\n" + await stderrTask;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(true); } catch { }
                }
                throw;
            }
        }

        private async Task<string> RunUfs2ToolWithProgressAsync(string arguments, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ufs2ToolPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Environment = { ["__COMPAT_LAYER"] = "RunAsInvoker" }
            };

            using var process = new Process { StartInfo = psi };
            var allOutput = new System.Text.StringBuilder();

            void HandleData(string? data)
            {
                if (data == null) return;

                lock (allOutput)
                {
                    allOutput.AppendLine(data);
                }
                Log(data);

                // 1. Parsing "Adding files to image...  35% (920/2591 files, 54.49 MiB/154.67 MiB)"
                var addMatch = Regex.Match(data, @"Adding files to image\.\.\.\s+(\d+)%", RegexOptions.IgnoreCase);
                if (addMatch.Success && int.TryParse(addMatch.Groups[1].Value, out int percent))
                {
                    // Map tool's 0-100% to UI's 65-98% range (main work phase)
                    int mappedPercent = 65 + (int)(percent * 0.33);
                    
                    string detail = data.Trim();
                    int markerIndex = detail.IndexOf("Adding files to image...", StringComparison.OrdinalIgnoreCase);
                    if (markerIndex >= 0)
                    {
                        detail = "Adding files: " + detail.Substring(markerIndex + "Adding files to image...".Length).Trim();
                    }
                    
                    ReportProgress("Creating", detail, mappedPercent);
                    return;
                }

                // 2. Parsing "Writing cylinder groups... 49%"
                var cgMatch = Regex.Match(data, @"Writing cylinder groups\.\.\.\s+(\d+)%", RegexOptions.IgnoreCase);
                if (cgMatch.Success && int.TryParse(cgMatch.Groups[1].Value, out int cgPercent))
                {
                    // Map tool's 0-100% to UI's 60-65% range (initialization phase)
                    int mappedPercent = 60 + (int)(cgPercent * 0.05);
                    ReportProgress("Creating", data.Trim(), mappedPercent);
                    return;
                }

                if (data.Contains("Populating", StringComparison.OrdinalIgnoreCase))
                {
                    ReportProgress("Creating", data.Trim(), 65);
                }
                else if (data.Contains("Cylinder groups:", StringComparison.OrdinalIgnoreCase))
                {
                    ReportProgress("Creating", "Initializing UFS2 filesystem structure...", 60);
                }
            }

            process.OutputDataReceived += (s, e) => HandleData(e.Data);
            process.ErrorDataReceived += (s, e) => HandleData(e.Data);

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0)
                {
                    throw new Exception($"UFS2Tool exited with code {process.ExitCode}.\n\n{allOutput}");
                }

                return allOutput.ToString();
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(true); } catch { }
                }
                throw;
            }
        }

        private void Log(string message)
        {
            OnLog?.Invoke(message);
        }

        private void ReportProgress(string stage, string detail, int percent, bool isError = false)
        {
            OnProgress?.Invoke(new ConversionProgress
            {
                Stage = stage,
                Detail = detail,
                PercentComplete = percent,
                IsError = isError
            });
        }

        private async Task WrapAsFfpkgAsync(string ffpkgPath, string titleId, string sourceDir, CancellationToken ct)
        {
            try
            {
                var sceSysPath = Path.Combine(sourceDir, "sce_sys");
                if (!Directory.Exists(sceSysPath))
                {
                    Log("⚠ Warning: sce_sys folder not found. Skipping compatibility trailer.");
                    return;
                }

                var files = new List<string>(Directory.GetFiles(sceSysPath, "*", SearchOption.AllDirectories));
                files.Sort(); // Deterministic order

                using var fs = new FileStream(ffpkgPath, FileMode.Append, FileAccess.Write, FileShare.None);
                using var bw = new BinaryWriter(fs);

                uint fileCount = (uint)files.Count;
                
                // Write entries: [file_data | uint64 size | path+null | uint16 path_len]
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    
                    var relativePath = file.Substring(sourceDir.Length).Replace("\\", "/").TrimStart('/');
                    var pathBytes = System.Text.Encoding.ASCII.GetBytes(relativePath);
                    byte[] fileData = await File.ReadAllBytesAsync(file, ct);
                    
                    bw.Write(fileData);
                    bw.Write((ulong)fileData.Length);
                    bw.Write(pathBytes);
                    bw.Write((byte)0); // Null terminator
                    bw.Write((ushort)(pathBytes.Length + 1));
                    
                    Log($"  + {relativePath} ({fileData.Length} bytes)");
                }

                // Header data
                byte[] magic = System.Text.Encoding.ASCII.GetBytes("ffpkg");
                byte[] title = System.Text.Encoding.ASCII.GetBytes(titleId.PadRight(9).Substring(0, 9));
                ushort version = 1;

                bw.Write(fileCount);
                bw.Write(title);
                bw.Write(version);
                bw.Write(magic);
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to apply compatibility trailer: {ex.Message}");
                throw;
            }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30)
                return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
            if (bytes >= 1L << 20)
                return $"{bytes / (1024.0 * 1024.0):F1} MB";
            if (bytes >= 1L << 10)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} bytes";
        }
    }
}
