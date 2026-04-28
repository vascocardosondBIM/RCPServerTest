using RevitSketchPoC.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace RevitSketchPoC.Views
{
    public sealed class SketchUploadWindow : Window
    {
        public SketchUploadViewModel ViewModel { get; }

        public SketchUploadWindow()
        {
            Title = "Sketch to BIM (PoC)";
            Width = 720;
            Height = 560;
            MinHeight = 420;
            MinWidth = 560;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            ViewModel = new SketchUploadViewModel();
            DataContext = ViewModel;

            Content = BuildLayout();

            Closing += OnClosingWhileBusy;
        }

        private void OnClosingWhileBusy(object? sender, CancelEventArgs e)
        {
            if (ViewModel.IsBusy)
            {
                e.Cancel = true;
                ViewModel.AppendStatus("Aguarda o processo terminar (ou cancelação futura) antes de fechar.");
            }
        }

        private UIElement BuildLayout()
        {
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Upload sketch and generate model in Revit",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(title, 0);
            Grid.SetColumnSpan(title, 2);
            root.Children.Add(title);

            var imagePathBox = new TextBox { Height = 34, VerticalContentAlignment = VerticalAlignment.Center, IsReadOnly = true };
            imagePathBox.SetBinding(TextBox.TextProperty, new Binding("ImagePath") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            Grid.SetRow(imagePathBox, 1);
            Grid.SetColumn(imagePathBox, 0);
            root.Children.Add(imagePathBox);

            var browseButton = new Button { Content = "Browse...", Width = 100, Height = 34, Margin = new Thickness(8, 0, 0, 0) };
            browseButton.SetBinding(Button.CommandProperty, new Binding("BrowseCommand"));
            Grid.SetRow(browseButton, 1);
            Grid.SetColumn(browseButton, 1);
            root.Children.Add(browseButton);

            var promptLabel = new TextBlock { Text = "Prompt (optional)", Margin = new Thickness(0, 12, 0, 4) };
            Grid.SetRow(promptLabel, 2);
            Grid.SetColumnSpan(promptLabel, 2);
            root.Children.Add(promptLabel);

            var promptBox = new TextBox { Height = 78, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            promptBox.SetBinding(TextBox.TextProperty, new Binding("Prompt") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            Grid.SetRow(promptBox, 3);
            Grid.SetColumnSpan(promptBox, 2);
            root.Children.Add(promptBox);

            var optionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var roomsCheck = new CheckBox { Content = "Create rooms", Margin = new Thickness(0, 0, 18, 0) };
            roomsCheck.SetBinding(CheckBox.IsCheckedProperty, new Binding("AutoCreateRooms"));
            var doorsCheck = new CheckBox { Content = "Create doors" };
            doorsCheck.SetBinding(CheckBox.IsCheckedProperty, new Binding("AutoCreateDoors"));
            optionsPanel.Children.Add(roomsCheck);
            optionsPanel.Children.Add(doorsCheck);
            Grid.SetRow(optionsPanel, 4);
            Grid.SetColumnSpan(optionsPanel, 2);
            root.Children.Add(optionsPanel);

            var statusBox = new TextBox
            {
                MinHeight = 160,
                MaxHeight = 320,
                Margin = new Thickness(0, 8, 0, 0),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(183, 28, 28)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(189, 189, 189)),
                VerticalContentAlignment = VerticalAlignment.Top
            };
            statusBox.SetBinding(TextBox.TextProperty, new Binding("Status"));
            Grid.SetRow(statusBox, 5);
            Grid.SetColumnSpan(statusBox, 2);
            root.Children.Add(statusBox);

            var generateButton = new Button
            {
                Content = "Generate Model",
                Width = 160,
                Height = 38,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            generateButton.SetBinding(Button.CommandProperty, new Binding("RunCommand"));
            Grid.SetRow(generateButton, 6);
            Grid.SetColumnSpan(generateButton, 2);
            root.Children.Add(generateButton);

            return root;
        }
    }
}
