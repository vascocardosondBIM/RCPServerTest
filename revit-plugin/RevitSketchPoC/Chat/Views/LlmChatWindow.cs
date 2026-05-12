using RevitSketchPoC.Chat.ViewModels;
using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace RevitSketchPoC.Chat.Views
{
    public sealed class LlmChatWindow : Window
    {
        private const string LayoutResourceName = "RevitSketchPoC.Chat.Views.LlmChatWindow.xaml";

        public LlmChatViewModel? ViewModel { get; private set; }

        public LlmChatWindow()
        {
            Title = "Assistente IA";
            Width = 620;
            Height = 620;
            MinWidth = 440;
            MinHeight = 400;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            Content = LoadLayoutFromXaml();
            Closing += OnClosingWhileBusy;
            Loaded += OnWindowLoaded;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (Content is not FrameworkElement root)
            {
                return;
            }

            if (root.FindName("ChatInputTextBox") is System.Windows.Controls.TextBox tb)
            {
                tb.PreviewKeyDown += ChatInputTextBox_PreviewKeyDown;
            }
        }

        private void ChatInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                return;
            }

            if (ViewModel?.SendCommand.CanExecute(null) == true)
            {
                ViewModel.SendCommand.Execute(null);
                e.Handled = true;
            }
        }

        public void SetViewModel(LlmChatViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;
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
            if (ViewModel?.IsBusy == true)
            {
                e.Cancel = true;
            }
        }
    }
}
