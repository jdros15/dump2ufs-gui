using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Dump2UfsGui.Services;
using MessageBox = System.Windows.MessageBox;

namespace Dump2UfsGui
{
    public partial class MainWindow : Window
    {
        private SettingsData _settings = new();
        private GameInfo? _gameInfo;
        private string? _ufs2ToolPath;
        private CancellationTokenSource? _cts;
        private bool _isConverting;

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsManager.Load();
            _ufs2ToolPath = SettingsManager.FindUfs2Tool(_settings);

            if (_ufs2ToolPath != null)
            {
                UpdateToolVersionDisplay();
            }
            else
            {
                // Show setup overlay
                OverlaySetup.Visibility = Visibility.Visible;
            }
        }

        // ═══════════════════════════════════════════
        // INPUT
        // ═══════════════════════════════════════════

        private void BtnBrowseInput_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select PS5 Game Dump Folder",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            if (!string.IsNullOrEmpty(_settings.LastInputDir) && Directory.Exists(_settings.LastInputDir))
                dialog.InitialDirectory = _settings.LastInputDir;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                SetInputPath(dialog.SelectedPath);
            }
        }

        private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)!;
                if (paths.Length > 0 && Directory.Exists(paths[0]))
                {
                    e.Effects = System.Windows.DragDropEffects.Copy;
                    e.Handled = true;
                    return;
                }
            }
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)!;
                if (paths.Length > 0 && Directory.Exists(paths[0]))
                {
                    SetInputPath(paths[0]);
                }
            }
        }

        private void SetInputPath(string path)
        {
            TxtInputPath.Text = path;
            TxtInputPath.Foreground = FindResource("TextPrimaryBrush") as System.Windows.Media.SolidColorBrush;
            PanelGameInfo.Visibility = Visibility.Collapsed;
            PanelInputError.Visibility = Visibility.Collapsed;
            TxtDropHint.Visibility = Visibility.Collapsed;

            _settings.LastInputDir = Path.GetDirectoryName(path);

            try
            {
                if (!GameDumpValidator.IsValidDump(path))
                {
                    ShowInputError("This folder does not contain sce_sys/param.json. Make sure you selected a valid PS5 game dump folder.");
                    BtnConvert.IsEnabled = false;
                    return;
                }

                _gameInfo = GameDumpValidator.ParseGameInfo(path);
                TxtGameTitle.Text = _gameInfo.TitleName;
                TxtGameId.Text = _gameInfo.TitleId;
                TxtLabel.Text = _gameInfo.AutoLabel;
                PanelGameInfo.Visibility = Visibility.Visible;

                // Auto-fill output path (same parent dir)
                var parentDir = Path.GetDirectoryName(path) ?? path;
                var outputPath = Path.Combine(parentDir, _gameInfo.SuggestedOutputName);
                TxtOutputPath.Text = outputPath;

                BtnConvert.IsEnabled = _ufs2ToolPath != null;
                TxtStatus.Text = $"Ready — {_gameInfo.TitleName} ({_gameInfo.TitleId})";

                AppendLog($"Detected game: {_gameInfo.TitleName} (ID: {_gameInfo.TitleId})");
                AppendLog($"Auto-label: {_gameInfo.AutoLabel}");
                AppendLog($"Output: {outputPath}");
            }
            catch (Exception ex)
            {
                ShowInputError(ex.Message);
                BtnConvert.IsEnabled = false;
            }
        }

        private void ShowInputError(string message)
        {
            TxtInputError.Text = $"⚠ {message}";
            PanelInputError.Visibility = Visibility.Visible;
        }

        // ═══════════════════════════════════════════
        // OUTPUT
        // ═══════════════════════════════════════════

        private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save .ffpkg File",
                Filter = "FFPkg Files (*.ffpkg)|*.ffpkg|All Files (*.*)|*.*",
                DefaultExt = "ffpkg"
            };

            if (_gameInfo != null)
                dialog.FileName = _gameInfo.SuggestedOutputName;

            if (!string.IsNullOrEmpty(TxtOutputPath.Text))
            {
                var dir = Path.GetDirectoryName(TxtOutputPath.Text);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
            }
            else if (!string.IsNullOrEmpty(_settings.LastOutputDir) && Directory.Exists(_settings.LastOutputDir))
            {
                dialog.InitialDirectory = _settings.LastOutputDir;
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TxtOutputPath.Text = dialog.FileName;
                _settings.LastOutputDir = Path.GetDirectoryName(dialog.FileName);
            }
        }

        // ═══════════════════════════════════════════
        // CONVERT
        // ═══════════════════════════════════════════

        private async void BtnConvert_Click(object sender, RoutedEventArgs e)
        {
            if (_isConverting || _ufs2ToolPath == null || _gameInfo == null) return;

            var inputPath = TxtInputPath.Text;
            var outputPath = TxtOutputPath.Text;

            if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputPath))
            {
                MessageBox.Show("Please specify both input folder and output file path.", "Missing Info",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (File.Exists(outputPath))
            {
                var result = MessageBox.Show(
                    $"The file already exists:\n{outputPath}\n\nOverwrite?",
                    "File Exists", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
            }

            _isConverting = true;
            _cts = new CancellationTokenSource();
            SetConvertingState(true);

            try
            {
                var converter = new Ufs2Converter(_ufs2ToolPath);

                converter.OnProgress += p =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        TxtProgressStage.Text = p.Stage;
                        TxtProgressDetail.Text = p.Detail;
                        TxtProgressPercent.Text = $"{p.PercentComplete}%";
                        ProgressBar.Value = p.PercentComplete;
                        TxtStatus.Text = $"{p.Stage}: {p.Detail}";

                        if (p.IsError)
                        {
                            TxtProgressStage.Foreground = FindResource("AccentRedBrush") as System.Windows.Media.SolidColorBrush;
                        }
                        else
                        {
                            TxtProgressStage.Foreground = FindResource("AccentBlueBrush") as System.Windows.Media.SolidColorBrush;
                        }
                    });
                };

                converter.OnLog += msg =>
                {
                    Dispatcher.Invoke(() => AppendLog(msg));
                };

                var convResult = await Task.Run(
                    () => converter.ConvertAsync(inputPath, outputPath, _gameInfo.AutoLabel, _cts.Token));

                if (convResult.Success)
                {
                    TxtProgressStage.Foreground = FindResource("AccentGreenBrush") as System.Windows.Media.SolidColorBrush;
                    TxtStatus.Text = $"✅ Done — {Ufs2Converter.FormatSize(convResult.FileSize)} saved to {Path.GetFileName(outputPath)}";

                    MessageBox.Show(
                        $"Conversion complete!\n\n" +
                        $"Game: {_gameInfo.TitleName}\n" +
                        $"Output: {outputPath}\n" +
                        $"Size: {Ufs2Converter.FormatSize(convResult.FileSize)}\n" +
                        $"Block size: {convResult.OptimalBlockSize}\n" +
                        $"Fragment size: {convResult.OptimalFragmentSize}",
                        "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (!string.IsNullOrEmpty(convResult.ErrorMessage) && convResult.ErrorMessage != "Conversion was cancelled.")
                {
                    MessageBox.Show($"Conversion failed:\n\n{convResult.ErrorMessage}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unexpected error:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isConverting = false;
                _cts?.Dispose();
                _cts = null;
                SetConvertingState(false);

                // Save settings
                SettingsManager.Save(_settings);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void SetConvertingState(bool converting)
        {
            BtnConvert.IsEnabled = !converting;
            BtnConvert.Visibility = converting ? Visibility.Collapsed : Visibility.Visible;
            BtnCancel.Visibility = converting ? Visibility.Visible : Visibility.Collapsed;
            BtnBrowseInput.IsEnabled = !converting;
            BtnBrowseOutput.IsEnabled = !converting;
            TxtOutputPath.IsReadOnly = converting;
            PanelProgress.Visibility = converting ? Visibility.Visible : PanelProgress.Visibility;

            if (!converting)
            {
                BtnConvert.IsEnabled = _gameInfo != null && _ufs2ToolPath != null;
            }
        }

        // ═══════════════════════════════════════════
        // LOG
        // ═══════════════════════════════════════════

        private void AppendLog(string message)
        {
            TxtLog.AppendText(message + Environment.NewLine);
            TxtLog.ScrollToEnd();
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Clear();
        }

        // ═══════════════════════════════════════════
        // SETUP / UFS2TOOL MANAGEMENT
        // ═══════════════════════════════════════════

        private async void BtnSetupDownload_Click(object sender, RoutedEventArgs e)
        {
            BtnSetupDownload.IsEnabled = false;
            BtnSetupBrowse.IsEnabled = false;
            SetupProgress.Visibility = Visibility.Visible;
            SetupProgress.IsIndeterminate = true;
            TxtSetupProgress.Visibility = Visibility.Visible;

            try
            {
                TxtSetupMessage.Text = "Checking for the latest version...";

                var update = await SettingsManager.CheckForUpdateAsync(null);

                if (string.IsNullOrEmpty(update.DownloadUrl))
                {
                    TxtSetupMessage.Text = "Could not find the download URL. Please download UFS2Tool.exe manually from GitHub.";
                    BtnSetupBrowse.IsEnabled = true;
                    return;
                }

                await SettingsManager.DownloadUfs2ToolAsync(
                    update.DownloadUrl,
                    update.LatestVersion,
                    progress => Dispatcher.Invoke(() => TxtSetupProgress.Text = progress));

                _settings = SettingsManager.Load();
                _ufs2ToolPath = SettingsManager.FindUfs2Tool(_settings);

                if (_ufs2ToolPath != null)
                {
                    OverlaySetup.Visibility = Visibility.Collapsed;
                    UpdateToolVersionDisplay();
                    AppendLog($"UFS2Tool {_settings.Ufs2ToolVersion} downloaded and ready.");
                    TxtStatus.Text = "Ready — select a PS5 game dump folder to begin";
                }
                else
                {
                    TxtSetupMessage.Text = "Download completed but UFS2Tool.exe was not found in the extracted files. Please locate it manually.";
                    BtnSetupBrowse.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                TxtSetupMessage.Text = $"Download failed: {ex.Message}\n\nPlease download UFS2Tool.exe manually.";
                BtnSetupBrowse.IsEnabled = true;
            }
            finally
            {
                SetupProgress.IsIndeterminate = false;
                BtnSetupDownload.IsEnabled = true;
            }
        }

        private void BtnSetupBrowse_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Locate UFS2Tool.exe",
                Filter = "UFS2Tool.exe|UFS2Tool.exe|All Files (*.*)|*.*",
                FileName = "UFS2Tool.exe"
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _ufs2ToolPath = dialog.FileName;
                _settings.Ufs2ToolPath = dialog.FileName;
                SettingsManager.Save(_settings);

                OverlaySetup.Visibility = Visibility.Collapsed;
                UpdateToolVersionDisplay();
                AppendLog($"UFS2Tool.exe set to: {dialog.FileName}");
                TxtStatus.Text = "Ready — select a PS5 game dump folder to begin";

                if (_gameInfo != null)
                    BtnConvert.IsEnabled = true;
            }
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckUpdate.IsEnabled = false;
            BtnCheckUpdate.Content = "🔄 Checking...";
            AppendLog("Checking for UFS2Tool updates...");

            try
            {
                var update = await SettingsManager.CheckForUpdateAsync(_settings.Ufs2ToolVersion);

                if (update.UpdateAvailable && !string.IsNullOrEmpty(update.LatestVersion))
                {
                    var current = _settings.Ufs2ToolVersion ?? "unknown";
                    var result = MessageBox.Show(
                        $"A new version of UFS2Tool is available!\n\n" +
                        $"Current: {current}\n" +
                        $"Latest: {update.LatestVersion}\n\n" +
                        $"Download and install the update?",
                        "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        AppendLog($"Downloading UFS2Tool {update.LatestVersion}...");
                        await SettingsManager.DownloadUfs2ToolAsync(
                            update.DownloadUrl,
                            update.LatestVersion,
                            progress => Dispatcher.Invoke(() =>
                            {
                                AppendLog(progress);
                                TxtStatus.Text = progress;
                            }));

                        _settings = SettingsManager.Load();
                        _ufs2ToolPath = SettingsManager.FindUfs2Tool(_settings);
                        UpdateToolVersionDisplay();
                        AppendLog($"Updated to UFS2Tool {_settings.Ufs2ToolVersion}!");
                    }
                }
                else
                {
                    AppendLog("UFS2Tool is up to date.");
                    MessageBox.Show("UFS2Tool is already up to date!", "No Updates",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Update check failed: {ex.Message}");
                MessageBox.Show($"Failed to check for updates:\n\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                BtnCheckUpdate.IsEnabled = true;
                BtnCheckUpdate.Content = "🔄 Check for Updates";
            }
        }

        private void UpdateToolVersionDisplay()
        {
            var version = _settings.Ufs2ToolVersion ?? "bundled";
            TxtToolVersion.Text = $"UFS2Tool: {version}";
        }
    }
}
