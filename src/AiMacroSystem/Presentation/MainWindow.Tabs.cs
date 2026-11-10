// Module: @role: MainWindowのマクロ一覧タブおよび操作記録タブのUI構築を担当するPartialクラス
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using static AiMacroSystem.Presentation.MainWindowUiFactory;

namespace AiMacroSystem.Presentation;

public sealed partial class MainWindow
{
    private Grid CreateWorkflowListTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        var card = CreateCard();
        var dock = new DockPanel();

        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var topRightBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var newMacroBtn = CreateButton("＋ 空のマクロを作成", _accentNeutral, _textPrimary, isCompact: false);
        newMacroBtn.Margin = new Thickness(0, 0, 8, 0);
        newMacroBtn.Click += (_, _) => _vm.StartNewMacroInEditor();
        topRightBtns.Children.Add(newMacroBtn);

        var recBtn = CreateButton("🔴 操作を記録して作成", _accentDanger, Colors.White, isCompact: false);
        recBtn.Click += (_, _) => _vm.StartEasyRecording();
        topRightBtns.Children.Add(recBtn);

        DockPanel.SetDock(topRightBtns, Dock.Right);
        topBar.Children.Add(topRightBtns);
        topBar.Children.Add(CreateSectionHeader("保存済みマクロ一覧", "実行・編集するマクロを選択してください（下書きは「履歴・監査」タブで確認できます）"));

        DockPanel.SetDock(topBar, Dock.Top);
        dock.Children.Add(topBar);

        var bottomBar = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal };

        var editBtn = CreateButton("✏ 手順を編集", _accentNeutral, _textPrimary, isCompact: false);
        editBtn.Margin = new Thickness(0, 0, 8, 0);
        editBtn.Click += (_, _) => _vm.OpenSelectedMacroInEditor();
        rightButtons.Children.Add(editBtn);

        var reviewBtn = CreateButton("🛡 確認・承認へ", _accentPrimary, Colors.White, isCompact: false);
        reviewBtn.Margin = new Thickness(0, 0, 8, 0);
        reviewBtn.Click += (_, _) =>
        {
            if (_vm.SelectedMacroInList is not null) _vm.LoadSelectedVersionIntoEditor(_vm.SelectedMacroInList);
            _mainTabs.SelectedIndex = 3;
        };
        rightButtons.Children.Add(reviewBtn);

        var runBtn = CreateButton("▶ すぐ実行", _accentSuccess, Colors.White, isCompact: false);
        runBtn.Margin = new Thickness(0, 0, 12, 0);
        runBtn.Click += async (_, _) => await _vm.ExecuteSelectedMacroFromListAsync();
        rightButtons.Children.Add(runBtn);

        var deleteBtn = CreateButton("🗑 削除", Color.FromRgb(254, 226, 226), Color.FromRgb(185, 28, 28), isCompact: true);
        deleteBtn.Click += (_, _) => _vm.DeleteSelectedMacro();
        rightButtons.Children.Add(deleteBtn);

        DockPanel.SetDock(rightButtons, Dock.Right);
        bottomBar.Children.Add(rightButtons);

        var approvalLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_textSecondary),
            FontSize = 12.5
        };
        approvalLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.CurrentApprovalStatusText)));
        bottomBar.Children.Add(approvalLabel);

        DockPanel.SetDock(bottomBar, Dock.Bottom);
        dock.Children.Add(bottomBar);

        var macroGrid = CreateCleanDataGrid();
        macroGrid.Columns.Add(new DataGridTextColumn { Header = "マクロ名", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        macroGrid.Columns.Add(new DataGridTextColumn { Header = "バージョン", Binding = new Binding("VersionNumber") { StringFormat = "v{0}" }, Width = 100 });
        macroGrid.Columns.Add(new DataGridTextColumn { Header = "保存日時", Binding = new Binding("CreatedAt") { StringFormat = "yyyy/MM/dd HH:mm" }, Width = 160 });
        macroGrid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding("WorkflowId"), Width = 160 });
        macroGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.WorkflowVersions)));
        macroGrid.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedMacroInList)) { Mode = BindingMode.TwoWay });
        dock.Children.Add(macroGrid);

        card.Child = dock;
        grid.Children.Add(card);
        return grid;
    }

    private Grid CreateRecordingTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        var card = CreateCard();
        var dock = new DockPanel();

        var headerDock = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var rightActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var aiGenBtn = CreateButton("✨ 記録ログから手順を再生成", _accentNeutral, _textPrimary, isCompact: true);
        aiGenBtn.Click += async (_, _) => await _vm.GenerateMacroFromRecordingViaAiAsync();
        rightActions.Children.Add(aiGenBtn);

        DockPanel.SetDock(rightActions, Dock.Right);
        headerDock.Children.Add(rightActions);
        headerDock.Children.Add(CreateSectionHeader("操作の記録とマクロ変換", "ブラウザ・Excel・アプリの操作を自動で検出し、手順ブロックへ変換します"));
        DockPanel.SetDock(headerDock, Dock.Top);
        dock.Children.Add(headerDock);

        var actionBar = new Border
        {
            Background = new SolidColorBrush(_bgCanvas),
            BorderBrush = new SolidColorBrush(_borderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var actionWrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

        var startRecBtn = CreateButton("🔴 デスクトップ記録を開始", _accentDanger, Colors.White, isCompact: false);
        startRecBtn.Margin = new Thickness(0, 0, 10, 0);
        startRecBtn.Click += (_, _) => _vm.StartEasyRecording();
        actionWrap.Children.Add(startRecBtn);

        var builtInRecBtn = CreateButton("🌐 内蔵ブラウザを開いて記録", Color.FromRgb(13, 148, 136), Colors.White, isCompact: false);
        builtInRecBtn.Margin = new Thickness(0, 0, 10, 0);
        builtInRecBtn.Click += (_, _) => OpenBuiltInSmartBrowserAndRecord();
        actionWrap.Children.Add(builtInRecBtn);

        var stopRecBtn = CreateButton("⏹ 記録を終了して手順を作成", _accentPrimary, Colors.White, isCompact: false);
        stopRecBtn.Click += async (_, _) => await _vm.StopEasyRecordingAndGenerateAsync();
        actionWrap.Children.Add(stopRecBtn);

        actionBar.Child = actionWrap;
        DockPanel.SetDock(actionBar, Dock.Top);
        dock.Children.Add(actionBar);

        var recGrid = CreateCleanDataGrid();
        recGrid.Columns.Add(new DataGridTextColumn { Header = "アプリ", Binding = new Binding("ApplicationKind"), Width = 100 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "操作", Binding = new Binding("EventKind"), Width = 100 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "対象 (URL / ファイル)", Binding = new Binding("UrlOrWorkbookPath"), Width = 240 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "要素 / セル範囲", Binding = new Binding("TargetLabel"), Width = 160 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "入力値", Binding = new Binding("CapturedValue"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        recGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "機密保護", Binding = new Binding("IsSensitive"), Width = 80 });
        recGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.RecordedEvents)));

        dock.Children.Add(recGrid);
        card.Child = dock;
        grid.Children.Add(card);
        return grid;
    }
}
