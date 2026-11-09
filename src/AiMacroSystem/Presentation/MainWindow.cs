// Module: @role: メインウィンドウのライフサイクル管理・ウィンドウ初期化・ヘッダーおよび復旧バー制御
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AiMacroSystem.Infrastructure;
using static AiMacroSystem.Presentation.MainWindowUiFactory;

namespace AiMacroSystem.Presentation;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ExecutionOverlayWindow _overlayWindow;
    private readonly TabControl _mainTabs;
    private readonly StackPanel _visualBlockChainPanel;

    private StackPanel _fieldUrlPanel = null!;
    private StackPanel _fieldCustomAppPanel = null!;
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
        _vm.VisualBlocksUpdated += () =>
        {
            RebuildVisualBlockChain();
            if (_vm.IsEditingMode)
            {
                _mainTabs.SelectedIndex = 2;
            }
        };
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        _vm.ConfirmExecutionTargetsAsync = ShowTargetWindowSelectionDialogAsync;
        if (_vm.LiveBrowserDriver is not null)
        {
            _vm.LiveBrowserDriver.BuiltInBrowserNavigateAsync = async (uri, ct) =>
            {
                var browserWin = BuiltInMacroBrowserWindow.GetOrCreate(_vm.RecordingService, ev => _vm.RecordedEvents.Add(ev));
                await browserWin.NavigateWaitAsync(uri, ct);
            };
            _vm.LiveBrowserDriver.BuiltInBrowserFillAsync = async (locator, val, ct) =>
            {
                var browserWin = BuiltInMacroBrowserWindow.GetOrCreate(_vm.RecordingService, ev => _vm.RecordedEvents.Add(ev));
                await browserWin.FillDomElementAsync(locator, val, ct);
            };
            _vm.LiveBrowserDriver.BuiltInBrowserClickAsync = async (locator, ct) =>
            {
                var browserWin = BuiltInMacroBrowserWindow.GetOrCreate(_vm.RecordingService, ev => _vm.RecordedEvents.Add(ev));
                await browserWin.ClickDomElementAsync(locator, ct);
            };
        }

        Closed += (_, _) => _overlayWindow.Close();
        UpdateInputFieldsVisibility(_vm.SelectedActionKind);
        RebuildVisualBlockChain();
    }

    private Task<IReadOnlyDictionary<string, WindowTargetSelection>?> ShowTargetWindowSelectionDialogAsync(
        IReadOnlyList<AppTargetPromptRequest> requests)
    {
        var dialog = new TargetWindowSelectionDialog(this, requests);
        bool? ok = dialog.ShowDialog();
        return Task.FromResult(ok == true ? dialog.ConfirmedSelections : null);
    }

    private void OpenBuiltInSmartBrowserAndRecord()
    {
        var browserWin = BuiltInMacroBrowserWindow.GetOrCreate(_vm.RecordingService, ev =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _vm.RecordedEvents.Add(ev);
                _vm.RecordingProgressText = $"記録中 ({_vm.RecordedEvents.Count} ログ): [内蔵ブラウザ] {ev.EventKind} ({ev.CapturedValue ?? ev.TargetLabel ?? ev.UrlOrWorkbookPath})";
            });
        });
        browserWin.Show();
        browserWin.Activate();
        if (!_vm.IsRecordingMacro)
        {
            _vm.StartEasyRecording();
        }
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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

        var headerBtnStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

        var headerBuiltInBrowserBtn = CreateButton("🌐 内蔵ブラウザで記録", Color.FromRgb(13, 148, 136), Colors.White, isCompact: true);
        headerBuiltInBrowserBtn.Margin = new Thickness(0, 0, 8, 0);
        headerBuiltInBrowserBtn.Click += (_, _) => OpenBuiltInSmartBrowserAndRecord();
        headerBtnStack.Children.Add(headerBuiltInBrowserBtn);

        var headerRecBtn = CreateButton("🔴 記録開始", _accentDanger, Colors.White, isCompact: true);
        headerRecBtn.Margin = new Thickness(0, 0, 8, 0);
        headerRecBtn.Click += (_, _) => _vm.StartEasyRecording();
        headerBtnStack.Children.Add(headerRecBtn);

        var headerSaveBtn = CreateButton("💾 マクロを保存", _accentPrimary, Colors.White, isCompact: true);
        headerSaveBtn.Click += (_, _) => _vm.SaveCurrentWorkflowAsVersion();
        headerBtnStack.Children.Add(headerSaveBtn);

        Grid.SetColumn(headerBtnStack, 2);
        grid.Children.Add(headerBtnStack);

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
}
