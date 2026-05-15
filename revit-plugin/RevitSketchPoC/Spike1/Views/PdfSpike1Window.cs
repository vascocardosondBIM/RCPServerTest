using RevitSketchPoC.Spike1.ViewModels;
using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace RevitSketchPoC.Spike1.Views
{
    public sealed class PdfSpike1Window : Window
    {
        private const string LayoutResourceName = "RevitSketchPoC.Spike1.Views.PdfSpike1Window.xaml";

        public PdfSpike1ViewModel ViewModel { get; }

        public PdfSpike1Window()
        {
            Title = "Spike 1 - PDF Vetorial para JSON";
            Width = 900;
            Height = 720;
            MinWidth = 700;
            MinHeight = 620;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            ViewModel = new PdfSpike1ViewModel();
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
                throw new InvalidOperationException("PdfSpike1Window.xaml root must be a UIElement.");
            }

            return element;
        }

        private static Stream OpenLayoutStream(Assembly asm, string logicalName)
        {
            var stream = asm.GetManifestResourceStream(logicalName);
            if (stream != null)
            {
                return stream;
            }

            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("PdfSpike1Window.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    stream = asm.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        return stream;
                    }
                }
            }

            throw new InvalidOperationException(
                "Embedded XAML not found. Expected manifest resource \"" + logicalName + "\". " +
                "Available: " + string.Join(", ", asm.GetManifestResourceNames()));
        }

        private void OnClosingWhileBusy(object? sender, CancelEventArgs e)
        {
            if (ViewModel.IsBusy)
            {
                e.Cancel = true;
                ViewModel.AppendStatus("Aguarda a geração de JSON terminar antes de fechar.");
            }
        }
    }
}
