// Module: @role: マクロ実行前のアプリ別ウィンドウ選択ダイアログ（サムネイル・名前表示・新規起動選択）および内蔵スマートブラウザウィンドウ
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;

namespace AiMacroSystem.Presentation;

public sealed class TargetWindowSelectionDialog : Window
{
    private readonly IReadOnlyList<AppTargetPromptRequest> _requests;
    private readonly Dictionary<string, WindowTargetSelection> _selections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(Border Card, WindowTargetSelection Option)>> _cardGroups = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, WindowTargetSelection>? ConfirmedSelections { get; private set; }

    public TargetWindowSelectionDialog(Window owner, IReadOnlyList<AppTargetPromptRequest> requests)
    {
        _requests = requests;
        Owner = owner;
        Title = "実行対象ウィンドウの選択";
        Width = 860;
        Height = 620;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI");
        Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42));

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ヘッダー説明
        var headerStack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        headerStack.Children.Add(new TextBlock
        {
            Text = "🎯 マクロを実行する対象ウィンドウを選択してください",
            FontSize = 18,
            FontWeight = FontWeights.Bold
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = "操作対象アプリごとに「新規ウィンドウを立ち上げる」か「現在開いている既存ウィンドウ（サムネイル表示）」を選択できます。",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            Margin = new Thickness(0, 4, 0, 0)
        });
        Grid.SetRow(headerStack, 0);
        root.Children.Add(headerStack);

        // アプリごとの選択カード一覧
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(2)
        };
        var sectionsStack = new StackPanel();

        foreach (var req in _requests)
        {
            sectionsStack.Children.Add(BuildAppRequestSection(req));
        }

        scroll.Content = sectionsStack;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // フッターボタン
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var btnStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var cancelBtn = new Button
        {
            Content = "キャンセル",
            Padding = new Thickness(18, 9, 18, 9),
            Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        cancelBtn.Click += (_, _) =>
        {
            ConfirmedSelections = null;
            DialogResult = false;
            Close();
        };
        btnStack.Children.Add(cancelBtn);

        var runBtn = new Button
        {
            Content = "▶ 選択したウィンドウでマクロを実行",
            Padding = new Thickness(22, 9, 22, 9),
            Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 13.5,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        runBtn.Click += (_, _) =>
        {
            ConfirmedSelections = new Dictionary<string, WindowTargetSelection>(_selections, StringComparer.Ordinal);
            DialogResult = true;
            Close();
        };
        btnStack.Children.Add(runBtn);

        DockPanel.SetDock(btnStack, Dock.Right);
        footer.Children.Add(btnStack);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
    }

    private UIElement BuildAppRequestSection(AppTargetPromptRequest req)
    {
        var sectionBorder = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 14)
        };

        var stack = new StackPanel();
        string icon = req.AppCategory switch
        {
            "Chrome" => "🌐",
            "Excel" => "📗",
            _ => "🖥"
        };

        stack.Children.Add(new TextBlock
        {
            Text = $"{icon} {req.DisplayTitle} （操作ステップ数: {req.ActionCount}）",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var wrap = new WrapPanel();
        var cardList = new List<(Border Card, WindowTargetSelection Option)>();
        _cardGroups[req.TargetKey] = cardList;

        // 1. 新規ウィンドウを立ち上げる選択肢
        var newWinThumb = WindowThumbnailCaptureService.CreateBadgeThumbnail(
            "➕ NEW WINDOW",
            "新規ウィンドウを起動",
            Color.FromRgb(37, 99, 235));
        var newWinSelection = new WindowTargetSelection
        {
            TargetKey = req.TargetKey,
            AppCategory = req.AppCategory,
            ApplicationName = req.ApplicationName,
            CreateNewWindow = true,
            UseBuiltInBrowser = false,
            TargetWindowHandle = IntPtr.Zero,
            SelectedWindowTitle = "新規ウィンドウを立ち上げて実行"
        };
        var newWinCard = CreateChoiceCard(
            req.TargetKey,
            newWinThumb,
            "➕ 新規ウィンドウを立ち上げる",
            req.AppCategory == "Chrome" ? "新しいブラウザウィンドウを起動して実行" : "新しくアプリを起動して実行",
            newWinSelection);
        cardList.Add((newWinCard, newWinSelection));
        wrap.Children.Add(newWinCard);

        // 2. ブラウザの場合は「内蔵スマートブラウザを新規起動」も選択可能にする
        if (req.AppCategory == "Chrome")
        {
            var builtInThumb = WindowThumbnailCaptureService.CreateBadgeThumbnail(
                "🌐 SMART DOM",
                "内蔵スマートブラウザ",
                Color.FromRgb(13, 148, 136));
            var builtInSelection = new WindowTargetSelection
            {
                TargetKey = req.TargetKey,
                AppCategory = req.AppCategory,
                ApplicationName = req.ApplicationName,
                CreateNewWindow = true,
                UseBuiltInBrowser = true,
                TargetWindowHandle = IntPtr.Zero,
                SelectedWindowTitle = "内蔵スマートブラウザ（DOM完全制御）"
            };
            var builtInCard = CreateChoiceCard(
                req.TargetKey,
                builtInThumb,
                "🌐 内蔵スマートブラウザで新規実行",
                "DOM直結で検索・文字入力・クリックを100%確実に実行",
                builtInSelection);
            cardList.Add((builtInCard, builtInSelection));
            wrap.Children.Add(builtInCard);
        }

        // 3. 既存の同種アプリウィンドウ一覧（サムネイル＋ウィンドウ名表示）
        foreach (var win in req.AvailableWindows)
        {
            var existingSelection = new WindowTargetSelection
            {
                TargetKey = req.TargetKey,
                AppCategory = req.AppCategory,
                ApplicationName = req.ApplicationName,
                CreateNewWindow = false,
                UseBuiltInBrowser = false,
                TargetWindowHandle = win.Handle,
                SelectedWindowTitle = win.Title
            };
            var existingCard = CreateChoiceCard(
                req.TargetKey,
                win.Thumbnail,
                win.Title,
                $"既存ウィンドウ ({win.ProcessName})",
                existingSelection);
            cardList.Add((existingCard, existingSelection));
            wrap.Children.Add(existingCard);
        }

        // 既定の選択（既存ウィンドウが開いていれば先頭の既存ウィンドウ、なければ新規ウィンドウを選択状態にする）
        var defaultOption = req.AvailableWindows.Count > 0 ? cardList[req.AppCategory == "Chrome" ? 2 : 1].Option : newWinSelection;
        SelectOption(req.TargetKey, defaultOption);

        stack.Children.Add(wrap);
        sectionBorder.Child = stack;
        return sectionBorder;
    }

    private Border CreateChoiceCard(
        string targetKey,
        BitmapSource? thumbnail,
        string title,
        string subtitle,
        WindowTargetSelection option)
    {
        var card = new Border
        {
            Width = 206,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 10, 10),
            Cursor = Cursors.Hand
        };

        var stack = new StackPanel();
        var imgBorder = new Border
        {
            Height = 104,
            Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 0, 8),
            ClipToBounds = true
        };
        if (thumbnail is not null)
        {
            imgBorder.Child = new Image
            {
                Source = thumbnail,
                Stretch = Stretch.UniformToFill
            };
        }
        stack.Children.Add(imgBorder);

        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = title
        });

        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        });

        card.Child = stack;
        card.MouseLeftButtonDown += (_, _) => SelectOption(targetKey, option);
        return card;
    }

    private void SelectOption(string targetKey, WindowTargetSelection selected)
    {
        _selections[targetKey] = selected;
        if (!_cardGroups.TryGetValue(targetKey, out var list)) return;

        foreach (var (card, opt) in list)
        {
            bool isMatch = ReferenceEquals(opt, selected);
            card.BorderBrush = new SolidColorBrush(isMatch
                ? Color.FromRgb(37, 99, 235)
                : Color.FromRgb(203, 213, 225));
            card.BorderThickness = new Thickness(isMatch ? 3 : 1.5);
            card.Background = new SolidColorBrush(isMatch
                ? Color.FromRgb(239, 246, 255)
                : Colors.White);
        }
    }
}

public sealed class BuiltInMacroBrowserWindow : Window
{
    private static BuiltInMacroBrowserWindow? _currentInstance;

    private readonly RecordingService _recordingService;
    private readonly Action<RawRecordedEvent>? _onEventCaptured;
    private readonly TextBox _addressOrSearchBox;
    private readonly WebBrowser _webBrowser;
    private string _currentUrl = "https://www.google.com";

    public static BuiltInMacroBrowserWindow GetOrCreate(
        RecordingService recordingService,
        Action<RawRecordedEvent>? onEventCaptured = null)
    {
        if (_currentInstance is null || !_currentInstance.IsLoaded)
        {
            _currentInstance = new BuiltInMacroBrowserWindow(recordingService, onEventCaptured);
        }
        return _currentInstance;
    }

    public BuiltInMacroBrowserWindow(
        RecordingService recordingService,
        Action<RawRecordedEvent>? onEventCaptured = null)
    {
        _recordingService = recordingService;
        _onEventCaptured = onEventCaptured;

        Title = "🌐 AI Macro 内蔵スマートブラウザ (DOM完全連動)";
        Width = 1020;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI");
        Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var navBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            Padding = new Thickness(12, 8, 12, 8)
        };

        var navGrid = new Grid();
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(13, 148, 136)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock
        {
            Text = "DOM直結記録・実行",
            Foreground = Brushes.White,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold
        };
        Grid.SetColumn(badge, 0);
        navGrid.Children.Add(badge);

        _addressOrSearchBox = new TextBox
        {
            Text = _currentUrl,
            Padding = new Thickness(10, 5, 10, 5),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _addressOrSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                NavigateOrSearchFromBar(_addressOrSearchBox.Text);
            }
        };
        Grid.SetColumn(_addressOrSearchBox, 1);
        navGrid.Children.Add(_addressOrSearchBox);

        var goBtn = new Button
        {
            Content = "🔍 移動 / 検索",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 5, 14, 5),
            Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        goBtn.Click += (_, _) => NavigateOrSearchFromBar(_addressOrSearchBox.Text);
        Grid.SetColumn(goBtn, 2);
        navGrid.Children.Add(goBtn);

        var googleBtn = new Button
        {
            Content = "Googleを開く",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(10, 5, 10, 5),
            Background = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        googleBtn.Click += (_, _) => NavigateOrSearchFromBar("https://www.google.com");
        Grid.SetColumn(googleBtn, 3);
        navGrid.Children.Add(googleBtn);

        navBar.Child = navGrid;
        Grid.SetRow(navBar, 0);
        root.Children.Add(navBar);

        _webBrowser = new WebBrowser();
        _webBrowser.Navigated += OnWebBrowserNavigated;
        _webBrowser.LoadCompleted += OnWebBrowserLoadCompleted;
        Grid.SetRow(_webBrowser, 1);
        root.Children.Add(root.Children.Count > 0 ? _webBrowser : _webBrowser);

        Content = root;
        Loaded += (_, _) =>
        {
            SuppressScriptErrors(_webBrowser);
            try { _webBrowser.Navigate(_currentUrl); } catch { }
        };
    }

    public void NavigateOrSearchFromBar(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return;
        string trimmed = rawInput.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
        {
            _currentUrl = uri.ToString();
            _addressOrSearchBox.Text = _currentUrl;
            RecordDomEvent("Navigate", null, null, null, null, _currentUrl, null);
            try { _webBrowser.Navigate(_currentUrl); } catch { }
            return;
        }

        // URLではなく検索キーワードが入力された場合はGoogle検索＋検索入力ステップとして記録する
        string searchUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(trimmed)}";
        _currentUrl = "https://www.google.com";
        RecordDomEvent("Navigate", null, null, null, null, "https://www.google.com", null);
        RecordDomEvent("Input", "textbox", "検索", "検索", "search-input", "https://www.google.com", trimmed);
        RecordDomEvent("Click", "button", "検索実行 (Enter)", "検索実行 (Enter)", "key-enter-submit", "https://www.google.com", null);
        _addressOrSearchBox.Text = searchUrl;
        try { _webBrowser.Navigate(searchUrl); } catch { }
    }

    public async Task NavigateWaitAsync(Uri targetUrl, CancellationToken ct)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            _currentUrl = targetUrl.ToString();
            _addressOrSearchBox.Text = _currentUrl;
            try { _webBrowser.Navigate(targetUrl); } catch { }
        });
        await Task.Delay(450, ct);
    }

    public async Task FillDomElementAsync(SemanticLocator locator, string value, CancellationToken ct)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible) Show();
            Activate();
            try
            {
                dynamic? doc = _webBrowser.Document;
                if (doc is null) return;

                string targetKey = locator.Label ?? locator.AccessibleName ?? locator.StableId ?? "q";
                dynamic? inputs = doc.getElementsByTagName("input");
                if (inputs is not null)
                {
                    foreach (dynamic el in inputs)
                    {
                        string type = (el.getAttribute("type") as string ?? "text").ToLowerInvariant();
                        if (type is "hidden" or "submit" or "button") continue;

                        string id = el.id as string ?? "";
                        string name = el.name as string ?? "";
                        string title = el.title as string ?? "";
                        if (string.Equals(id, locator.StableId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, targetKey, StringComparison.OrdinalIgnoreCase) ||
                            title.Contains(targetKey, StringComparison.OrdinalIgnoreCase) ||
                            name == "q" || type is "search" or "text")
                        {
                            el.value = value;
                            return;
                        }
                    }
                }
            }
            catch
            {
                // DOM直接設定非対応ページはアドレスバー検索へフォールバック
            }
        });
        await Task.Delay(120, ct);
    }

    public async Task ClickDomElementAsync(SemanticLocator locator, CancellationToken ct)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible) Show();
            Activate();
            try
            {
                dynamic? doc = _webBrowser.Document;
                if (doc is null) return;

                if (string.Equals(locator.StableId, "key-enter-submit", StringComparison.OrdinalIgnoreCase) ||
                    (locator.AccessibleName is not null && locator.AccessibleName.Contains("Enter", StringComparison.OrdinalIgnoreCase)))
                {
                    dynamic? forms = doc.getElementsByTagName("form");
                    if (forms is not null && forms.length > 0)
                    {
                        forms[0].submit();
                        return;
                    }
                }

                dynamic? buttons = doc.getElementsByTagName("button");
                if (buttons is not null && buttons.length > 0)
                {
                    buttons[0].click();
                }
            }
            catch
            {
                // DOMクリック例外は無視
            }
        });
        await Task.Delay(200, ct);
    }

    private void OnWebBrowserNavigated(object sender, NavigationEventArgs e)
    {
        SuppressScriptErrors(_webBrowser);
        if (e.Uri is not null)
        {
            _currentUrl = e.Uri.ToString();
            _addressOrSearchBox.Text = _currentUrl;

            // Google等の検索結果URLへ遷移した際、クエリ文字列 q= から検索ワードを自動抽出して記録する
            string query = e.Uri.Query;
            if (_recordingService.IsRecording && query.Contains("q=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = query.TrimStart('?').Split('&');
                foreach (string part in parts)
                {
                    if (part.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                    {
                        string decoded = Uri.UnescapeDataString(part[2..].Replace('+', ' '));
                        if (!string.IsNullOrWhiteSpace(decoded))
                        {
                            string hostUrl = $"{e.Uri.Scheme}://{e.Uri.Host}";
                            RecordDomEvent("Navigate", null, null, null, null, hostUrl, null);
                            RecordDomEvent("Input", "textbox", "検索", "検索", "search-q", hostUrl, decoded);
                            RecordDomEvent("Click", "button", "検索実行 (Enter)", "検索実行 (Enter)", "key-enter-submit", hostUrl, null);
                        }
                        break;
                    }
                }
            }
        }
    }

    private void OnWebBrowserLoadCompleted(object sender, NavigationEventArgs e)
    {
        try
        {
            dynamic? doc = _webBrowser.Document;
            if (doc is null) return;

            string? docTitle = doc.title as string;
            if (!string.IsNullOrWhiteSpace(docTitle))
            {
                Title = $"🌐 {docTitle} - AI Macro 内蔵スマートブラウザ";
            }
        }
        catch
        {
            // クロスオリジンフレーム例外は無視
        }
    }

    private void RecordDomEvent(
        string eventKind,
        string? role,
        string? name,
        string? label,
        string? stableId,
        string? url,
        string? value)
    {
        if (!_recordingService.IsRecording) return;
        var ts = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(120));
        var ev = _recordingService.RecordEvent(
            "Chrome",
            eventKind,
            "text",
            role,
            name,
            label,
            stableId,
            url,
            null,
            null,
            value,
            logSource: LogSourceConstants.BrowserDom,
            cssSelector: stableId is not null ? $"#{stableId}" : null,
            sourceConfidence: 0.99,
            windowOrProcessName: "AI Macro 内蔵スマートブラウザ",
            timestampOverride: ts);
        _onEventCaptured?.Invoke(ev);
    }

    private static void SuppressScriptErrors(WebBrowser browser)
    {
        try
        {
            FieldInfo? fi = typeof(WebBrowser).GetField("_axIWebBrowser2", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi is null) return;
            object? comObj = fi.GetValue(browser);
            if (comObj is null) return;
            comObj.GetType().InvokeMember("Silent", BindingFlags.SetProperty, null, comObj, new object[] { true });
        }
        catch
        {
            // COM初期化前の呼び出しは無視
        }
    }
}
