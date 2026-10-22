// Module: @role: 記録中および実行中にメイン画面を隠してモニター最上部中央へ常駐表示される小型フローティングUI
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace AiMacroSystem.Presentation;

public sealed class ExecutionOverlayWindow : Window
{
    private readonly Border _pillBorder;
    private readonly Border _modeBadge;
    private readonly TextBlock _modeBadgeText;
    private readonly TextBlock _statusText;
    private readonly StackPanel _executionButtonsPanel;
    private readonly StackPanel _recordingButtonsPanel;

    public ExecutionOverlayWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        Title = "実行中 / 記録中";
        Width = 740;
        Height = 58;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;

        PositionAtTopCenter();

        _pillBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(29),
            Padding = new Thickness(16, 8, 14, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _modeBadge = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Background = new SolidColorBrush(Color.FromRgb(16, 185, 129))
        };
        _modeBadgeText = new TextBlock
        {
            Text = "実行中",
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 12
        };
        _modeBadge.Child = _modeBadgeText;
        Grid.SetColumn(_modeBadge, 0);
        grid.Children.Add(_modeBadge);

        _statusText = new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_statusText, 1);
        grid.Children.Add(_statusText);

        var rightContainer = new Grid { VerticalAlignment = VerticalAlignment.Center };

        // 実行中コントロール群
        _executionButtonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        var pauseBtn = CreateMiniButton("⏸ 一時停止", Color.FromRgb(51, 65, 85));
        pauseBtn.Click += (_, _) => viewModel.PauseActiveMacro();
        _executionButtonsPanel.Children.Add(pauseBtn);

        var resumeBtn = CreateMiniButton("▶ 再開", Color.FromRgb(37, 99, 235));
        resumeBtn.Click += (_, _) => viewModel.ResumeActiveMacro();
        _executionButtonsPanel.Children.Add(resumeBtn);

        var stopExecBtn = CreateMiniButton("⏹ 停止", Color.FromRgb(220, 38, 38));
        stopExecBtn.Click += (_, _) => viewModel.CancelActiveMacro();
        _executionButtonsPanel.Children.Add(stopExecBtn);

        // 記録中コントロール群（操作対象アプリはアプリ側で自動判定するため手動アプリ選択ボタンは配置しない）
        _recordingButtonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var autoDetectBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        autoDetectBadge.Child = new TextBlock
        {
            Text = "自動アプリ判定: ON (複数アプリ横断対応)",
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        _recordingButtonsPanel.Children.Add(autoDetectBadge);

        var finishRecBtn = CreateMiniButton("⏹ 記録終了（自動マクロ変換）", Color.FromRgb(220, 38, 38));
        finishRecBtn.Click += async (_, _) => await viewModel.StopEasyRecordingAndGenerateAsync();
        _recordingButtonsPanel.Children.Add(finishRecBtn);

        rightContainer.Children.Add(_executionButtonsPanel);
        rightContainer.Children.Add(_recordingButtonsPanel);
        Grid.SetColumn(rightContainer, 2);
        grid.Children.Add(rightContainer);

        _pillBorder.Child = grid;
        Content = _pillBorder;

        UpdateOverlayMode(isRecording: false);
    }

    public void UpdateOverlayMode(bool isRecording)
    {
        if (isRecording)
        {
            Title = "記録中";
            _modeBadgeText.Text = "🔴 記録中";
            _modeBadge.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            _pillBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            _statusText.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.RecordingProgressText)));
            _executionButtonsPanel.Visibility = Visibility.Collapsed;
            _recordingButtonsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            Title = "実行中";
            _modeBadgeText.Text = "▶ 実行中";
            _modeBadge.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            _pillBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
            _statusText.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.RunningProgressText)));
            _recordingButtonsPanel.Visibility = Visibility.Collapsed;
            _executionButtonsPanel.Visibility = Visibility.Visible;
        }
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
