using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Dump2UfsGui.Services
{
    /// <summary>
    /// Creates exFAT disk images using OSFMount — mirrors New-OsfExfatImage.ps1.
    /// Runs all operations in a single elevated PowerShell process (one UAC prompt)
    /// and monitors progress via a log file.
    /// </summary>
    public class ExfatConverter
    {
        public event Action<ConversionProgress>? OnProgress;
        public event Action<string>? OnLog;

        // ─── OSFMount detection (mirrors Find-OSFMountCom) ───

        public static string? FindOsfMountCom()
        {
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
            foreach (var dir in pathDirs)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "osfmount.com");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OSFMount", "osfmount.com"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OSFMount", "osfmount.com"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PassMark", "OSFMount", "osfmount.com"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PassMark", "OSFMount", "osfmount.com"),
            };

            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            return null;
        }

        public static bool IsOsfMountInstalled() => FindOsfMountCom() != null;

        public static bool IsRunningAsAdmin()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        // ─── Main conversion ───

        public async Task<ConversionResult> ConvertAsync(
            string inputPath, string outputPath, string label,
            CancellationToken cancellationToken = default)
        {
            var result = new ConversionResult { OutputPath = outputPath };
            string? logFile = null;
            string? scriptFile = null;

            try
            {
                // Truncate label to 11 chars — exFAT format.com max
                if (label.Length > 11)
                    label = label.Substring(0, 11);

                Log("=== Starting exFAT conversion ===");
                Log($"Input: {inputPath}");
                Log($"Output: {outputPath}");
                Log($"Label: {label}");
                Log("");

                ReportProgress("Validating", "Checking prerequisites...", 2);

                if (!Directory.Exists(inputPath))
                    throw new Exception($"Source directory not found: {inputPath}");
                if (!File.Exists(Path.Combine(inputPath, "eboot.bin")))
                    throw new Exception($"eboot.bin not found in source directory: {inputPath}");

                var osfPath = FindOsfMountCom();
                if (osfPath == null)
                    throw new Exception("OSFMount is not installed.");

                Log($"OSFMount found: {osfPath}");

                var outDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);

                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                    Log("Deleted existing output file.");
                }

                // Create temp files
                logFile = Path.Combine(Path.GetTempPath(), $"exfat_{Guid.NewGuid():N}.log");
                scriptFile = Path.Combine(Path.GetTempPath(), $"exfat_{Guid.NewGuid():N}.ps1");

                // Generate the PS1 script that mirrors New-OsfExfatImage.ps1 exactly
                var ps1 = GenerateConversionScript(inputPath, outputPath, label, logFile);
                File.WriteAllText(scriptFile, ps1, Encoding.UTF8);

                // Launch elevated (single UAC prompt)
                ReportProgress("Launching", "Requesting Administrator privileges...", 5);
                Log("Launching elevated PowerShell — you may see a UAC prompt.");

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptFile}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                Process? process;
                try { process = Process.Start(psi); }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    throw new Exception("Administrator privileges were denied. Cannot create exFAT image without elevation.");
                }

                if (process == null)
                    throw new Exception("Failed to start elevated process.");

                // Monitor log file for progress
                using (process)
                {
                    await MonitorLogFileAsync(logFile, process, cancellationToken);
                }

                // Check result markers in log
                if (File.Exists(logFile))
                {
                    var content = File.ReadAllText(logFile);
                    if (content.Contains("@@EXFAT_ERROR@@"))
                    {
                        var m = Regex.Match(content, @"@@EXFAT_ERROR@@\s*(.+)");
                        throw new Exception(m.Success ? m.Groups[1].Value.Trim() : "Conversion failed in elevated process.");
                    }
                }

                // Verify output
                if (File.Exists(outputPath))
                {
                    var fi = new FileInfo(outputPath);
                    result.FileSize = fi.Length;
                    result.Success = true;
                    ReportProgress("Complete", $"Successfully created {Ufs2Converter.FormatSize(fi.Length)} exFAT image!", 100);
                    Log($"\nOK: Image created at: {outputPath}");
                    Log($"   File size: {Ufs2Converter.FormatSize(fi.Length)}");
                }
                else
                {
                    throw new Exception("Conversion process completed but the output file was not found.");
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
            finally
            {
                try { if (scriptFile != null && File.Exists(scriptFile)) File.Delete(scriptFile); } catch { }
                try { if (logFile != null && File.Exists(logFile)) File.Delete(logFile); } catch { }

                if (!result.Success && File.Exists(outputPath))
                {
                    try
                    {
                        Log("🧹 Cleaning up incomplete output file...");
                        for (int i = 0; i < 5; i++)
                        {
                            try { File.Delete(outputPath); break; }
                            catch { await Task.Delay(500); }
                        }
                    }
                    catch { }
                }
            }

            return result;
        }

        // ─── Generate self-contained PS1 script (mirrors New-OsfExfatImage.ps1) ───

        private string GenerateConversionScript(string inputPath, string outputPath, string label, string logFile)
        {
            // Escape single quotes for PS1 strings
            string Esc(string s) => s.Replace("'", "''");

            return $@"
# Auto-generated exFAT conversion script
# Mirrors New-OsfExfatImage.ps1 logic exactly
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$logPath = '{Esc(logFile)}'
function Log($msg) {{
    Write-Host $msg
    Add-Content -LiteralPath $logPath -Value $msg -Encoding UTF8
}}

function Find-OSFMountCom {{
    $cmd = Get-Command 'osfmount.com' -ErrorAction SilentlyContinue
    if ($cmd) {{ return $cmd.Source }}
    $candidates = @(
        ""${{env:ProgramFiles}}\OSFMount\osfmount.com"",
        ""${{env:ProgramFiles(x86)}}\OSFMount\osfmount.com"",
        ""${{env:ProgramFiles}}\PassMark\OSFMount\osfmount.com"",
        ""${{env:ProgramFiles(x86)}}\PassMark\OSFMount\osfmount.com""
    ) | Where-Object {{ $_ -and (Test-Path $_) }}
    $candidates = @($candidates)
    if ($candidates.Count -gt 0) {{ return $candidates[0] }}
    throw 'osfmount.com not found.'
}}

function Get-FreeDriveLetter {{
    $used = (Get-PSDrive -PSProvider FileSystem).Name
    foreach ($code in 68..90) {{
        $letter = [char]$code
        if ($used -notcontains [string]$letter) {{ return [string]$letter }}
    }}
    throw 'No free drive letters available.'
}}

function Wait-ForLogicalDrive([string]$dl, [int]$timeout = 30) {{
    $target = ""${{dl}}:""
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeout) {{
        $logical = Get-CimInstance -ClassName Win32_LogicalDisk -Filter ""DeviceID='$target'"" -ErrorAction SilentlyContinue
        if ($logical) {{ return $true }}
        Start-Sleep -Milliseconds 500
    }}
    return $false
}}

function Dismount-OsfVolume([string]$osfPath, [string]$mountPoint, [int]$maxAttempts = 6) {{
    if ([string]::IsNullOrWhiteSpace($mountPoint)) {{ return $false }}
    $targets = @($mountPoint)
    if (-not $mountPoint.EndsWith('\')) {{ $targets += ""$mountPoint\"" }}
    for ($i = 1; $i -le $maxAttempts; $i++) {{
        foreach ($target in $targets) {{
            try {{
                & $osfPath -d -m $target 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) {{ return $true }}
            }} catch {{ }}
        }}
        Start-Sleep -Milliseconds 500
    }}
    return $false
}}

function Get-OptimalExfatClusterSize([string]$dir) {{
    $files = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force)
    if ($files.Count -eq 0) {{ return 32768 }}
    [Int64]$raw = 0
    foreach ($f in $files) {{ $raw += [Int64]$f.Length }}
    [Int64]$avg = [Int64]($raw / [Int64]$files.Count)
    if ($avg -ge 1MB) {{ return 65536 }}
    return 32768
}}

function Get-OptimalImageSizeBytes([string]$dir, [int]$clusterBytes) {{
    $cluster = [Int64]$clusterBytes
    $files = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force)
    $dirs = @(Get-ChildItem -LiteralPath $dir -Recurse -Directory -Force)
    [Int64]$rawFileBytes = 0; [Int64]$dataBytes = 0
    foreach ($f in $files) {{
        $len = [Int64]$f.Length
        $rawFileBytes += $len
        $dataBytes += [Int64]([Math]::Ceiling($len / [double]$cluster) * $cluster)
    }}
    [Int64]$dataClusters = [Int64]([Math]::Ceiling($dataBytes / [double]$cluster))
    [Int64]$fatBytes = $dataClusters * 4
    [Int64]$bitmapBytes = [Int64]([Math]::Ceiling($dataClusters / 8.0))
    [Int64]$entryBytes = (([Int64]$files.Count + [Int64]$dirs.Count) * 256)
    [Int64]$baseTotal = $dataBytes + $fatBytes + $bitmapBytes + $entryBytes + 32MB
    [Int64]$spareBytes = [Int64]([Math]::Ceiling($baseTotal / 200.0))
    if ($spareBytes -lt 64MB) {{ $spareBytes = 64MB }}
    if ($spareBytes -gt 512MB) {{ $spareBytes = 512MB }}
    [Int64]$total = $baseTotal + $spareBytes
    [Int64]$minTotal = $rawFileBytes + 64MB
    if ($total -lt $minTotal) {{ $total = $minTotal }}
    $total = [Int64]([Math]::Ceiling($total / [double]1MB) * 1MB)
    return $total
}}

function Format-AU([int]$cs) {{
    if ($cs -ge 1MB -and ($cs % 1MB) -eq 0) {{ return ""$($cs / 1MB)M"" }}
    if ($cs -ge 1KB -and ($cs % 1KB) -eq 0) {{ return ""$($cs / 1KB)K"" }}
    return ""$cs""
}}

function Get-LogicalDriveFileSystem([string]$dl) {{
    $target = ""${{dl}}:""
    $logical = Get-CimInstance -ClassName Win32_LogicalDisk -Filter ""DeviceID='$target'"" -ErrorAction SilentlyContinue
    if ($logical) {{ return [string]$logical.FileSystem }}
    return ''
}}

function Invoke-FormatVolume([string]$driveLetter, [int]$clusterSize, [string]$label) {{
    $target = ""${{driveLetter}}:""
    $fileSystem = 'exFAT'
    $clusterArg = Format-AU -cs $clusterSize

    Log '[Info] Attempting format using PowerShell Format-Volume...'
    try {{
        $volume = Get-Volume -DriveLetter $driveLetter -ErrorAction SilentlyContinue
        if ($volume) {{
            $volume | Format-Volume -FileSystem $fileSystem -NewFileSystemLabel $label -AllocationUnitSize $clusterSize -Full:$false -Force -ErrorAction Stop | Out-Null
            Log ""[Info] Format-Volume succeeded on $target.""
            return
        }}
    }} catch {{
        Log ""[Warning] Format-Volume failed: $($_.Exception.Message). Falling back to format.com...""
    }}

    $fmtArgs = @($target, ""/FS:$fileSystem"", ""/A:$clusterArg"", '/Q', ""/V:$label"", '/X', '/Y')
    Log ""[Info] format.com attempt: $fileSystem quick with allocation unit $clusterArg""
    $stdoutPath = [IO.Path]::GetTempFileName()
    $stderrPath = [IO.Path]::GetTempFileName()
    try {{
        $proc = Start-Process -FilePath 'format.com' -ArgumentList $fmtArgs -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        $lastFormatExitCode = [int]$proc.ExitCode

        if (Test-Path -LiteralPath $stdoutPath) {{
            Get-Content -LiteralPath $stdoutPath -ErrorAction SilentlyContinue | ForEach-Object {{ Log $_ }}
        }}
        if (Test-Path -LiteralPath $stderrPath) {{
            Get-Content -LiteralPath $stderrPath -ErrorAction SilentlyContinue | ForEach-Object {{ Log $_ }}
        }}
    }} finally {{
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }}

    if (Wait-ForLogicalDrive -dl $driveLetter -timeout 10) {{
        $actualFs = Get-LogicalDriveFileSystem -dl $driveLetter
        if ($actualFs -and $actualFs.ToUpperInvariant() -eq $fileSystem.ToUpperInvariant()) {{
            Log ""[Info] format result: detected filesystem '$actualFs' on $target.""
            return
        }}
    }}

    throw ""Formatting failed for $target (exit code $lastFormatExitCode).""
}}

# ── Main ──
$SourceDir = '{Esc(inputPath)}'
$ImagePath = '{Esc(outputPath)}'
$Label     = '{Esc(label)}'

if (-not (Test-Path $SourceDir -PathType Container)) {{ throw ""Source not found: $SourceDir"" }}
$outDir = Split-Path -Parent $ImagePath
if ($outDir -and -not (Test-Path $outDir)) {{ New-Item -ItemType Directory -Path $outDir | Out-Null }}
if (Test-Path $ImagePath) {{ Remove-Item -Force $ImagePath }}

$clusterSize = Get-OptimalExfatClusterSize -dir $SourceDir
$imageSize = Get-OptimalImageSizeBytes -dir $SourceDir -clusterBytes $clusterSize
Log ""[Info] Computed image size: $([Math]::Round($imageSize/1GB, 2)) GB ($imageSize bytes)""
Log ""[Info] Selected cluster size: $clusterSize""

$osf = Find-OSFMountCom
$DriveLetter = Get-FreeDriveLetter
$MountPoint = ""${{DriveLetter}}:""
$Mounted = $false

try {{
    Log ""[1/4] Creating & mounting image via OSFMount on $MountPoint ...""
    $out = & $osf -a -t file -f $ImagePath -s $imageSize -m $MountPoint -o rw,rem 2>&1
    Log ($out | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {{ throw ""osfmount.com failed with exit code $LASTEXITCODE."" }}
    $Mounted = $true
    if (-not (Wait-ForLogicalDrive -dl $DriveLetter -timeout 30)) {{
        throw ""Mounted drive $MountPoint did not appear in time.""
    }}
    Log ""[Info] Drive $MountPoint is ready.""

    $dest = ""${{DriveLetter}}:\""

    Log ""[2/4] Formatting $dest as exFAT (cluster=$clusterSize, label='$Label') ...""
    Invoke-FormatVolume -driveLetter $DriveLetter -clusterSize $clusterSize -label $Label

    if (-not (Test-Path $dest)) {{ throw ""Drive $dest not accessible after formatting."" }}

    Log ""[3/4] Copying '$SourceDir' -> '$dest' ...""
    & robocopy.exe $SourceDir $dest /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /ETA
    $rc = $LASTEXITCODE
    if ($rc -gt 7) {{ throw ""robocopy failed. Exit code: $rc"" }}
    Log ""[Info] Robocopy completed (exit code $rc).""

    Log ""[4/4] Dismounting OSFMount volume...""
}} finally {{
    if ($Mounted -and -not [string]::IsNullOrWhiteSpace($MountPoint)) {{
        try {{
            $cur = (Get-Location).Path
            if ($cur -like ""$MountPoint*"") {{ Set-Location ""$env:SystemDrive\"" }}
        }} catch {{ }}
        if (-not (Dismount-OsfVolume -osfPath $osf -mountPoint $MountPoint -maxAttempts 6)) {{
            Log '@@EXFAT_WARNING@@ Failed to dismount OSFMount volume.'
        }}
    }}
}}

Log ""OK: Image created at: $ImagePath""
Log '@@EXFAT_SUCCESS@@'
";
        }

        // ─── Log file monitor ───

        private async Task MonitorLogFileAsync(string logFile, Process process, CancellationToken ct)
        {
            long lastPos = 0;
            int stage = 5;

            while (!process.HasExited || (File.Exists(logFile) && new FileInfo(logFile).Length > lastPos))
            {
                ct.ThrowIfCancellationRequested();

                if (File.Exists(logFile))
                {
                    try
                    {
                        using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (fs.Length > lastPos)
                        {
                            fs.Seek(lastPos, SeekOrigin.Begin);
                            using var reader = new StreamReader(fs);
                            string? line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                ct.ThrowIfCancellationRequested();
                                Log(line.Replace("@@EXFAT_SUCCESS@@", "").Replace("@@EXFAT_WARNING@@", "⚠").Trim());
                                ParseProgress(line, ref stage);
                            }
                            lastPos = fs.Position;
                        }
                    }
                    catch (IOException) { }
                }

                if (!process.HasExited)
                    await Task.Delay(300, ct);
            }
        }

        private void ParseProgress(string line, ref int stage)
        {
            if (line.Contains("[1/4]")) { stage = 15; ReportProgress("Mounting", "Creating & mounting image via OSFMount...", stage); }
            else if (line.Contains("[2/4]")) { stage = 30; ReportProgress("Formatting", "Formatting as exFAT...", stage); }
            else if (line.Contains("[3/4]")) { stage = 45; ReportProgress("Copying", "Copying game files...", stage); }
            else if (line.Contains("[4/4]")) { stage = 90; ReportProgress("Finalizing", "Dismounting volume...", stage); }
            else if (line.Contains("@@EXFAT_SUCCESS@@")) { ReportProgress("Complete", "Image created successfully!", 98); }
            else
            {
                var m = Regex.Match(line, @"(\d+(?:\.\d+)?)%");
                if (m.Success && float.TryParse(m.Groups[1].Value, out float pct))
                {
                    stage = 45 + (int)(pct * 0.40);
                    ReportProgress("Copying", $"Copying files: {pct:F0}%", stage);
                }
            }
        }

        // ─── Helpers ───

        private void Log(string msg) { if (!string.IsNullOrEmpty(msg)) OnLog?.Invoke(msg); }

        private void ReportProgress(string stage, string detail, int pct, bool err = false)
            => OnProgress?.Invoke(new ConversionProgress { Stage = stage, Detail = detail, PercentComplete = pct, IsError = err });
    }
}
