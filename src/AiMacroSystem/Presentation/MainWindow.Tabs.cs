// Module: @role: MainWindowの各業務タブ（一覧・記録・検証レビュー承認・監視履歴・復旧設定）のUI構築を担当するPartialクラス
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
    // ① ワークフロー一覧（正式版・ドラフト・状態）
    private Grid CreateWorkflowListTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var card = CreateCard();
        var dock = new DockPanel();

        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var newBtn = CreateButton("🔴 記録開始", _accentDanger, Colors.White, isCompact: false);
        newBtn.Click += (_, _) => _vm.StartEasyRecording();
        DockPanel.SetDock(newBtn, Dock.Right);
        topBar.Children.Add(newBtn);

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock { Text = "正式ワークフローバージョン一覧（不変スナップショット）", FontSize = 16, FontWeight = FontWeights.Bold });
        titleStack.Children.Add(new TextBlock { Text = "正式版を選択して編集・レビュー・承認・実行を行えます", FontSize = 12, Foreground = new SolidColorBrush(_textSecondary) });
        topBar.Children.Add(titleStack);
        DockPanel.SetDock(topBar, Dock.Top);
        dock.Children.Add(topBar);

        var bottomBar = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal };

        var editBtn = CreateButton("✏ 編集を開く", _accentNeutral, _textPrimary, isCompact: true);
        editBtn.Margin = new Thickness(0, 0, 8, 0);
        editBtn.Click += (_, _) => _vm.OpenSelectedMacroInEditor();
        rightButtons.Children.Add(editBtn);

        var reviewBtn = CreateButton("🔍 実行前レビュー・承認へ", _accentPrimary, Colors.White, isCompact: true);
        reviewBtn.Margin = new Thickness(0, 0, 8, 0);
        reviewBtn.Click += (_, _) =>
        {
            if (_vm.SelectedMacroInList is not null) _vm.LoadSelectedVersionIntoEditor(_vm.SelectedMacroInList);
            _mainTabs.SelectedIndex = 3;
        };
        rightButtons.Children.Add(reviewBtn);

        var deleteBtn = CreateButton("🗑 削除", Color.FromRgb(254, 226, 226), Color.FromRgb(185, 28, 28), isCompact: true);
        deleteBtn.Margin = new Thickness(0, 0, 8, 0);
        deleteBtn.Click += (_, _) => _vm.DeleteSelectedMacro();
        rightButtons.Children.Add(deleteBtn);

        var runBtn = CreateButton("▶ 承認済みとして実行", _accentSuccess, Colors.White, isCompact: true);
        runBtn.Click += async (_, _) => await _vm.ExecuteSelectedMacroFromListAsync();
        rightButtons.Children.Add(runBtn);

        DockPanel.SetDock(rightButtons, Dock.Right);
        bottomBar.Children.Add(rightButtons);

        var approvalLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(_textSecondary), FontSize = 12 };
        approvalLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.CurrentApprovalStatusText)));
        bottomBar.Children.Add(approvalLabel);

        DockPanel.SetDock(bottomBar, Dock.Bottom);
        dock.Children.Add(bottomBar);

        var macroGrid = CreateCleanDataGrid();
        macroGrid.Columns.Add(new DataGridTextColumn { Header = "Workflow ID", Binding = new Binding("WorkflowId"), Width = 160 });
        macroGrid.Columns.Add(new DataGridTextColumn { Header = "マクロ名", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        macroGrid.Columns.Add(new DataGridTextColumn { Header = "正式版 (Version)", Binding = new Binding("VersionNumber"), Width = 120 });
        macroGrid.Columns.Add(new DataGridTextColumn { Header = "Schema", Binding = new Binding("SchemaVersion"), Width = 80 });
        macroGrid.Columns.Add(new DataGridTextColumn { Header = "確定日時", Binding = new Binding("CreatedAt") { StringFormat = "yyyy/MM/dd HH:mm:ss" }, Width = 170 });
        macroGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.WorkflowVersions)));
        macroGrid.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedMacroInList)) { Mode = BindingMode.TwoWay });
        dock.Children.Add(macroGrid);

        card.Child = dock;
        Grid.SetRow(card, 0);
        grid.Children.Add(card);

        var draftCard = CreateCard();
        var draftDock = new DockPanel();
        draftDock.Children.Add(new TextBlock
        {
            Text = "保存済みドラフト一覧（正式版から分離された編集作業データ）",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        DockPanel.SetDock(draftDock.Children[0], Dock.Top);

        var draftGrid = CreateCleanDataGrid();
        draftGrid.Columns.Add(new DataGridTextColumn { Header = "Draft ID", Binding = new Binding("DraftId"), Width = 220 });
        draftGrid.Columns.Add(new DataGridTextColumn { Header = "Workflow ID", Binding = new Binding("WorkflowId"), Width = 180 });
        draftGrid.Columns.Add(new DataGridTextColumn { Header = "基底Version ID", Binding = new Binding("BaseVersionId"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        draftGrid.Columns.Add(new DataGridTextColumn { Header = "更新日時", Binding = new Binding("UpdatedAt") { StringFormat = "yyyy/MM/dd HH:mm:ss" }, Width = 170 });
        draftGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.WorkflowDrafts)));
        draftDock.Children.Add(draftGrid);

        draftCard.Child = draftDock;
        Grid.SetRow(draftCard, 2);
        grid.Children.Add(draftCard);

        return grid;
    }

    // ② 操作記録・AI変換（アプリ自動判定・アプリ横断転記・機密保護・AI構造化IR生成）
    private Grid CreateRecordingTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        var card = CreateCard();
        var dock = new DockPanel();

        var topControls = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var startRecBtn = CreateButton("🔴 記録開始", _accentDanger, Colors.White, isCompact: false);
        startRecBtn.Margin = new Thickness(0, 0, 8, 6);
        startRecBtn.Click += (_, _) => _vm.StartEasyRecording();
        topControls.Children.Add(startRecBtn);

        var builtInRecBtn = CreateButton("🌐 内蔵スマートブラウザを開いて記録", Color.FromRgb(13, 148, 136), Colors.White, isCompact: false);
        builtInRecBtn.Margin = new Thickness(0, 0, 8, 6);
        builtInRecBtn.Click += (_, _) => OpenBuiltInSmartBrowserAndRecord();
        topControls.Children.Add(builtInRecBtn);

        var stopRecBtn = CreateButton("⏹ 記録終了（自動でマクロへ変換）", _accentNeutral, _textPrimary, isCompact: false);
        stopRecBtn.Margin = new Thickness(0, 0, 8, 6);
        stopRecBtn.Click += async (_, _) => await _vm.StopEasyRecordingAndGenerateAsync();
        topControls.Children.Add(stopRecBtn);

        var saveMacroBtn = CreateButton("💾 マクロを保存", _accentSuccess, Colors.White, isCompact: false);
        saveMacroBtn.Margin = new Thickness(0, 0, 12, 6);
        saveMacroBtn.Click += (_, _) => _vm.SaveCurrentWorkflowAsVersion();
        topControls.Children.Add(saveMacroBtn);

        var aiGenBtn = CreateButton("✨ 記録済み複数ログから再抽出・検証", _accentPrimary, Colors.White, isCompact: true);
        aiGenBtn.Margin = new Thickness(0, 0, 12, 6);
        aiGenBtn.Click += async (_, _) => await _vm.GenerateMacroFromRecordingViaAiAsync();
        topControls.Children.Add(aiGenBtn);

        var autoInfoText = new TextBlock
        {
            Text = "※ 操作中のアプリ（ブラウザ / Excel / 独自アプリ）およびブラウザからExcelへのコピー＆ペースト等のアプリ横断操作はアプリ側で自動判定・統合されます。",
            Foreground = new SolidColorBrush(_textSecondary),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 6)
        };
        topControls.Children.Add(autoInfoText);

        DockPanel.SetDock(topControls, Dock.Top);
        dock.Children.Add(topControls);

        var recGrid = CreateCleanDataGrid();
        recGrid.Columns.Add(new DataGridTextColumn { Header = "Event ID", Binding = new Binding("EventId"), Width = 140 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "ログソース", Binding = new Binding("LogSource"), Width = 110 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "対象アプリ", Binding = new Binding("ApplicationKind"), Width = 95 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "操作種別", Binding = new Binding("EventKind"), Width = 95 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "対象URL / ブック", Binding = new Binding("UrlOrWorkbookPath"), Width = 220 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "対象ラベル/範囲", Binding = new Binding("TargetLabel"), Width = 140 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "記録値 (機密保護済)", Binding = new Binding("CapturedValue"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        recGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "機密保護", Binding = new Binding("IsSensitive"), Width = 80 });
        recGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.RecordedEvents)));

        dock.Children.Add(recGrid);
        card.Child = dock;
        grid.Children.Add(card);
        return grid;
    }

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

    // ⑥ 復旧判断・AI監査・設定UI
    private Grid CreateRecoveryAndSettingsTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var recCard = CreateCard();
        var recDock = new DockPanel();

        var recHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var simCrashBtn = CreateButton("⚡ 異常終了をシミュレート", _accentNeutral, _textPrimary, isCompact: true);
        simCrashBtn.Click += async (_, _) => await _vm.SimulateAbnormalTerminationForDemoAsync();
        DockPanel.SetDock(simCrashBtn, Dock.Right);
        recHeader.Children.Add(simCrashBtn);
        recHeader.Children.Add(new TextBlock { Text = "起動時リカバリー・Unknown状態照合・ユーザー復旧判断", FontSize = 14, FontWeight = FontWeights.Bold });
        DockPanel.SetDock(recHeader, Dock.Top);
        recDock.Children.Add(recHeader);

        var recActionBar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var recBtns = new StackPanel { Orientation = Orientation.Horizontal };

        var resolveFailBtn = CreateButton("失敗確定として解決", _accentDanger, Colors.White, isCompact: true);
        resolveFailBtn.Margin = new Thickness(0, 0, 6, 0);
        resolveFailBtn.Click += (_, _) => _vm.ResolveFirstPendingRecovery(RecoveryStatus.ResolvedAsFailed, _vm.RecoveryDecisionNotesInput);
        recBtns.Children.Add(resolveFailBtn);

        var resolveCancelBtn = CreateButton("キャンセル確定として解決", _accentWarning, Colors.White, isCompact: true);
        resolveCancelBtn.Margin = new Thickness(0, 0, 6, 0);
        resolveCancelBtn.Click += (_, _) => _vm.ResolveFirstPendingRecovery(RecoveryStatus.ResolvedAsCancelled, _vm.RecoveryDecisionNotesInput);
        recBtns.Children.Add(resolveCancelBtn);

        var resolveDoneBtn = CreateButton("外部完了確認済として解決", _accentSuccess, Colors.White, isCompact: true);
        resolveDoneBtn.Margin = new Thickness(0, 0, 6, 0);
        resolveDoneBtn.Click += (_, _) => _vm.ResolveFirstPendingRecovery(RecoveryStatus.ResolvedAsCompleted, _vm.RecoveryDecisionNotesInput);
        recBtns.Children.Add(resolveDoneBtn);

        var resolveFreshBtn = CreateButton("新規実行を許可", _accentPrimary, Colors.White, isCompact: true);
        resolveFreshBtn.Click += (_, _) => _vm.ResolveFirstPendingRecovery(RecoveryStatus.ApprovedForFreshRun, _vm.RecoveryDecisionNotesInput);
        recBtns.Children.Add(resolveFreshBtn);

        DockPanel.SetDock(recBtns, Dock.Right);
        recActionBar.Children.Add(recBtns);

        var noteBox = CreateCleanTextBox(nameof(MainViewModel.RecoveryDecisionNotesInput));
        noteBox.Margin = new Thickness(0, 0, 12, 0);
        recActionBar.Children.Add(noteBox);
        DockPanel.SetDock(recActionBar, Dock.Bottom);
        recDock.Children.Add(recActionBar);

        var recGrid = CreateCleanDataGrid();
        recGrid.Columns.Add(new DataGridTextColumn { Header = "Recovery ID", Binding = new Binding("RecoveryId"), Width = 150 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "Execution ID", Binding = new Binding("ExecutionId"), Width = 150 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "復旧状態", Binding = new Binding("RecoveryStatus"), Width = 140 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "照合結果・判断記録", Binding = new Binding("DecisionNotes"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "検出時刻", Binding = new Binding("CreatedAt") { StringFormat = "yyyy/MM/dd HH:mm:ss" }, Width = 150 });
        recGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.RecoveryHistory)));
        recDock.Children.Add(recGrid);

        recCard.Child = recDock;
        Grid.SetRow(recCard, 0);
        grid.Children.Add(recCard);

        var bottomGrid = new Grid();
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        var settingsCard = CreateCard();
        var settingsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var settingsStack = new StackPanel();
        settingsStack.Children.Add(new TextBlock { Text = "設定（ローカルAI・外部AI制限・セキュリティポリシー）", FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
        settingsStack.Children.Add(CreateLabeledInput("ローカルAI エンドポイント (Ollama等)", nameof(MainViewModel.LocalAiEndpoint)));
        settingsStack.Children.Add(CreateLabeledInput("ローカルAI モデル識別子", nameof(MainViewModel.LocalAiModel)));
        settingsStack.Children.Add(CreateLabeledInput("既定の許可オリジン Host", nameof(MainViewModel.AllowedOriginHostInput)));
        settingsStack.Children.Add(CreateLabeledInput("許可Excelディレクトリ (空欄で制限なし)", nameof(MainViewModel.AllowedExcelDirectoryInput)));

        var extAiCheck = new CheckBox { Content = "外部AIプロバイダーの利用をポリシーで許可する (自動フォールバック禁止)", Margin = new Thickness(0, 4, 0, 6) };
        extAiCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.AllowExternalAiPolicy)));
        settingsStack.Children.Add(extAiCheck);

        var coordCheck = new CheckBox { Content = "例外用途の座標指定クリック (allowCoordinateClick) を許可する", Margin = new Thickness(0, 0, 0, 6) };
        coordCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.AllowCoordinateClickPolicy)));
        settingsStack.Children.Add(coordCheck);

        var launchBrowserCheck = new CheckBox { Content = "Webページ遷移時にシステムの既定ブラウザを起動・前面化する", Margin = new Thickness(0, 0, 0, 6) };
        launchBrowserCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.OpenSystemBrowserOnNavigate)));
        settingsStack.Children.Add(launchBrowserCheck);

        settingsScroll.Content = settingsStack;
        settingsCard.Child = settingsScroll;
        Grid.SetColumn(settingsCard, 0);
        bottomGrid.Children.Add(settingsCard);

        var aiAuditCard = CreateCard();
        var aiAuditDock = new DockPanel();
        aiAuditDock.Children.Add(new TextBlock { Text = "AI監査ログ (プロバイダー・モデル・検証段階・再要求回数・外部送信判定)", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        DockPanel.SetDock(aiAuditDock.Children[0], Dock.Top);

        var aiGrid = CreateCleanDataGrid();
        aiGrid.Columns.Add(new DataGridTextColumn { Header = "プロバイダー", Binding = new Binding("ProviderName"), Width = 140 });
        aiGrid.Columns.Add(new DataGridTextColumn { Header = "モデル", Binding = new Binding("ModelId"), Width = 130 });
        aiGrid.Columns.Add(new DataGridTextColumn { Header = "検証結果", Binding = new Binding("ValidationStageSummary"), Width = 130 });
        aiGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "受理", Binding = new Binding("IsAccepted"), Width = 55 });
        aiGrid.Columns.Add(new DataGridTextColumn { Header = "再要求数", Binding = new Binding("RepairAttemptCount"), Width = 75 });
        aiGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "外部送信", Binding = new Binding("ExternalSendApproved"), Width = 75 });
        aiGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.AiAuditLogs)));
        aiAuditDock.Children.Add(aiGrid);
        aiAuditCard.Child = aiAuditDock;
        Grid.SetColumn(aiAuditCard, 2);
        bottomGrid.Children.Add(aiAuditCard);

        Grid.SetRow(bottomGrid, 2);
        grid.Children.Add(bottomGrid);

        return grid;
    }
}
