using Microsoft.Win32;
using RevitSketchPoC.Core.ViewModels;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RevitSketchPoC.Spike1.ViewModels
{
    public sealed class PdfSpike1ViewModel : INotifyPropertyChanged
    {
        private const string PresetRapido = "Rápido";
        private const string PresetBalanceado = "Balanceado";
        private const string PresetAltaPrecisao = "Alta precisão";
        private const string PresetCustomizado = "Customizado";

        private string? _pdfPath;
        private int _pdfPageNumber = 1;
        private bool _isBusy;
        private string? _status;
        private string? _generatedJsonPath;
        private string? _generatedJsonPreview;
        private string? _currentJobId;
        private string? _cleanJsonPath;
        private string? _semanticReadyManifestPath;
        private string? _semanticPixelsPath;
        private string? _tilesDirectoryPath;
        private string _selectedExecutionMode = JobExecutionMode.Auto;
        private string _selectedCalibrationMode = "AutoScale";
        private int _manualScaleDenominator = 100;
        private double _referenceP1XPt;
        private double _referenceP1YPt;
        private double _referenceP2XPt;
        private double _referenceP2YPt;
        private double _referenceDistanceMeters;
        private int _selectedTileSizePt = 256;
        private int _selectedRasterDpi = 300;
        private string _selectedQualityPreset = PresetBalanceado;
        private bool _isApplyingPreset;
        private readonly RelayCommand _generateCommand;
        private readonly RelayCommand _runSemanticCommand;
        private readonly RelayCommand _saveCommand;
        private readonly RelayCommand _openFolderCommand;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<PdfVectorJsonRequest>? GenerateRequested;
        public event EventHandler<SemanticTileInferenceRequest>? RunSemanticRequested;

        public PdfSpike1ViewModel()
        {
            QualityPresetOptions = new ObservableCollection<string>
            {
                PresetRapido,
                PresetBalanceado,
                PresetAltaPrecisao,
                PresetCustomizado
            };
            TileSizeOptions = new ObservableCollection<int> { 192, 256, 384, 512 };
            RasterDpiOptions = new ObservableCollection<int> { 200, 300, 400 };
            ExecutionModeOptions = new ObservableCollection<string> { JobExecutionMode.Auto, JobExecutionMode.Guided };
            CalibrationModeOptions = new ObservableCollection<string> { "AutoScale", "ManualScale", "ReferencePoints" };
            _generateCommand = new RelayCommand(_ => RaiseGenerateRequested(), _ => CanGenerate());
            _runSemanticCommand = new RelayCommand(_ => RaiseRunSemanticRequested(), _ => CanRunSemantic());
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
                _runSemanticCommand.RaiseCanExecuteChanged();
                _saveCommand.RaiseCanExecuteChanged();
                _openFolderCommand.RaiseCanExecuteChanged();
            }
        }

        public ObservableCollection<string> QualityPresetOptions { get; }

        public string SelectedQualityPreset
        {
            get => _selectedQualityPreset;
            set
            {
                if (string.IsNullOrWhiteSpace(value) || value == _selectedQualityPreset)
                {
                    return;
                }

                _selectedQualityPreset = value;
                OnPropertyChanged();
                ApplyQualityPreset(value);
            }
        }

        public ObservableCollection<int> TileSizeOptions { get; }

        public int SelectedTileSizePt
        {
            get => _selectedTileSizePt;
            set
            {
                if (value <= 0)
                {
                    return;
                }

                _selectedTileSizePt = value;
                OnPropertyChanged();
                if (!_isApplyingPreset)
                {
                    SyncPresetFromValues();
                }
            }
        }

        public ObservableCollection<int> RasterDpiOptions { get; }

        public ObservableCollection<string> ExecutionModeOptions { get; }

        public string SelectedExecutionMode
        {
            get => _selectedExecutionMode;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                _selectedExecutionMode = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<string> CalibrationModeOptions { get; }

        public string SelectedCalibrationMode
        {
            get => _selectedCalibrationMode;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                _selectedCalibrationMode = value;
                OnPropertyChanged();
            }
        }

        public int ManualScaleDenominator
        {
            get => _manualScaleDenominator;
            set
            {
                _manualScaleDenominator = value < 1 ? 1 : value;
                OnPropertyChanged();
            }
        }

        public double ReferenceP1XPt
        {
            get => _referenceP1XPt;
            set
            {
                _referenceP1XPt = value;
                OnPropertyChanged();
            }
        }

        public double ReferenceP1YPt
        {
            get => _referenceP1YPt;
            set
            {
                _referenceP1YPt = value;
                OnPropertyChanged();
            }
        }

        public double ReferenceP2XPt
        {
            get => _referenceP2XPt;
            set
            {
                _referenceP2XPt = value;
                OnPropertyChanged();
            }
        }

        public double ReferenceP2YPt
        {
            get => _referenceP2YPt;
            set
            {
                _referenceP2YPt = value;
                OnPropertyChanged();
            }
        }

        public double ReferenceDistanceMeters
        {
            get => _referenceDistanceMeters;
            set
            {
                _referenceDistanceMeters = value;
                OnPropertyChanged();
            }
        }

        public int SelectedRasterDpi
        {
            get => _selectedRasterDpi;
            set
            {
                if (value <= 0)
                {
                    return;
                }

                _selectedRasterDpi = value;
                OnPropertyChanged();
                if (!_isApplyingPreset)
                {
                    SyncPresetFromValues();
                }
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

        public string? CurrentJobId
        {
            get => _currentJobId;
            set
            {
                _currentJobId = value;
                OnPropertyChanged();
            }
        }

        public ICommand BrowsePdfCommand => new RelayCommand(_ => BrowsePdf());
        public ICommand GenerateJsonCommand => _generateCommand;
        public ICommand RunSemanticCommand => _runSemanticCommand;
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

        public void SetGeneratedJson(
            string cleanJsonPath,
            string semanticReadyManifestPath,
            string semanticPixelsPath,
            string tilesDirectoryPath,
            string preview)
        {
            _cleanJsonPath = cleanJsonPath;
            _semanticReadyManifestPath = semanticReadyManifestPath;
            _semanticPixelsPath = semanticPixelsPath;
            _tilesDirectoryPath = tilesDirectoryPath;
            GeneratedJsonPath = cleanJsonPath;
            GeneratedJsonPreview = preview;
            _runSemanticCommand.RaiseCanExecuteChanged();
            _openFolderCommand.RaiseCanExecuteChanged();
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
                _cleanJsonPath = null;
                _semanticReadyManifestPath = null;
                _semanticPixelsPath = null;
                _tilesDirectoryPath = null;
                _runSemanticCommand.RaiseCanExecuteChanged();
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
                PdfPageNumber = PdfPageNumber,
                TileSizePt = SelectedTileSizePt,
                RasterDpi = SelectedRasterDpi
            });
        }

        private bool CanSave()
        {
            return !IsBusy && !string.IsNullOrWhiteSpace(GeneratedJsonPath) && File.Exists(GeneratedJsonPath);
        }

        private bool CanRunSemantic()
        {
            return !IsBusy &&
                   !string.IsNullOrWhiteSpace(_cleanJsonPath) &&
                   !string.IsNullOrWhiteSpace(_semanticReadyManifestPath) &&
                   !string.IsNullOrWhiteSpace(_semanticPixelsPath) &&
                   File.Exists(_cleanJsonPath) &&
                   File.Exists(_semanticReadyManifestPath) &&
                   File.Exists(_semanticPixelsPath);
        }

        private bool CanOpenFolder()
        {
            var path = !string.IsNullOrWhiteSpace(_tilesDirectoryPath) ? _tilesDirectoryPath : GeneratedJsonPath;
            if (IsBusy || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (Directory.Exists(path))
            {
                return true;
            }

            if (File.Exists(path))
            {
                var folder = Path.GetDirectoryName(path);
                return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
            }

            return false;
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
            if (!string.IsNullOrWhiteSpace(_tilesDirectoryPath) && Directory.Exists(_tilesDirectoryPath))
            {
                folder = _tilesDirectoryPath;
            }
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

        private void RaiseRunSemanticRequested()
        {
            if (!CanRunSemantic())
            {
                AppendStatus("Gera os artefactos do Spike 1 primeiro (clean + manifest + semantic_pixels).");
                return;
            }

            RunSemanticRequested?.Invoke(this, new SemanticTileInferenceRequest
            {
                JobId = CurrentJobId ?? string.Empty,
                ExecutionMode = SelectedExecutionMode,
                CleanJsonPath = _cleanJsonPath ?? string.Empty,
                SemanticReadyManifestPath = _semanticReadyManifestPath ?? string.Empty,
                SemanticPixelsPath = _semanticPixelsPath ?? string.Empty,
                MaxSnapDistancePt = 6.0,
                CalibrationMode = SelectedCalibrationMode,
                ManualScaleDenominator = ManualScaleDenominator,
                ReferenceP1XPt = ReferenceP1XPt,
                ReferenceP1YPt = ReferenceP1YPt,
                ReferenceP2XPt = ReferenceP2XPt,
                ReferenceP2YPt = ReferenceP2YPt,
                ReferenceDistanceMeters = ReferenceDistanceMeters
            });
        }

        private void ApplyQualityPreset(string preset)
        {
            _isApplyingPreset = true;
            try
            {
                switch (preset)
                {
                    case PresetRapido:
                        SelectedTileSizePt = 384;
                        SelectedRasterDpi = 200;
                        break;
                    case PresetAltaPrecisao:
                        SelectedTileSizePt = 192;
                        SelectedRasterDpi = 400;
                        break;
                    case PresetCustomizado:
                        break;
                    default:
                        SelectedTileSizePt = 256;
                        SelectedRasterDpi = 300;
                        break;
                }
            }
            finally
            {
                _isApplyingPreset = false;
            }
        }

        private void SyncPresetFromValues()
        {
            var expectedPreset =
                (_selectedTileSizePt, _selectedRasterDpi) switch
                {
                    (384, 200) => PresetRapido,
                    (256, 300) => PresetBalanceado,
                    (192, 400) => PresetAltaPrecisao,
                    _ => PresetCustomizado
                };

            if (expectedPreset != _selectedQualityPreset)
            {
                _selectedQualityPreset = expectedPreset;
                OnPropertyChanged(nameof(SelectedQualityPreset));
            }
        }
    }
}
