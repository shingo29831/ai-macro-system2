// Module: @role: マクロ実行中にモニター最上部中央へ常駐表示される小型フローティングUI
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace AiMacroSystem.Presentation;

public sealed class ExecutionOverlayWindow : Window
{
    public ExecutionOverlayWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        Title = "マクロ実行中";
        Width = 520;
        Height = 56;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;

        PositionAtTopCenter();

        var pillBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(18, 8, 14, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 緑色の実行中インジケータードット
        var pulseDot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(pulseDot, 0);
        grid.Children.Add(pulseDot);

        // 実行中マクロ名・ステップ表示
        var progressText = new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        progressText.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.RunningProgressText)));
        Grid.SetColumn(progressText, 1);
        grid.Children.Add(progressText);

        // 一時停止・再開・停止ボタン
        var btnStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        var pauseBtn = CreateMiniButton("⏸ 一時停止", Color.FromRgb(51, 65, 85));
        pauseBtn.Click += (_, _) => viewModel.PauseActiveMacro();
        btnStack.Children.Add(pauseBtn);

        var resumeBtn = CreateMiniButton("▶ 再開", Color.FromRgb(37, 99, 235));
        resumeBtn.Click += (_, _) => viewModel.ResumeActiveMacro();
        btnStack.Children.Add(resumeBtn);

        var stopBtn = CreateMiniButton("⏹ 停止", Color.FromRgb(220, 38, 38));
        stopBtn.Click += (_, _) => viewModel.CancelActiveMacro();
        btnStack.Children.Add(stopBtn);

        Grid.SetColumn(btnStack, 2);
        grid.Children.Add(btnStack);

        pillBorder.Child = grid;
        Content = pillBorder;
    }

    public void PositionAtTopCenter()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2.0;
        Top = workArea.Top + 12;
    }

    private static Button CreateMiniButton(string label, Color bg)
    {
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentFactory);
        template.VisualTree = borderFactory;

        return new Button
        {
            Content = label,
            Template = template,
            Background = new SolidColorBrush(bg),
            Foreground = Brushes.White,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand
        };
    }
}
