// Module: @role: MainWindowの検証レビュー承認タブおよび実行監視履歴タブのUI構築を担当するPartialクラス
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using AiMacroSystem.Domain;
using static AiMacroSystem.Presentation.MainWindowUiFactory;

namespace AiMacroSystem.Presentation;

public sealed partial class MainWindow
{
    // ④ 検証結果・実行前レビュー・承認UI
    private Grid CreateValidationReviewApprovalTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var valCard = CreateCard();
        var valDock = new DockPanel();
        valDock.Children.Add(new TextBlock { Text = "8段階静的検証・ポリシー検証結果（対象箇所・エラー理由・修正案）", FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        DockPanel.SetDock(valDock.Children[0], Dock.Top);

        var valGrid = CreateCleanDataGrid();
        valGrid.Columns.Add(new DataGridTextColumn { Header = "種別", Binding = new Binding("Severity"), Width = 90 });
        valGrid.Columns.Add(new DataGridTextColumn { Header = "対象箇所 (NodeId)", Binding = new Binding("FriendlyLocation"), Width = 140 });
        valGrid.Columns.Add(new DataGridTextColumn { Header = "検証段階 / コード", Binding = new Binding("TechnicalCode"), Width = 220 });
        valGrid.Columns.Add(new DataGridTextColumn { Header = "エラー内容", Binding = new Binding("FriendlyReason"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        valGrid.Columns.Add(new DataGridTextColumn { Header = "修正案", Binding = new Binding("HowToFix"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        valGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.FriendlyValidationIssues)));
        valDock.Children.Add(valGrid);
        valCard.Child = valDock;
        Grid.SetRow(valCard, 0);
        grid.Children.Add(valCard);

        var planCard = CreateCard();
        var planDock = new DockPanel();
        planDock.Children.Add(new TextBlock { Text = "実行前レビュー（Execution Plan：対象・操作・入力データ・副作用・リスクレベル）", FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        DockPanel.SetDock(planDock.Children[0], Dock.Top);

        var planGrid = CreateCleanDataGrid();
        planGrid.Columns.Add(new DataGridTextColumn { Header = "Node ID", Binding = new Binding("NodeId"), Width = 90 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "操作 (ActionType)", Binding = new Binding("ActionType"), Width = 150 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "対象 (Target)", Binding = new Binding("TargetSummary"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "入力データ", Binding = new Binding("InputSummary"), Width = 180 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "副作用分類", Binding = new Binding("SideEffects"), Width = 130 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "リスク評価", Binding = new Binding("EvaluatedRisk"), Width = 100 });
        planGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "個別承認必須", Binding = new Binding("RequiresIndividualApproval"), Width = 100 });
        planGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.CurrentPlannedActions)));
        planDock.Children.Add(planGrid);
        planCard.Child = planDock;
        Grid.SetRow(planCard, 2);
        grid.Children.Add(planCard);

        var appCard = CreateCard();
        var appDock = new DockPanel();

        var appBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var approveBtn = CreateButton("✅ 実行計画を承認する", _accentPrimary, Colors.White, isCompact: false);
        approveBtn.Margin = new Thickness(0, 0, 8, 0);
        approveBtn.Click += (_, _) => _vm.ApproveCurrentWorkflow();
        appBtns.Children.Add(approveBtn);

        var rejectBtn = CreateButton("🚫 却下する", Color.FromRgb(254, 226, 226), Color.FromRgb(185, 28, 28), isCompact: false);
        rejectBtn.Margin = new Thickness(0, 0, 12, 0);
        rejectBtn.Click += (_, _) => _vm.RejectCurrentWorkflow();
        appBtns.Children.Add(rejectBtn);

        var execBtn = CreateButton("▶ 承認済み計画を実行", _accentSuccess, Colors.White, isCompact: false);
        execBtn.Click += async (_, _) => await _vm.ExecuteApprovedMacroAsync();
        appBtns.Children.Add(execBtn);

        DockPanel.SetDock(appBtns, Dock.Right);
        appDock.Children.Add(appBtns);

        var appLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        appLeft.Children.Add(new TextBlock { Text = "承認者:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var approverBox = CreateCleanTextBox(nameof(MainViewModel.ApproverNameInput));
        approverBox.Width = 140;
        approverBox.Margin = new Thickness(0, 0, 16, 0);
        appLeft.Children.Add(approverBox);

        var highRiskCheck = new CheckBox
        {
            Content = "高リスク(High)アクションの個別承認を含めて承認する",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        highRiskCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.ApproveHighRiskChecked)));
        appLeft.Children.Add(highRiskCheck);

        appDock.Children.Add(appLeft);
        appCard.Child = appDock;
        Grid.SetRow(appCard, 4);
        grid.Children.Add(appCard);

        return grid;
    }

    // ⑤ 実行モニター・履歴（失敗分類を単一の「失敗」にまとめず理由を明示）
    private Grid CreateMonitorAndHistoryTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.1, GridUnitType.Star) });

        var ctrlCard = CreateCard();
        ctrlCard.Padding = new Thickness(16, 10, 16, 10);
        var ctrlDock = new DockPanel();
        var ctrlBtns = new StackPanel { Orientation = Orientation.Horizontal };

        var pauseBtn = CreateButton("⏸ 協調的一時停止", _accentWarning, Colors.White, isCompact: true);
        pauseBtn.Margin = new Thickness(0, 0, 8, 0);
        pauseBtn.Click += (_, _) => _vm.PauseActiveMacro();
        ctrlBtns.Children.Add(pauseBtn);

        var resumeBtn = CreateButton("▶ 再開", _accentPrimary, Colors.White, isCompact: true);
        resumeBtn.Margin = new Thickness(0, 0, 8, 0);
        resumeBtn.Click += (_, _) => _vm.ResumeActiveMacro();
        ctrlBtns.Children.Add(resumeBtn);

        var cancelBtn = CreateButton("⏹ 協調的キャンセル", _accentDanger, Colors.White, isCompact: true);
        cancelBtn.Click += (_, _) => _vm.CancelActiveMacro();
        ctrlBtns.Children.Add(cancelBtn);

        DockPanel.SetDock(ctrlBtns, Dock.Right);
        ctrlDock.Children.Add(ctrlBtns);

        var runStatusText = new TextBlock { FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        runStatusText.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.RunningProgressText)));
        ctrlDock.Children.Add(runStatusText);

        ctrlCard.Child = ctrlDock;
        Grid.SetRow(ctrlCard, 0);
        grid.Children.Add(ctrlCard);

        var actCard = CreateCard();
        var actDock = new DockPanel();
        actDock.Children.Add(new TextBlock
        {
            Text = "実行モニター：アクション状態・試行回数・詳細エラー分類（対象不明・許可外遷移・読み戻し不一致・成否不明を個別明示）",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        DockPanel.SetDock(actDock.Children[0], Dock.Top);

        var actGrid = CreateCleanDataGrid();
        actGrid.Columns.Add(new DataGridTextColumn { Header = "Node ID", Binding = new Binding("NodeId"), Width = 100 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "アクション状態", Binding = new Binding("Status"), Width = 120 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "試行回数", Binding = new Binding("AttemptCount"), Width = 90 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "詳細分類 (FailureClassification)", Binding = new Binding("LastFailureClassification"), Width = 210 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "詳細理由メッセージ", Binding = new Binding("LastMessage"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        actGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.CurrentRunActionStatuses)));
        actDock.Children.Add(actGrid);
        actCard.Child = actDock;
        Grid.SetRow(actCard, 2);
        grid.Children.Add(actCard);

        var histGridContainer = new Grid();
        histGridContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        histGridContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        histGridContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });

        var execListCard = CreateCard();
        var execListDock = new DockPanel();
        execListDock.Children.Add(new TextBlock { Text = "実行履歴一覧 (バージョン・承認ID・計画ハッシュ追跡)", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        DockPanel.SetDock(execListDock.Children[0], Dock.Top);

        var execGrid = CreateCleanDataGrid();
        execGrid.Columns.Add(new DataGridTextColumn { Header = "Execution ID", Binding = new Binding("ExecutionId"), Width = 130 });
        execGrid.Columns.Add(new DataGridTextColumn { Header = "Version ID", Binding = new Binding("VersionId"), Width = 110 });
        execGrid.Columns.Add(new DataGridTextColumn { Header = "Approval ID", Binding = new Binding("ApprovalId"), Width = 110 });
        execGrid.Columns.Add(new DataGridTextColumn { Header = "終端状態", Binding = new Binding("Status"), Width = 85 });
        execGrid.Columns.Add(new DataGridTextColumn { Header = "開始時刻", Binding = new Binding("StartedAt") { StringFormat = "HH:mm:ss" }, Width = 75 });
        execGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.AllExecutions)));
        execGrid.SelectionChanged += (_, _) =>
        {
            if (execGrid.SelectedItem is ExecutionRecord rec) _vm.RefreshExecutionMonitor(rec.ExecutionId);
        };
        execListDock.Children.Add(execGrid);
        execListCard.Child = execListDock;
        Grid.SetColumn(execListCard, 0);
        histGridContainer.Children.Add(execListCard);

        var evCard = CreateCard();
        var evDock = new DockPanel();
        evDock.Children.Add(new TextBlock { Text = "追記型監査イベント・状態遷移ログ (ExecutionEvents)", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        DockPanel.SetDock(evDock.Children[0], Dock.Top);

        var evGrid = CreateCleanDataGrid();
        evGrid.Columns.Add(new DataGridTextColumn { Header = "時刻", Binding = new Binding("Timestamp") { StringFormat = "HH:mm:ss.fff" }, Width = 100 });
        evGrid.Columns.Add(new DataGridTextColumn { Header = "イベント種別", Binding = new Binding("EventType"), Width = 210 });
        evGrid.Columns.Add(new DataGridTextColumn { Header = "監査詳細", Binding = new Binding("DetailsJson"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        evGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.ExecutionHistoryEvents)));
        evDock.Children.Add(evGrid);
        evCard.Child = evDock;
        Grid.SetColumn(evCard, 2);
        histGridContainer.Children.Add(evCard);

        Grid.SetRow(histGridContainer, 4);
        grid.Children.Add(histGridContainer);

        return grid;
    }
}
