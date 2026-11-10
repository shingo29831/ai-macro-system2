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

    // ⑤ 実行モニター・履歴（失敗分類を単一の「失敗」にまとめず理由を明示）

    // ⑥ 復旧判断・AI監査・設定UI
}
