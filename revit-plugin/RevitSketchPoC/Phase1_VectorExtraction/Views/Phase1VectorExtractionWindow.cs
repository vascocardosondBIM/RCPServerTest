using RevitSketchPoC.Phase1_VectorExtraction.ViewModels;
using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace RevitSketchPoC.Phase1_VectorExtraction.Views
{
    public sealed class Phase1VectorExtractionWindow : Window
    {
        private const string LayoutResourceName =
            "RevitSketchPoC.Phase1_VectorExtraction.Views.Phase1VectorExtractionWindow.xaml";

        public Phase1VectorExtractionViewModel ViewModel { get; }

        public Phase1VectorExtractionWindow()
        {
            Title = "Fase 1 — Extração PDF + zonas";
            Width = 920;
            Height = 700;
            MinWidth = 720;
            MinHeight = 540;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            ViewModel = new Phase1VectorExtractionViewModel();
            DataContext = ViewModel;
            Content = LoadLayoutFromXaml();
            Closing += OnClosingWhileBusy;
        }

        private static UIElement LoadLayoutFromXaml()
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = OpenLayoutStream(asm, LayoutResourceName);
            var root = XamlReader.Load(stream);
            if (root is not UIElement element)
            {
                throw new InvalidOperationException("Phase1VectorExtractionWindow.xaml root must be a UIElement.");
            }

            return element;
        }

        private static Stream OpenLayoutStream(Assembly asm, string logicalName)
        {
            var stream = asm.GetManifestResourceStream(logicalName);
            if (stream != null) return stream;

            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("Phase1VectorExtractionWindow.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    stream = asm.GetManifestResourceStream(name);
                    if (stream != null) return stream;
                }
            }

            throw new InvalidOperationException(
                "Embedded XAML not found. Expected \"" + logicalName + "\".");
        }

        private void OnClosingWhileBusy(object? sender, CancelEventArgs e)
        {
            if (ViewModel.IsBusy)
            {
                e.Cancel = true;
                ViewModel.AppendStatus("Aguarda a extração terminar antes de fechar.");
            }
        }
    }
}
