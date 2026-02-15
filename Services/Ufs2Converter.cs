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

                long inputDirSize = 0;
                try
                {
                    var files = Directory.GetFiles(inputPath, "*", SearchOption.AllDirectories);
                    foreach (var f in files) inputDirSize += new FileInfo(f).Length;
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
                    var percent = 5 + (int)((i + 1) / (float)testableBlockSizes.Count * 50);

                    ReportProgress("Optimizing", $"Testing block size {b} ({i + 1}/{testableBlockSizes.Count})...", percent);

                    // Clean up temp file
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);

                    var args = $"makefs -b 0 -o bsize={b},fsize={f},minfree=0,version=2,optimization=space -s {b} \"{tempFile}\" \"{inputPath}\"";
                    var output = await RunUfs2ToolAsync(args, cancellationToken);

                    // Try to parse image size from output
                    // UFS2Tool format: "Image size (6 833 565 696 bytes)" with non-breaking spaces
                    var sizeMatch = Regex.Match(output, @"Image size \(([\d\s\u00A0,.]+) bytes\)");
                    if (!sizeMatch.Success)
                    {
                        // Also try the Linux makefs format: "... size of NNNN ..."
                        sizeMatch = Regex.Match(output, @"size of (\d+)");
                    }

                    if (sizeMatch.Success)
                    {
                        var sizeStr = Regex.Replace(sizeMatch.Groups[1].Value, @"[\s\u00A0,.]", "");
                        if (long.TryParse(sizeStr, out var size))
                        {
                            Log($"  Block {b}, Fragment {f} → {FormatSize(size)}");

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
                    else
                    {
                        Log($"  Block {b}: Could not parse size (output may indicate an error)");
                    }
                }

                // Clean up temp file
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }

                if (bestResult == null)
                {
                    throw new Exception("Failed to determine optimal block size. UFS2Tool may have changed its output format.");
                }

                Log("");
                Log($"Optimal: Block {bestResult.BlockSize}, Fragment {bestResult.FragmentSize} → {FormatSize(bestResult.ImageSize)}");
                Log("");

                result.OptimalBlockSize = bestResult.BlockSize;
                result.OptimalFragmentSize = bestResult.FragmentSize;

                // Phase 2: Create the actual image
                cancellationToken.ThrowIfCancellationRequested();

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
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                Log($"❌ Error: {ex.Message}");
                ReportProgress("Error", ex.Message, 0, true);
            }

            return result;
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
            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(ct);

            return stdout + "\n" + stderr;
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

            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    allOutput.AppendLine(e.Data);
                    Log(e.Data);
                }
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    allOutput.AppendLine(e.Data);
                    Log(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Pulse progress while running
            _ = Task.Run(async () =>
            {
                int p = 65;
                while (!process.HasExited && !ct.IsCancellationRequested)
                {
                    ReportProgress("Creating", "Writing UFS2 filesystem image...", Math.Min(p, 95));
                    p += 2;
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }, ct);

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                throw new Exception($"UFS2Tool exited with code {process.ExitCode}.\n\n{allOutput}");
            }

            return allOutput.ToString();
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
