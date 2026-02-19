using System;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
        private bool _isCancelling;
        private StringBuilder _rawLog = new();

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
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isProcessingQueue)
            {
                var result = MessageBox.Show(
                    "A conversion is currently in progress. Are you sure you want to exit?\n\nThe current process will be terminated and incomplete files will be deleted.",
                    "Exit Application", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            // Signal cancellation to all running tasks
            _cts?.Cancel();
            
            base.OnClosing(e);
        }

        // ═══════════════════════════════════════════
        // STARTUP CLEANUP
        // ═══════════════════════════════════════════

        private void CleanupOrphanedTools()
        {
            try
            {
                var orphans = Process.GetProcessesByName("UFS2Tool");
                foreach (var p in orphans)
                {
                    try { p.Kill(true); }
                    catch { /* Ignore if already gone */ }
                }
            }
            catch { /* System error getting processes */ }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CleanupOrphanedTools();
            _settings = SettingsManager.Load();

            // Perform health check on internal tools
            bool needsInit = !UpdateService.VerifyIntegratedToolHealth();

            if (needsInit)
            {
                TxtStatus.Text = "Initializing internal tools...";
                OverlaySetup.Visibility = Visibility.Visible;
                PanelLoading.Visibility = Visibility.Visible;
                PanelSetup.Visibility = Visibility.Collapsed;
            }

            try
            {
                var initTask = UpdateService.InitializeAsync();
                
                if (needsInit)
                {
                    // Ensure setup is visible for at least 800ms for UX clarity, unless it's already extracted
                    await Task.WhenAll(initTask, Task.Delay(1000));
                }
                else
                {
                    await initTask;
                }

                _ufs2ToolPath = UpdateService.GetEffectiveToolPath();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize UFS2Tool:\n{ex.Message}", "Init Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            if (_ufs2ToolPath != null)
            {
                OverlaySetup.Visibility = Visibility.Collapsed;
                RefreshToolStatus();
            }
            else
            {
                // Fallback to manual setup if internal extraction failed (unlikely)
                OverlaySetup.Visibility = Visibility.Visible;
                PanelLoading.Visibility = Visibility.Collapsed;
                PanelSetup.Visibility = Visibility.Visible;
            }

            // FOR FUTURE CONTEXT: The format selector is currently hidden in MainWindow.xaml
            // and we are forcing ffpkg for this build. To re-enable the format selector,
            // set Visibility back to Visible in MainWindow.xaml and remove the force below.
            _settings.OutputFormat = "ffpkg"; 

            // Initialize format combo
            if (ComboFormat != null)
            {
                foreach (System.Windows.Controls.ComboBoxItem item in ComboFormat.Items)
                {
                    if (item.Tag?.ToString() == _settings.OutputFormat)
                    {
                        item.IsSelected = true;
                        break;
                    }
                }
            }

            // Initialize experimental ffpkg checkbox
            CheckExperimentalFfpkg.IsChecked = _settings.EnableExperimentalFfpkg;
            CheckExperimentalFfpkg.Visibility = _settings.OutputFormat == "ffpkg" ? Visibility.Visible : Visibility.Collapsed;

            // Update UI
            UpdateQueueUI();
        }

        // ═══════════════════════════════════════════
        // QUEUE MANAGEMENT
        // ═══════════════════════════════════════════

        private void AddToQueue(string folderPath)
        {
            // Normalize path strictly (full path, no trailing slashes)
            folderPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Check for duplicates
            foreach (var existing in _queue)
            {
                var existingNormalized = Path.GetFullPath(existing.InputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(folderPath, existingNormalized, StringComparison.OrdinalIgnoreCase))
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

                // Determine effective output directory
                // If it's the first item, we use the parent of the input folder
                // If the box is already filled, we strictly use that
                string outputDir;
                if (string.IsNullOrWhiteSpace(TxtOutputDir.Text))
                {
                    outputDir = Path.GetDirectoryName(folderPath) ?? folderPath;
                    TxtOutputDir.Text = outputDir;
                    AppendLog($"ℹ️ Output directory anchored to: {outputDir}");
                }
                else
                {
                    outputDir = TxtOutputDir.Text;
                    
                    // Conflict Detection: If the existing output dir is INSIDE this new dump folder,
                    // it will cause a file lock error during conversion.
                    if (outputDir.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(
                            $"⚠️ CONFLICT DETECTED\n\n" +
                            $"The current output directory:\n{outputDir}\n\n" +
                            $"is located INSIDE the game folder you just added:\n{folderPath}\n\n" +
                            $"This will cause conversion to fail because UFS2Tool cannot save the image inside the directory it's reading from.\n\n" +
                            $"Please change the output directory using the 'Browse' button.",
                            "Path Conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                var extension = _settings.OutputFormat == "pfs" ? ".pfs" : ".ffpkg";
                var suggestedName = _settings.OutputFormat == "pfs" 
                    ? Path.GetFileName(folderPath) + ".pfs" 
                    : gameInfo.SuggestedOutputName;

                var outputPath = Path.Combine(outputDir, suggestedName);

                var item = new QueueItem
                {
                    InputPath = folderPath,
                    OutputPath = outputPath,
                    GameInfo = gameInfo,
                    StatusText = "Waiting"
                };

                _queue.Add(item);
                AppendLog($"Added to queue: {gameInfo.TitleName} ({gameInfo.TitleId})");
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

            // Visibility of status indicators
            BtnCancel.Visibility = _isProcessingQueue ? Visibility.Visible : Visibility.Collapsed;
            BtnConvert.Visibility = _isProcessingQueue ? Visibility.Collapsed : Visibility.Visible;
            IconStatusBusy.Visibility = _isProcessingQueue ? Visibility.Visible : Visibility.Collapsed;
            CheckExperimentalFfpkg.Visibility = _settings?.OutputFormat == "ffpkg" ? Visibility.Visible : Visibility.Collapsed;

            // Handle Cancelling state
            if (_isCancelling)
            {
                BtnCancel.IsEnabled = false;
                TxtCancelButton.Text = "Cancelling...";
                BtnCancel.Opacity = 0.7;
            }
            else
            {
                BtnCancel.IsEnabled = true;
                TxtCancelButton.Text = "Cancel";
                BtnCancel.Opacity = 1.0;
            }

            // Update queue header counts
            if (_isProcessingQueue)
            {
                TxtQueueCount.Text = $"({doneCount}/{totalCount} done)";
                TxtConvertButton.Text = "Processing Queue...";
            }
            else if (totalCount > 0)
            {
                TxtQueueCount.Text = $"({totalCount} game{(totalCount == 1 ? "" : "s")})";
                var formatExt = (_settings?.OutputFormat ?? "ffpkg") == "pfs" ? ".pfs" : ".ffpkg";
                TxtConvertButton.Text = waitingCount > 1
                    ? $"Convert {waitingCount} Games to {formatExt}"
                    : $"Convert to {formatExt}";
            }
            else
            {
                TxtQueueCount.Text = "";
                TxtConvertButton.Text = $"Convert to .{_settings?.OutputFormat ?? "ffpkg"}";
            }

            // Master enable/disable
            BtnConvert.IsEnabled = waitingCount > 0 && _ufs2ToolPath != null && !_isProcessingQueue;
            BtnBrowseOutput.IsEnabled = !_isProcessingQueue;
            ComboFormat.IsEnabled = !_isProcessingQueue;
            CheckExperimentalFfpkg.IsEnabled = !_isProcessingQueue;
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
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select PS5 Game Dump Folder(s)",
                Multiselect = true
            };

            if (!string.IsNullOrEmpty(_settings.LastInputDir) && Directory.Exists(_settings.LastInputDir))
                dialog.InitialDirectory = _settings.LastInputDir;

            if (dialog.ShowDialog() == true)
            {
                // dialog.FolderName is the first selected folder
                _settings.LastInputDir = Path.GetDirectoryName(dialog.FolderName);
                
                int added = 0;
                foreach (var folder in dialog.FolderNames)
                {
                    var countBefore = _queue.Count;
                    AddToQueue(folder);
                    if (_queue.Count > countBefore) added++;
                }

                if (added > 1)
                {
                    TxtStatus.Text = $"Added {added} games to queue";
                }
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
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Output Directory for .ffpkg Files"
            };

            if (!string.IsNullOrEmpty(TxtOutputDir.Text) && Directory.Exists(TxtOutputDir.Text))
                dialog.InitialDirectory = TxtOutputDir.Text;
            else if (!string.IsNullOrEmpty(_settings.LastOutputDir) && Directory.Exists(_settings.LastOutputDir))
                dialog.InitialDirectory = _settings.LastOutputDir;

            if (dialog.ShowDialog() == true)
            {
                TxtOutputDir.Text = dialog.FolderName;
                _settings.LastOutputDir = dialog.FolderName;

                // Update output paths for all waiting items
                var extension = _settings.OutputFormat == "pfs" ? ".pfs" : ".ffpkg";
                foreach (var item in _queue.Where(q => q.Status == QueueItemStatus.Waiting))
                {
                    var baseName = _settings.OutputFormat == "pfs" 
                        ? Path.GetFileName(item.InputPath) 
                        : item.GameInfo.TitleId;
                    item.OutputPath = Path.Combine(dialog.FolderName, baseName + extension);
                }
            }
        }

        private void ComboFormat_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboFormat?.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                var format = selectedItem.Tag?.ToString() ?? "ffpkg";
                if (_settings.OutputFormat != format)
                {
                    _settings.OutputFormat = format;
                    SettingsManager.Save(_settings);

                    // Update output paths for all waiting items in queue
                    var extension = format == "pfs" ? ".pfs" : ".ffpkg";
                    var outputDir = TxtOutputDir.Text;
                    if (!string.IsNullOrWhiteSpace(outputDir))
                    {
                        foreach (var item in _queue.Where(q => q.Status == QueueItemStatus.Waiting))
                        {
                            var baseName = format == "pfs" 
                                ? Path.GetFileName(item.InputPath) 
                                : item.GameInfo.TitleId;
                            item.OutputPath = Path.Combine(outputDir, baseName + extension);
                        }
                    }

                    CheckExperimentalFfpkg.Visibility = format == "ffpkg" ? Visibility.Visible : Visibility.Collapsed;
                    UpdateQueueUI();
                }
            }
        }

        private void CheckExperimentalFfpkg_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null && CheckExperimentalFfpkg != null)
            {
                _settings.EnableExperimentalFfpkg = CheckExperimentalFfpkg.IsChecked ?? false;
                SettingsManager.Save(_settings);
            }
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Failed to open link: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        // BATCH CONVERSION
        // ═══════════════════════════════════════════

        private async void BtnConvert_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessingQueue || _ufs2ToolPath == null) return;

            // Set flag and update UI immediately to prevent re-entrancy
            _isProcessingQueue = true;
            UpdateQueueUI();

            var waitingItems = _queue.Where(q => q.Status == QueueItemStatus.Waiting).ToList();
            if (waitingItems.Count == 0)
            {
                _isProcessingQueue = false;
                UpdateQueueUI();
                return;
            }

            // Validate output directory
            var outputDir = TxtOutputDir.Text;
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                MessageBox.Show("Please specify an output directory.", "Missing Info",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                _isProcessingQueue = false;
                UpdateQueueUI();
                return;
            }

            if (!Directory.Exists(outputDir))
            {
                try { Directory.CreateDirectory(outputDir); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Cannot create output directory:\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    _isProcessingQueue = false;
                    UpdateQueueUI();
                    return;
                }
            }

            // Update output paths to use current output dir
            var extension = _settings.OutputFormat == "pfs" ? ".pfs" : ".ffpkg";
            foreach (var item in waitingItems)
            {
                var baseName = _settings.OutputFormat == "pfs" 
                    ? Path.GetFileName(item.InputPath) 
                    : item.GameInfo.TitleId;
                item.OutputPath = Path.Combine(outputDir, baseName + extension);
            }

            // Check for existing files
            var existing = waitingItems.Where(i => File.Exists(i.OutputPath)).ToList();
            if (existing.Count > 0)
            {
                var names = string.Join("\n", existing.Select(i => Path.GetFileName(i.OutputPath)));
                var result = MessageBox.Show(
                    $"The following files already exist and will be overwritten:\n\n{names}\n\nContinue?",
                    "Files Exist", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    _isProcessingQueue = false;
                    UpdateQueueUI();
                    return;
                }
            }

            _cts = new CancellationTokenSource();
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
                    var nextExt = _settings.OutputFormat == "pfs" ? ".pfs" : ".ffpkg";
                    var baseName = _settings.OutputFormat == "pfs" 
                        ? Path.GetFileName(nextItem.InputPath) 
                        : nextItem.GameInfo.TitleId;
                    nextItem.OutputPath = Path.Combine(TxtOutputDir.Text, baseName + nextExt);

                    // Safety check: Is output inside input?
                    if (Path.GetFullPath(nextItem.OutputPath).StartsWith(Path.GetFullPath(nextItem.InputPath), StringComparison.OrdinalIgnoreCase))
                    {
                        nextItem.Status = QueueItemStatus.Error;
                        nextItem.StatusText = "❌ Output path cannot be inside the input folder";
                        AppendLog($"❌ Error: Output file '{nextItem.OutputPath}' is inside its own source directory. This is not allowed as it causes UFS2Tool to collide with itself.");
                        failed++;
                        continue;
                    }

                    nextItem.Status = QueueItemStatus.Processing;
                    nextItem.StatusText = "Optimizing block sizes...";
                    nextItem.Progress = 0;
                    nextItem.ElapsedTime = TimeSpan.Zero;

                    TxtStatus.Text = $"Converting {nextItem.GameInfo.TitleName}... ({completed + 1}/{completed + _queue.Count(q => q.Status == QueueItemStatus.Waiting) + 1})";
                    AppendLog($"\n=== Converting: {nextItem.GameInfo.TitleName} ({nextItem.GameInfo.TitleId}) ===");

                    var sw = Stopwatch.StartNew();
                    using var ctsTime = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    var timeTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!ctsTime.Token.IsCancellationRequested)
                            {
                                await Task.Delay(1000, ctsTime.Token);
                                Dispatcher.Invoke(() => nextItem.ElapsedTime = sw.Elapsed);
                            }
                        }
                        catch (OperationCanceledException) { }
                    });

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

                        var convResult = await converter.ConvertAsync(
                            nextItem.InputPath,
                            nextItem.OutputPath,
                            nextItem.GameInfo.AutoLabel,
                            nextItem.GameInfo.TitleId,
                            _settings.EnableExperimentalFfpkg,
                            _cts.Token);

                        ctsTime.Cancel();
                        await timeTask;
                        sw.Stop();
                        nextItem.ElapsedTime = sw.Elapsed;

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
                    
                    if (completed + failed > 0)
                    {
                        if (failed == 0)
                        {
                            TxtCompleteIcon.Text = "✅";
                            TxtCompleteIcon.Foreground = FindResource("AccentGreenBrush") as SolidColorBrush;
                            TxtCompleteTitle.Text = "Queue Complete";
                            TxtCompleteSummary.Text = $"All {completed} game{(completed == 1 ? "" : "s")} converted successfully!";
                            System.Media.SystemSounds.Asterisk.Play();
                        }
                        else if (completed == 0)
                        {
                            TxtCompleteIcon.Text = "❌";
                            TxtCompleteIcon.Foreground = FindResource("AccentRedBrush") as SolidColorBrush;
                            TxtCompleteTitle.Text = "Conversion Failed";
                            TxtCompleteSummary.Text = $"All {failed} attempt{(failed == 1 ? "" : "s")} failed.\nCheck the log for details.";
                            System.Media.SystemSounds.Hand.Play();
                        }
                        else
                        {
                            TxtCompleteIcon.Text = "⚠️";
                            TxtCompleteIcon.Foreground = FindResource("AccentOrangeBrush") as SolidColorBrush;
                            TxtCompleteTitle.Text = "Queue Complete";
                            TxtCompleteSummary.Text = $"{completed} succeeded, {failed} failed.\nCheck the log for details.";
                            System.Media.SystemSounds.Exclamation.Play();
                        }
                        OverlayComplete.Visibility = Visibility.Visible;
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
                _isCancelling = false;
                _cts?.Dispose();
                _cts = null;
                UpdateQueueUI();
                SettingsManager.Save(_settings);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_isCancelling) return;

            var result = MessageBox.Show(
                "Are you sure you want to cancel the current conversion?\n\nThe current item will be stopped and remaining items will be skipped.",
                "Cancel Conversion", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _isCancelling = true;
                UpdateQueueUI();
                _cts?.Cancel();
            }
        }





        // ═══════════════════════════════════════════
        // LOG
        // ═══════════════════════════════════════════

        private void AppendLog(string message)
        {
            // Always add to raw log for full verbose output
            _rawLog.AppendLine(message);

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

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(_rawLog.ToString());
                TxtStatus.Text = "Log copied to clipboard!";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy to clipboard: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCopyError_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is QueueItem item)
            {
                try
                {
                    System.Windows.Clipboard.SetText(item.StatusText);
                    TxtStatus.Text = "Error details copied to clipboard!";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to copy: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
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

            // DO NOT dedup if it's an error, important warning, or block size test
            if (trimmed.StartsWith("❌") || trimmed.StartsWith("⚠") || trimmed.StartsWith("Error") || 
                trimmed.StartsWith("=== ") || trimmed.StartsWith("Block"))
                return "";

            // Match lines like "Adding files to image...", etc.
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
            _rawLog.Clear();
            _lastLogPrefix = "";
            _lastLogLineStart = -1;
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
                PanelLoading.Visibility = Visibility.Collapsed;
                PanelSetup.Visibility = Visibility.Collapsed;
                AppendLog($"UFS2Tool.exe set to: {dialog.FileName}");
                TxtStatus.Text = "Ready — drag PS5 game dump folders to add to queue";
                RefreshToolStatus();
                UpdateQueueUI();
            }
        }

        private void BtnGetUfs2Tool_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/SvenGDK/UFS2Tool/releases/latest",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open browser: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshToolStatus()
        {
            TxtToolVersion.Text = "UFS2Tool v3.0";
            TxtToolVersion.Foreground = FindResource("TextMutedBrush") as SolidColorBrush;
        }



        // ═══════════════════════════════════════════
        // COMPLETION OVERLAY
        // ═══════════════════════════════════════════

        private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = TxtOutputDir.Text;
                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Failed to open output folder: {ex.Message}");
            }
        }

        private void BtnCompleteDone_Click(object sender, RoutedEventArgs e)
        {
            OverlayComplete.Visibility = Visibility.Collapsed;
        }

    }
}
