// Module: @role: MainWindowの確認承認タブ・実行モニタータブ・履歴監査タブのUI構築を担当するPartialクラス
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
    private Grid CreateValidationReviewApprovalTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var appCard = CreateCard();
        appCard.Margin = new Thickness(0, 0, 0, 10);
        var appDock = new DockPanel();

        var appBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var approveBtn = CreateButton("✅ 承認する", _accentPrimary, Colors.White, isCompact: false);
        approveBtn.Margin = new Thickness(0, 0, 8, 0);
        approveBtn.Click += (_, _) => _vm.ApproveCurrentWorkflow();
        appBtns.Children.Add(approveBtn);

        var rejectBtn = CreateButton("🚫 却下", Color.FromRgb(254, 226, 226), Color.FromRgb(185, 28, 28), isCompact: false);
        rejectBtn.Margin = new Thickness(0, 0, 12, 0);
        rejectBtn.Click += (_, _) => _vm.RejectCurrentWorkflow();
        appBtns.Children.Add(rejectBtn);

        var execBtn = CreateButton("▶ 承認済みマクロを実行", _accentSuccess, Colors.White, isCompact: false);
        execBtn.Click += async (_, _) => await _vm.ExecuteApprovedMacroAsync();
        appBtns.Children.Add(execBtn);

        DockPanel.SetDock(appBtns, Dock.Right);
        appDock.Children.Add(appBtns);

        var appLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        appLeft.Children.Add(new TextBlock { Text = "承認者:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var approverBox = CreateCleanTextBox(nameof(MainViewModel.ApproverNameInput));
        approverBox.Width = 130;
        approverBox.Margin = new Thickness(0, 0, 16, 0);
        appLeft.Children.Add(approverBox);

        var highRiskCheck = new CheckBox
        {
            Content = "高リスク(High)操作も含めて承認する",
            VerticalAlignment = VerticalAlignment.Center
        };
        highRiskCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.ApproveHighRiskChecked)));
        appLeft.Children.Add(highRiskCheck);

        appDock.Children.Add(appLeft);
        appCard.Child = appDock;
        Grid.SetRow(appCard, 0);
        grid.Children.Add(appCard);

        var reviewSubTabs = CreateSubTabControl();

        var planCard = CreateCard();
        planCard.Margin = new Thickness(0, 6, 0, 0);
        var planDock = new DockPanel();
        planDock.Children.Add(CreateSectionHeader("実行プラン（事前確認）", "マクロがアクセスする対象とリスクレベルを確認してください"));
        DockPanel.SetDock(planDock.Children[0], Dock.Top);

        var planGrid = CreateCleanDataGrid();
        planGrid.Columns.Add(new DataGridTextColumn { Header = "手順ID", Binding = new Binding("NodeId"), Width = 90 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "操作種別", Binding = new Binding("ActionType"), Width = 150 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "操作対象", Binding = new Binding("TargetSummary"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "入力値", Binding = new Binding("InputSummary"), Width = 180 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "影響範囲", Binding = new Binding("SideEffects"), Width = 120 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "リスク", Binding = new Binding("EvaluatedRisk"), Width = 90 });
        planGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "個別承認", Binding = new Binding("RequiresIndividualApproval"), Width = 80 });
        planGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.CurrentPlannedActions)));
        planDock.Children.Add(planGrid);
        planCard.Child = planDock;

        var valCard = CreateCard();
        valCard.Margin = new Thickness(0, 6, 0, 0);
        var valDock = new DockPanel();
        valDock.Children.Add(CreateSectionHeader("安全性・構文チェック結果", "エラーがある場合は手順編集画面で修正してください"));
        DockPanel.SetDock(valDock.Children[0], Dock.Top);

        var valGrid = CreateCleanDataGrid();
        valGrid.Columns.Add(new DataGridTextColumn { Header = "状態", Binding = new Binding("Severity"), Width = 80 });
        valGrid.Columns.Add(new DataGridTextColumn { Header = "箇所", Binding = new Binding("FriendlyLocation"), Width = 130 });
        valGrid.Columns.Add(new DataGridTextColumn { Header = "内容", Binding = new Binding("FriendlyReason"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        valGrid.Columns.Add(new DataGridTextColumn { Header = "対処方法", Binding = new Binding("HowToFix"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        valGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.FriendlyValidationIssues)));
        valDock.Children.Add(valGrid);
        valCard.Child = valDock;

        reviewSubTabs.Items.Add(new TabItem { Header = "📋 実行プラン (操作とリスク)", Padding = new Thickness(12, 5, 12, 5), Content = planCard });
        reviewSubTabs.Items.Add(new TabItem { Header = "🔍 安全チェック結果", Padding = new Thickness(12, 5, 12, 5), Content = valCard });

        Grid.SetRow(reviewSubTabs, 1);
        grid.Children.Add(reviewSubTabs);
        return grid;
    }

    private Grid CreateMonitorAndHistoryTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var ctrlCard = CreateCard();
        ctrlCard.Padding = new Thickness(16, 12, 16, 12);
        var ctrlDock = new DockPanel();
        var ctrlBtns = new StackPanel { Orientation = Orientation.Horizontal };

        var pauseBtn = CreateButton("⏸ 一時停止", _accentWarning, Colors.White, isCompact: false);
        pauseBtn.Margin = new Thickness(0, 0, 8, 0);
        pauseBtn.Click += (_, _) => _vm.PauseActiveMacro();
        ctrlBtns.Children.Add(pauseBtn);

        var resumeBtn = CreateButton("▶ 再開", _accentPrimary, Colors.White, isCompact: false);
        resumeBtn.Margin = new Thickness(0, 0, 8, 0);
        resumeBtn.Click += (_, _) => _vm.ResumeActiveMacro();
        ctrlBtns.Children.Add(resumeBtn);

        var cancelBtn = CreateButton("⏹ キャンセル", _accentDanger, Colors.White, isCompact: false);
        cancelBtn.Click += (_, _) => _vm.CancelActiveMacro();
        ctrlBtns.Children.Add(cancelBtn);

        DockPanel.SetDock(ctrlBtns, Dock.Right);
        ctrlDock.Children.Add(ctrlBtns);

        var runStatusText = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        runStatusText.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.RunningProgressText)));
        ctrlDock.Children.Add(runStatusText);

        ctrlCard.Child = ctrlDock;
        Grid.SetRow(ctrlCard, 0);
        grid.Children.Add(ctrlCard);

        var actCard = CreateCard();
        var actDock = new DockPanel();
        actDock.Children.Add(CreateSectionHeader("ステップ別の進行状況", "各手順の実行結果とエラー理由をリアルタイムに表示します（過去の履歴は「履歴・監査」タブへ）"));
        DockPanel.SetDock(actDock.Children[0], Dock.Top);

        var actGrid = CreateCleanDataGrid();
        actGrid.Columns.Add(new DataGridTextColumn { Header = "手順ID", Binding = new Binding("NodeId"), Width = 110 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "状態", Binding = new Binding("Status"), Width = 120 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "試行回数", Binding = new Binding("AttemptCount"), Width = 90 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "エラー分類", Binding = new Binding("LastFailureClassification"), Width = 190 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "詳細メッセージ", Binding = new Binding("LastMessage"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        actGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.CurrentRunActionStatuses)));
        actDock.Children.Add(actGrid);
        actCard.Child = actDock;

        Grid.SetRow(actCard, 2);
        grid.Children.Add(actCard);
        return grid;
    }

    private Grid CreateHistoryAndAuditTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        var historySubTabs = CreateSubTabControl();

        var execHistoryGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        execHistoryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        execHistoryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        execHistoryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        var execListCard = CreateCard();
        var execListDock = new DockPanel();
        execListDock.Children.Add(CreateSectionHeader("実行履歴一覧", "行を選択すると右側に詳細イベントを表示し、実行モニターにも反映します"));
        DockPanel.SetDock(execListDock.Children[0], Dock.Top);

        var execGrid = CreateCleanDataGrid();
        execGrid.Columns.Add(new DataGridTextColumn { Header = "結果", Binding = new Binding("Status"), Width = 95 });
        execGrid.Columns.Add(new DataGridTextColumn { Header = "開始時刻", Binding = new Binding("StartedAt") { StringFormat = "MM/dd HH:mm:ss" }, Width = 120 });
        execGrid.Columns.Add(new DataGridTextColumn { Header = "実行ID", Binding = new Binding("ExecutionId"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        execGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.AllExecutions)));
        execGrid.SelectionChanged += (_, _) =>
        {
            if (execGrid.SelectedItem is ExecutionRecord rec) _vm.RefreshExecutionMonitor(rec.ExecutionId);
        };
        execListDock.Children.Add(execGrid);
        execListCard.Child = execListDock;
        Grid.SetColumn(execListCard, 0);
        execHistoryGrid.Children.Add(execListCard);

        var evCard = CreateCard();
        var evDock = new DockPanel();
        evDock.Children.Add(CreateSectionHeader("イベント詳細ログ", "選択した実行の状態遷移・監査イベント記録"));
        DockPanel.SetDock(evDock.Children[0], Dock.Top);

        var evGrid = CreateCleanDataGrid();
        evGrid.Columns.Add(new DataGridTextColumn { Header = "時刻", Binding = new Binding("Timestamp") { StringFormat = "HH:mm:ss.fff" }, Width = 100 });
        evGrid.Columns.Add(new DataGridTextColumn { Header = "イベント", Binding = new Binding("EventType"), Width = 190 });
        evGrid.Columns.Add(new DataGridTextColumn { Header = "詳細", Binding = new Binding("DetailsJson"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        evGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.ExecutionHistoryEvents)));
        evDock.Children.Add(evGrid);
        evCard.Child = evDock;
        Grid.SetColumn(evCard, 2);
        execHistoryGrid.Children.Add(evCard);

        var aiAuditCard = CreateCard();
        aiAuditCard.Margin = new Thickness(0, 6, 0, 0);
        var aiAuditDock = new DockPanel();
        aiAuditDock.Children.Add(CreateSectionHeader("AI変換監査ログ", "AIによるマクロ生成・検証・自動修復の履歴"));
        DockPanel.SetDock(aiAuditDock.Children[0], Dock.Top);

        var aiGrid = CreateCleanDataGrid();
        aiGrid.Columns.Add(new DataGridTextColumn { Header = "プロバイダー", Binding = new Binding("ProviderName"), Width = 150 });
        aiGrid.Columns.Add(new DataGridTextColumn { Header = "モデル", Binding = new Binding("ModelId"), Width = 150 });
        aiGrid.Columns.Add(new DataGridTextColumn { Header = "検証結果", Binding = new Binding("ValidationStageSummary"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        aiGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "受理", Binding = new Binding("IsAccepted"), Width = 70 });
        aiGrid.Columns.Add(new DataGridTextColumn { Header = "再試行", Binding = new Binding("RepairAttemptCount"), Width = 80 });
        aiGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "外部送信", Binding = new Binding("ExternalSendApproved"), Width = 80 });
        aiGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.AiAuditLogs)));
        aiAuditDock.Children.Add(aiGrid);
        aiAuditCard.Child = aiAuditDock;

        var draftCard = CreateCard();
        draftCard.Margin = new Thickness(0, 6, 0, 0);
        var draftDock = new DockPanel();
        draftDock.Children.Add(CreateSectionHeader("保存済み下書き (ドラフト) 一覧", "正式版として確定する前の作業データ一覧"));
        DockPanel.SetDock(draftDock.Children[0], Dock.Top);

        var draftGrid = CreateCleanDataGrid();
        draftGrid.Columns.Add(new DataGridTextColumn { Header = "ワークフローID", Binding = new Binding("WorkflowId"), Width = 200 });
        draftGrid.Columns.Add(new DataGridTextColumn { Header = "更新日時", Binding = new Binding("UpdatedAt") { StringFormat = "yyyy/MM/dd HH:mm:ss" }, Width = 180 });
        draftGrid.Columns.Add(new DataGridTextColumn { Header = "下書きID", Binding = new Binding("DraftId"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        draftGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.WorkflowDrafts)));
        draftDock.Children.Add(draftGrid);
        draftCard.Child = draftDock;

        historySubTabs.Items.Add(new TabItem { Header = "🕘 実行履歴・イベント", Padding = new Thickness(12, 5, 12, 5), Content = execHistoryGrid });
        historySubTabs.Items.Add(new TabItem { Header = "🤖 AI監査ログ", Padding = new Thickness(12, 5, 12, 5), Content = aiAuditCard });
        historySubTabs.Items.Add(new TabItem { Header = "📝 下書き一覧", Padding = new Thickness(12, 5, 12, 5), Content = draftCard });

        grid.Children.Add(historySubTabs);
        return grid;
    }
}
