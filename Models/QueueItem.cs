using System.ComponentModel;
using System.Runtime.CompilerServices;
using Dump2UfsGui.Services;

namespace Dump2UfsGui.Models
{
    public enum QueueItemStatus
    {
        Waiting,
        Processing,
        Done,
        Error,
        Cancelled
    }

    public class QueueItem : INotifyPropertyChanged
    {
        private QueueItemStatus _status = QueueItemStatus.Waiting;
        private int _progress;
        private string _statusText = "Waiting";

        public string InputPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public GameInfo GameInfo { get; set; } = new();
        public string FolderName => System.IO.Path.GetFileName(InputPath);

        public QueueItemStatus Status
        {
            get => _status;
            set 
            { 
                _status = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(IsWaiting)); 
                OnPropertyChanged(nameof(CanRemove)); 
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(IsProcessing));
                OnPropertyChanged(nameof(IsDone));
            }
        }

        public int Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public bool IsWaiting => _status == QueueItemStatus.Waiting;
        public bool CanRemove => _status == QueueItemStatus.Waiting;
        public bool IsError => _status == QueueItemStatus.Error;
        public bool IsProcessing => _status == QueueItemStatus.Processing;
        public bool IsDone => _status == QueueItemStatus.Done;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
