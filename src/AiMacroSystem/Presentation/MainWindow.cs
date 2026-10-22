// Module: @role: 仕様書15章の必須UI10画面（一覧・記録・編集・検証・実行前レビュー・承認・監視・履歴・復旧・設定）を提供するメインウィンドウ
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Presentation;

public sealed class MainWindow : Window
{
    private static readonly Color _bgCanvas = Color.FromRgb(248, 250, 252);
    private static readonly Color _bgCard = Color.FromRgb(255, 255, 255);
    private static readonly Color _borderSubtle = Color.FromRgb(226, 232, 240);
    private static readonly Color _textPrimary = Color.FromRgb(15, 23, 42);
    private static readonly Color _textSecondary = Color.FromRgb(100, 116, 139);
    private static readonly Color _accentPrimary = Color.FromRgb(37, 99, 235);
    private static readonly Color _accentSuccess = Color.FromRgb(16, 185, 129);
    private static readonly Color _accentWarning = Color.FromRgb(217, 119, 6);
    private static readonly Color _accentDanger = Color.FromRgb(239, 68, 68);
    private static readonly Color _accentNeutral = Color.FromRgb(241, 245, 249);

    private readonly MainViewModel _vm;
    private readonly ExecutionOverlayWindow _overlayWindow;
    private readonly TabControl _mainTabs;
    private readonly StackPanel _visualBlockChainPanel;

    private StackPanel _fieldUrlPanel = null!;
    private StackPanel _fieldElementPanel = null!;
    private StackPanel _fieldValuePanel = null!;
    private CheckBox _fieldPasswordCheck = null!;
    private StackPanel _fieldExcelPathPanel = null!;
    private Grid _fieldExcelMetaGrid = null!;
    private StackPanel _fieldExcelWritePanel = null!;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        Title = "AI Macro Studio v0.7";
        Width = 1180;
        Height = 780;
        MinWidth = 960;
        MinHeight = 640;
        FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI");
        Background = new SolidColorBrush(_bgCanvas);
        Foreground = new SolidColorBrush(_textPrimary);

        _overlayWindow = new ExecutionOverlayWindow(_vm);
        _visualBlockChainPanel = new StackPanel { Margin = new Thickness(8, 4, 8, 20) };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = CreateHeaderBar();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var recoveryBar = CreateRecoveryAlertBar();
        Grid.SetRow(recoveryBar, 1);
        root.Children.Add(recoveryBar);

        _mainTabs = new TabControl
        {
            Margin = new Thickness(18, 12, 18, 18),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };

        _mainTabs.Items.Add(new TabItem { Header = "① ワークフロー一覧", Content = CreateWorkflowListTab() });
        _mainTabs.Items.Add(new TabItem { Header = "② 操作記録・AI変換", Content = CreateRecordingTab() });
        _mainTabs.Items.Add(new TabItem { Header = "③ 編集 (ブロック/IR)", Content = CreateVisualBlockEditorView() });
        _mainTabs.Items.Add(new TabItem { Header = "④ 検証・実行前レビュー・承認", Content = CreateValidationReviewApprovalTab() });
        _mainTabs.Items.Add(new TabItem { Header = "⑤ 実行モニター・履歴", Content = CreateMonitorAndHistoryTab() });
        _mainTabs.Items.Add(new TabItem { Header = "⑥ 復旧・設定", Content = CreateRecoveryAndSettingsTab() });

        Grid.SetRow(_mainTabs, 2);
        root.Children.Add(_mainTabs);

        Content = root;

        ((INotifyCollectionChanged)_vm.EasySteps).CollectionChanged += (_, _) => RebuildVisualBlockChain();
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        Closed += (_, _) => _overlayWindow.Close();
        UpdateInputFieldsVisibility(_vm.SelectedActionKind);
        RebuildVisualBlockChain();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsEditingMode))
        {
            _mainTabs.SelectedIndex = _vm.IsEditingMode ? 2 : 0;
            if (_vm.IsEditingMode)
            {
                RebuildVisualBlockChain();
            }
        }
        else if (e.PropertyName is nameof(MainViewModel.IsExecutingMacro) or nameof(MainViewModel.IsRecordingMacro))
        {
            bool showOverlay = _vm.IsRecordingMacro || _vm.IsExecutingMacro;
            if (showOverlay)
            {
                _overlayWindow.UpdateOverlayMode(isRecording: _vm.IsRecordingMacro);
                _overlayWindow.PositionAtTopCenter();
                if (!_overlayWindow.IsVisible)
                {
                    _overlayWindow.Show();
                }
                if (IsVisible)
                {
                    Hide();
                }
            }
            else
            {
                if (!IsVisible)
                {
                    Show();
                }
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Activate();
                if (_overlayWindow.IsVisible)
                {
                    _overlayWindow.Hide();
                }
            }
        }
    }

    private Border CreateHeaderBar()
    {
        var header = new Border
        {
            Background = new SolidColorBrush(_bgCard),
            BorderBrush = new SolidColorBrush(_borderSubtle),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 12, 24, 12)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border
        {
            Background = new SolidColorBrush(_accentPrimary),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 10, 0)
        };
        badge.Child = new TextBlock { Text = "AI", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13 };
        titleStack.Children.Add(badge);
        titleStack.Children.Add(new TextBlock
        {
            Text = "Macro Studio v0.7",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(titleStack, 0);
        grid.Children.Add(titleStack);

        var statusPill = new Border
        {
            Background = new SolidColorBrush(_accentNeutral),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(20, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusText = new TextBlock
        {
            FontSize = 12.5,
            Foreground = new SolidColorBrush(_textPrimary),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        statusText.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.StatusBanner)));
        statusPill.Child = statusText;
        Grid.SetColumn(statusPill, 1);
        grid.Children.Add(statusPill);

        var headerRecBtn = CreateButton("🔴 記録開始", _accentDanger, Colors.White, isCompact: true);
        headerRecBtn.Margin = new Thickness(12, 0, 0, 0);
        headerRecBtn.Click += (_, _) => _vm.StartEasyRecording();
        Grid.SetColumn(headerRecBtn, 2);
        grid.Children.Add(headerRecBtn);

        header.Child = grid;
        return header;
    }

    private Border CreateRecoveryAlertBar()
    {
        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(254, 242, 242)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 10, 24, 10),
            Visibility = _vm.PendingRecoveries.Count > 0 ? Visibility.Visible : Visibility.Collapsed
        };

        ((INotifyCollectionChanged)_vm.PendingRecoveries).CollectionChanged += (_, _) =>
        {
            bar.Visibility = _vm.PendingRecoveries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        };

        var dock = new DockPanel();
        var unlockBtn = CreateButton("復旧タブで判断する", _accentDanger, Colors.White, isCompact: true);
        unlockBtn.Click += (_, _) => _mainTabs.SelectedIndex = 5;
        DockPanel.SetDock(unlockBtn, Dock.Right);
        dock.Children.Add(unlockBtn);

        dock.Children.Add(new TextBlock
        {
            Text = "⚠️ 未終端または成否不明(Unknown)の実行が検出されました。状態照合と復旧判断が完了するまで新規実行は禁止されます。",
            Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28)),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        bar.Child = dock;
        return bar;
    }

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

        // ドラフト一覧
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

    // ② 操作記録・AI変換（開始・停止、記録状態、対象アプリ、機密保護、AI構造化IR生成）
    private Grid CreateRecordingTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        var card = CreateCard();
        var dock = new DockPanel();

        var topControls = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var startRecBtn = CreateButton("🔴 記録開始", _accentDanger, Colors.White, isCompact: true);
        startRecBtn.Margin = new Thickness(0, 0, 8, 6);
        startRecBtn.Click += (_, _) => _vm.StartEasyRecording();
        topControls.Children.Add(startRecBtn);

        var stopRecBtn = CreateButton("⏹ 記録終了（自動でマクロへ変換）", _accentNeutral, _textPrimary, isCompact: true);
        stopRecBtn.Margin = new Thickness(0, 0, 16, 6);
        stopRecBtn.Click += async (_, _) => await _vm.StopEasyRecordingAndGenerateAsync();
        topControls.Children.Add(stopRecBtn);

        var simWebBtn = CreateButton("＋ Chrome遷移イベント記録", _accentNeutral, _textPrimary, isCompact: true);
        simWebBtn.Margin = new Thickness(0, 0, 8, 6);
        simWebBtn.Click += (_, _) => _vm.RecordSimulatedWebOpen();
        topControls.Children.Add(simWebBtn);

        var simInputBtn = CreateButton("＋ Chrome入力イベント記録", _accentNeutral, _textPrimary, isCompact: true);
        simInputBtn.Margin = new Thickness(0, 0, 8, 6);
        simInputBtn.Click += (_, _) => _vm.RecordSimulatedWebInput(asPassword: false);
        topControls.Children.Add(simInputBtn);

        var simPwdBtn = CreateButton("＋ パスワード入力記録(自動保護)", _accentNeutral, _textPrimary, isCompact: true);
        simPwdBtn.Margin = new Thickness(0, 0, 8, 6);
        simPwdBtn.Click += (_, _) => _vm.RecordSimulatedWebInput(asPassword: true);
        topControls.Children.Add(simPwdBtn);

        var simExcelBtn = CreateButton("＋ Excel書込イベント記録", _accentNeutral, _textPrimary, isCompact: true);
        simExcelBtn.Margin = new Thickness(0, 0, 8, 6);
        simExcelBtn.Click += (_, _) => _vm.RecordSimulatedExcelWrite();
        topControls.Children.Add(simExcelBtn);

        var simCustomAppBtn = CreateButton("＋ 独自アプリ複数ログ記録", _accentNeutral, _textPrimary, isCompact: true);
        simCustomAppBtn.Margin = new Thickness(0, 0, 16, 6);
        simCustomAppBtn.Click += (_, _) => _vm.RecordSimulatedCustomAppAction();
        topControls.Children.Add(simCustomAppBtn);

        var aiGenBtn = CreateButton("✨ 複数ログから高精度抽出しマクロを生成・全8段階検証", _accentPrimary, Colors.White, isCompact: true);
        aiGenBtn.Margin = new Thickness(0, 0, 0, 6);
        aiGenBtn.Click += async (_, _) => await _vm.GenerateMacroFromRecordingViaAiAsync();
        topControls.Children.Add(aiGenBtn);

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

    // ③ 編集（ビジュアルブロック＋構造化IR JSON・ドラフト保存・正式版確定）
    private Grid CreateVisualBlockEditorView()
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var navCard = CreateCard();
        navCard.Padding = new Thickness(16, 10, 16, 10);
        navCard.Margin = new Thickness(0, 0, 0, 12);

        var navGrid = new Grid();
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var idBox = CreateCleanTextBox(nameof(MainViewModel.WorkflowIdInput));
        idBox.Width = 180;
        Grid.SetColumn(idBox, 0);
        navGrid.Children.Add(idBox);

        var nameBox = CreateCleanTextBox(nameof(MainViewModel.WorkflowNameInput));
        nameBox.FontSize = 14;
        nameBox.FontWeight = FontWeights.Bold;
        nameBox.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetColumn(nameBox, 1);
        navGrid.Children.Add(nameBox);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var draftBtn = CreateButton("📝 ドラフト保存", _accentNeutral, _textPrimary, isCompact: true);
        draftBtn.Margin = new Thickness(0, 0, 8, 0);
        draftBtn.Click += (_, _) => _vm.SaveCurrentAsDraft();
        rightBtns.Children.Add(draftBtn);

        var validateBtn = CreateButton("🔍 8段階検証を実行", _accentNeutral, _textPrimary, isCompact: true);
        validateBtn.Margin = new Thickness(0, 0, 8, 0);
        validateBtn.Click += (_, _) => _vm.ValidateCurrentEditorIr();
        rightBtns.Children.Add(validateBtn);

        var saveBtn = CreateButton("💾 新しい正式バージョンとして確定", _accentPrimary, Colors.White, isCompact: true);
        saveBtn.Click += (_, _) => _vm.SaveCurrentWorkflowAsVersion();
        rightBtns.Children.Add(saveBtn);

        Grid.SetColumn(rightBtns, 2);
        navGrid.Children.Add(rightBtns);
        navCard.Child = navGrid;

        Grid.SetRow(navCard, 0);
        grid.Children.Add(navCard);

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });

        // 左：ブロックチェーン
        var flowCard = CreateCard();
        var flowDock = new DockPanel();
        var flowHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var clearBtn = CreateButton("クリア", _accentNeutral, _textSecondary, isCompact: true);
        clearBtn.Click += (_, _) => _vm.ClearEasySteps();
        DockPanel.SetDock(clearBtn, Dock.Right);
        flowHeader.Children.Add(clearBtn);
        flowHeader.Children.Add(new TextBlock { Text = "ビジュアルブロック連鎖", FontSize = 14, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(flowHeader, Dock.Top);
        flowDock.Children.Add(flowHeader);

        var canvasScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(_bgCanvas), Padding = new Thickness(12) };
        canvasScroll.Content = _visualBlockChainPanel;
        flowDock.Children.Add(canvasScroll);
        flowCard.Child = flowDock;
        Grid.SetColumn(flowCard, 0);
        contentGrid.Children.Add(flowCard);

        // 中央：アクション追加フォーム
        var addCard = CreateCard();
        var formScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var formStack = new StackPanel();
        formStack.Children.Add(new TextBlock { Text = "登録済みアクション追加", FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });

        formStack.Children.Add(CreateFieldLabel("アクション種別"));
        var actionCombo = new ComboBox
        {
            DisplayMemberPath = "Value",
            SelectedValuePath = "Key",
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 10)
        };
        actionCombo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.AvailableActionKinds)));
        actionCombo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(MainViewModel.SelectedActionKind)));
        actionCombo.SelectionChanged += (_, _) =>
        {
            if (actionCombo.SelectedValue is string kind) UpdateInputFieldsVisibility(kind);
        };
        formStack.Children.Add(actionCombo);

        _fieldUrlPanel = CreateLabeledInput("URL (オリジン許可リスト対象)", nameof(MainViewModel.StepUrlInput));
        _fieldElementPanel = CreateLabeledInput("意味的ロケーター (ラベル/アクセシブル名)", nameof(MainViewModel.StepElementNameInput));
        _fieldValuePanel = CreateLabeledInput("入力値", nameof(MainViewModel.StepFillValueInput));

        _fieldPasswordCheck = new CheckBox
        {
            Content = "機密情報 (パスワード等) として保護",
            Foreground = new SolidColorBrush(_textSecondary),
            Margin = new Thickness(0, 0, 0, 10)
        };
        _fieldPasswordCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.StepIsPasswordInput)));

        _fieldExcelPathPanel = CreateLabeledInput("Excelブック明示パス", nameof(MainViewModel.StepExcelPathInput));

        _fieldExcelMetaGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        _fieldExcelMetaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _fieldExcelMetaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        _fieldExcelMetaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sheetPanel = CreateLabeledInput("明示シート名", nameof(MainViewModel.StepExcelSheetInput));
        Grid.SetColumn(sheetPanel, 0);
        _fieldExcelMetaGrid.Children.Add(sheetPanel);

        var rangePanel = CreateLabeledInput("明示A1範囲", nameof(MainViewModel.StepExcelRangeInput));
        Grid.SetColumn(rangePanel, 2);
        _fieldExcelMetaGrid.Children.Add(rangePanel);

        _fieldExcelWritePanel = CreateLabeledInput("2次元書き込みデータ (CSV形式)", nameof(MainViewModel.StepExcelWriteTextInput));

        formStack.Children.Add(_fieldUrlPanel);
        formStack.Children.Add(_fieldElementPanel);
        formStack.Children.Add(_fieldValuePanel);
        formStack.Children.Add(_fieldPasswordCheck);
        formStack.Children.Add(_fieldExcelPathPanel);
        formStack.Children.Add(_fieldExcelMetaGrid);
        formStack.Children.Add(_fieldExcelWritePanel);

        var addBtn = CreateButton("＋ ブロックを追加", _accentPrimary, Colors.White, isCompact: false);
        addBtn.Click += (_, _) => _vm.AddStepFromBuilderUi();
        formStack.Children.Add(addBtn);

        formScroll.Content = formStack;
        addCard.Child = formScroll;
        Grid.SetColumn(addCard, 2);
        contentGrid.Children.Add(addCard);

        // 右：構造化Workflow IR JSON（Sequence/If/ForEach/While/DataReference編集対応）
        var irCard = CreateCard();
        var irDock = new DockPanel();
        irDock.Children.Add(new TextBlock
        {
            Text = "構造化 Workflow IR JSON (分岐 If / 反復 ForEach・While / DataReference 編集)",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        DockPanel.SetDock(irDock.Children[0], Dock.Top);

        var jsonBox = new TextBox
        {
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas, Yu Gothic UI"),
            FontSize = 12,
            Padding = new Thickness(8),
            BorderBrush = new SolidColorBrush(_borderSubtle)
        };
        jsonBox.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.EditorIrJson)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        irDock.Children.Add(jsonBox);
        irCard.Child = irDock;
        Grid.SetColumn(irCard, 4);
        contentGrid.Children.Add(irCard);

        Grid.SetRow(contentGrid, 1);
        grid.Children.Add(contentGrid);
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

        // 1. 検証結果（エラー、警告、対象箇所、修正案）
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

        // 2. 実行前レビュー（対象、操作、入力データ、副作用、リスク、個別承認要否）
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

        // 3. 承認パネル（全体承認＋高リスク操作の個別承認）
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

        // 停止制御バー
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

        // アクション単位の実行状態（TargetNotFound / PolicyViolation / PostconditionMismatch / ExternalOutcomeUnknown を明示区別）
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

        // 実行一覧＆追記型状態遷移イベント履歴
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

        // 上段：未終端・Unknown復旧判断UI
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

        // 下段：左＝設定UI（ローカルAI・外部AI許可・ポリシー設定）、右＝AI監査履歴
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

    private void RebuildVisualBlockChain()
    {
        _visualBlockChainPanel.Children.Clear();
        _visualBlockChainPanel.Children.Add(CreateTerminalBadge("▶ スタート", _accentSuccess));
        _visualBlockChainPanel.Children.Add(CreateConnectorArrow());

        if (_vm.EasySteps.Count == 0)
        {
            var emptyBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(_borderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            emptyBorder.Child = new TextBlock { Text = "アクションを追加してください", Foreground = new SolidColorBrush(_textSecondary) };
            _visualBlockChainPanel.Children.Add(emptyBorder);
            _visualBlockChainPanel.Children.Add(CreateConnectorArrow());
        }
        else
        {
            foreach (var step in _vm.EasySteps)
            {
                _visualBlockChainPanel.Children.Add(CreateEventBlockNode(step));
                _visualBlockChainPanel.Children.Add(CreateConnectorArrow());
            }
        }

        _visualBlockChainPanel.Children.Add(CreateTerminalBadge("🏁 完了", Color.FromRgb(71, 85, 105)));
    }

    private UIElement CreateEventBlockNode(EasyStepItem step)
    {
        Color accentColor;
        try
        {
            accentColor = (Color)ColorConverter.ConvertFromString(step.AccentColorHex);
        }
        catch
        {
            accentColor = _accentPrimary;
        }

        var blockBorder = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(accentColor),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Background = new SolidColorBrush(accentColor),
            CornerRadius = new CornerRadius(6),
            Width = 34,
            Height = 34,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock
        {
            Text = $"{step.StepNumber}",
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoStack.Children.Add(new TextBlock { Text = step.FriendlyTitle, FontWeight = FontWeights.Bold, FontSize = 13 });
        infoStack.Children.Add(new TextBlock { Text = step.FriendlyDetail, FontSize = 11.5, Foreground = new SolidColorBrush(_textSecondary), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        var ctrlStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var upBtn = CreateButton("↑", _accentNeutral, _textSecondary, isCompact: true);
        upBtn.Padding = new Thickness(6, 2, 6, 2);
        upBtn.Margin = new Thickness(0, 0, 3, 0);
        upBtn.Click += (_, _) => _vm.MoveStepUp(step);
        ctrlStack.Children.Add(upBtn);

        var downBtn = CreateButton("↓", _accentNeutral, _textSecondary, isCompact: true);
        downBtn.Padding = new Thickness(6, 2, 6, 2);
        downBtn.Margin = new Thickness(0, 0, 3, 0);
        downBtn.Click += (_, _) => _vm.MoveStepDown(step);
        ctrlStack.Children.Add(downBtn);

        var delBtn = CreateButton("✕", Color.FromRgb(254, 226, 226), Color.FromRgb(185, 28, 28), isCompact: true);
        delBtn.Padding = new Thickness(6, 2, 6, 2);
        delBtn.Click += (_, _) => _vm.RemoveSpecificStep(step);
        ctrlStack.Children.Add(delBtn);

        Grid.SetColumn(ctrlStack, 2);
        grid.Children.Add(ctrlStack);

        blockBorder.Child = grid;
        return blockBorder;
    }

    private static UIElement CreateConnectorArrow()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 1) };
        stack.Children.Add(new Border { Width = 2, Height = 10, Background = new SolidColorBrush(Color.FromRgb(148, 163, 184)), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = "▼", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -3, 0, 0) });
        return stack;
    }

    private static UIElement CreateTerminalBadge(string text, Color bg)
    {
        var border = new Border { Background = new SolidColorBrush(bg), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Center };
        border.Child = new TextBlock { Text = text, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11.5 };
        return border;
    }

    private void UpdateInputFieldsVisibility(string actionKind)
    {
        if (_fieldUrlPanel is null) return;

        bool isNav = actionKind == IrConstants.ActionChromeNavigate;
        bool isFill = actionKind == IrConstants.ActionChromeFill;
        bool isClick = actionKind == IrConstants.ActionChromeClick;
        bool isSubmit = actionKind == IrConstants.ActionChromeSubmit;
        bool isExcelRead = actionKind == IrConstants.ActionExcelReadRange;
        bool isExcelWrite = actionKind == IrConstants.ActionExcelWriteRange;
        bool isCustomClick = actionKind == IrConstants.ActionCustomAppClick;
        bool isCustomFill = actionKind == IrConstants.ActionCustomAppFill;
        bool isCustomRead = actionKind == IrConstants.ActionCustomAppReadText;

        _fieldUrlPanel.Visibility = (isNav || isSubmit) ? Visibility.Visible : Visibility.Collapsed;
        _fieldElementPanel.Visibility = (isFill || isClick || isSubmit || isCustomClick || isCustomFill || isCustomRead) ? Visibility.Visible : Visibility.Collapsed;
        _fieldValuePanel.Visibility = (isFill || isCustomFill) ? Visibility.Visible : Visibility.Collapsed;
        _fieldPasswordCheck.Visibility = (isFill || isCustomFill) ? Visibility.Visible : Visibility.Collapsed;

        _fieldExcelPathPanel.Visibility = (isExcelRead || isExcelWrite) ? Visibility.Visible : Visibility.Collapsed;
        _fieldExcelMetaGrid.Visibility = (isExcelRead || isExcelWrite) ? Visibility.Visible : Visibility.Collapsed;
        _fieldExcelWritePanel.Visibility = isExcelWrite ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Border CreateCard() =>
        new()
        {
            Background = new SolidColorBrush(_bgCard),
            BorderBrush = new SolidColorBrush(_borderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14)
        };

    private static DataGrid CreateCleanDataGrid() =>
        new()
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(_borderSubtle),
            BorderThickness = new Thickness(1),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(_borderSubtle),
            RowBackground = Brushes.White,
            AlternatingRowBackground = new SolidColorBrush(_bgCanvas),
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0,
            RowHeight = 32,
            FontSize = 12.5
        };

    private static TextBlock CreateFieldLabel(string text) =>
        new()
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_textSecondary),
            Margin = new Thickness(0, 0, 0, 4)
        };

    private static TextBox CreateCleanTextBox(string bindingPath)
    {
        var tb = new TextBox
        {
            Padding = new Thickness(8, 5, 8, 5),
            BorderBrush = new SolidColorBrush(_borderSubtle),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(_bgCard),
            Foreground = new SolidColorBrush(_textPrimary)
        };
        tb.SetBinding(TextBox.TextProperty, new Binding(bindingPath) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        return tb;
    }

    private static StackPanel CreateLabeledInput(string label, string bindingPath)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(CreateFieldLabel(label));
        sp.Children.Add(CreateCleanTextBox(bindingPath));
        return sp;
    }

    private static Button CreateButton(string text, Color bg, Color fg, bool isCompact)
    {
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
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
