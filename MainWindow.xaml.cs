using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Forms;
using Dump2UfsGui.Models;
using Dump2UfsGui.Services;
using MessageBox = System.Windows.MessageBox;

namespace Dump2UfsGui
{
    public partial class MainWindow : Window
    {
        private SettingsData _settings = new();
        private string? _ufs2ToolPath;
        private CancellationTokenSource? _cts;
        private bool _isProcessingQueue;

        // Smart log dedup
        private string _lastLogPrefix = "";
        private int _lastLogLineStart = -1;

        // Queue
        private readonly ObservableCollection<QueueItem> _queue = new();

        // Drag-reorder state
        private Point _dragStartPoint;
        private QueueItem? _draggedItem;

        public MainWindow()
        {
            InitializeComponent();
            QueueListBox.ItemsSource = _queue;
            _queue.CollectionChanged += (_, __) => UpdateQueueUI();
        }

        // Custom title bar buttons
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsManager.Load();
            _ufs2ToolPath = SettingsManager.FindUfs2Tool(_settings);

            if (_ufs2ToolPath != null)
            {
                // Path found
            }
            else
            {
                // Show setup overlay
                OverlaySetup.Visibility = Visibility.Visible;
            }

            // Pre-fill output dir from settings
            if (!string.IsNullOrEmpty(_settings.LastOutputDir) && Directory.Exists(_settings.LastOutputDir))
            {
                TxtOutputDir.Text = _settings.LastOutputDir;
            }

        }

        // ═══════════════════════════════════════════
        // QUEUE MANAGEMENT
        // ═══════════════════════════════════════════

        private void AddToQueue(string folderPath)
        {
            // Check for duplicates (case-insensitive path comparison)
            var normalizedPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var existing in _queue)
            {
                var existingNormalized = Path.GetFullPath(existing.InputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalizedPath, existingNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog($"⚠ Skipped duplicate: {Path.GetFileName(folderPath)}");
                    return;
                }
            }

            // Validate it's a game dump
            if (!GameDumpValidator.IsValidDump(folderPath))
            {
                AppendLog($"⚠ Not a valid PS5 dump: {Path.GetFileName(folderPath)} (missing sce_sys/param.json)");
                return;
            }

            try
            {
                var gameInfo = GameDumpValidator.ParseGameInfo(folderPath);

                // Auto-generate output path
                var outputDir = !string.IsNullOrWhiteSpace(TxtOutputDir.Text)
                    ? TxtOutputDir.Text
                    : Path.GetDirectoryName(folderPath) ?? folderPath;

                var outputPath = Path.Combine(outputDir, gameInfo.SuggestedOutputName);

                var item = new QueueItem
                {
                    InputPath = folderPath,
                    OutputPath = outputPath,
                    GameInfo = gameInfo,
                    StatusText = "Waiting"
                };

                _queue.Add(item);
                AppendLog($"Added to queue: {gameInfo.TitleName} ({gameInfo.TitleId})");

                // Auto-fill output dir if empty
                if (string.IsNullOrWhiteSpace(TxtOutputDir.Text))
                {
                    TxtOutputDir.Text = outputDir;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"⚠ Error parsing {Path.GetFileName(folderPath)}: {ex.Message}");
            }
        }

        private void UpdateQueueUI()
        {
            var waitingCount = _queue.Count(q => q.Status == QueueItemStatus.Waiting);
            var totalCount = _queue.Count;
            var doneCount = _queue.Count(q => q.Status == QueueItemStatus.Done);

            if (_isProcessingQueue)
            {
                TxtQueueCount.Text = $"({doneCount}/{totalCount} done)";
            }
            else if (totalCount > 0)
            {
                TxtQueueCount.Text = $"({totalCount} game{(totalCount == 1 ? "" : "s")})";
            }
            else
            {
                TxtQueueCount.Text = "";
            }

            BtnConvert.IsEnabled = waitingCount > 0 && _ufs2ToolPath != null && !_isProcessingQueue;
            TxtConvertButton.Text = waitingCount > 1
                ? $"Convert {waitingCount} Games to .ffpkg"
                : "Convert to .ffpkg";
        }

        private void BtnRemoveQueueItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is QueueItem item)
            {
                if (item.Status == QueueItemStatus.Waiting)
                {
                    _queue.Remove(item);
                    AppendLog($"Removed from queue: {item.GameInfo.TitleName}");
                }
            }
        }

        private void BtnClearQueue_Click(object sender, RoutedEventArgs e)
        {
            // Remove all items that are not currently processing
            var removable = _queue.Where(q => q.Status != QueueItemStatus.Processing).ToList();
            foreach (var item in removable)
            {
                _queue.Remove(item);
            }
            AppendLog($"Cleared {removable.Count} item(s) from queue.");
        }

        // ═══════════════════════════════════════════
        // DRAG & DROP — ADD TO QUEUE
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
                _settings.LastInputDir = Path.GetDirectoryName(dialog.SelectedPath);
                AddToQueue(dialog.SelectedPath);
            }
        }

        private void DropZone_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)!;
                if (paths.Any(p => Directory.Exists(p)))
                {
                    e.Effects = System.Windows.DragDropEffects.Copy;
                    SetDropZoneActive(true);
                    e.Handled = true;
                    return;
                }
            }
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void DropZone_DragLeave(object sender, System.Windows.DragEventArgs e)
        {
            SetDropZoneActive(false);
        }

        private void DropZone_Drop(object sender, System.Windows.DragEventArgs e)
        {
            SetDropZoneActive(false);

            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)!;
                int added = 0;
                foreach (var path in paths)
                {
                    if (Directory.Exists(path))
                    {
                        var countBefore = _queue.Count;
                        AddToQueue(path);
                        if (_queue.Count > countBefore) added++;
                    }
                }
                if (added > 0)
                {
                    TxtStatus.Text = $"Added {added} game{(added == 1 ? "" : "s")} to queue";
                }
            }
        }

        private void DropZone_MouseClick(object sender, MouseButtonEventArgs e)
        {
            BtnBrowseInput_Click(sender, new RoutedEventArgs());
        }

        private void SetDropZoneActive(bool active)
        {
            if (active)
            {
                DropZoneBorderBrush.Color = (Color)ColorConverter.ConvertFromString("#7B2FFF");
                DropZoneBgBrush.Color = (Color)ColorConverter.ConvertFromString("#120D25");
                DropZoneHighlight.Visibility = Visibility.Visible;
                DropZoneText.Text = "Drop your folder(s) here!";
                DropZoneText.Foreground = FindResource("AccentPurpleBrush") as SolidColorBrush;

                var scaleUp = new DoubleAnimation(1.2, TimeSpan.FromMilliseconds(150))
                { EasingFunction = new QuadraticEase() };
                DropZoneIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
                DropZoneIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
            }
            else
            {
                DropZoneBorderBrush.Color = (Color)ColorConverter.ConvertFromString("#353560");
                DropZoneBgBrush.Color = (Color)ColorConverter.ConvertFromString("#0A0A1A");
                DropZoneHighlight.Visibility = Visibility.Collapsed;
                DropZoneText.Text = "Drag & drop PS5 game dump folders here to add to queue";
                DropZoneText.Foreground = FindResource("TextSecondaryBrush") as SolidColorBrush;

                var scaleDown = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
                { EasingFunction = new QuadraticEase() };
                DropZoneIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
                DropZoneIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
            }
        }

        // ═══════════════════════════════════════════
        // DRAG REORDER — QUEUE LIST
        // ═══════════════════════════════════════════

        private void QueueList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void QueueList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var currentPos = e.GetPosition(null);
            var diff = _dragStartPoint - currentPos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                // Find the item being dragged
                var listBoxItem = FindVisualParent<System.Windows.Controls.ListBoxItem>(
                    (DependencyObject)e.OriginalSource);

                if (listBoxItem == null) return;

                var item = (QueueItem)QueueListBox.ItemContainerGenerator.ItemFromContainer(listBoxItem);

                // Only allow dragging waiting items
                if (item == null || item.Status != QueueItemStatus.Waiting) return;

                _draggedItem = item;

                var data = new System.Windows.DataObject("QueueItem", item);
                DragDrop.DoDragDrop(listBoxItem, data, System.Windows.DragDropEffects.Move);
                _draggedItem = null;
            }
        }

        private void QueueList_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("QueueItem"))
            {
                e.Effects = System.Windows.DragDropEffects.None;
                e.Handled = true;
                return;
            }
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
        }

        private void QueueList_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("QueueItem")) return;

            var droppedData = (QueueItem)e.Data.GetData("QueueItem")!;
            var targetItem = FindQueueItemAtPosition(e.GetPosition(QueueListBox));

            if (targetItem == null || droppedData == targetItem) return;

            // Only allow reorder among waiting items, and target must be a waiting slot
            if (droppedData.Status != QueueItemStatus.Waiting) return;

            int oldIndex = _queue.IndexOf(droppedData);
            int newIndex = _queue.IndexOf(targetItem);

            // Find the valid range boundaries (can only move within waiting items)
            // Items that are done/processing are at the top, waiting items below
            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                // Only allow placing among other waiting items
                if (targetItem.Status != QueueItemStatus.Waiting) return;

                _queue.Move(oldIndex, newIndex);
            }
        }

        private QueueItem? FindQueueItemAtPosition(Point position)
        {
            var element = QueueListBox.InputHitTest(position) as DependencyObject;
            if (element == null) return null;

            var listBoxItem = FindVisualParent<System.Windows.Controls.ListBoxItem>(element);
            if (listBoxItem == null) return null;

            return QueueListBox.ItemContainerGenerator.ItemFromContainer(listBoxItem) as QueueItem;
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent)
                    return parent;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        // ═══════════════════════════════════════════
        // OUTPUT
        // ═══════════════════════════════════════════

        private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select Output Directory for .ffpkg Files",
                UseDescriptionForTitle = true
            };

            if (!string.IsNullOrEmpty(TxtOutputDir.Text) && Directory.Exists(TxtOutputDir.Text))
                dialog.InitialDirectory = TxtOutputDir.Text;
            else if (!string.IsNullOrEmpty(_settings.LastOutputDir) && Directory.Exists(_settings.LastOutputDir))
                dialog.InitialDirectory = _settings.LastOutputDir;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TxtOutputDir.Text = dialog.SelectedPath;
                _settings.LastOutputDir = dialog.SelectedPath;

                // Update output paths for all waiting items
                foreach (var item in _queue.Where(q => q.Status == QueueItemStatus.Waiting))
                {
                    item.OutputPath = Path.Combine(dialog.SelectedPath, item.GameInfo.SuggestedOutputName);
                }
            }
        }

        // ═══════════════════════════════════════════
        // BATCH CONVERSION
        // ═══════════════════════════════════════════

        private async void BtnConvert_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessingQueue || _ufs2ToolPath == null) return;

            var waitingItems = _queue.Where(q => q.Status == QueueItemStatus.Waiting).ToList();
            if (waitingItems.Count == 0) return;

            // Validate output directory
            var outputDir = TxtOutputDir.Text;
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                MessageBox.Show("Please specify an output directory.", "Missing Info",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Directory.Exists(outputDir))
            {
                try { Directory.CreateDirectory(outputDir); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Cannot create output directory:\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            // Update output paths to use current output dir
            foreach (var item in waitingItems)
            {
                item.OutputPath = Path.Combine(outputDir, item.GameInfo.SuggestedOutputName);
            }

            // Check for existing files
            var existing = waitingItems.Where(i => File.Exists(i.OutputPath)).ToList();
            if (existing.Count > 0)
            {
                var names = string.Join("\n", existing.Select(i => Path.GetFileName(i.OutputPath)));
                var result = MessageBox.Show(
                    $"The following files already exist and will be overwritten:\n\n{names}\n\nContinue?",
                    "Files Exist", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
            }

            _isProcessingQueue = true;
            _cts = new CancellationTokenSource();
            SetConvertingState(true);
            PanelLog.Visibility = Visibility.Visible;
            LogColumn.Width = new GridLength(1, GridUnitType.Star);

            int completed = 0, failed = 0;

            try
            {
                // Process each waiting item one by one
                // Re-query waiting items each iteration in case user removed items during processing
                while (true)
                {
                    var nextItem = _queue.FirstOrDefault(q => q.Status == QueueItemStatus.Waiting);
                    if (nextItem == null) break;

                    if (_cts.Token.IsCancellationRequested) break;

                    // Update output path in case output dir changed
                    nextItem.OutputPath = Path.Combine(TxtOutputDir.Text, nextItem.GameInfo.SuggestedOutputName);

                    nextItem.Status = QueueItemStatus.Processing;
                    nextItem.StatusText = "Optimizing block sizes...";
                    nextItem.Progress = 0;

                    TxtStatus.Text = $"Converting {nextItem.GameInfo.TitleName}... ({completed + 1}/{completed + _queue.Count(q => q.Status == QueueItemStatus.Waiting) + 1})";
                    AppendLog($"\n=== Converting: {nextItem.GameInfo.TitleName} ({nextItem.GameInfo.TitleId}) ===");

                    try
                    {
                        var converter = new Ufs2Converter(_ufs2ToolPath);

                        converter.OnProgress += p =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                nextItem.Progress = p.PercentComplete;
                                nextItem.StatusText = $"{p.Stage}: {p.Detail}";
                            });
                        };

                        converter.OnLog += msg =>
                        {
                            Dispatcher.Invoke(() => AppendLog(msg));
                        };

                        var convResult = await Task.Run(
                            () => converter.ConvertAsync(nextItem.InputPath, nextItem.OutputPath, nextItem.GameInfo.AutoLabel, _cts.Token));

                        if (convResult.Success)
                        {
                            nextItem.Status = QueueItemStatus.Done;
                            nextItem.Progress = 100;
                            nextItem.StatusText = $"✅ Done — {Ufs2Converter.FormatSize(convResult.FileSize)}";
                            completed++;
                            AppendLog($"✅ {nextItem.GameInfo.TitleName} completed: {Ufs2Converter.FormatSize(convResult.FileSize)}");
                        }
                        else if (convResult.ErrorMessage == "Conversion was cancelled.")
                        {
                            nextItem.Status = QueueItemStatus.Cancelled;
                            nextItem.StatusText = "Cancelled";

                            // Mark remaining waiting items as cancelled too
                            foreach (var remaining in _queue.Where(q => q.Status == QueueItemStatus.Waiting))
                            {
                                remaining.Status = QueueItemStatus.Cancelled;
                                remaining.StatusText = "Cancelled";
                            }
                            break;
                        }
                        else
                        {
                            nextItem.Status = QueueItemStatus.Error;
                            nextItem.StatusText = $"❌ {convResult.ErrorMessage}";
                            failed++;
                            AppendLog($"❌ {nextItem.GameInfo.TitleName} failed: {convResult.ErrorMessage}");
                        }
                    }
                    catch (Exception ex)
                    {
                        nextItem.Status = QueueItemStatus.Error;
                        nextItem.StatusText = $"❌ {ex.Message}";
                        failed++;
                        AppendLog($"❌ {nextItem.GameInfo.TitleName} error: {ex.Message}");
                    }

                    UpdateQueueUI();
                }

                // Final summary
                if (!_cts.Token.IsCancellationRequested)
                {
                    TxtStatus.Text = $"✅ Queue complete — {completed} succeeded, {failed} failed";
                    if (completed > 0 && failed == 0)
                    {
                        MessageBox.Show(
                            $"All {completed} game{(completed == 1 ? "" : "s")} converted successfully!",
                            "Queue Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else if (completed > 0)
                    {
                        MessageBox.Show(
                            $"{completed} succeeded, {failed} failed.\nCheck the log for details.",
                            "Queue Complete", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    TxtStatus.Text = "Queue cancelled";
                }
            }
            finally
            {
                _isProcessingQueue = false;
                _cts?.Dispose();
                _cts = null;
                SetConvertingState(false);
                SettingsManager.Save(_settings);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to cancel the current conversion?\n\nThe current item will be stopped and remaining items will be skipped.",
                "Cancel Conversion", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _cts?.Cancel();
            }
        }

        private void SetConvertingState(bool converting)
        {
            BtnConvert.IsEnabled = !converting;
            BtnConvert.Visibility = converting ? Visibility.Collapsed : Visibility.Visible;
            BtnCancel.Visibility = converting ? Visibility.Visible : Visibility.Collapsed;
            BtnBrowseOutput.IsEnabled = !converting;
            TxtOutputDir.IsReadOnly = converting;

            if (!converting)
            {
                BtnConvert.IsEnabled = _queue.Any(q => q.Status == QueueItemStatus.Waiting) && _ufs2ToolPath != null;
            }
        }

        // ═══════════════════════════════════════════
        // LOG
        // ═══════════════════════════════════════════

        private void AppendLog(string message)
        {
            // Smart dedup: if this line has the same prefix as the last, replace it
            var prefix = GetLogLinePrefix(message);
            if (!string.IsNullOrEmpty(prefix) && prefix == _lastLogPrefix && _lastLogLineStart >= 0)
            {
                // Replace the last line
                var text = TxtLog.Text;
                TxtLog.Text = text.Substring(0, _lastLogLineStart) + message + Environment.NewLine;
            }
            else
            {
                _lastLogLineStart = TxtLog.Text.Length;
                TxtLog.AppendText(message + Environment.NewLine);
                _lastLogPrefix = prefix;
            }
            TxtLog.ScrollToEnd();
        }

        /// <summary>
        /// Extracts a stable prefix from progress lines like "Adding files to image...  17% (114/376 files, 1.44 GiB/8.47 GiB)"
        /// so we can detect repeating lines that only differ in numbers.
        /// Returns empty string if this doesn't look like a progress line.
        /// </summary>
        private static string GetLogLinePrefix(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "";
            var trimmed = line.TrimStart();

            // Match lines like "Adding files to image...", "Block XXXX:", etc.
            // Extract text up to the first number or percentage
            int firstDigit = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (char.IsDigit(trimmed[i]))
                {
                    firstDigit = i;
                    break;
                }
            }

            if (firstDigit > 3)
            {
                // Has a meaningful text prefix before numbers
                return trimmed.Substring(0, firstDigit).TrimEnd();
            }

            return "";
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Clear();
        }

        private void BtnShowLog_Click(object sender, RoutedEventArgs e)
        {
            if (PanelLog.Visibility == Visibility.Visible)
            {
                PanelLog.Visibility = Visibility.Collapsed;
                LogColumn.Width = new GridLength(0);
            }
            else
            {
                PanelLog.Visibility = Visibility.Visible;
                LogColumn.Width = new GridLength(1, GridUnitType.Star);
            }
        }

        private void BtnToggleLog_Click(object sender, RoutedEventArgs e)
        {
            PanelLog.Visibility = Visibility.Collapsed;
            LogColumn.Width = new GridLength(0);
        }

        // ═══════════════════════════════════════════
        // SETUP / UFS2TOOL MANAGEMENT
        // ═══════════════════════════════════════════

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
                AppendLog($"UFS2Tool.exe set to: {dialog.FileName}");
                TxtStatus.Text = "Ready — drag PS5 game dump folders to add to queue";

                UpdateQueueUI();
            }
        }


    }
}
