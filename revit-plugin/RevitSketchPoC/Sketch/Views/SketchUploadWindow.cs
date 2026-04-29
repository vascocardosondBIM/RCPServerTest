using RevitSketchPoC.Sketch.ViewModels;
using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace RevitSketchPoC.Sketch.Views
{
    public sealed class SketchUploadWindow : Window
    {
        private const string LayoutResourceName = "RevitSketchPoC.Sketch.Views.SketchUploadWindow.xaml";

        public SketchUploadViewModel ViewModel { get; }

        public SketchUploadWindow()
        {
            Title = "Sketch to BIM (PoC)";
            Width = 760;
            Height = 580;
            MinWidth = 580;
            MinHeight = 440;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            ViewModel = new SketchUploadViewModel();
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
                throw new InvalidOperationException("SketchUploadWindow.xaml root must be a UIElement.");
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
                if (name.EndsWith("SketchUploadWindow.xaml", StringComparison.OrdinalIgnoreCase))
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
                ViewModel.AppendStatus("Aguarda o processo terminar (ou cancelação futura) antes de fechar.");
            }
        }
    }
}
