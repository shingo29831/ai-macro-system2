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
    private Grid CreateVisualBlockEditorView()
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var navCard = CreateCard();
        navCard.Padding = new Thickness(14, 10, 14, 10);
        navCard.Margin = new Thickness(0, 0, 0, 10);

        var navGrid = new Grid();
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameLabel = new TextBlock
        {
            Text = "マクロ名:",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_textSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(nameLabel, 0);
        navGrid.Children.Add(nameLabel);

        var nameBox = CreateCleanTextBox(nameof(MainViewModel.WorkflowNameInput));
        nameBox.FontSize = 14;
        nameBox.FontWeight = FontWeights.Bold;
        nameBox.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(nameBox, 1);
        navGrid.Children.Add(nameBox);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal };

        var draftBtn = CreateButton("📝 下書き保存", _accentNeutral, _textPrimary, isCompact: true);
        draftBtn.Margin = new Thickness(0, 0, 8, 0);
        draftBtn.Click += (_, _) => _vm.SaveCurrentAsDraft();
        rightBtns.Children.Add(draftBtn);

        var saveBtn = CreateButton("💾 保存", _accentPrimary, Colors.White, isCompact: false);
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

        var editorSubTabs = CreateSubTabControl();
        editorSubTabs.Items.Add(new TabItem
        {
            Header = "🧩 かんたんブロック編集",
            Padding = new Thickness(12, 5, 12, 5),
            Content = CreateBlockBuilderTwoColumnPane()
        });
        editorSubTabs.Items.Add(new TabItem
        {
            Header = "📄 高度な定義 (IR JSON)",
            Padding = new Thickness(12, 5, 12, 5),
            Content = CreateAdvancedIrJsonPane()
        });

        Grid.SetRow(editorSubTabs, 1);
        grid.Children.Add(editorSubTabs);
        return grid;
    }

    private Grid CreateBlockBuilderTwoColumnPane()
    {
        var contentGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var flowCard = CreateCard();
        var flowDock = new DockPanel();
        var flowHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

        var clearBtn = CreateButton("すべてクリア", _accentNeutral, _textSecondary, isCompact: true);
        clearBtn.Click += (_, _) => _vm.ClearEasySteps();
        DockPanel.SetDock(clearBtn, Dock.Right);
        flowHeader.Children.Add(clearBtn);

        flowHeader.Children.Add(new TextBlock
        {
            Text = "手順フロー（クリックして右側で編集）",
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
            Padding = new Thickness(14)
        };
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
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        };
        editHeaderBlock.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.EditingBlockHeaderText)));
        formStack.Children.Add(editHeaderBlock);

        formStack.Children.Add(CreateFieldLabel("操作の種類"));
        var actionCombo = new ComboBox
        {
            DisplayMemberPath = "Value",
            SelectedValuePath = "Key",
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 12)
        };
        actionCombo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.AvailableActionKinds)));
        actionCombo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(MainViewModel.SelectedActionKind)));
        actionCombo.SelectionChanged += (_, _) =>
        {
            if (actionCombo.SelectedValue is string kind) UpdateInputFieldsVisibility(kind);
        };
        formStack.Children.Add(actionCombo);

        _fieldCustomAppPanel = CreateLabeledInput("アプリ名 / ウィンドウ名", nameof(MainViewModel.StepCustomAppNameInput));
        _fieldUrlPanel = CreateLabeledInput("Webページ URL", nameof(MainViewModel.StepUrlInput));
        _fieldElementPanel = CreateLabeledInput("操作対象の名前・ラベル", nameof(MainViewModel.StepElementNameInput));
        _fieldValuePanel = CreateLabeledInput("入力する文字", nameof(MainViewModel.StepFillValueInput));

        _fieldPasswordCheck = new CheckBox
        {
            Content = "パスワード等の機密情報としてマスクする",
            Foreground = new SolidColorBrush(_textSecondary),
            Margin = new Thickness(0, 0, 0, 12)
        };
        _fieldPasswordCheck.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.StepIsPasswordInput)));

        _fieldExcelPathPanel = CreateLabeledInput("Excelファイルのパス", nameof(MainViewModel.StepExcelPathInput));

        _fieldExcelMetaGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        _fieldExcelMetaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _fieldExcelMetaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        _fieldExcelMetaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sheetPanel = CreateLabeledInput("シート名", nameof(MainViewModel.StepExcelSheetInput));
        Grid.SetColumn(sheetPanel, 0);
        _fieldExcelMetaGrid.Children.Add(sheetPanel);

        var rangePanel = CreateLabeledInput("セル範囲 (例: A1:B5)", nameof(MainViewModel.StepExcelRangeInput));
        Grid.SetColumn(rangePanel, 2);
        _fieldExcelMetaGrid.Children.Add(rangePanel);

        _fieldExcelWritePanel = CreateLabeledInput("書き込むデータ (カンマ区切り)", nameof(MainViewModel.StepExcelWriteTextInput));

        formStack.Children.Add(_fieldCustomAppPanel);
        formStack.Children.Add(_fieldUrlPanel);
        formStack.Children.Add(_fieldElementPanel);
        formStack.Children.Add(_fieldValuePanel);
        formStack.Children.Add(_fieldPasswordCheck);
        formStack.Children.Add(_fieldExcelPathPanel);
        formStack.Children.Add(_fieldExcelMetaGrid);
        formStack.Children.Add(_fieldExcelWritePanel);

        var updateBtn = CreateButton("💾 選択中の手順を更新", _accentSuccess, Colors.White, isCompact: false);
        updateBtn.Margin = new Thickness(0, 6, 0, 8);
        updateBtn.Click += (_, _) => _vm.UpdateSelectedStepFromBuilderUi();
        formStack.Children.Add(updateBtn);

        var addBtn = CreateButton("＋ 新しい手順として末尾に追加", _accentPrimary, Colors.White, isCompact: false);
        addBtn.Click += (_, _) => _vm.AddStepFromBuilderUi();
        formStack.Children.Add(addBtn);

        formScroll.Content = formStack;
        addCard.Child = formScroll;
        Grid.SetColumn(addCard, 2);
        contentGrid.Children.Add(addCard);

        return contentGrid;
    }

    private Border CreateAdvancedIrJsonPane()
    {
        var irCard = CreateCard();
        irCard.Margin = new Thickness(0, 6, 0, 0);
        var irDock = new DockPanel();

        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        rightStack.Children.Add(new TextBlock
        {
            Text = "Workflow ID:",
            Foreground = new SolidColorBrush(_textSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        var idBox = CreateCleanTextBox(nameof(MainViewModel.WorkflowIdInput));
        idBox.Width = 180;
        idBox.Margin = new Thickness(0, 0, 10, 0);
        rightStack.Children.Add(idBox);

        var validateBtn = CreateButton("🔍 JSON構文・ポリシー検証", _accentNeutral, _textPrimary, isCompact: true);
        validateBtn.Click += (_, _) => _vm.ValidateCurrentEditorIr();
        rightStack.Children.Add(validateBtn);

        DockPanel.SetDock(rightStack, Dock.Right);
        topBar.Children.Add(rightStack);
        topBar.Children.Add(CreateSectionHeader("構造化 Workflow IR JSON", "条件分岐 (If) やループ (ForEach / While)、データ参照を直接編集できます"));
        DockPanel.SetDock(topBar, Dock.Top);
        irDock.Children.Add(topBar);

        var jsonBox = new TextBox
        {
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas, Yu Gothic UI"),
            FontSize = 12.5,
            Padding = new Thickness(10),
            BorderBrush = new SolidColorBrush(_borderSubtle)
        };
        jsonBox.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.EditorIrJson)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        irDock.Children.Add(jsonBox);

        irCard.Child = irDock;
        return irCard;
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
            emptyBorder.Child = new TextBlock { Text = "右側フォームまたは「操作記録」から手順を追加してください", Foreground = new SolidColorBrush(_textSecondary) };
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
            BorderThickness = new Thickness(step.IsSelectedForEdit ? 3 : 1.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.Hand,
            ToolTip = step.SourceLogSummary
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
            Width = 30,
            Height = 30,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock
        {
            Text = $"{step.StepNumber}",
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var headerWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
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
                Foreground = new SolidColorBrush(_textSecondary),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
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
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }

        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        var ctrlStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

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
        stack.Children.Add(new Border { Width = 2, Height = 8, Background = new SolidColorBrush(Color.FromRgb(148, 163, 184)), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = "▼", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -3, 0, 0) });
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
