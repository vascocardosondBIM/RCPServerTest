using RevitSketchPoC.Phase1_VectorExtraction.Contracts;
using RevitSketchPoC.Phase1_VectorExtraction.Services;
using RevitSketchPoC.Phase1_VectorExtraction.Services.Regions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RevitSketchPoC.Phase1_VectorExtraction.Views
{
    public sealed class Phase1RegionEditorWindow : Window
    {
        private static readonly Brush[] RegionBrushes =
        {
            new SolidColorBrush(Color.FromArgb(90, 37, 99, 235)),
            new SolidColorBrush(Color.FromArgb(90, 16, 185, 129)),
            new SolidColorBrush(Color.FromArgb(90, 245, 158, 11)),
            new SolidColorBrush(Color.FromArgb(90, 239, 68, 68)),
            new SolidColorBrush(Color.FromArgb(90, 139, 92, 246)),
            new SolidColorBrush(Color.FromArgb(90, 236, 72, 153))
        };

        private readonly string _outputRoot;
        private readonly string _previewPath;
        private readonly PageRegionExportService.PageDimensions _dims;
        private readonly ObservableCollection<RegionRow> _regions = new ObservableCollection<RegionRow>();
        private readonly ListBox _regionList;
        private readonly TextBlock _hintText;
        private readonly TextBlock _statusText;
        private readonly TextBox _summaryBox;
        private readonly Grid _surface;
        private readonly Image _image;
        private readonly Canvas _overlay;

        private Point? _dragStart;
        private Rectangle? _rubberBand;
        private int _regionCounter;

        /// <summary>True se o utilizador exportou zonas nesta sessão (para refrescar o resumo na janela principal).</summary>
        public bool RegionsWereExported { get; private set; }

        /// <summary>True após exportação por cor (PNG PyMuPDF por pasta).</summary>
        public bool ColorLayersWereExported { get; private set; }

        public Phase1RegionEditorWindow(string outputRoot)
        {
            _outputRoot = outputRoot ?? throw new ArgumentNullException(nameof(outputRoot));
            _previewPath = PageRegionExportService.GetPreviewPngPath(outputRoot);
            _dims = PageRegionExportService.ReadPageDimensions(outputRoot);

            Title = "Definir zonas da folha";
            Width = 1100;
            Height = 780;
            MinWidth = 800;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(241, 245, 249));
            FontFamily = new FontFamily("Segoe UI");

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock
            {
                Text = "Recorta zonas sobre o preview",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
            });
            _hintText = new TextBlock
            {
                Text = BuildHint(),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 4, 0, 0)
            };
            header.Children.Add(_hintText);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });

            var previewBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4)
            };

            _surface = new Grid { ClipToBounds = true };
            _image = new Image
            {
                Stretch = Stretch.Uniform,
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(_previewPath, UriKind.Absolute))
            };
            _overlay = new Canvas { Background = Brushes.Transparent };
            _surface.Children.Add(_image);
            _surface.Children.Add(_overlay);
            _surface.SizeChanged += (_, _) => RedrawRegionOverlays();
            _surface.MouseLeftButtonDown += OnSurfaceMouseDown;
            _surface.MouseMove += OnSurfaceMouseMove;
            _surface.MouseLeftButtonUp += OnSurfaceMouseUp;
            previewBorder.Child = _surface;
            Grid.SetColumn(previewBorder, 0);
            body.Children.Add(previewBorder);

            var side = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            side.Children.Add(new TextBlock
            {
                Text = "Zonas",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            _regionList = new ListBox
            {
                ItemsSource = _regions,
                DisplayMemberPath = nameof(RegionRow.Display),
                Height = 280,
                Margin = new Thickness(0, 0, 0, 8)
            };
            side.Children.Add(_regionList);

            side.Children.Add(MakeButton("Preset: 2 colunas (≈65% / 35%)", (_, _) => OnPresetTwoColumns()));
            side.Children.Add(MakeButton("Remover zona seleccionada", (_, _) => OnRemoveSelected()));
            side.Children.Add(MakeButton("Limpar todas", (_, _) => OnClearAll()));
            side.Children.Add(MakeButton("Exportar por cor (zona seleccionada)", (_, _) => OnExportSelectedByColor()));
            side.Children.Add(MakeButton("Exportar por cor (todas as zonas)", (_, _) => OnExportAllByColor()));
            side.Children.Add(new TextBlock
            {
                Text = "Resumo (após exportar)",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 6),
                FontSize = 12
            });
            _summaryBox = new TextBox
            {
                Height = 200,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252))
            };
            side.Children.Add(_summaryBox);
            TryShowFullPageSummary();
            Grid.SetColumn(side, 1);
            body.Children.Add(side);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            _statusText = new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 28, 28)),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(_statusText, 2);
            root.Children.Add(_statusText);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            footer.Children.Add(MakePrimaryButton("Exportar zonas (PNG + JSON)", OnExport));
            footer.Children.Add(MakeSecondaryButton("Fechar", (_, _) => Close()));
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            Content = root;
            Loaded += (_, _) => TryLoadSavedRegions();
        }

        private string BuildHint()
        {
            var rotNote = _dims.RotationDegrees != 0
                ? " Atenção: a folha tinha rotação " + _dims.RotationDegrees + "°; o JSON clean está desrotado — confirma o alinhamento visual."
                : string.Empty;
            return "Arrasta com o rato sobre a imagem para desenhar um retângulo. Podes criar várias zonas (2, 3, …)." + rotNote;
        }

        private void TryLoadSavedRegions()
        {
            var path = System.IO.Path.Combine(_outputRoot, Configuration.Phase1ArtifactLayout.PageRegionsFileName);
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                if (jo["regions"] is not Newtonsoft.Json.Linq.JArray arr)
                {
                    return;
                }

                foreach (var item in arr)
                {
                    if (item is not Newtonsoft.Json.Linq.JObject obj)
                    {
                        continue;
                    }

                    var norm = obj["bbox_norm"]?.ToObject<double[]>();
                    if (norm == null || norm.Length < 4)
                    {
                        continue;
                    }

                    _regions.Add(new RegionRow
                    {
                        Id = obj["id"]?.ToString() ?? "zone",
                        Label = obj["label"]?.ToString() ?? obj["id"]?.ToString() ?? "Zona",
                        BboxNorm = norm
                    });
                }

                RedrawRegionOverlays();
                SetStatus("Carregadas " + _regions.Count + " zonas de page_regions.json.");
            }
            catch (Exception ex)
            {
                SetStatus("Não foi possível carregar zonas guardadas: " + ex.Message);
            }
        }

        private void OnPresetTwoColumns()
        {
            _regions.Clear();
            _regionCounter = 0;
            AddRegion("drawing", "Desenho", 0, 0, 0.65, 1);
            AddRegion("sheet_info", "Legendas / info", 0.65, 0, 1, 1);
            RedrawRegionOverlays();
            SetStatus("Preset aplicado. Ajusta os retângulos arrastando novas zonas se necessário.");
        }

        private void OnRemoveSelected()
        {
            if (_regionList.SelectedItem is RegionRow row)
            {
                _regions.Remove(row);
                RedrawRegionOverlays();
            }
        }

        private void OnClearAll()
        {
            _regions.Clear();
            RedrawRegionOverlays();
        }

        private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(_surface);
            _surface.CaptureMouse();
            _rubberBand = new Rectangle
            {
                Stroke = Brushes.DodgerBlue,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(40, 37, 99, 235))
            };
            Canvas.SetLeft(_rubberBand, _dragStart.Value.X);
            Canvas.SetTop(_rubberBand, _dragStart.Value.Y);
            _overlay.Children.Add(_rubberBand);
        }

        private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStart == null || _rubberBand == null)
            {
                return;
            }

            var p = e.GetPosition(_surface);
            var x = Math.Min(_dragStart.Value.X, p.X);
            var y = Math.Min(_dragStart.Value.Y, p.Y);
            var w = Math.Abs(p.X - _dragStart.Value.X);
            var h = Math.Abs(p.Y - _dragStart.Value.Y);
            Canvas.SetLeft(_rubberBand, x);
            Canvas.SetTop(_rubberBand, y);
            _rubberBand.Width = w;
            _rubberBand.Height = h;
        }

        private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragStart == null)
            {
                return;
            }

            _surface.ReleaseMouseCapture();
            var p = e.GetPosition(_surface);
            var norm = CanvasRectToNorm(
                Math.Min(_dragStart.Value.X, p.X),
                Math.Min(_dragStart.Value.Y, p.Y),
                Math.Abs(p.X - _dragStart.Value.X),
                Math.Abs(p.Y - _dragStart.Value.Y));

            if (_rubberBand != null)
            {
                _overlay.Children.Remove(_rubberBand);
                _rubberBand = null;
            }

            _dragStart = null;

            if (norm[2] - norm[0] < 0.02 || norm[3] - norm[1] < 0.02)
            {
                SetStatus("Zona demasiado pequena — arrasta um retângulo maior.");
                return;
            }

            _regionCounter++;
            var id = "zone_" + _regionCounter.ToString(CultureInfo.InvariantCulture);
            AddRegion(id, "Zona " + _regionCounter, norm[0], norm[1], norm[2], norm[3]);
            RedrawRegionOverlays();
            SetStatus("Zona adicionada. Total: " + _regions.Count + ".");
        }

        private void AddRegion(string id, string label, double x0, double y0, double x1, double y1)
        {
            _regions.Add(new RegionRow
            {
                Id = id,
                Label = label,
                BboxNorm = new[] { x0, y0, x1, y1 }
            });
        }

        private double[] CanvasRectToNorm(double x, double y, double w, double h)
        {
            var sw = _surface.ActualWidth;
            var sh = _surface.ActualHeight;
            if (sw < 1 || sh < 1)
            {
                return new double[] { 0, 0, 1, 1 };
            }

            var img = _image.Source as System.Windows.Media.Imaging.BitmapSource;
            if (img == null)
            {
                return new[]
                {
                    x / sw, y / sh, (x + w) / sw, (y + h) / sh
                };
            }

            var iw = img.PixelWidth;
            var ih = img.PixelHeight;
            var scale = Math.Min(sw / iw, sh / ih);
            var dispW = iw * scale;
            var dispH = ih * scale;
            var offX = (sw - dispW) / 2;
            var offY = (sh - dispH) / 2;

            double N(double px, double total, double offset) =>
                Math.Max(0, Math.Min(1, (px - offset) / total));

            return new[]
            {
                N(x, dispW, offX),
                N(y, dispH, offY),
                N(x + w, dispW, offX),
                N(y + h, dispH, offY)
            };
        }

        private void RedrawRegionOverlays()
        {
            _overlay.Children.Clear();
            var i = 0;
            foreach (var region in _regions)
            {
                var rect = NormToCanvasRect(region.BboxNorm);
                if (rect == null)
                {
                    continue;
                }

                var brush = RegionBrushes[i % RegionBrushes.Length];
                var r = new Rectangle
                {
                    Width = rect.Value.Width,
                    Height = rect.Value.Height,
                    Fill = brush,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.5,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(r, rect.Value.X);
                Canvas.SetTop(r, rect.Value.Y);
                _overlay.Children.Add(r);

                var label = new TextBlock
                {
                    Text = region.Label,
                    Foreground = Brushes.Black,
                    Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                    Padding = new Thickness(4, 2, 4, 2),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(label, rect.Value.X + 4);
                Canvas.SetTop(label, rect.Value.Y + 4);
                _overlay.Children.Add(label);
                i++;
            }
        }

        private Rect? NormToCanvasRect(double[] norm)
        {
            var sw = _surface.ActualWidth;
            var sh = _surface.ActualHeight;
            if (sw < 1 || sh < 1 || norm.Length < 4)
            {
                return null;
            }

            var img = _image.Source as System.Windows.Media.Imaging.BitmapSource;
            if (img == null)
            {
                return new Rect(norm[0] * sw, norm[1] * sh, (norm[2] - norm[0]) * sw, (norm[3] - norm[1]) * sh);
            }

            var iw = img.PixelWidth;
            var ih = img.PixelHeight;
            var scale = Math.Min(sw / iw, sh / ih);
            var dispW = iw * scale;
            var dispH = ih * scale;
            var offX = (sw - dispW) / 2;
            var offY = (sh - dispH) / 2;

            return new Rect(
                offX + norm[0] * dispW,
                offY + norm[1] * dispH,
                (norm[2] - norm[0]) * dispW,
                (norm[3] - norm[1]) * dispH);
        }

        private void OnExportSelectedByColor()
        {
            if (_regionList.SelectedItem is not RegionRow row)
            {
                SetStatus("Selecciona uma zona na lista.");
                return;
            }

            try
            {
                SetStatus("A exportar PNG por cor (PyMuPDF) para «" + row.Label + "»…");
                var bboxPt = BboxNormToPt(row.BboxNorm);
                var result = PageRegionColorExportService.ExportRegion(_outputRoot, row.Id, bboxPt);
                ColorLayersWereExported = true;
                RegionsWereExported = true;
                SetStatus(
                    "Por cor: " + result.ColorHexKeys.Count + " cores em " + result.ByColorRoot);
                TryShowFullPageSummary();
                MessageBox.Show(
                    "Exportadas " + result.ColorHexKeys.Count + " cores.\n" + result.ByColorRoot,
                    "Exportar por cor",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SetStatus("Falha exportação por cor: " + ex.Message);
            }
        }

        private void OnExportAllByColor()
        {
            if (_regions.Count == 0)
            {
                SetStatus("Define pelo menos uma zona.");
                return;
            }

            try
            {
                var totalColors = 0;
                foreach (var row in _regions.ToList())
                {
                    SetStatus("Por cor: «" + row.Label + "»…");
                    var bboxPt = BboxNormToPt(row.BboxNorm);
                    var result = PageRegionColorExportService.ExportRegion(_outputRoot, row.Id, bboxPt);
                    totalColors += result.ColorHexKeys.Count;
                }

                ColorLayersWereExported = true;
                RegionsWereExported = true;
                SetStatus("Por cor concluído: " + totalColors + " pastas de cor no total.");
                TryShowFullPageSummary();
            }
            catch (Exception ex)
            {
                SetStatus("Falha exportação por cor: " + ex.Message);
            }
        }

        private double[] BboxNormToPt(double[] norm)
        {
            return new[]
            {
                norm[0] * _dims.WidthPt,
                norm[1] * _dims.HeightPt,
                norm[2] * _dims.WidthPt,
                norm[3] * _dims.HeightPt
            };
        }

        private void OnExport(object sender, RoutedEventArgs e)
        {
            if (_regions.Count == 0)
            {
                SetStatus("Adiciona pelo menos uma zona (arrasta na imagem ou usa o preset).");
                return;
            }

            try
            {
                var request = new PageRegionsExportRequest
                {
                    OutputRoot = _outputRoot,
                    Regions = _regions.Select(r => new PageRegionDefinition
                    {
                        Id = r.Id,
                        Label = r.Label,
                        BboxNorm = r.BboxNorm
                    }).ToList()
                };

                var result = PageRegionExportService.Export(request);
                RegionsWereExported = true;
                SetStatus(
                    "Exportação concluída: " + result.RegionIds.Count + " zonas, " +
                    result.TotalEntitiesExported + " entidades filtradas. " +
                    result.PageRegionsJsonPath);

                TryShowFullPageSummary();

                MessageBox.Show(
                    "Zonas exportadas para:\n" + System.IO.Path.Combine(_outputRoot, Configuration.Phase1ArtifactLayout.RegionsRootDir) +
                    "\n\nÍndice: page_regions.json\n\nVê o resumo no painel à direita.",
                    "Zonas",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SetStatus("Falha na exportação: " + ex.Message);
            }
        }

        private void TryShowFullPageSummary()
        {
            try
            {
                var summary = Phase1ExtractionSummaryService.BuildFromOutputRoot(_outputRoot);
                _summaryBox.Text = Phase1ExtractionSummaryService.FormatAsText(summary);
            }
            catch (Exception ex)
            {
                _summaryBox.Text = "Resumo indisponível: " + ex.Message;
            }
        }

        private void SetStatus(string text) => _statusText.Text = text;

        private static Button MakeButton(string text, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content = text,
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            b.Click += click;
            return b;
        }

        private static Button MakePrimaryButton(string text, RoutedEventHandler click)
        {
            var b = MakeButton(text, click);
            b.Margin = new Thickness(0, 0, 10, 0);
            b.FontWeight = FontWeights.SemiBold;
            b.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            b.Foreground = Brushes.White;
            return b;
        }

        private static Button MakeSecondaryButton(string text, RoutedEventHandler click) =>
            MakeButton(text, click);

        private sealed class RegionRow
        {
            public string Id { get; set; } = string.Empty;
            public string Label { get; set; } = string.Empty;
            public double[] BboxNorm { get; set; } = { 0, 0, 1, 1 };
            public string Display => Label + "  [" + FormatNorm(BboxNorm) + "]";
        }

        private static string FormatNorm(double[] n) =>
            string.Format(CultureInfo.InvariantCulture, "{0:F2}-{1:F2} … {2:F2}-{3:F2}", n[0], n[1], n[2], n[3]);
    }
}
