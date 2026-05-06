using Microsoft.Win32;
using RevitSketchPoC.Core.ViewModels;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RevitSketchPoC.Sketch.ViewModels
{
    public sealed class SketchUploadViewModel : INotifyPropertyChanged
    {
        private string? _inputPath;
        private string? _status;
        private string _prompt = "Create walls, rooms and doors from this sketch.";
        private bool _autoCreateRooms = true;
        private bool _autoCreateDoors = true;
        private bool _autoCreateWindows = true;
        private bool _autoCreateFloors = true;
        private bool _showInterpretationPreview = true;
        private bool _isBusy;
        private readonly RelayCommand _runCommand;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<SketchToBimRequest>? RunRequested;

        public SketchUploadViewModel()
        {
            _runCommand = new RelayCommand(_ => RaiseRunRequested(), _ => CanRun());
        }

        public string? InputPath
        {
            get => _inputPath;
            set
            {
                _inputPath = value;
                OnPropertyChanged();
                _runCommand.RaiseCanExecuteChanged();
            }
        }

        public string? Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
            }
        }

        public void ClearStatus()
        {
            Status = null;
        }

        public void AppendStatus(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            Status = string.IsNullOrWhiteSpace(Status) ? line.Trim() : Status + Environment.NewLine + line.Trim();
        }

        public string Prompt
        {
            get => _prompt;
            set
            {
                _prompt = value;
                OnPropertyChanged();
            }
        }

        public bool AutoCreateRooms
        {
            get => _autoCreateRooms;
            set
            {
                _autoCreateRooms = value;
                OnPropertyChanged();
            }
        }

        public bool AutoCreateDoors
        {
            get => _autoCreateDoors;
            set
            {
                _autoCreateDoors = value;
                OnPropertyChanged();
            }
        }

        public bool AutoCreateWindows
        {
            get => _autoCreateWindows;
            set
            {
                _autoCreateWindows = value;
                OnPropertyChanged();
            }
        }

        public bool AutoCreateFloors
        {
            get => _autoCreateFloors;
            set
            {
                _autoCreateFloors = value;
                OnPropertyChanged();
            }
        }

        /// <summary>When true, shows a side-by-side preview of the image vs. LLM interpretation before creating elements in Revit.</summary>
        public bool ShowInterpretationPreview
        {
            get => _showInterpretationPreview;
            set
            {
                _showInterpretationPreview = value;
                OnPropertyChanged();
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                OnPropertyChanged();
                _runCommand.RaiseCanExecuteChanged();
            }
        }

        public ICommand BrowseCommand => new RelayCommand(_ => Browse());
        public ICommand RunCommand => _runCommand;

        private void Browse()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                InputPath = dialog.FileName;
                Status = null;
            }
        }

        private bool CanRun()
        {
            return !IsBusy && !string.IsNullOrWhiteSpace(InputPath) && File.Exists(InputPath);
        }

        private void RaiseRunRequested()
        {
            if (!CanRun())
            {
                Status = "Select a valid image first.";
                return;
            }

            RunRequested?.Invoke(this, new SketchToBimRequest
            {
                ImagePath = InputPath,
                Prompt = Prompt,
                AutoCreateRooms = AutoCreateRooms,
                AutoCreateDoors = AutoCreateDoors,
                AutoCreateWindows = AutoCreateWindows,
                AutoCreateFloors = AutoCreateFloors,
                ShowPreviewUi = ShowInterpretationPreview
            });
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
