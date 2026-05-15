using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitSketchPoC.Spike1.Views
{
    public sealed class Spike2ProgressWindow : Window
    {
        private readonly TextBox _statusTextBox;

        public Spike2ProgressWindow()
        {
            Title = "Spike 2 - Progresso";
            Width = 760;
            Height = 500;
            MinWidth = 620;
            MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(241, 245, 249));
            FontFamily = new FontFamily("Segoe UI");

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock
            {
                Text = "Execução do Spike 2",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
            });
            header.Children.Add(new TextBlock
            {
                Text = "Acompanhe os steps e mensagens em tempo real.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 4, 0, 0)
            });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            _statusTextBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240))
            };
            Grid.SetRow(_statusTextBox, 1);
            root.Children.Add(_statusTextBox);

            Content = root;
        }

        public void AppendStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var prefix = DateTime.Now.ToString("HH:mm:ss");
            _statusTextBox.AppendText("[" + prefix + "] " + message + Environment.NewLine);
            _statusTextBox.ScrollToEnd();
        }
    }
}
