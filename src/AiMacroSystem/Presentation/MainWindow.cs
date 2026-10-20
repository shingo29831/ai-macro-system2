// Module: @role: 最小限のボタンと動的フォームによるモダン・シンプルなWPFメインウィンドウ
using System.Collections.Specialized;
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
    private static readonly Color _accentDanger = Color.FromRgb(239, 68, 68);
    private static readonly Color _accentNeutral = Color.FromRgb(241, 245, 249);

    private readonly MainViewModel _vm;
    private readonly TabControl _tabControl;

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
        Title = "AI Macro";
        Width = 1040;
        Height = 720;
        MinWidth = 860;
        MinHeight = 600;
        FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI");
        Background = new SolidColorBrush(_bgCanvas);
        Foreground = new SolidColorBrush(_textPrimary);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 1. モダンヘッダーバー（タイトル＋ステータス＋最小限の主ボタン）
        Grid.SetRow(CreateHeaderBar(), 0);
        root.Children.Add(CreateHeaderBar());

        // 2. 異常終了時のみ表示される1行リカバリーバー
        var recoveryBanner = CreateRecoveryAlertBar();
        Grid.SetRow(recoveryBanner, 1);
        root.Children.Add(recoveryBanner);

        // 3. シンプルな3タブ構成
        _tabControl = new TabControl
        {
            Margin = new Thickness(24, 16, 24, 24),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 13.5
        };

        _tabControl.Items.Add(CreateWorkspaceTab());
        _tabControl.Items.Add(CreateStatusAndSafetyTab());
        _tabControl.Items.Add(CreateSettingsAndAdvancedTab());

        Grid.SetRow(_tabControl, 2);
        root.Children.Add(_tabControl);

        Content = root;
        UpdateInputFieldsVisibility(_vm.SelectedActionKind);
    }

    private Border CreateHeaderBar()
    {
        var header = new Border
        {
            Background = new SolidColorBrush(_bgCard),
            BorderBrush = new SolidColorBrush(_borderSubtle),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 14, 24, 14)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // アプリロゴ＋タイトル
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
            Text = "Macro Studio",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(titleStack, 0);
        grid.Children.Add(titleStack);

        // ステータスピル（1行・控えめ）
        var statusPill = new Border
        {
            Background = new SolidColorBrush(_accentNeutral),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(24, 0, 24, 0),
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

        // 右側メイン操作ボタン（最小限の2ボタン：停止 / 実行）
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var stopBtn = CreateButton("⏹ 停止", _accentNeutral, _textPrimary, isCompact: true);
        stopBtn.Margin = new Thickness(0, 0, 10, 0);
        stopBtn.Click += (_, _) => _vm.CancelActiveMacro();
        actions.Children.Add(stopBtn);

        var runBtn = CreateButton("▶ 実行する", _accentSuccess, Brushes.White.Color, isCompact: false);
        runBtn.Click += async (_, _) =>
        {
            await _vm.ExecuteOneClickAllInOneAsync();
            _tabControl.SelectedIndex = 1;
        };
        actions.Children.Add(runBtn);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

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
        var unlockBtn = CreateButton("確認してロック解除", _accentDanger, Colors.White, isCompact: true);
        unlockBtn.Click += (_, _) => _vm.ResolveFirstPendingRecovery(RecoveryStatus.ResolvedAsFailed, "ユーザー確認によるロック解除");
        DockPanel.SetDock(unlockBtn, Dock.Right);
        dock.Children.Add(unlockBtn);

        dock.Children.Add(new TextBlock
        {
            Text = "⚠️ 前回途中で終了したマクロがあります。二重実行を防ぐため安全ロック中です。",
            Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28)),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        bar.Child = dock;
        return bar;
    }

    private TabItem CreateWorkspaceTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左カード：手順リスト
        var leftCard = CreateCard();
        var leftDock = new DockPanel();

        // 左ヘッダー（マクロ名＋控えめな補助ボタン）
        var leftTop = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        leftTop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        leftTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBox = CreateCleanTextBox(nameof(MainViewModel.WorkflowNameInput));
        nameBox.FontSize = 15;
        nameBox.FontWeight = FontWeights.Bold;
        Grid.SetColumn(nameBox, 0);
        leftTop.Children.Add(nameBox);

        var subBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        var sampleBtn = CreateButton("サンプル", _accentNeutral, _textSecondary, isCompact: true);
        sampleBtn.Margin = new Thickness(0, 0, 6, 0);
        sampleBtn.Click += (_, _) => _vm.LoadSampleMacro();
        subBtns.Children.Add(sampleBtn);

        var undoBtn = CreateButton("1つ戻す", _accentNeutral, _textSecondary, isCompact: true);
        undoBtn.Margin = new Thickness(0, 0, 6, 0);
        undoBtn.Click += (_, _) => _vm.RemoveLastEasyStep();
        subBtns.Children.Add(undoBtn);

        var clearBtn = CreateButton("クリア", _accentNeutral, _textSecondary, isCompact: true);
        clearBtn.Click += (_, _) => _vm.ClearEasySteps();
        subBtns.Children.Add(clearBtn);

        Grid.SetColumn(subBtns, 1);
        leftTop.Children.Add(subBtns);
        DockPanel.SetDock(leftTop, Dock.Top);
        leftDock.Children.Add(leftTop);

        // ステップ一覧（カード風リスト）
        var stepsGrid = CreateCleanDataGrid();
        stepsGrid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding("StepNumber"), Width = 40 });
        stepsGrid.Columns.Add(new DataGridTextColumn { Header = "操作", Binding = new Binding("FriendlyTitle"), Width = 185 });
        stepsGrid.Columns.Add(new DataGridTextColumn { Header = "内容", Binding = new Binding("FriendlyDetail"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        stepsGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.EasySteps)));
        leftDock.Children.Add(stepsGrid);

        leftCard.Child = leftDock;
        Grid.SetColumn(leftCard, 0);
        grid.Children.Add(leftCard);

        // 右カード：ステップ追加（選んだ操作に必要な項目だけを表示）＋ AI自動作成
        var rightStack = new StackPanel();

        var addCard = CreateCard();
        var formStack = new StackPanel();
        formStack.Children.Add(new TextBlock
        {
            Text = "手順を追加",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 14)
        });

        formStack.Children.Add(CreateFieldLabel("やりたい操作"));
        var actionCombo = new ComboBox
        {
            DisplayMemberPath = "Value",
            SelectedValuePath = "Key",
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 14)
        };
        actionCombo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.AvailableActionKinds)));
        actionCombo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(MainViewModel.SelectedActionKind)));
        actionCombo.SelectionChanged += (_, _) =>
        {
            if (actionCombo.SelectedValue is string kind)
            {
                UpdateInputFieldsVisibility(kind);
            }
        };
        formStack.Children.Add(actionCombo);

        // 動的フィールド群（必要なものだけ表示）
        _fieldUrlPanel = CreateLabeledInput("URL (Webアドレス)", nameof(MainViewModel.StepUrlInput));
        _fieldElementPanel = CreateLabeledInput("対象の項目名・ボタン名", nameof(MainViewModel.StepElementNameInput));
        _fieldValuePanel = CreateLabeledInput("入力する文字", nameof(MainViewModel.StepFillValueInput));

        _fieldPasswordCheck = new CheckBox
        {
            Content = "パスワードとして伏せ字で保護する",
            Foreground = new SolidColorBrush(_textSecondary),
            Margin = new Thickness(0, 0, 0, 14)
        };
        _fieldPasswordCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.StepIsPasswordInput)));

        _fieldExcelPathPanel = CreateLabeledInput("Excelファイルの場所", nameof(MainViewModel.StepExcelPathInput));

        _fieldExcelMetaGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        _fieldExcelMetaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _fieldExcelMetaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        _fieldExcelMetaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sheetPanel = CreateLabeledInput("シート名", nameof(MainViewModel.StepExcelSheetInput));
        sheetPanel.Margin = new Thickness(0);
        Grid.SetColumn(sheetPanel, 0);
        _fieldExcelMetaGrid.Children.Add(sheetPanel);

        var rangePanel = CreateLabeledInput("セル範囲 (例 A1:B2)", nameof(MainViewModel.StepExcelRangeInput));
        rangePanel.Margin = new Thickness(0);
        Grid.SetColumn(rangePanel, 2);
        _fieldExcelMetaGrid.Children.Add(rangePanel);

        _fieldExcelWritePanel = CreateLabeledInput("書き込むデータ (カンマ区切り)", nameof(MainViewModel.StepExcelWriteTextInput));

        formStack.Children.Add(_fieldUrlPanel);
        formStack.Children.Add(_fieldElementPanel);
        formStack.Children.Add(_fieldValuePanel);
        formStack.Children.Add(_fieldPasswordCheck);
        formStack.Children.Add(_fieldExcelPathPanel);
        formStack.Children.Add(_fieldExcelMetaGrid);
        formStack.Children.Add(_fieldExcelWritePanel);

        var addBtn = CreateButton("＋ 手順に追加", _accentPrimary, Colors.White, isCompact: false);
        addBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        addBtn.Margin = new Thickness(0, 4, 0, 0);
        addBtn.Click += (_, _) => _vm.AddStepFromBuilderUi();
        formStack.Children.Add(addBtn);

        addCard.Child = formStack;
        rightStack.Children.Add(addCard);

        // AIおまかせ作成ミニカード
        var aiCard = CreateCard();
        aiCard.Margin = new Thickness(0, 16, 0, 0);
        var aiStack = new StackPanel();
        aiStack.Children.Add(new TextBlock
        {
            Text = "✨ お手本からAI自動作成",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        aiStack.Children.Add(new TextBlock
        {
            Text = "今の入力内容をお手本として記録し、AIが安全なマクロに組み立てます。",
            FontSize = 12,
            Foreground = new SolidColorBrush(_textSecondary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var aiBtn = CreateButton("🔴 お手本を記録してAI作成", _accentNeutral, _textPrimary, isCompact: false);
        aiBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        aiBtn.Click += async (_, _) =>
        {
            _vm.StartEasyRecording();
            _vm.RecordSimulatedWebOpen();
            _vm.RecordSimulatedWebInput(asPassword: _vm.StepIsPasswordInput);
            _vm.RecordSimulatedExcelWrite();
            _vm.StopEasyRecording();
            await _vm.GenerateMacroFromRecordingViaAiAsync();
        };
        aiStack.Children.Add(aiBtn);
        aiCard.Child = aiStack;
        rightStack.Children.Add(aiCard);

        Grid.SetColumn(rightStack, 2);
        grid.Children.Add(rightStack);

        return new TabItem { Header = "  手順をつくる  ", Content = grid };
    }

    private TabItem CreateStatusAndSafetyTab()
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 上段：実行結果モニター
        var runCard = CreateCard();
        var runDock = new DockPanel();

        var runHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var pauseBtn = CreateButton("⏸ 一時停止", _accentNeutral, _textPrimary, isCompact: true);
        pauseBtn.Margin = new Thickness(0, 0, 8, 0);
        pauseBtn.Click += (_, _) => _vm.PauseActiveMacro();
        var resumeBtn = CreateButton("▶ 再開", _accentNeutral, _textPrimary, isCompact: true);
        resumeBtn.Click += (_, _) => _vm.ResumeActiveMacro();

        var rightCtrls = new StackPanel { Orientation = Orientation.Horizontal };
        rightCtrls.Children.Add(pauseBtn);
        rightCtrls.Children.Add(resumeBtn);
        DockPanel.SetDock(rightCtrls, Dock.Right);
        runHeader.Children.Add(rightCtrls);

        runHeader.Children.Add(new TextBlock
        {
            Text = "実行モニター",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        DockPanel.SetDock(runHeader, Dock.Top);
        runDock.Children.Add(runHeader);

        var actGrid = CreateCleanDataGrid();
        actGrid.Columns.Add(new DataGridTextColumn { Header = "ステップ", Binding = new Binding("NodeId"), Width = 100 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "状態", Binding = new Binding("Status"), Width = 120 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "メッセージ", Binding = new Binding("LastMessage"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        actGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.CurrentRunActionStatuses)));
        runDock.Children.Add(actGrid);

        runCard.Child = runDock;
        Grid.SetRow(runCard, 0);
        grid.Children.Add(runCard);

        // 下段：安全チェック結果と影響範囲
        var safetyCard = CreateCard();
        var safetyDock = new DockPanel();
        safetyDock.Children.Add(new TextBlock
        {
            Text = "安全チェック・影響範囲",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 12)
        });
        DockPanel.SetDock(safetyDock.Children[0], Dock.Top);

        var planGrid = CreateCleanDataGrid();
        planGrid.Columns.Add(new DataGridTextColumn { Header = "操作", Binding = new Binding("OperationName"), Width = 180 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "対象", Binding = new Binding("TargetDescription"), Width = 300 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "影響", Binding = new Binding("SideEffectDescription"), Width = 220 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "リスク", Binding = new Binding("RiskBadge"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        planGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.FriendlyPlanItems)));
        safetyDock.Children.Add(planGrid);

        safetyCard.Child = safetyDock;
        Grid.SetRow(safetyCard, 2);
        grid.Children.Add(safetyCard);

        return new TabItem { Header = "  確認・結果  ", Content = grid };
    }

    private TabItem CreateSettingsAndAdvancedTab()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) };
        var stack = new StackPanel();

        // 基本設定カード
        var setCard = CreateCard();
        setCard.Margin = new Thickness(0, 0, 0, 16);
        var setStack = new StackPanel();
        setStack.Children.Add(new TextBlock { Text = "動作設定", FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });

        var browserChk = new CheckBox
        {
            Content = "Webページを開くとき、PCの標準ブラウザーも実際に起動する",
            Margin = new Thickness(0, 0, 0, 10)
        };
        browserChk.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.OpenSystemBrowserOnNavigate)));
        setStack.Children.Add(browserChk);

        var extAiChk = new CheckBox
        {
            Content = "外部AIの利用を許可する（OFFのときはPC内のローカルAIのみ使用）",
            Margin = new Thickness(0, 0, 0, 14)
        };
        extAiChk.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.AllowExternalAiPolicy)));
        setStack.Children.Add(extAiChk);

        var crashSimBtn = CreateButton("異常終了の保護ロックをテストする", _accentNeutral, _textSecondary, isCompact: true);
        crashSimBtn.HorizontalAlignment = HorizontalAlignment.Left;
        crashSimBtn.Click += async (_, _) => await _vm.SimulateAbnormalTerminationForDemoAsync();
        setStack.Children.Add(crashSimBtn);

        setCard.Child = setStack;
        stack.Children.Add(setCard);

        // 保存済みバージョン一覧カード
        var verCard = CreateCard();
        verCard.Margin = new Thickness(0, 0, 0, 16);
        var verStack = new StackPanel();
        verStack.Children.Add(new TextBlock { Text = "保存済みマクロ履歴", FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });

        var verGrid = CreateCleanDataGrid();
        verGrid.Height = 140;
        verGrid.Columns.Add(new DataGridTextColumn { Header = "マクロ名", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        verGrid.Columns.Add(new DataGridTextColumn { Header = "バージョン", Binding = new Binding("VersionNumber"), Width = 90 });
        verGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.WorkflowVersions)));
        verGrid.MouseDoubleClick += (_, _) =>
        {
            if (verGrid.SelectedItem is WorkflowVersionRecord rec)
            {
                _vm.LoadSelectedVersionIntoEditor(rec);
                _tabControl.SelectedIndex = 0;
            }
        };
        verStack.Children.Add(verGrid);
        verCard.Child = verStack;
        stack.Children.Add(verCard);

        // 上級者向けJSON IR折りたたみ
        var advCard = CreateCard();
        var exp = new Expander { Header = "上級者向け：Workflow IR (JSON) と監査ログを表示" };
        var expStack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var irBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 140,
            FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 10)
        };
        irBox.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.EditorIrJson)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        expStack.Children.Add(irBox);

        var logGrid = CreateCleanDataGrid();
        logGrid.Height = 140;
        logGrid.AutoGenerateColumns = true;
        logGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.ExecutionHistoryEvents)));
        expStack.Children.Add(logGrid);

        exp.Content = expStack;
        advCard.Child = exp;
        stack.Children.Add(advCard);

        scroll.Content = stack;
        return new TabItem { Header = "  設定・詳細  ", Content = scroll };
    }

    // 選択した操作に必要な入力欄だけを表示し、不要な項目を隠す
    private void UpdateInputFieldsVisibility(string actionKind)
    {
        if (_fieldUrlPanel is null) return;

        bool isNav = actionKind == IrConstants.ActionChromeNavigate;
        bool isFill = actionKind == IrConstants.ActionChromeFill;
        bool isClick = actionKind == IrConstants.ActionChromeClick;
        bool isSubmit = actionKind == IrConstants.ActionChromeSubmit;
        bool isExcelRead = actionKind == IrConstants.ActionExcelReadRange;
        bool isExcelWrite = actionKind == IrConstants.ActionExcelWriteRange;

        _fieldUrlPanel.Visibility = (isNav || isSubmit) ? Visibility.Visible : Visibility.Collapsed;
        _fieldElementPanel.Visibility = (isFill || isClick || isSubmit) ? Visibility.Visible : Visibility.Collapsed;
        _fieldValuePanel.Visibility = isFill ? Visibility.Visible : Visibility.Collapsed;
        _fieldPasswordCheck.Visibility = isFill ? Visibility.Visible : Visibility.Collapsed;

        _fieldExcelPathPanel.Visibility = (isExcelRead || isExcelWrite) ? Visibility.Visible : Visibility.Collapsed;
        _fieldExcelMetaGrid.Visibility = (isExcelRead || isExcelWrite) ? Visibility.Visible : Visibility.Collapsed;
        _fieldExcelWritePanel.Visibility = isExcelWrite ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Border CreateCard()
    {
        return new Border
        {
            Background = new SolidColorBrush(_bgCard),
            BorderBrush = new SolidColorBrush(_borderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20)
        };
    }

    private static DataGrid CreateCleanDataGrid()
    {
        return new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(_borderSubtle),
            BorderThickness = new Thickness(1),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(_borderSubtle),
            RowBackground = Brushes.White,
            AlternatingRowBackground = new SolidColorBrush(_bgCanvas),
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0,
            FontSize = 13
        };
    }

    private static TextBlock CreateFieldLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_textSecondary),
            Margin = new Thickness(0, 0, 0, 6)
        };
    }

    private static TextBox CreateCleanTextBox(string bindingPath)
    {
        var tb = new TextBox
        {
            Padding = new Thickness(10, 7, 10, 7),
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
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(CreateFieldLabel(label));
        sp.Children.Add(CreateCleanTextBox(bindingPath));
        return sp;
    }

    private static Button CreateButton(string text, Color bg, Color fg, bool isCompact)
    {
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
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
            FontSize = isCompact ? 12.5 : 13.5,
            Padding = isCompact ? new Thickness(12, 6, 12, 6) : new Thickness(18, 10, 18, 10),
            Cursor = Cursors.Hand
        };
    }
}
