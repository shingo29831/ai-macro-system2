// Module: @role: MainWindowの手順ブロック視覚編集ビュー・ビジュアルブロック連鎖描画・入力フォーム制御を担当するPartialクラス
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AiMacroSystem.Domain;
using static AiMacroSystem.Presentation.MainWindowUiFactory;

namespace AiMacroSystem.Presentation;

public sealed partial class MainWindow
{
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

        var saveBtn = CreateButton("💾 マクロを保存", _accentPrimary, Colors.White, isCompact: false);
        saveBtn.Margin = new Thickness(0, 0, 8, 0);
        saveBtn.Click += (_, _) => _vm.SaveCurrentWorkflowAsVersion();
        rightBtns.Children.Add(saveBtn);

        var quickRunBtn = CreateButton("▶ 保存してすぐ実行", _accentSuccess, Colors.White, isCompact: false);
        quickRunBtn.Click += async (_, _) => await _vm.ExecuteOneClickAllInOneAsync();
        rightBtns.Children.Add(quickRunBtn);

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

        var flowFooter = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        flowFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        flowFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        flowFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var saveBlocksBtn = CreateButton("💾 このマクロを保存する", _accentPrimary, Colors.White, isCompact: false);
        saveBlocksBtn.Click += (_, _) => _vm.SaveCurrentWorkflowAsVersion();
        Grid.SetColumn(saveBlocksBtn, 0);
        flowFooter.Children.Add(saveBlocksBtn);

        var backToListBtn = CreateButton("📋 保存して一覧へ", _accentNeutral, _textPrimary, isCompact: false);
        backToListBtn.Click += (_, _) =>
        {
            _vm.SaveCurrentWorkflowAsVersion();
            if (_vm.CurrentValidationIssues.Count == 0)
            {
                _vm.IsEditingMode = false;
            }
        };
        Grid.SetColumn(backToListBtn, 2);
        flowFooter.Children.Add(backToListBtn);

        DockPanel.SetDock(flowFooter, Dock.Bottom);
        flowDock.Children.Add(flowFooter);

        var canvasScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(_bgCanvas), Padding = new Thickness(12) };
        canvasScroll.Content = _visualBlockChainPanel;
        flowDock.Children.Add(canvasScroll);
        flowCard.Child = flowDock;
        Grid.SetColumn(flowCard, 0);
        contentGrid.Children.Add(flowCard);

        var addCard = CreateCard();
        var formScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var formStack = new StackPanel();
        var editHeaderBlock = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        };
        editHeaderBlock.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.EditingBlockHeaderText)));
        formStack.Children.Add(editHeaderBlock);

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

        _fieldCustomAppPanel = CreateLabeledInput("独自アプリ名 / ウィンドウ識別名", nameof(MainViewModel.StepCustomAppNameInput));
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

        formStack.Children.Add(_fieldCustomAppPanel);
        formStack.Children.Add(_fieldUrlPanel);
        formStack.Children.Add(_fieldElementPanel);
        formStack.Children.Add(_fieldValuePanel);
        formStack.Children.Add(_fieldPasswordCheck);
        formStack.Children.Add(_fieldExcelPathPanel);
        formStack.Children.Add(_fieldExcelMetaGrid);
        formStack.Children.Add(_fieldExcelWritePanel);

        var updateBtn = CreateButton("💾 選択中のブロックを更新", _accentSuccess, Colors.White, isCompact: false);
        updateBtn.Margin = new Thickness(0, 0, 0, 8);
        updateBtn.Click += (_, _) => _vm.UpdateSelectedStepFromBuilderUi();
        formStack.Children.Add(updateBtn);

        var addBtn = CreateButton("＋ 新しいブロックとして末尾に追加", _accentPrimary, Colors.White, isCompact: false);
        addBtn.Click += (_, _) => _vm.AddStepFromBuilderUi();
        formStack.Children.Add(addBtn);

        formScroll.Content = formStack;
        addCard.Child = formScroll;
        Grid.SetColumn(addCard, 2);
        contentGrid.Children.Add(addCard);

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
            Background = step.IsSelectedForEdit
                ? new SolidColorBrush(Color.FromRgb(239, 246, 255))
                : Brushes.White,
            BorderBrush = new SolidColorBrush(accentColor),
            BorderThickness = new Thickness(step.IsSelectedForEdit ? 3 : 2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.Hand
        };
        blockBorder.MouseLeftButtonDown += (_, _) => _vm.SelectStepForEditing(step);

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
            VerticalAlignment = VerticalAlignment.Top
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

        var headerWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 3) };
        var appPill = new Border
        {
            Background = new SolidColorBrush(accentColor),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        appPill.Child = new TextBlock
        {
            Text = step.TargetAppBadge,
            Foreground = Brushes.White,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold
        };
        headerWrap.Children.Add(appPill);
        headerWrap.Children.Add(new TextBlock
        {
            Text = step.FriendlyTitle,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        infoStack.Children.Add(headerWrap);

        if (!string.IsNullOrWhiteSpace(step.TargetLocationSummary))
        {
            infoStack.Children.Add(new TextBlock
            {
                Text = $"🎯 {step.TargetLocationSummary}",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(_textPrimary),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1)
            });
        }

        if (!string.IsNullOrWhiteSpace(step.OperationValueSummary))
        {
            infoStack.Children.Add(new TextBlock
            {
                Text = $"⚡ {step.OperationValueSummary}",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accentColor),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1)
            });
        }

        infoStack.Children.Add(new TextBlock
        {
            Text = $"📋 {step.SourceLogSummary}",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(_textSecondary),
            TextWrapping = TextWrapping.Wrap
        });

        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        var ctrlStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 0) };
        var editBtn = CreateButton("✏", _accentNeutral, _textPrimary, isCompact: true);
        editBtn.Padding = new Thickness(6, 2, 6, 2);
        editBtn.Margin = new Thickness(0, 0, 3, 0);
        editBtn.Click += (_, e) =>
        {
            e.Handled = true;
            _vm.SelectStepForEditing(step);
        };
        ctrlStack.Children.Add(editBtn);

        var upBtn = CreateButton("↑", _accentNeutral, _textSecondary, isCompact: true);
        upBtn.Padding = new Thickness(6, 2, 6, 2);
        upBtn.Margin = new Thickness(0, 0, 3, 0);
        upBtn.Click += (_, e) =>
        {
            e.Handled = true;
            _vm.MoveStepUp(step);
        };
        ctrlStack.Children.Add(upBtn);

        var downBtn = CreateButton("↓", _accentNeutral, _textSecondary, isCompact: true);
        downBtn.Padding = new Thickness(6, 2, 6, 2);
        downBtn.Margin = new Thickness(0, 0, 3, 0);
        downBtn.Click += (_, e) =>
        {
            e.Handled = true;
            _vm.MoveStepDown(step);
        };
        ctrlStack.Children.Add(downBtn);

        var delBtn = CreateButton("✕", Color.FromRgb(254, 226, 226), Color.FromRgb(185, 28, 28), isCompact: true);
        delBtn.Padding = new Thickness(6, 2, 6, 2);
        delBtn.Click += (_, e) =>
        {
            e.Handled = true;
            _vm.RemoveSpecificStep(step);
        };
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

        _fieldCustomAppPanel.Visibility = (isCustomClick || isCustomFill || isCustomRead) ? Visibility.Visible : Visibility.Collapsed;
        _fieldUrlPanel.Visibility = (isNav || isSubmit) ? Visibility.Visible : Visibility.Collapsed;
        _fieldElementPanel.Visibility = (isFill || isClick || isSubmit || isCustomClick || isCustomFill || isCustomRead) ? Visibility.Visible : Visibility.Collapsed;
        _fieldValuePanel.Visibility = (isFill || isCustomFill) ? Visibility.Visible : Visibility.Collapsed;
        _fieldPasswordCheck.Visibility = (isFill || isCustomFill) ? Visibility.Visible : Visibility.Collapsed;

        _fieldExcelPathPanel.Visibility = (isExcelRead || isExcelWrite) ? Visibility.Visible : Visibility.Collapsed;
        _fieldExcelMetaGrid.Visibility = (isExcelRead || isExcelWrite) ? Visibility.Visible : Visibility.Collapsed;
        _fieldExcelWritePanel.Visibility = isExcelWrite ? Visibility.Visible : Visibility.Collapsed;
    }
}
