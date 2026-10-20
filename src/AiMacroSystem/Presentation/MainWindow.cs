"""Module: @role: WPF 10機能タブ画面（一覧・記録・編集・検証・レビュー・承認・モニター・履歴・復旧・設定）のUI構築"""
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AiMacroSystem.Presentation;

public sealed class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        Title = "AI Macro System v0.7 - Safe Chrome & Excel Automation";
        Width = 1180;
        Height = 780;

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bannerText = new TextBlock
        {
            Margin = new Thickness(12, 8, 12, 8),
            FontWeight = FontWeights.Bold
        };
        bannerText.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.StatusBanner)));
        Grid.SetRow(bannerText, 0);
        rootGrid.Children.Add(bannerText);

        var tabControl = new TabControl { Margin = new Thickness(10) };
        tabControl.Items.Add(CreateTab("1. ワークフロー一覧", nameof(MainViewModel.WorkflowVersions)));
        tabControl.Items.Add(CreateTab("2. 操作記録", nameof(MainViewModel.RecordedEvents)));
        tabControl.Items.Add(CreateEditorTab(viewModel));
        tabControl.Items.Add(CreateTab("4. 検証結果", nameof(MainViewModel.CurrentValidationIssues)));
        tabControl.Items.Add(CreateTab("5. 実行前レビュー & 6. 承認", nameof(MainViewModel.CurrentPlannedActions)));
        tabControl.Items.Add(CreateTab("7. 実行モニター & 8. 履歴", nameof(MainViewModel.ExecutionHistoryEvents)));
        tabControl.Items.Add(CreateTab("9. 復旧 (Recovery)", nameof(MainViewModel.PendingRecoveries)));
        tabControl.Items.Add(CreateSettingsTab());

        Grid.SetRow(tabControl, 1);
        rootGrid.Children.Add(tabControl);

        Content = rootGrid;
    }

    private static TabItem CreateTab(string header, string collectionBindingPath)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = true,
            IsReadOnly = true,
            Margin = new Thickness(8)
        };
        grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(collectionBindingPath));
        return new TabItem { Header = header, Content = grid };
    }

    private static TabItem CreateEditorTab(MainViewModel vm)
    {
        var panel = new DockPanel { Margin = new Thickness(8) };
        var validateBtn = new Button
        {
            Content = "IR 8段階検証 & 実行計画生成",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 0, 8)
        };
        validateBtn.Click += (_, _) => vm.ValidateCurrentEditorIr();
        DockPanel.SetDock(validateBtn, Dock.Top);
        panel.Children.Add(validateBtn);

        var editorBox = new TextBox
        {
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        editorBox.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.EditorIrJson))
        {
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        panel.Children.Add(editorBox);

        return new TabItem { Header = "3. ワークフロー編集 (IR)", Content = panel };
    }

    private static TabItem CreateSettingsTab()
    {
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock { Text = "ローカルAI (Ollama) エンドポイント:" });
        var endpointBox = new TextBox { Margin = new Thickness(0, 4, 0, 12) };
        endpointBox.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.LocalAiEndpoint)));
        stack.Children.Add(endpointBox);

        var extAiCheck = new CheckBox
        {
            Content = "外部AIへの送信ポリシーを明示的に許可する（機密除外・個別承認必須）"
        };
        extAiCheck.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.AllowExternalAiPolicy)));
        stack.Children.Add(extAiCheck);

        return new TabItem { Header = "10. 設定 (AI・ポリシー)", Content = stack };
    }
}
