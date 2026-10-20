// Module: @role: IT知識の少ないユーザーでも直感的に操作できるガイド付きWPFメインウィンドウ
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Presentation;

public sealed class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly TabControl _mainTabControl;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        Title = "かんたんAIマクロ (AI Macro System v0.7) - Chrome & Excel 自動化アシスタント";
        Width = 1240;
        Height = 840;
        Background = new SolidColorBrush(Color.FromRgb(244, 246, 249));

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 1. 上部ステータス案内バナー
        var bannerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 58, 138)),
            Padding = new Thickness(16, 12, 16, 12)
        };
        var bannerText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        bannerText.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.StatusBanner)));
        bannerBorder.Child = bannerText;
        Grid.SetRow(bannerBorder, 0);
        rootGrid.Children.Add(bannerBorder);

        // 2. 常時表示のクイックアクションバー（迷わず使えるワンクリック導線）
        var quickBarBorder = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(219, 224, 233)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10, 16, 10)
        };
        var quickPanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

        quickPanel.Children.Add(new TextBlock
        {
            Text = "かんたん操作メニュー：",
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });

        var sampleBtn = CreateStyledButton("💡 ① お試しサンプル読込", Color.FromRgb(71, 85, 105));
        sampleBtn.Click += (_, _) => { _vm.LoadSampleMacro(); _mainTabControl!.SelectedIndex = 0; };
        quickPanel.Children.Add(sampleBtn);

        var saveBtn = CreateStyledButton("💾 ② マクロを保存", Color.FromRgb(37, 99, 235));
        saveBtn.Click += (_, _) => _vm.SaveCurrentWorkflowAsVersion();
        quickPanel.Children.Add(saveBtn);

        var approveBtn = CreateStyledButton("✅ ③ 安全確認して承認", Color.FromRgb(13, 148, 136));
        approveBtn.Click += (_, _) => { _vm.ApproveCurrentWorkflow(); _mainTabControl!.SelectedIndex = 2; };
        quickPanel.Children.Add(approveBtn);

        var oneClickRunBtn = CreateStyledButton("▶ ④ マクロをワンクリック実行（保存＋承認＋自動実行）", Color.FromRgb(22, 163, 74));
        oneClickRunBtn.Click += async (_, _) =>
        {
            _mainTabControl!.SelectedIndex = 3;
            await _vm.ExecuteOneClickAllInOneAsync();
        };
        quickPanel.Children.Add(oneClickRunBtn);

        quickBarBorder.Child = quickPanel;
        Grid.SetRow(quickBarBorder, 1);
        rootGrid.Children.Add(quickBarBorder);

        // 3. メインタブ
        _mainTabControl = new TabControl
        {
            Margin = new Thickness(14, 10, 14, 14),
            FontSize = 13
        };

        _mainTabControl.Items.Add(CreateTab1EasyBuilder());
        _mainTabControl.Items.Add(CreateTab2RecordingAndAi());
        _mainTabControl.Items.Add(CreateTab3SafetyAndApproval());
        _mainTabControl.Items.Add(CreateTab4ExecutionMonitor());
        _mainTabControl.Items.Add(CreateTab5HistoryAndRecovery());
        _mainTabControl.Items.Add(CreateTab6Settings());

        Grid.SetRow(_mainTabControl, 2);
        rootGrid.Children.Add(_mainTabControl);

        Content = rootGrid;
    }

    private TabItem CreateTab1EasyBuilder()
    {
        var mainGrid = new Grid { Margin = new Thickness(10) };
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左カラム：保存済みマクロ一覧
        var leftGroup = new GroupBox
        {
            Header = "📁 保存済みのマクロ一覧",
            Padding = new Thickness(8),
            Background = Brushes.White
        };
        var leftDock = new DockPanel();
        var loadVerBtn = CreateStyledButton("📂 選んだマクロを呼び出す", Color.FromRgb(37, 99, 235));
        DockPanel.SetDock(loadVerBtn, Dock.Bottom);
        leftDock.Children.Add(loadVerBtn);

        var verGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            Margin = new Thickness(0, 0, 0, 8)
        };
        verGrid.Columns.Add(new DataGridTextColumn { Header = "マクロ名", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        verGrid.Columns.Add(new DataGridTextColumn { Header = "版", Binding = new Binding("VersionNumber"), Width = 45 });
        verGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.WorkflowVersions)));
        loadVerBtn.Click += (_, _) =>
        {
            if (verGrid.SelectedItem is WorkflowVersionRecord rec)
            {
                _vm.LoadSelectedVersionIntoEditor(rec);
            }
        };
        leftDock.Children.Add(verGrid);
        leftGroup.Content = leftDock;
        Grid.SetColumn(leftGroup, 0);
        mainGrid.Children.Add(leftGroup);

        // 右カラム：かんたん手順ビルダー
        var rightScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var rightStack = new StackPanel();

        // マクロ基本情報
        var nameGroup = new GroupBox
        {
            Header = "🏷 マクロの名前",
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 10),
            Background = Brushes.White
        };
        var namePanel = new DockPanel();
        namePanel.Children.Add(new TextBlock { Text = "わかりやすい名前：", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var nameBox = new TextBox { Padding = new Thickness(6) };
        nameBox.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.WorkflowNameInput)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        namePanel.Children.Add(nameBox);
        nameGroup.Content = namePanel;
        rightStack.Children.Add(nameGroup);

        // ステップ追加フォーム
        var addGroup = new GroupBox
        {
            Header = "➕ やりたい操作を選んで手順に追加（専門知識は不要です）",
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            Background = Brushes.White
        };
        var formStack = new StackPanel();

        formStack.Children.Add(new TextBlock { Text = "① やりたい操作の種類を選んでください：", FontWeight = FontWeights.Bold });
        var actionCombo = new ComboBox
        {
            DisplayMemberPath = "Value",
            SelectedValuePath = "Key",
            Margin = new Thickness(0, 4, 0, 10),
            Padding = new Thickness(6)
        };
        actionCombo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.AvailableActionKinds)));
        actionCombo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(MainViewModel.SelectedActionKind)));
        formStack.Children.Add(actionCombo);

        // Web操作用入力欄
        var webWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        webWrap.Children.Add(CreateLabeledTextBox("WebページのURL:", nameof(MainViewModel.StepUrlInput), 280));
        webWrap.Children.Add(CreateLabeledTextBox("ボタンや入力欄の名前:", nameof(MainViewModel.StepElementNameInput), 180));
        webWrap.Children.Add(CreateLabeledTextBox("入力する文字:", nameof(MainViewModel.StepFillValueInput), 180));

        var pwdCheck = new CheckBox
        {
            Content = "🔒 パスワードとして保護する",
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 6)
        };
        pwdCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.StepIsPasswordInput)));
        webWrap.Children.Add(pwdCheck);
        formStack.Children.Add(webWrap);

        // Excel操作用入力欄
        var excelWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        excelWrap.Children.Add(CreateLabeledTextBox("Excelファイル保存先パス:", nameof(MainViewModel.StepExcelPathInput), 320));
        excelWrap.Children.Add(CreateLabeledTextBox("シート名:", nameof(MainViewModel.StepExcelSheetInput), 100));
        excelWrap.Children.Add(CreateLabeledTextBox("セル範囲 (例 A1:B2):", nameof(MainViewModel.StepExcelRangeInput), 130));
        excelWrap.Children.Add(CreateLabeledTextBox("書き込む内容 (カンマ区切り):", nameof(MainViewModel.StepExcelWriteTextInput), 220));
        formStack.Children.Add(excelWrap);

        var btnBar = new WrapPanel();
        var addStepBtn = CreateStyledButton("＋ この操作を手順リストに追加する", Color.FromRgb(37, 99, 235));
        addStepBtn.Click += (_, _) => _vm.AddStepFromBuilderUi();
        btnBar.Children.Add(addStepBtn);

        var removeLastBtn = CreateStyledButton("↩ 最後の1つを取り消す", Color.FromRgb(100, 116, 139));
        removeLastBtn.Click += (_, _) => _vm.RemoveLastEasyStep();
        btnBar.Children.Add(removeLastBtn);

        var clearBtn = CreateStyledButton("🗑 すべてクリア", Color.FromRgb(185, 28, 28));
        clearBtn.Click += (_, _) => _vm.ClearEasySteps();
        btnBar.Children.Add(clearBtn);

        formStack.Children.Add(btnBar);
        addGroup.Content = formStack;
        rightStack.Children.Add(addGroup);

        // 現在の手順リスト
        var stepsGroup = new GroupBox
        {
            Header = "📋 現在のマクロ手順リスト（上から順番に自動実行されます）",
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 10),
            Background = Brushes.White
        };
        var stepsGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            Height = 200
        };
        stepsGrid.Columns.Add(new DataGridTextColumn { Header = "順番", Binding = new Binding("StepNumber"), Width = 55 });
        stepsGrid.Columns.Add(new DataGridTextColumn { Header = "やること", Binding = new Binding("FriendlyTitle"), Width = 240 });
        stepsGrid.Columns.Add(new DataGridTextColumn { Header = "詳しい内容", Binding = new Binding("FriendlyDetail"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        stepsGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.EasySteps)));
        stepsGroup.Content = stepsGrid;
        rightStack.Children.Add(stepsGroup);

        // 上級者向けJSON IR折りたたみ
        var irExpander = new Expander
        {
            Header = "🔧 内部データ (Workflow IR v0.7 JSON) を確認・直接編集する（上級者向け）",
            Background = Brushes.White,
            Padding = new Thickness(8)
        };
        var irDock = new DockPanel();
        var revalBtn = CreateStyledButton("JSONを再検証する", Color.FromRgb(71, 85, 105));
        revalBtn.Click += (_, _) => _vm.ValidateCurrentEditorIr();
        DockPanel.SetDock(revalBtn, Dock.Top);
        irDock.Children.Add(revalBtn);

        var irBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas")
        };
        irBox.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.EditorIrJson)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        irDock.Children.Add(irBox);
        irExpander.Content = irDock;
        rightStack.Children.Add(irExpander);

        rightScroll.Content = rightStack;
        Grid.SetColumn(rightScroll, 2);
        mainGrid.Children.Add(rightScroll);

        return new TabItem { Header = "① かんたん作成・編集", Content = mainGrid };
    }

    private TabItem CreateTab2RecordingAndAi()
    {
        var stack = new StackPanel { Margin = new Thickness(12) };

        var guideGroup = new GroupBox
        {
            Header = "🎥 お手本操作を記録してマクロを自動作成する（AI変換 & パスワード自動保護）",
            Padding = new Thickness(12),
            Background = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var ctrlStack = new StackPanel();
        ctrlStack.Children.Add(new TextBlock
        {
            Text = "「🔴 記録スタート」を押してから操作を行うと、AIが手順を自動で組み立てます。パスワード欄は自動的に伏せ字（[REDACTED]）として安全に保護されます。",
            Margin = new Thickness(0, 0, 0, 10)
        });

        var recBtnWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var startRecBtn = CreateStyledButton("🔴 記録スタート", Color.FromRgb(220, 38, 38));
        startRecBtn.Click += (_, _) => _vm.StartEasyRecording();
        recBtnWrap.Children.Add(startRecBtn);

        var stopRecBtn = CreateStyledButton("⏹ 記録ストップ", Color.FromRgb(71, 85, 105));
        stopRecBtn.Click += (_, _) => _vm.StopEasyRecording();
        recBtnWrap.Children.Add(stopRecBtn);

        var simWebBtn = CreateStyledButton("🌐 Webページ移動を記録", Color.FromRgb(37, 99, 235));
        simWebBtn.Click += (_, _) => _vm.RecordSimulatedWebOpen();
        recBtnWrap.Children.Add(simWebBtn);

        var simInputBtn = CreateStyledButton("✏️ 文字入力を記録", Color.FromRgb(37, 99, 235));
        simInputBtn.Click += (_, _) => _vm.RecordSimulatedWebInput(asPassword: false);
        recBtnWrap.Children.Add(simInputBtn);

        var simPwdBtn = CreateStyledButton("🔒 パスワード入力を記録（自動保護テスト）", Color.FromRgb(147, 51, 234));
        simPwdBtn.Click += (_, _) => _vm.RecordSimulatedWebInput(asPassword: true);
        recBtnWrap.Children.Add(simPwdBtn);

        var simExcelBtn = CreateStyledButton("📝 Excel書き込みを記録", Color.FromRgb(22, 163, 74));
        simExcelBtn.Click += (_, _) => _vm.RecordSimulatedExcelWrite();
        recBtnWrap.Children.Add(simExcelBtn);

        ctrlStack.Children.Add(recBtnWrap);

        var aiGenBtn = CreateStyledButton("✨ 記録した操作からマクロをAI自動生成する（安全チェック付き）", Color.FromRgb(13, 148, 136));
        aiGenBtn.Click += async (_, _) =>
        {
            await _vm.GenerateMacroFromRecordingViaAiAsync();
            _mainTabControl.SelectedIndex = 2;
        };
        ctrlStack.Children.Add(aiGenBtn);

        guideGroup.Content = ctrlStack;
        stack.Children.Add(guideGroup);

        var listGroup = new GroupBox
        {
            Header = "📜 記録された操作イベント一覧（機密情報は取得段階で除外されています）",
            Padding = new Thickness(10),
            Background = Brushes.White
        };
        var recGrid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 360 };
        recGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.RecordedEvents)));
        listGroup.Content = recGrid;
        stack.Children.Add(listGroup);

        return new TabItem { Header = "② お手本記録・AI作成", Content = stack };
    }

    private TabItem CreateTab3SafetyAndApproval()
    {
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 上部：ワンクリック承認カード
        var approveGroup = new GroupBox
        {
            Header = "✅ 実行前の最終確認と承認（勝手に危険な操作をしないための安全ロック）",
            Padding = new Thickness(12),
            Background = Brushes.White,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var appStack = new StackPanel();
        var statusTxt = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) };
        statusTxt.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.CurrentApprovalStatusText)));
        appStack.Children.Add(statusTxt);

        var appInputWrap = new WrapPanel();
        appInputWrap.Children.Add(CreateLabeledTextBox("承認する人の名前:", nameof(MainViewModel.ApproverNameInput), 180));
        var highRiskChk = new CheckBox
        {
            Content = "外部送信や上書きなどの重要操作も含めて個別に承認する",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 14, 16, 0)
        };
        highRiskChk.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.ApproveHighRiskChecked)));
        appInputWrap.Children.Add(highRiskChk);

        var doApproveBtn = CreateStyledButton("✅ この内容でマクロを承認する", Color.FromRgb(13, 148, 136));
        doApproveBtn.Click += (_, _) => _vm.ApproveCurrentWorkflow();
        appInputWrap.Children.Add(doApproveBtn);

        var goRunBtn = CreateStyledButton("▶ 承認して今すぐマクロを実行する", Color.FromRgb(22, 163, 74));
        goRunBtn.Click += async (_, _) =>
        {
            _mainTabControl.SelectedIndex = 3;
            await _vm.ExecuteOneClickAllInOneAsync();
        };
        appInputWrap.Children.Add(goRunBtn);

        appStack.Children.Add(appInputWrap);
        approveGroup.Content = appStack;
        Grid.SetRow(approveGroup, 0);
        grid.Children.Add(approveGroup);

        // 中段：これから行う操作と影響（実行前レビュー）
        var planGroup = new GroupBox
        {
            Header = "🔍 これからマクロが行う操作と安全レベルの一覧（実行前レビュー）",
            Padding = new Thickness(8),
            Background = Brushes.White,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var planGrid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true };
        planGrid.Columns.Add(new DataGridTextColumn { Header = "ステップ", Binding = new Binding("NodeId"), Width = 90 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "操作の内容", Binding = new Binding("OperationName"), Width = 200 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "操作の対象 (URL / Excel範囲)", Binding = new Binding("TargetDescription"), Width = 320 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "パソコンや外部への影響", Binding = new Binding("SideEffectDescription"), Width = 220 });
        planGrid.Columns.Add(new DataGridTextColumn { Header = "リスク判定", Binding = new Binding("RiskBadge"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        planGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.FriendlyPlanItems)));
        planGroup.Content = planGrid;
        Grid.SetRow(planGroup, 1);
        grid.Children.Add(planGroup);

        // 下段：安全チェック結果（問題がある場合のみ具体的理由と直し方を表示）
        var valGroup = new GroupBox
        {
            Header = "🛡️ 安全チェック結果（エラーがある場合はここに理由と直し方が表示されます）",
            Padding = new Thickness(8),
            Background = Brushes.White
        };
        var valGrid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true };
        valGrid.Columns.Add(new DataGridTextColumn { Header = "状態", Binding = new Binding("Severity"), Width = 90 });
        valGrid.Columns.Add(new DataGridTextColumn { Header = "場所", Binding = new Binding("FriendlyLocation"), Width = 120 });
        valGrid.Columns.Add(new DataGridTextColumn { Header = "直し方ガイド", Binding = new Binding("HowToFix"), Width = 420 });
        valGrid.Columns.Add(new DataGridTextColumn { Header = "詳細な理由", Binding = new Binding("FriendlyReason"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        valGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.FriendlyValidationIssues)));
        valGroup.Content = valGrid;
        Grid.SetRow(valGroup, 2);
        grid.Children.Add(valGroup);

        return new TabItem { Header = "③ 安全チェック・承認", Content = grid };
    }

    private TabItem CreateTab4ExecutionMonitor()
    {
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var ctrlGroup = new GroupBox
        {
            Header = "🎛️ マクロ実行コントロール（実行中いつでも一時停止・安全キャンセルできます）",
            Padding = new Thickness(12),
            Background = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var btnWrap = new WrapPanel();

        var runBtn = CreateStyledButton("▶ 承認済みマクロを実行開始", Color.FromRgb(22, 163, 74));
        runBtn.Click += async (_, _) => await _vm.ExecuteApprovedMacroAsync();
        btnWrap.Children.Add(runBtn);

        var pauseBtn = CreateStyledButton("⏸ 一時停止する", Color.FromRgb(217, 119, 6));
        pauseBtn.Click += (_, _) => _vm.PauseActiveMacro();
        btnWrap.Children.Add(pauseBtn);

        var resumeBtn = CreateStyledButton("▶ 再開する", Color.FromRgb(37, 99, 235));
        resumeBtn.Click += (_, _) => _vm.ResumeActiveMacro();
        btnWrap.Children.Add(resumeBtn);

        var cancelBtn = CreateStyledButton("⏹ 安全に停止（キャンセル）", Color.FromRgb(220, 38, 38));
        cancelBtn.Click += (_, _) => _vm.CancelActiveMacro();
        btnWrap.Children.Add(cancelBtn);

        ctrlGroup.Content = btnWrap;
        Grid.SetRow(ctrlGroup, 0);
        grid.Children.Add(ctrlGroup);

        var monGroup = new GroupBox
        {
            Header = "📊 各ステップの実行結果・読み戻し確認モニター",
            Padding = new Thickness(10),
            Background = Brushes.White
        };
        var actGrid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true };
        actGrid.Columns.Add(new DataGridTextColumn { Header = "ステップID", Binding = new Binding("NodeId"), Width = 110 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "状態", Binding = new Binding("Status"), Width = 120 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "試行回数", Binding = new Binding("AttemptCount"), Width = 80 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "エラー分類", Binding = new Binding("LastFailureClassification"), Width = 180 });
        actGrid.Columns.Add(new DataGridTextColumn { Header = "実行結果・確認メッセージ", Binding = new Binding("LastMessage"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        actGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.CurrentRunActionStatuses)));
        monGroup.Content = actGrid;
        Grid.SetRow(monGroup, 1);
        grid.Children.Add(monGroup);

        return new TabItem { Header = "④ マクロ実行モニター", Content = grid };
    }

    private TabItem CreateTab5HistoryAndRecovery()
    {
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 上段：異常終了・Unknownからのトラブル復旧パネル
        var recGroup = new GroupBox
        {
            Header = "🚑 トラブル復旧パネル（パソコンの電源切れや通信エラーで途中停止したマクロの安全確認）",
            Padding = new Thickness(12),
            Background = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var recStack = new StackPanel();
        recStack.Children.Add(new TextBlock
        {
            Text = "※ 途中で異常終了したマクロがある場合、二重送信や二重書き込みを防ぐため、確認が終わるまで新しい実行を自動でブロックします。",
            Margin = new Thickness(0, 0, 0, 8)
        });

        var recBtnWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var resolveFailBtn = CreateStyledButton("✖ 「未完了・中止」として解決しロックを解除する", Color.FromRgb(22, 163, 74));
        resolveFailBtn.Click += (_, _) => _vm.ResolveFirstPendingRecovery(RecoveryStatus.ResolvedAsFailed, "ユーザーが画面・ファイルを確認し未完了として解決");
        recBtnWrap.Children.Add(resolveFailBtn);

        var resolveDoneBtn = CreateStyledButton("✔ 「すでに完了していた」として解決しロックを解除する", Color.FromRgb(37, 99, 235));
        resolveDoneBtn.Click += (_, _) => _vm.ResolveFirstPendingRecovery(RecoveryStatus.ResolvedAsCompleted, "ユーザーが外部システムの完了を確認して解決");
        recBtnWrap.Children.Add(resolveDoneBtn);

        var simCrashBtn = CreateStyledButton("⚡ 異常終了（停電等）の保護動作をテスト体験する", Color.FromRgb(100, 116, 139));
        simCrashBtn.Click += async (_, _) => await _vm.SimulateAbnormalTerminationForDemoAsync();
        recBtnWrap.Children.Add(simCrashBtn);

        recStack.Children.Add(recBtnWrap);

        var pendingGrid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 110 };
        pendingGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.PendingRecoveries)));
        recStack.Children.Add(pendingGrid);
        recGroup.Content = recStack;
        Grid.SetRow(recGroup, 0);
        grid.Children.Add(recGroup);

        // 下段：実行履歴と監査イベントログ
        var histGroup = new GroupBox
        {
            Header = "📜 監査・状態遷移ログ（いつ・どのバージョンが・どう動いたかの完全な記録）",
            Padding = new Thickness(8),
            Background = Brushes.White
        };
        var evGrid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true };
        evGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.ExecutionHistoryEvents)));
        histGroup.Content = evGrid;
        Grid.SetRow(histGroup, 1);
        grid.Children.Add(histGroup);

        return new TabItem { Header = "⑤ 実行履歴・トラブル復旧", Content = grid };
    }

    private TabItem CreateTab6Settings()
    {
        var stack = new StackPanel { Margin = new Thickness(16) };

        var group = new GroupBox
        {
            Header = "⚙️ 動作モード・AI・セキュリティ設定",
            Padding = new Thickness(14),
            Background = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var inner = new StackPanel();

        var browserCheck = new CheckBox
        {
            Content = "🌐 Webページを開くステップで、パソコンの標準ブラウザー（Chrome / Edge）も実際に開く",
            Margin = new Thickness(0, 0, 0, 12),
            FontWeight = FontWeights.Bold
        };
        browserCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.OpenSystemBrowserOnNavigate)));
        inner.Children.Add(browserCheck);

        inner.Children.Add(new TextBlock { Text = "ローカルAI (Ollama) 接続先URL:" });
        var endpointBox = new TextBox { Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(6) };
        endpointBox.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.LocalAiEndpoint)));
        inner.Children.Add(endpointBox);

        inner.Children.Add(new TextBlock { Text = "使用するローカルAIモデル名:" });
        var modelBox = new TextBox { Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(6) };
        modelBox.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.LocalAiModel)));
        inner.Children.Add(modelBox);

        var extAiCheck = new CheckBox
        {
            Content = "クラウド等の外部AI利用を許可する（OFFの場合はパソコン内のローカルAIのみを使用します）"
        };
        extAiCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.AllowExternalAiPolicy)));
        inner.Children.Add(extAiCheck);

        group.Content = inner;
        stack.Children.Add(group);

        var aiAuditGroup = new GroupBox
        {
            Header = "🤖 AI生成・検証の監査履歴",
            Padding = new Thickness(10),
            Background = Brushes.White
        };
        var aiGrid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 260 };
        aiGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.AiAuditLogs)));
        aiAuditGroup.Content = aiGrid;
        stack.Children.Add(aiAuditGroup);

        return new TabItem { Header = "⑥ 設定（AI・安全ルール）", Content = stack };
    }

    private static Button CreateStyledButton(string text, Color bgColor)
    {
        return new Button
        {
            Content = text,
            Background = new SolidColorBrush(bgColor),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 6),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }

    private static StackPanel CreateLabeledTextBox(string label, string bindingPath, double width)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 6) };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 2) });
        var tb = new TextBox { Width = width, Padding = new Thickness(5) };
        tb.SetBinding(TextBox.TextProperty, new Binding(bindingPath) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        sp.Children.Add(tb);
        return sp;
    }
}
