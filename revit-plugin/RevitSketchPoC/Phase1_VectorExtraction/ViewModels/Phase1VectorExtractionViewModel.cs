using Microsoft.Win32;
using RevitSketchPoC.Core.ViewModels;
using RevitSketchPoC.Phase1_VectorExtraction.Configuration;
using RevitSketchPoC.Phase1_VectorExtraction.Contracts;
using RevitSketchPoC.Sketch.Contracts;
using RevitSketchPoC.Phase1_VectorExtraction.Services.Export;
using RevitSketchPoC.Phase1_VectorExtraction.Services.Regions;
using RevitSketchPoC.Phase1_VectorExtraction.Views;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RevitSketchPoC.Phase1_VectorExtraction.ViewModels
{
    public sealed class Phase1VectorExtractionViewModel : INotifyPropertyChanged
    {
        private string? _pdfPath;
        private int _pdfPageNumber = 1;
        private bool _isBusy;
        private string? _status;
        private string? _generatedJsonPath;
        private string? _generatedJsonPreview;
        private string? _cleanJsonPath;
        private string? _semanticReadyManifestPath;
        private string? _semanticPixelsPath;
        private string? _tilesDirectoryPath;
        private string? _outputRootPath;
        private string? _indexJsonPath;
        private string _selectedCalibrationMode = "AutoScale";
        private int _manualScaleDenominator = 100;
        private double _referenceP1XPt;
        private double _referenceP1YPt;
        private double _referenceP2XPt;
        private double _referenceP2YPt;
        private double _referenceDistanceMeters;
        private int _selectedTileSizePt = 256;
        private int _selectedRasterDpi = 300;
        private string _selectedQualityPreset = Phase1RasterPresets.PresetBalanced;
        private bool _isApplyingPreset;
        private readonly RelayCommand _generateCommand;
        private readonly RelayCommand _runSemanticCommand;
        private readonly RelayCommand _saveCommand;
        private readonly RelayCommand _openFolderCommand;
        private readonly RelayCommand _exportAllCommand;
        private readonly RelayCommand _openRegionsCommand;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<Phase1ExtractionRequest>? ExtractRequested;
        public event EventHandler<SemanticTileInferenceRequest>? RunSemanticRequested;

        public Phase1VectorExtractionViewModel()
        {
            QualityPresetOptions = new ObservableCollection<string>
            {
                Phase1RasterPresets.PresetFast,
                Phase1RasterPresets.PresetBalanced,
                Phase1RasterPresets.PresetHighPrecision,
                Phase1RasterPresets.PresetCustom
            };
            TileSizeOptions = new ObservableCollection<int> { 192, 256, 384, 512 };
            RasterDpiOptions = new ObservableCollection<int> { 200, 300, 400 };
            CalibrationModeOptions = new ObservableCollection<string> { "AutoScale", "ManualScale", "ReferencePoints" };
            _generateCommand = new RelayCommand(_ => RaiseExtractRequested(), _ => CanGenerate());
            _runSemanticCommand = new RelayCommand(_ => RaiseRunSemanticRequested(), _ => CanRunSemantic());
            _saveCommand = new RelayCommand(_ => SaveGeneratedJson(), _ => CanSave());
            _openFolderCommand = new RelayCommand(_ => OpenOutputFolder(), _ => CanOpenFolder());
            _exportAllCommand = new RelayCommand(_ => ExportAllGeneratedFiles(), _ => CanExportAll());
            _openRegionsCommand = new RelayCommand(_ => OpenRegionEditor(), _ => CanOpenRegionEditor());
        }

        public string? PdfPath
        {
            get => _pdfPath;
            set { _pdfPath = value; OnPropertyChanged(); _generateCommand.RaiseCanExecuteChanged(); }
        }

        public int PdfPageNumber
        {
            get => _pdfPageNumber;
            set { _pdfPageNumber = value < 1 ? 1 : value; OnPropertyChanged(); }
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
                _exportAllCommand.RaiseCanExecuteChanged();
                _openRegionsCommand.RaiseCanExecuteChanged();
            }
        }

        public ObservableCollection<string> QualityPresetOptions { get; }
        public string SelectedQualityPreset
        {
            get => _selectedQualityPreset;
            set
            {
                if (string.IsNullOrWhiteSpace(value) || value == _selectedQualityPreset) return;
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
                if (value <= 0) return;
                _selectedTileSizePt = value;
                OnPropertyChanged();
                if (!_isApplyingPreset) SyncPresetFromValues();
            }
        }

        public ObservableCollection<int> RasterDpiOptions { get; }
        public ObservableCollection<string> CalibrationModeOptions { get; }

        public string SelectedCalibrationMode
        {
            get => _selectedCalibrationMode;
            set { if (!string.IsNullOrWhiteSpace(value)) { _selectedCalibrationMode = value; OnPropertyChanged(); } }
        }

        public int ManualScaleDenominator
        {
            get => _manualScaleDenominator;
            set { _manualScaleDenominator = value < 1 ? 1 : value; OnPropertyChanged(); }
        }

        public double ReferenceP1XPt { get => _referenceP1XPt; set { _referenceP1XPt = value; OnPropertyChanged(); } }
        public double ReferenceP1YPt { get => _referenceP1YPt; set { _referenceP1YPt = value; OnPropertyChanged(); } }
        public double ReferenceP2XPt { get => _referenceP2XPt; set { _referenceP2XPt = value; OnPropertyChanged(); } }
        public double ReferenceP2YPt { get => _referenceP2YPt; set { _referenceP2YPt = value; OnPropertyChanged(); } }
        public double ReferenceDistanceMeters { get => _referenceDistanceMeters; set { _referenceDistanceMeters = value; OnPropertyChanged(); } }

        public int SelectedRasterDpi
        {
            get => _selectedRasterDpi;
            set
            {
                if (value <= 0) return;
                _selectedRasterDpi = value;
                OnPropertyChanged();
                if (!_isApplyingPreset) SyncPresetFromValues();
            }
        }

        public string? Status { get => _status; set { _status = value; OnPropertyChanged(); } }
        public string? GeneratedJsonPath { get => _generatedJsonPath; set { _generatedJsonPath = value; OnPropertyChanged(); _saveCommand.RaiseCanExecuteChanged(); _openFolderCommand.RaiseCanExecuteChanged(); } }
        /// <summary><c>phase1_index.json</c> no output root (mapa modular).</summary>
        public string? IndexJsonPath { get => _indexJsonPath; set { _indexJsonPath = value; OnPropertyChanged(); } }
        public string? GeneratedJsonPreview { get => _generatedJsonPreview; set { _generatedJsonPreview = value; OnPropertyChanged(); } }

        public ICommand BrowsePdfCommand => new RelayCommand(_ => BrowsePdf());
        public ICommand GenerateJsonCommand => _generateCommand;
        public ICommand RunSemanticCommand => _runSemanticCommand;
        public ICommand SaveJsonCommand => _saveCommand;
        public ICommand OpenFolderCommand => _openFolderCommand;
        public ICommand ExportAllOutputsCommand => _exportAllCommand;
        public ICommand OpenRegionEditorCommand => _openRegionsCommand;

        public void AppendStatus(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            Status = string.IsNullOrWhiteSpace(Status) ? line.Trim() : Status + Environment.NewLine + line.Trim();
        }

        public void SetRawOnlyResult(string outputRoot, string rawJsonPath, string preview)
        {
            _outputRootPath = outputRoot;
            _cleanJsonPath = null;
            _semanticReadyManifestPath = null;
            _semanticPixelsPath = null;
            _tilesDirectoryPath = null;
            IndexJsonPath = null;
            GeneratedJsonPath = rawJsonPath;
            GeneratedJsonPreview = preview;
            _runSemanticCommand.RaiseCanExecuteChanged();
            _openFolderCommand.RaiseCanExecuteChanged();
            _exportAllCommand.RaiseCanExecuteChanged();
            _openRegionsCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// raw.json mantém-se como artefacto principal para “Guardar”; clean + semântica permitem o passo seguinte.
        /// </summary>
        public void SetPhase1ModularResult(
            string outputRoot,
            string rawJsonPath,
            string indexJsonPath,
            string cleanJsonPath,
            string semanticReadyManifestPath,
            string semanticPixelsPath,
            string tilesDirectoryPath,
            string preview)
        {
            _outputRootPath = outputRoot;
            _cleanJsonPath = cleanJsonPath;
            _semanticReadyManifestPath = semanticReadyManifestPath;
            _semanticPixelsPath = semanticPixelsPath;
            _tilesDirectoryPath = tilesDirectoryPath;
            GeneratedJsonPath = rawJsonPath;
            GeneratedJsonPreview = preview;
            IndexJsonPath = indexJsonPath;
            _runSemanticCommand.RaiseCanExecuteChanged();
            _openFolderCommand.RaiseCanExecuteChanged();
            _exportAllCommand.RaiseCanExecuteChanged();
            _openRegionsCommand.RaiseCanExecuteChanged();
        }

        private void BrowsePdf()
        {
            var dialog = new OpenFileDialog { Filter = "PDF|*.pdf|All files|*.*" };
            if (dialog.ShowDialog() != true) return;
            PdfPath = dialog.FileName;
            Status = null;
            GeneratedJsonPath = null;
            GeneratedJsonPreview = null;
            _cleanJsonPath = null;
            _semanticReadyManifestPath = null;
            _semanticPixelsPath = null;
            _tilesDirectoryPath = null;
            _outputRootPath = null;
            IndexJsonPath = null;
            _runSemanticCommand.RaiseCanExecuteChanged();
            _exportAllCommand.RaiseCanExecuteChanged();
            _openRegionsCommand.RaiseCanExecuteChanged();
        }

        private bool CanGenerate() => !IsBusy && !string.IsNullOrWhiteSpace(PdfPath) && File.Exists(PdfPath!);

        private bool CanOpenRegionEditor() =>
            !IsBusy &&
            !string.IsNullOrWhiteSpace(_outputRootPath) &&
            Directory.Exists(_outputRootPath) &&
            File.Exists(Path.Combine(_outputRootPath, Phase1ArtifactLayout.PreviewPagePngRelative()));

        private void OpenRegionEditor()
        {
            if (!CanOpenRegionEditor())
            {
                AppendStatus("Gera a Fase 1 primeiro (precisas de raster/preview/page.png).");
                return;
            }

            try
            {
                var editor = new Phase1RegionEditorWindow(_outputRootPath!);
                editor.Owner = System.Windows.Application.Current?.Windows
                    .OfType<Phase1VectorExtractionWindow>()
                    .FirstOrDefault();
                editor.ShowDialog();
                AppendStatus("Editor de zonas fechado.");
            }
            catch (Exception ex)
            {
                AppendStatus("Não foi possível abrir o editor de zonas: " + ex.Message);
            }
        }

        private void RaiseExtractRequested()
        {
            if (!CanGenerate()) { Status = "Seleciona um PDF válido primeiro."; return; }
            var settings = new Phase1RasterSettings();
            Phase1RasterPresets.ApplyPreset(_selectedQualityPreset, settings);
            if (_selectedQualityPreset == Phase1RasterPresets.PresetCustom)
            {
                settings.TileSizePt = SelectedTileSizePt;
                settings.AiRasterDpi = SelectedRasterDpi;
            }

            ExtractRequested?.Invoke(this, new Phase1ExtractionRequest
            {
                PdfPath = PdfPath ?? string.Empty,
                PdfPageNumber = PdfPageNumber,
                TileSizePt = settings.TileSizePt,
                AiRasterDpi = settings.AiRasterDpi,
                PreviewRasterDpi = settings.PreviewRasterDpi,
                OcrRasterDpi = settings.OcrRasterDpi,
                UltraRasterDpi = settings.UltraRasterDpi
            });
        }

        private bool CanSave() => !IsBusy && !string.IsNullOrWhiteSpace(GeneratedJsonPath) && File.Exists(GeneratedJsonPath!);
        private bool CanRunSemantic() =>
            !IsBusy &&
            !string.IsNullOrWhiteSpace(_cleanJsonPath) &&
            File.Exists(_cleanJsonPath!) &&
            File.Exists(_semanticReadyManifestPath ?? string.Empty) &&
            File.Exists(_semanticPixelsPath ?? string.Empty);

        private bool CanOpenFolder()
        {
            if (IsBusy) return false;
            if (!string.IsNullOrWhiteSpace(_outputRootPath) && Directory.Exists(_outputRootPath)) return true;
            return CanSave() || (!string.IsNullOrWhiteSpace(_tilesDirectoryPath) && Directory.Exists(_tilesDirectoryPath));
        }

        private bool CanExportAll() =>
            !IsBusy &&
            !string.IsNullOrWhiteSpace(_outputRootPath) &&
            Directory.Exists(_outputRootPath) &&
            (File.Exists(Path.Combine(_outputRootPath, Phase1ArtifactLayout.RawRootLegacy)) ||
             File.Exists(Path.Combine(_outputRootPath, Phase1ArtifactLayout.IndexFileName)));

        private void ExportAllGeneratedFiles()
        {
            if (!CanExportAll())
            {
                Status = "Só podes exportar após gerar a Fase 1 com sucesso (raw.json ou phase1_index.json).";
                return;
            }

            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Escolhe a pasta onde copiar todo o output da Fase 1 (JSON, raster, parquet, etc.)",
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                return;
            }

            try
            {
                var result = Phase1OutputFolderExporter.CopyTo(_outputRootPath!, dialog.SelectedPath);
                AppendStatus(
                    "Exportação completa: " + result.FileCount + " ficheiros em " + result.DirectoryCount +
                    " pastas → " + result.DestinationRoot);
            }
            catch (Exception ex)
            {
                AppendStatus("Falha ao exportar output: " + ex.Message);
            }
        }

        private void SaveGeneratedJson()
        {
            if (!CanSave()) { Status = "Não há JSON gerado para guardar."; return; }
            var dialog = new SaveFileDialog { Filter = "JSON|*.json|All files|*.*", FileName = Path.GetFileName(GeneratedJsonPath) };
            if (dialog.ShowDialog() == true)
            {
                File.Copy(GeneratedJsonPath!, dialog.FileName, overwrite: true);
                AppendStatus("JSON guardado em: " + dialog.FileName);
            }
        }

        private void OpenOutputFolder()
        {
            if (!CanOpenFolder()) { Status = "Não há pasta de output disponível."; return; }
            var folder = !string.IsNullOrWhiteSpace(_outputRootPath) && Directory.Exists(_outputRootPath)
                ? _outputRootPath
                : Path.GetDirectoryName(GeneratedJsonPath);
            if (string.IsNullOrWhiteSpace(folder)) { Status = "Não foi possível resolver a pasta de output."; return; }
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            AppendStatus("Pasta de output aberta: " + folder);
        }

        private void RaiseRunSemanticRequested()
        {
            if (!CanRunSemantic())
            {
                AppendStatus("Executa a Fase 1 primeiro (clean + manifest + semantic_pixels).");
                return;
            }

            RunSemanticRequested?.Invoke(this, new SemanticTileInferenceRequest
            {
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
                var s = new Phase1RasterSettings();
                Phase1RasterPresets.ApplyPreset(preset, s);
                if (preset != Phase1RasterPresets.PresetCustom)
                {
                    SelectedTileSizePt = s.TileSizePt;
                    SelectedRasterDpi = s.AiRasterDpi;
                }
            }
            finally { _isApplyingPreset = false; }
        }

        private void SyncPresetFromValues()
        {
            var expected = (_selectedTileSizePt, _selectedRasterDpi) switch
            {
                (384, 200) => Phase1RasterPresets.PresetFast,
                (256, 300) => Phase1RasterPresets.PresetBalanced,
                (192, 400) => Phase1RasterPresets.PresetHighPrecision,
                _ => Phase1RasterPresets.PresetCustom
            };
            if (expected != _selectedQualityPreset)
            {
                _selectedQualityPreset = expected;
                OnPropertyChanged(nameof(SelectedQualityPreset));
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
