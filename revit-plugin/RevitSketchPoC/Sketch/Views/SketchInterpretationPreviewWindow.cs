using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace RevitSketchPoC.Sketch.Views
{
    /// <summary>
    /// Compares the source sketch image with a vector preview of what the LLM parsed, before committing to Revit.
    /// </summary>
    public sealed class SketchInterpretationPreviewWindow : Window
    {
        private const double CanvasLogicalSize = 520.0;

        /// <summary>Returns true if the user accepts the interpretation; false if cancelled.</summary>
        public static bool ConfirmWithUser(Window owner, SketchToBimRequest request, SketchInterpretation interpretation)
        {
            var dlg = new SketchInterpretationPreviewWindow(request, interpretation)
            {
                Owner = owner
            };
            return dlg.ShowDialog() == true;
        }

        private SketchInterpretationPreviewWindow(SketchToBimRequest request, SketchInterpretation interpretation)
        {
            Title = "Confirmar interpretação do sketch";
            Width = 960;
            Height = 640;
            MinWidth = 720;
            MinHeight = 480;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
            FontFamily = new FontFamily("Segoe UI");

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.Children.Add(new TextBlock
            {
                Text = "A tua imagem",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Margin = new Thickness(0, 0, 0, 8)
            });
            var t1 = new TextBlock
            {
                Text = "Interpretação (paredes a ciano, portas a amarelo, divisões a laranja)",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Margin = new Thickness(8, 0, 0, 8)
            };
            Grid.SetColumn(t1, 1);
            titleRow.Children.Add(t1);
            Grid.SetRow(titleRow, 0);
            root.Children.Add(titleRow);

            var split = new Grid();
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.Black
            };
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8)
            };
            img.Source = LoadImageSource(request);
            leftBorder.Child = img;
            Grid.SetColumn(leftBorder, 0);

            var rightBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                ClipToBounds = true
            };
            rightBorder.Child = BuildInterpretationCanvas(interpretation);
            Grid.SetColumn(rightBorder, 1);

            split.Children.Add(leftBorder);
            split.Children.Add(rightBorder);
            Grid.SetRow(split, 1);
            root.Children.Add(split);

            var stats = interpretation.Walls.Count + " paredes · " + interpretation.Doors.Count + " portas · " +
                        interpretation.Rooms.Count + " divisões (rooms)";
            if (!string.IsNullOrWhiteSpace(interpretation.Notes))
            {
                stats += " — Notas do modelo: " + interpretation.Notes;
            }

            var help = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 12, 0, 12),
                Text = "Compara os dois lados. Se faltar uma parede, houver uma a mais, ou as proporções não baterem com o desenho, " +
                       "carrega em «Cancelar» e ajusta o texto do prompt ou envia uma imagem mais clara (com cotas ajuda). " +
                       "Quando estiveres satisfeito, carrega em «Aplicar no Revit».\n\n" + stats
            };
            Grid.SetRow(help, 2);
            root.Children.Add(help);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var cancel = new Button
            {
                Content = "Cancelar",
                Padding = new Thickness(18, 10, 18, 10),
                Margin = new Thickness(0, 0, 10, 0),
                IsCancel = true
            };
            cancel.Click += (_, _) =>
            {
                DialogResult = false;
                Close();
            };
            var ok = new Button
            {
                Content = "Aplicar no Revit",
                Padding = new Thickness(22, 10, 22, 10),
                FontWeight = FontWeights.SemiBold,
                IsDefault = true,
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            ok.Click += (_, _) =>
            {
                DialogResult = true;
                Close();
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            Content = root;
        }

        private static ImageSource? LoadImageSource(SketchToBimRequest request)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(request.ImagePath) && File.Exists(request.ImagePath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(request.ImagePath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }

                if (!string.IsNullOrWhiteSpace(request.ImageBase64))
                {
                    var bytes = Convert.FromBase64String(request.ImageBase64);
                    var buffer = (byte[])bytes.Clone();
                    using var ms = new MemoryStream(buffer, writable: false);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = ms;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch
            {
                // ignored
            }

            return null;
        }

        private static FrameworkElement BuildInterpretationCanvas(SketchInterpretation interpretation)
        {
            var canvas = new Canvas
            {
                Width = CanvasLogicalSize,
                Height = CanvasLogicalSize,
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42))
            };

            var points = new List<(double x, double y)>();
            foreach (var w in interpretation.Walls)
            {
                points.Add((w.Start.X, w.Start.Y));
                points.Add((w.End.X, w.End.Y));
            }

            foreach (var d in interpretation.Doors)
            {
                points.Add((d.Location.X, d.Location.Y));
            }

            foreach (var r in interpretation.Rooms)
            {
                foreach (var p in r.Boundary)
                {
                    points.Add((p.X, p.Y));
                }
            }

            if (points.Count == 0)
            {
                canvas.Children.Add(new TextBlock
                {
                    Text = "Sem geometria para desenhar.",
                    Foreground = Brushes.LightGray,
                    Margin = new Thickness(16)
                });
                return new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
            }

            var minX = points.Min(p => p.x);
            var maxX = points.Max(p => p.x);
            var minY = points.Min(p => p.y);
            var maxY = points.Max(p => p.y);
            var dx = Math.Max(0.01, maxX - minX);
            var dy = Math.Max(0.01, maxY - minY);
            const double pad = 24.0;
            var sx = (CanvasLogicalSize - 2 * pad) / dx;
            var sy = (CanvasLogicalSize - 2 * pad) / dy;
            var scale = Math.Min(sx, sy);

            double Tx(double x) => pad + (x - minX) * scale;
            double Ty(double y) => pad + (maxY - y) * scale;

            foreach (var w in interpretation.Walls)
            {
                var line = new Line
                {
                    X1 = Tx(w.Start.X),
                    Y1 = Ty(w.Start.Y),
                    X2 = Tx(w.End.X),
                    Y2 = Ty(w.End.Y),
                    Stroke = new SolidColorBrush(Color.FromRgb(34, 211, 238)),
                    StrokeThickness = 2.5
                };
                canvas.Children.Add(line);
            }

            foreach (var r in interpretation.Rooms)
            {
                if (r.Boundary == null || r.Boundary.Count < 2)
                {
                    continue;
                }

                var poly = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(251, 146, 60)),
                    StrokeThickness = 1.5,
                    Opacity = 0.95
                };
                foreach (var p in r.Boundary)
                {
                    poly.Points.Add(new System.Windows.Point(Tx(p.X), Ty(p.Y)));
                }

                if (r.Boundary.Count > 2)
                {
                    var f = r.Boundary[0];
                    poly.Points.Add(new System.Windows.Point(Tx(f.X), Ty(f.Y)));
                }

                canvas.Children.Add(poly);

                if (r.Boundary.Count > 0)
                {
                    var cx = r.Boundary.Average(p => p.X);
                    var cy = r.Boundary.Average(p => p.Y);
                    var tb = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(r.Name) ? "?" : r.Name,
                        Foreground = new SolidColorBrush(Color.FromRgb(254, 243, 199)),
                        FontSize = 11,
                        Background = new SolidColorBrush(Color.FromArgb(160, 15, 23, 42))
                    };
                    tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(tb, Tx(cx) - tb.DesiredSize.Width / 2);
                    Canvas.SetTop(tb, Ty(cy) - tb.DesiredSize.Height / 2);
                    canvas.Children.Add(tb);
                }
            }

            foreach (var d in interpretation.Doors)
            {
                var ell = new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(Color.FromRgb(250, 204, 21)),
                    Stroke = Brushes.White,
                    StrokeThickness = 1
                };
                Canvas.SetLeft(ell, Tx(d.Location.X) - 5);
                Canvas.SetTop(ell, Ty(d.Location.Y) - 5);
                canvas.Children.Add(ell);
            }

            var scaleText = new TextBlock
            {
                Text = "Escala: ~" + Math.Round(dx, 2).ToString(CultureInfo.InvariantCulture) + " × " +
                       Math.Round(dy, 2).ToString(CultureInfo.InvariantCulture) + " m (caixa envolvente)",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 11
            };
            Canvas.SetLeft(scaleText, pad);
            Canvas.SetTop(scaleText, 4);
            canvas.Children.Add(scaleText);

            return new Viewbox
            {
                Child = canvas,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(8)
            };
        }
    }
}
