// Module: @role: MainWindowの復旧判断・AI監査・セキュリティ設定タブのUI構築を担当するPartialクラス
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
