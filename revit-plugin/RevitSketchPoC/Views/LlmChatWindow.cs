using RevitSketchPoC.ViewModels;
using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace RevitSketchPoC.Views
{
    public sealed class LlmChatWindow : Window
    {
        private const string LayoutResourceName = "RevitSketchPoC.Views.LlmChatWindow.xaml";

        public LlmChatViewModel ViewModel { get; }

        public LlmChatWindow(LlmChatViewModel viewModel)
        {
            ViewModel = viewModel;
            Title = "Assistente IA";
            Width = 580;
            Height = 540;
            MinWidth = 420;
            MinHeight = 380;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            DataContext = viewModel;
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
                throw new InvalidOperationException("LlmChatWindow.xaml root must be a UIElement.");
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
                if (name.EndsWith("LlmChatWindow.xaml", StringComparison.OrdinalIgnoreCase))
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
            }
        }
    }
}
