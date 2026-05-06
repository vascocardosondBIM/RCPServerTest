using Microsoft.Win32;
using RevitSketchPoC.Core.ViewModels;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RevitSketchPoC.Spike1.ViewModels
{
    public sealed class PdfSpike1ViewModel : INotifyPropertyChanged
    {
        private string? _pdfPath;
        private int _pdfPageNumber = 1;
        private bool _isBusy;
        private string? _status;
        private string? _generatedJsonPath;
        private string? _generatedJsonPreview;
        private readonly RelayCommand _generateCommand;
        private readonly RelayCommand _saveCommand;
        private readonly RelayCommand _openFolderCommand;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<PdfVectorJsonRequest>? GenerateRequested;

        public PdfSpike1ViewModel()
        {
            _generateCommand = new RelayCommand(_ => RaiseGenerateRequested(), _ => CanGenerate());
            _saveCommand = new RelayCommand(_ => SaveGeneratedJson(), _ => CanSave());
            _openFolderCommand = new RelayCommand(_ => OpenJsonFolder(), _ => CanOpenFolder());
        }

        public string? PdfPath
        {
            get => _pdfPath;
            set
            {
                _pdfPath = value;
                OnPropertyChanged();
                _generateCommand.RaiseCanExecuteChanged();
            }
        }

        public int PdfPageNumber
        {
            get => _pdfPageNumber;
            set
            {
                _pdfPageNumber = value < 1 ? 1 : value;
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
                _generateCommand.RaiseCanExecuteChanged();
                _saveCommand.RaiseCanExecuteChanged();
                _openFolderCommand.RaiseCanExecuteChanged();
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

        public string? GeneratedJsonPath
        {
            get => _generatedJsonPath;
            set
            {
                _generatedJsonPath = value;
                OnPropertyChanged();
                _saveCommand.RaiseCanExecuteChanged();
                _openFolderCommand.RaiseCanExecuteChanged();
            }
        }

        public string? GeneratedJsonPreview
        {
            get => _generatedJsonPreview;
            set
            {
                _generatedJsonPreview = value;
                OnPropertyChanged();
            }
        }

        public ICommand BrowsePdfCommand => new RelayCommand(_ => BrowsePdf());
        public ICommand GenerateJsonCommand => _generateCommand;
        public ICommand SaveJsonCommand => _saveCommand;
        public ICommand OpenFolderCommand => _openFolderCommand;

        public void AppendStatus(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            Status = string.IsNullOrWhiteSpace(Status) ? line.Trim() : Status + Environment.NewLine + line.Trim();
        }

        public void SetGeneratedJson(string jsonPath, string preview)
        {
            GeneratedJsonPath = jsonPath;
            GeneratedJsonPreview = preview;
        }

        private void BrowsePdf()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "PDF|*.pdf|All files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                PdfPath = dialog.FileName;
                Status = null;
                GeneratedJsonPath = null;
                GeneratedJsonPreview = null;
            }
        }

        private bool CanGenerate()
        {
            return !IsBusy && !string.IsNullOrWhiteSpace(PdfPath) && File.Exists(PdfPath);
        }

        private void RaiseGenerateRequested()
        {
            if (!CanGenerate())
            {
                Status = "Seleciona um PDF válido primeiro.";
                return;
            }

            GenerateRequested?.Invoke(this, new PdfVectorJsonRequest
            {
                PdfPath = PdfPath ?? string.Empty,
                PdfPageNumber = PdfPageNumber
            });
        }

        private bool CanSave()
        {
            return !IsBusy && !string.IsNullOrWhiteSpace(GeneratedJsonPath) && File.Exists(GeneratedJsonPath);
        }

        private bool CanOpenFolder()
        {
            if (IsBusy || string.IsNullOrWhiteSpace(GeneratedJsonPath) || !File.Exists(GeneratedJsonPath))
            {
                return false;
            }

            var folder = Path.GetDirectoryName(GeneratedJsonPath);
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        }

        private void SaveGeneratedJson()
        {
            if (!CanSave())
            {
                Status = "Não há JSON gerado para guardar.";
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "JSON|*.json|All files|*.*",
                FileName = Path.GetFileName(GeneratedJsonPath)
            };

            if (dialog.ShowDialog() == true)
            {
                File.Copy(GeneratedJsonPath!, dialog.FileName, overwrite: true);
                AppendStatus("JSON guardado em: " + dialog.FileName);
            }
        }

        private void OpenJsonFolder()
        {
            if (!CanOpenFolder())
            {
                Status = "Não há pasta de JSON disponível para abrir.";
                return;
            }

            var folder = Path.GetDirectoryName(GeneratedJsonPath);
            if (string.IsNullOrWhiteSpace(folder))
            {
                Status = "Não foi possível resolver a pasta do JSON.";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });

            AppendStatus("Pasta dos JSONs aberta: " + folder);
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
