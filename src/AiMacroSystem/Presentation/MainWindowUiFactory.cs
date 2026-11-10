// Module: @role: MainWindow向けの共通UI部品（ボタン、テキストボックス、ラベル、カード、データグリッド）生成およびカラーパレット定義
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace AiMacroSystem.Presentation;

public static class MainWindowUiFactory
{
    public static readonly Color BgCanvas = Color.FromRgb(248, 250, 252);
    public static readonly Color BgCard = Color.FromRgb(255, 255, 255);
    public static readonly Color BorderSubtle = Color.FromRgb(226, 232, 240);
    public static readonly Color TextPrimary = Color.FromRgb(15, 23, 42);
    public static readonly Color TextSecondary = Color.FromRgb(100, 116, 139);
    public static readonly Color AccentPrimary = Color.FromRgb(37, 99, 235);
    public static readonly Color AccentSuccess = Color.FromRgb(16, 185, 129);
    public static readonly Color AccentWarning = Color.FromRgb(217, 119, 6);
    public static readonly Color AccentDanger = Color.FromRgb(239, 68, 68);
    public static readonly Color AccentNeutral = Color.FromRgb(241, 245, 249);

    public static readonly Color _bgCanvas = BgCanvas;
    public static readonly Color _bgCard = BgCard;
    public static readonly Color _borderSubtle = BorderSubtle;
    public static readonly Color _textPrimary = TextPrimary;
    public static readonly Color _textSecondary = TextSecondary;
    public static readonly Color _accentPrimary = AccentPrimary;
    public static readonly Color _accentSuccess = AccentSuccess;
    public static readonly Color _accentWarning = AccentWarning;
    public static readonly Color _accentDanger = AccentDanger;
    public static readonly Color _accentNeutral = AccentNeutral;

    public static Border CreateCard() =>
        new()
        {
            Background = new SolidColorBrush(BgCard),
            BorderBrush = new SolidColorBrush(BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14)
        };

    public static DataGrid CreateCleanDataGrid() =>
        new()
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(BorderSubtle),
            BorderThickness = new Thickness(1),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(BorderSubtle),
            RowBackground = Brushes.White,
            AlternatingRowBackground = new SolidColorBrush(BgCanvas),
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0,
            RowHeight = 32,
            FontSize = 12.5
        };

    public static TextBlock CreateFieldLabel(string text) =>
        new()
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(TextSecondary),
            Margin = new Thickness(0, 0, 0, 4)
        };

    public static TextBox CreateCleanTextBox(string bindingPath)
    {
        var tb = new TextBox
        {
            Padding = new Thickness(8, 5, 8, 5),
            BorderBrush = new SolidColorBrush(BorderSubtle),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(BgCard),
            Foreground = new SolidColorBrush(TextPrimary)
        };
        tb.SetBinding(TextBox.TextProperty, new Binding(bindingPath) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        return tb;
    }

    public static StackPanel CreateLabeledInput(string label, string bindingPath)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(CreateFieldLabel(label));
        sp.Children.Add(CreateCleanTextBox(bindingPath));
        return sp;
    }

    public static TabControl CreateSubTabControl() =>
        new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(0, 8, 0, 0)
        };

    public static StackPanel CreateSectionHeader(string title, string? subtitle = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(TextPrimary)
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = new SolidColorBrush(TextSecondary),
                Margin = new Thickness(0, 2, 0, 0)
            });
        }
        return stack;
    }

    public static Button CreateButton(string text, Color bg, Color fg, bool isCompact)
    {
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentFactory);
        template.VisualTree = borderFactory;

        return new Button
        {
            Content = text,
            Template = template,
            Background = new SolidColorBrush(bg),
            Foreground = new SolidColorBrush(fg),
            FontWeight = FontWeights.SemiBold,
            FontSize = isCompact ? 12 : 13,
            Padding = isCompact ? new Thickness(10, 5, 10, 5) : new Thickness(14, 8, 14, 8),
            Cursor = Cursors.Hand
        };
    }
}
