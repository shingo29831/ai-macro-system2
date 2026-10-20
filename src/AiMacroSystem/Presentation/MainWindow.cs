// Module: @role: マクロ一覧メイン画面・ビジュアルブロック連結エディタ・実行時フローティングUI連動ウィンドウ
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
    private static readonly Color _accentDanger = Color.FromRgb(239, 68, 68);
    private static readonly Color _accentNeutral = Color.FromRgb(241, 245, 249);

    private readonly MainViewModel _vm;
    private readonly ExecutionOverlayWindow _overlayWindow;
    private readonly Grid _homeListView;
    private readonly Grid _blockEditorView;
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
        Title = "AI Macro Studio";
        Width = 1020;
        Height = 700;
        MinWidth = 840;
        MinHeight = 580;
        FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI");
        Background = new SolidColorBrush(_bgCanvas);
        Foreground = new SolidColorBrush(_textPrimary);

        _overlayWindow = new ExecutionOverlayWindow(_vm);
        _visualBlockChainPanel = new StackPanel { Margin = new Thickness(8, 4, 8, 20) };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 1. シンプルなヘッダーバー
        var header = CreateHeaderBar();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // 2. 異常終了時のリカバリー警告バー（該当がある時のみ表示）
        var recoveryBar = CreateRecoveryAlertBar();
        Grid.SetRow(recoveryBar, 1);
        root.Children.Add(recoveryBar);

        // 3. メインコンテンツ領域（ホーム画面：マクロ一覧 ⇔ 編集画面：ブロックエディタ）
        var bodyContainer = new Grid { Margin = new Thickness(24, 18, 24, 24) };
        _homeListView = CreateHomeMacroListView();
        _blockEditorView = CreateVisualBlockEditorView();
        _blockEditorView.Visibility = Visibility.Collapsed;

        bodyContainer.Children.Add(_homeListView);
        bodyContainer.Children.Add(_blockEditorView);

        Grid.SetRow(bodyContainer, 2);
        root.Children.Add(bodyContainer);

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
            _homeListView.Visibility = _vm.IsEditingMode ? Visibility.Collapsed : Visibility.Visible;
            _blockEditorView.Visibility = _vm.IsEditingMode ? Visibility.Visible : Visibility.Collapsed;
            if (_vm.IsEditingMode)
            {
                RebuildVisualBlockChain();
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsExecutingMacro))
        {
            if (_vm.IsExecutingMacro)
            {
                // マクロ実行中はメイン画面を非表示にし、モニター上部に小型オーバーレイを表示
                _overlayWindow.PositionAtTopCenter();
                _overlayWindow.Show();
                Hide();
            }
            else
            {
                _overlayWindow.Hide();
                Show();
                Activate();
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
            Padding = new Thickness(24, 14, 24, 14)
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
            Text = "Macro Studio",
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

    // --- メイン画面：マクロ一覧（選択して 実行 / 編集 / 削除） ---
    private Grid CreateHomeMacroListView()
    {
        var grid = new Grid();
        var card = CreateCard();
        var dock = new DockPanel();

        // 上部タイトル＋新規作成ボタン
        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var newBtn = CreateButton("＋ 新しいマクロを作る", _accentPrimary, Colors.White, isCompact: false);
        newBtn.Click += (_, _) => _vm.StartNewMacroInEditor();
        DockPanel.SetDock(newBtn, Dock.Right);
        topBar.Children.Add(newBtn);

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "マクロ一覧",
            FontSize = 18,
            FontWeight = FontWeights.Bold
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "一覧からマクロを選んで「▶ 実行」「✏ 編集」「🗑 削除」を行えます",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(_textSecondary),
            Margin = new Thickness(0, 4, 0, 0)
        });
        topBar.Children.Add(titleStack);
        DockPanel.SetDock(topBar, Dock.Top);
        dock.Children.Add(topBar);

        // 下部アクションバー（選択したマクロに対する3つの操作ボタン）
        var bottomBar = new Border
        {
            Background = new SolidColorBrush(_bgCanvas),
            BorderBrush = new SolidColorBrush(_borderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 16, 0, 0)
        };
        var actionDock = new DockPanel();

        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal };

        var editBtn = CreateButton("✏ 編集する", _accentNeutral, _textPrimary, isCompact: false);
        editBtn.Margin = new Thickness(0, 0, 10, 0);
        editBtn.Click += (_, _) => _vm.OpenSelectedMacroInEditor();
        rightButtons.Children.Add(editBtn);

        var deleteBtn = CreateButton("🗑 削除する", Color.FromRgb(254, 226, 226), Color.FromRgb(185, 28, 28), isCompact: false);
        deleteBtn.Margin = new Thickness(0, 0, 14, 0);
        deleteBtn.Click += (_, _) => _vm.DeleteSelectedMacro();
        rightButtons.Children.Add(deleteBtn);

        var runBtn = CreateButton("▶ 選択したマクロを実行", _accentSuccess, Colors.White, isCompact: false);
        runBtn.Click += async (_, _) => await _vm.ExecuteSelectedMacroFromListAsync();
        rightButtons.Children.Add(runBtn);

        DockPanel.SetDock(rightButtons, Dock.Right);
        actionDock.Children.Add(rightButtons);

        var browserCheck = new CheckBox
        {
            Content = "実行時にPCの標準ブラウザーも開く",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_textSecondary)
        };
        browserCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.OpenSystemBrowserOnNavigate)));
        actionDock.Children.Add(browserCheck);

        bottomBar.Child = actionDock;
        DockPanel.SetDock(bottomBar, Dock.Bottom);
        dock.Children.Add(bottomBar);

        // 中央マクロ一覧リスト
        var macroGrid = CreateCleanDataGrid();
        macroGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "マクロ名",
            Binding = new Binding("Name"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        macroGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "バージョン",
            Binding = new Binding("VersionNumber"),
            Width = 100
        });
        macroGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "更新日時",
            Binding = new Binding("CreatedAt") { StringFormat = "yyyy/MM/dd HH:mm" },
            Width = 170
        });
        macroGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.WorkflowVersions)));
        macroGrid.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedMacroInList)) { Mode = BindingMode.TwoWay });
        macroGrid.MouseDoubleClick += (_, _) => _vm.OpenSelectedMacroInEditor();

        dock.Children.Add(macroGrid);
        card.Child = dock;
        grid.Children.Add(card);
        return grid;
    }

    // --- 編集画面：マクロイベントをブロックと矢印の繋がりで視覚表現 ---
    private Grid CreateVisualBlockEditorView()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 上部ナビゲーションバー（一覧へ戻る / マクロ名 / 保存 / 実行）
        var navCard = CreateCard();
        navCard.Padding = new Thickness(16, 12, 16, 12);
        navCard.Margin = new Thickness(0, 0, 0, 16);

        var navGrid = new Grid();
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var backBtn = CreateButton("← マクロ一覧に戻る", _accentNeutral, _textPrimary, isCompact: true);
        backBtn.Click += (_, _) => _vm.IsEditingMode = false;
        Grid.SetColumn(backBtn, 0);
        navGrid.Children.Add(backBtn);

        var nameBox = CreateCleanTextBox(nameof(MainViewModel.WorkflowNameInput));
        nameBox.FontSize = 15;
        nameBox.FontWeight = FontWeights.Bold;
        nameBox.Margin = new Thickness(16, 0, 16, 0);
        Grid.SetColumn(nameBox, 1);
        navGrid.Children.Add(nameBox);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var saveBtn = CreateButton("💾 保存して一覧へ", _accentPrimary, Colors.White, isCompact: true);
        saveBtn.Margin = new Thickness(0, 0, 8, 0);
        saveBtn.Click += (_, _) =>
        {
            _vm.SaveCurrentWorkflowAsVersion();
            if (_vm.CurrentValidationIssues.Count == 0)
            {
                _vm.IsEditingMode = false;
            }
        };
        rightBtns.Children.Add(saveBtn);

        var runNowBtn = CreateButton("▶ 今すぐ実行", _accentSuccess, Colors.White, isCompact: true);
        runNowBtn.Click += async (_, _) => await _vm.ExecuteOneClickAllInOneAsync();
        rightBtns.Children.Add(runNowBtn);

        Grid.SetColumn(rightBtns, 2);
        navGrid.Children.Add(rightBtns);
        navCard.Child = navGrid;

        Grid.SetRow(navCard, 0);
        grid.Children.Add(navCard);

        // 下部メイン領域（左：ブロック連結キャンバス / 右：ブロック追加パネル）
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左：ブロックの繋がりキャンバス
        var flowCard = CreateCard();
        var flowDock = new DockPanel();

        var flowHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var clearBtn = CreateButton("すべてクリア", _accentNeutral, _textSecondary, isCompact: true);
        clearBtn.Click += (_, _) => _vm.ClearEasySteps();
        DockPanel.SetDock(clearBtn, Dock.Right);
        flowHeader.Children.Add(clearBtn);

        flowHeader.Children.Add(new TextBlock
        {
            Text = "マクロのブロックフロー（上から順番に繋がって実行されます）",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        DockPanel.SetDock(flowHeader, Dock.Top);
        flowDock.Children.Add(flowHeader);

        var canvasScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(_bgCanvas),
            Padding = new Thickness(16)
        };
        canvasScroll.Content = _visualBlockChainPanel;
        flowDock.Children.Add(canvasScroll);

        flowCard.Child = flowDock;
        Grid.SetColumn(flowCard, 0);
        contentGrid.Children.Add(flowCard);

        // 右：新しいブロックを追加する設定パネル
        var addCard = CreateCard();
        var formStack = new StackPanel();
        formStack.Children.Add(new TextBlock
        {
            Text = "ブロックを追加",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 14)
        });

        formStack.Children.Add(CreateFieldLabel("ブロックの種類"));
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

        var addBtn = CreateButton("＋ このブロックを末尾に繋げる", _accentPrimary, Colors.White, isCompact: false);
        addBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        addBtn.Margin = new Thickness(0, 6, 0, 0);
        addBtn.Click += (_, _) => _vm.AddStepFromBuilderUi();
        formStack.Children.Add(addBtn);

        addCard.Child = formStack;
        Grid.SetColumn(addCard, 2);
        contentGrid.Children.Add(addCard);

        Grid.SetRow(contentGrid, 1);
        grid.Children.Add(contentGrid);
        return grid;
    }

    // ブロックと矢印コネクタの視覚チェーンを再構築する
    private void RebuildVisualBlockChain()
    {
        _visualBlockChainPanel.Children.Clear();

        // 開始バッジ
        _visualBlockChainPanel.Children.Add(CreateTerminalBadge("▶ スタート", Color.FromRgb(16, 185, 129)));
        _visualBlockChainPanel.Children.Add(CreateConnectorArrow());

        if (_vm.EasySteps.Count == 0)
        {
            var emptyBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(_borderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(20),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            emptyBorder.Child = new TextBlock
            {
                Text = "右側のパネルからブロックを追加してください",
                Foreground = new SolidColorBrush(_textSecondary)
            };
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

        // 終了バッジ
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
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左：ステップ番号＋アイコンバッジ
        var badge = new Border
        {
            Background = new SolidColorBrush(accentColor),
            CornerRadius = new CornerRadius(8),
            Width = 42,
            Height = 42,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock
        {
            Text = $"{step.StepNumber}",
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        // 中央：ブロック種類と詳細内容
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoStack.Children.Add(new TextBlock
        {
            Text = step.FriendlyTitle,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = new SolidColorBrush(_textPrimary)
        });
        infoStack.Children.Add(new TextBlock
        {
            Text = step.FriendlyDetail,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(_textSecondary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        // 右：順番入れ替え・削除ボタン
        var ctrlStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var upBtn = CreateButton("↑", _accentNeutral, _textSecondary, isCompact: true);
        upBtn.Padding = new Thickness(8, 4, 8, 4);
        upBtn.Margin = new Thickness(0, 0, 4, 0);
        upBtn.Click += (_, _) => _vm.MoveStepUp(step);
        ctrlStack.Children.Add(upBtn);

        var downBtn = CreateButton("↓", _accentNeutral, _textSecondary, isCompact: true);
        downBtn.Padding = new Thickness(8, 4, 8, 4);
        downBtn.Margin = new Thickness(0, 0, 4, 0);
        downBtn.Click += (_, _) => _vm.MoveStepDown(step);
        ctrlStack.Children.Add(downBtn);

        var delBtn = CreateButton("✕", Color.FromRgb(254, 226, 226), Color.FromRgb(185, 28, 28), isCompact: true);
        delBtn.Padding = new Thickness(8, 4, 8, 4);
        delBtn.Click += (_, _) => _vm.RemoveSpecificStep(step);
        ctrlStack.Children.Add(delBtn);

        Grid.SetColumn(ctrlStack, 2);
        grid.Children.Add(ctrlStack);

        blockBorder.Child = grid;
        return blockBorder;
    }

    private static UIElement CreateConnectorArrow()
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2)
        };
        stack.Children.Add(new Border
        {
            Width = 3,
            Height = 14,
            Background = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = "▼",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -4, 0, 0)
        });
        return stack;
    }

    private static UIElement CreateTerminalBadge(string text, Color bg)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 5, 16, 5),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        border.Child = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 12
        };
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
            RowHeight = 38,
            FontSize = 13.5
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
