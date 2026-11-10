// Module: @role: MainWindowのセキュリティ・AI設定および異常終了後の復旧管理タブのUI構築を担当するPartialクラス
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
    private Grid CreateRecoveryAndSettingsTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        _settingsAndRecoverySubTabs = CreateSubTabControl();

        var settingsCard = CreateCard();
        settingsCard.Margin = new Thickness(0, 6, 0, 0);
        var settingsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var settingsStack = new StackPanel { MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };

        settingsStack.Children.Add(CreateSectionHeader("セキュリティ・動作設定", "アクセスを許可するWebサイト・フォルダおよびAIモデルを設定します"));
        settingsStack.Children.Add(CreateLabeledInput("許可するWebサイトのホスト名 (例: example.com)", nameof(MainViewModel.AllowedOriginHostInput)));
        settingsStack.Children.Add(CreateLabeledInput("許可するExcelフォルダパス (空欄で制限なし)", nameof(MainViewModel.AllowedExcelDirectoryInput)));
        settingsStack.Children.Add(CreateLabeledInput("ローカルAI エンドポイント (Ollama等)", nameof(MainViewModel.LocalAiEndpoint)));
        settingsStack.Children.Add(CreateLabeledInput("ローカルAI モデル名", nameof(MainViewModel.LocalAiModel)));

        var launchBrowserCheck = new CheckBox
        {
            Content = "Webページを開く際にPCの既定ブラウザを起動する",
            Margin = new Thickness(0, 8, 0, 8)
        };
        launchBrowserCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.OpenSystemBrowserOnNavigate)));
        settingsStack.Children.Add(launchBrowserCheck);

        var coordCheck = new CheckBox
        {
            Content = "要素名で特定できない場合に画面座標でのクリックを許可する",
            Margin = new Thickness(0, 0, 0, 8)
        };
        coordCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.AllowCoordinateClickPolicy)));
        settingsStack.Children.Add(coordCheck);

        var extAiCheck = new CheckBox
        {
            Content = "外部AIサービスの利用を許可する",
            Margin = new Thickness(0, 0, 0, 8)
        };
        extAiCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.AllowExternalAiPolicy)));
        settingsStack.Children.Add(extAiCheck);

        settingsScroll.Content = settingsStack;
        settingsCard.Child = settingsScroll;

        var recCard = CreateCard();
        recCard.Margin = new Thickness(0, 6, 0, 0);
        var recDock = new DockPanel();

        var recHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var simCrashBtn = CreateButton("⚡ テスト用：異常終了をシミュレート", _accentNeutral, _textPrimary, isCompact: true);
        simCrashBtn.Click += async (_, _) => await _vm.SimulateAbnormalTerminationForDemoAsync();
        DockPanel.SetDock(simCrashBtn, Dock.Right);
        recHeader.Children.Add(simCrashBtn);
        recHeader.Children.Add(CreateSectionHeader("中断されたマクロの復旧判断", "異常終了または結果不明(Unknown)となった実行の扱いを選択してロックを解除します"));
        DockPanel.SetDock(recHeader, Dock.Top);
        recDock.Children.Add(recHeader);

        var recActionBar = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var recBtns = new StackPanel { Orientation = Orientation.Horizontal };

        var resolveFailBtn = CreateButton("失敗として解決", _accentDanger, Colors.White, isCompact: false);
        resolveFailBtn.Margin = new Thickness(0, 0, 8, 0);
        resolveFailBtn.Click += (_, _) => _vm.ResolveFirstPendingRecovery(RecoveryStatus.ResolvedAsFailed, _vm.RecoveryDecisionNotesInput);
        recBtns.Children.Add(resolveFailBtn);

        var resolveCancelBtn = CreateButton("キャンセルとして解決", _accentWarning, Colors.White, isCompact: false);
        resolveCancelBtn.Margin = new Thickness(0, 0, 8, 0);
        resolveCancelBtn.Click += (_, _) => _vm.ResolveFirstPendingRecovery(RecoveryStatus.ResolvedAsCancelled, _vm.RecoveryDecisionNotesInput);
        recBtns.Children.Add(resolveCancelBtn);

        var resolveDoneBtn = CreateButton("完了済みとして解決", _accentSuccess, Colors.White, isCompact: false);
        resolveDoneBtn.Margin = new Thickness(0, 0, 8, 0);
        resolveDoneBtn.Click += (_, _) => _vm.ResolveFirstPendingRecovery(RecoveryStatus.ResolvedAsCompleted, _vm.RecoveryDecisionNotesInput);
        recBtns.Children.Add(resolveDoneBtn);

        var resolveFreshBtn = CreateButton("再実行を許可", _accentPrimary, Colors.White, isCompact: false);
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
        recGrid.Columns.Add(new DataGridTextColumn { Header = "状態", Binding = new Binding("RecoveryStatus"), Width = 140 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "照合結果・メモ", Binding = new Binding("DecisionNotes"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "検出日時", Binding = new Binding("CreatedAt") { StringFormat = "yyyy/MM/dd HH:mm:ss" }, Width = 160 });
        recGrid.Columns.Add(new DataGridTextColumn { Header = "実行ID", Binding = new Binding("ExecutionId"), Width = 150 });
        recGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.RecoveryHistory)));
        recDock.Children.Add(recGrid);
        recCard.Child = recDock;

        _settingsAndRecoverySubTabs.Items.Add(new TabItem { Header = "⚙ 基本・セキュリティ設定", Padding = new Thickness(12, 5, 12, 5), Content = settingsCard });
        _settingsAndRecoverySubTabs.Items.Add(new TabItem { Header = "🚑 異常終了からの復旧", Padding = new Thickness(12, 5, 12, 5), Content = recCard });

        grid.Children.Add(_settingsAndRecoverySubTabs);
        return grid;
    }
}
