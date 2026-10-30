using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using FluentZones.Core;

namespace FluentZones.UI;

public sealed partial class MainWindow : Window
{
    private readonly LayoutService _layouts = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly AppSettings _settings;

    private readonly Dictionary<string, HotkeyRow> _rows = new();
    private readonly Dictionary<string, TextBlock> _keycapTexts = new();
    private readonly Dictionary<string, int> _layoutHotkeyIds = new();
    private readonly Dictionary<string, int> _allMonitorHotkeyIds = new();
    private readonly Dictionary<LayoutKind, Button> _cards = new();

    private Button? _highlightedCard;
    private Button? _restoreCard;
    private string? _editingAction;
    private int _nextHotkeyId = 1;
    private bool _initializing = true;
    private bool _syncingToggle;
    private bool _syncingZoneToggle;
    private bool _hotkeysAvailable;
    private bool _prompting;
    private bool _elevationPrompted;

    // 分区工作区的状态机（切分树、窗口句柄、显示器、布局类型、边框、拖拽钩子、窗口事件）都在 Core；
    // 这里只保留开关、窗口的收集/摆放、状态栏、对话框、卡片高亮和快照这些 UI 胶水。
    private readonly ZoneSession _zones;

    // 分区模式的边框：由 DWM 画在窗口自己身上；间隙与一次性布局共用设置里的「窗口间距」
    private const int ZoneMargin = 4;

    private enum StatusLevel
    {
        Info,
        Success,
        Warn,
        Error,
    }

    public MainWindow()
    {
        InitializeComponent();

        // 上次被强杀 / 崩溃时可能没恢复窗口边框，启动时先补回来
        ZoneStyleStore.RestoreAll();
        ZoneStyleStore.RepairFromLog();

        _settings = SettingsStore.Load();

        // 工作区状态机在 Core：这里只注入「枚举窗口 / 量可用区域 / 取间隙 / 回 UI 线程」四件胶水
        _zones = new ZoneSession(
            collectWindows: () => _layouts.GetWindows(CurrentOptions().Scope),
            getArea: ZoneArea,
            getGap: () => CurrentOptions().Gap,
            postToUi: PostToUi);
        _zones.PlaceRequested += placements =>
            _layouts.PlaceRects(placements.Select(p => (p.Window.Handle, p.Rect)));
        _zones.StatusRequested += OnZoneStatus;
        _zones.OverflowRequested = AskZoneOverflowAsync;
        _zones.IsInteractionBusy = () => _prompting;

        Title = "Fluent Zones";
        AppWindow.Title = "Fluent Zones";
        RootGrid.Loaded += (_, _) => FitWindowToWorkArea();

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TryStyleTitleBar();

        BuildLayoutCards();
        HookStartupCardHover();
        StartupExpander.Expanding += (_, _) => AnimateStartupContent();
        ApplyOptionsToUi();
        BuildHotkeyRows();
        BuildAboutLinks();

        SetVersionText();
        Nav.SelectedItem = Nav.MenuItems[0];

        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);

        // 录制热键时窗口失焦（Alt+Tab、点别的程序）就取消录制，
        // 否则录制状态会一直挂着，全局热键也跟着被静音
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                CancelCapture(silent: true);
            }
        };

        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        try
        {
            _hotkeys.Start();
            _hotkeysAvailable = true;
            RegisterAllHotkeys();
            if (!_hotkeys.TryRegister(
                    9000,
                    new HotkeyCombo(HotkeyCombo.ModControl | HotkeyCombo.ModAlt, 0x5A),
                    HotkeyAction.Zones,
                    out var zoneError))
            {
                UpdateStatus("分区模式热键 Ctrl+Alt+Z 注册失败：" + zoneError, StatusLevel.Warn);
            }
            else
            {
                UpdateStatus("就绪：Ctrl+Alt+Z 开关分区模式；选布局进入工作区", StatusLevel.Info);
            }
        }
        catch (Exception ex)
        {
            _hotkeysAvailable = false;
            UpdateStatus("热键服务启动失败：" + ex.Message, StatusLevel.Error);
        }

        _initializing = false;
        Closed += OnClosed;
    }

    // ---------------- 窗口 ----------------

    private bool _sized;

    /// <summary>按当前显示器的缩放比例决定窗口大小（AppWindow 用的是物理像素）。</summary>
    private void FitWindowToWorkArea()
    {
        if (_sized)
        {
            return;
        }

        _sized = true;

        try
        {
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var width = Math.Min((int)Math.Round(1000 * scale), (int)(work.Width * 0.95));
            var height = Math.Min((int)Math.Round(880 * scale), (int)(work.Height * 0.92));

            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(
                work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2));

            ContentScroller.Focus(FocusState.Programmatic);
        }
        catch
        {
            // 尺寸调整失败不影响使用
        }
    }

    private void TryStyleTitleBar()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        catch
        {
            // 某些系统版本不支持自定义标题栏颜色
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // 恢复工作区里被去掉的窗口边框（并清掉落盘记录），再释放拖拽钩子与窗口监听
        _zones.Dispose();
        _hotkeys.Dispose();
    }

    // ---------------- 布局卡片 ----------------

    private void BuildLayoutCards()
    {
        var kinds = new[]
        {
            LayoutKind.Cascade,
            LayoutKind.Columns,
            LayoutKind.Rows,
            LayoutKind.Grid,
            LayoutKind.MainLeft,
        };

        for (var i = 0; i < kinds.Length; i++)
        {
            var card = CreateLayoutCard(kinds[i]);
            var gear = CreateGearButton(kinds[i]);

            // 齿轮和卡片是兄弟而不是父子：点齿轮不会触发"单击卡片立即排布"
            var host = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
            host.Children.Add(card);
            host.Children.Add(gear);
            AttachGearHover(card, gear);

            Grid.SetColumn(host, i % 3);
            Grid.SetRow(host, i / 3);
            LayoutCardsHost.Children.Add(host);
            _cards[kinds[i]] = card;
        }

        _restoreCard = CreateRestoreCard();
        Grid.SetColumn(_restoreCard, 2);
        Grid.SetRow(_restoreCard, 1);
        LayoutCardsHost.Children.Add(_restoreCard);
        _restoreCard.IsEnabled = false;
    }

    private Button CreateLayoutCard(LayoutKind kind)
    {
        var action = kind.ToString();
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(BuildPreview(kind));

        stack.Children.Add(new TextBlock
        {
            Text = LayoutNames.Display(kind),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });

        stack.Children.Add(new TextBlock
        {
            Text = LayoutNames.Description(kind),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MinHeight = 32,
        });

        var (keycap, keycapText) = CreateKeycap(GetComboText(action));
        _keycapTexts[action] = keycapText;
        stack.Children.Add(keycap);

        var button = new Button
        {
            Content = stack,
            Style = (Style)Application.Current.Resources["LayoutCardButtonStyle"],
        };
        ToolTipService.SetToolTip(button,
            $"{LayoutNames.Display(kind)}　{GetComboText(action)}\nShift + 热键 = 作用于所有显示器");
        button.Click += (_, _) => ApplyLayout(kind);
        return button;
    }

    /// <summary>
    /// 卡片右上角的齿轮：打开该布局的参数设置。网格和左右并排有参数，其余三张卡片的齿轮置灰。
    /// </summary>
    private Button CreateGearButton(LayoutKind kind)
    {
        var configurable = kind is LayoutKind.Grid or LayoutKind.Columns;

        var gear = new Button
        {
            Content = new FontIcon { Glyph = "\uE713", FontSize = 14 },
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            IsEnabled = configurable,
            Opacity = configurable ? 0.5 : 0.25,
        };

        AutomationProperties.SetName(
            gear,
            configurable ? $"{LayoutNames.Display(kind)}设置" : $"{LayoutNames.Display(kind)}暂无可调参数");
        ToolTipService.SetToolTip(
            gear,
            kind == LayoutKind.Grid
                ? "设置：最多窗口数"
                : kind == LayoutKind.Columns
                    ? "设置：每行列数"
                    : "该布局暂无可调参数");

        if (configurable)
        {
            gear.Click += async (_, _) =>
            {
                if (kind == LayoutKind.Grid)
                {
                    await ShowGridSettingsAsync();
                }
                else
                {
                    await ShowColumnsSettingsAsync();
                }
            };
        }

        return gear;
    }

    /// <summary>齿轮平时淡显，鼠标悬停卡片时变明显。</summary>
    private static void AttachGearHover(Button card, Button gear)
    {
        var normal = gear.Opacity;

        void Enter() => gear.Opacity = gear.IsEnabled ? 0.95 : 0.35;
        void Exit() => gear.Opacity = normal;

        card.PointerEntered += (_, _) => Enter();
        card.PointerExited += (_, _) => Exit();
        gear.PointerEntered += (_, _) => Enter();
        gear.PointerExited += (_, _) => Exit();
    }

    /// <summary>
    /// 网格的设置弹窗：最多窗口数（<see cref="AppSettings.GridLimitMin"/>–<see cref="AppSettings.GridLimitMax"/>）。
    /// 「恢复默认」只把弹窗里的值改回默认，点「保存」才写入设置。
    /// </summary>
    private async Task ShowGridSettingsAsync()
    {
        if (_prompting)
        {
            return;
        }

        _prompting = true;

        try
        {
            // 正在录制热键时把录制收起来，否则弹窗里的按键会被录制流程吃掉
            CancelCapture(silent: true);
            BringWindowToFront();

            var limitBox = new NumberBox
            {
                Header = "最多窗口数",
                Value = _settings.GridLimit,
                Minimum = AppSettings.GridLimitMin,
                Maximum = AppSettings.GridLimitMax,
                SmallChange = 1,
                LargeChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
                Width = 160,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            var content = new StackPanel { Spacing = 12, MinWidth = 340 };
            content.Children.Add(new TextBlock
            {
                Text = $"窗口比格子多时，多出来的会被最小化（按 Z 序，最久没用的先下去），" +
                       $"Ctrl+Alt+0 可一起还原。范围 {AppSettings.GridLimitMin}–{AppSettings.GridLimitMax}。",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            content.Children.Add(limitBox);

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "网格平铺设置",
                Content = content,
                PrimaryButtonText = "保存",
                SecondaryButtonText = "恢复默认",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            // 恢复默认不关弹窗，只把值改回默认，再点保存才生效
            dialog.SecondaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                limitBox.Value = AppSettings.GridLimitDefault;
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var raw = limitBox.Value;
            var value = double.IsNaN(raw) ? _settings.GridLimit : (int)Math.Round(raw);
            _settings.GridLimit = Math.Clamp(value, AppSettings.GridLimitMin, AppSettings.GridLimitMax);
            SettingsStore.Save(_settings);

            UpdateStatus(
                $"网格：最多 {_settings.GridLimit} 格（超出的窗口按 Z 序最小化）",
                StatusLevel.Info);
        }
        catch (Exception ex)
        {
            UpdateStatus("打开设置失败：" + ex.Message, StatusLevel.Error);
        }
        finally
        {
            _prompting = false;
        }
    }

    /// <summary>左右并排的设置：每行列数（3 / 4），用下拉选，不要数字输入。</summary>
    private async Task ShowColumnsSettingsAsync()
    {
        if (_prompting)
        {
            return;
        }

        _prompting = true;

        try
        {
            CancelCapture(silent: true);
            BringWindowToFront();

            var combo = new ComboBox
            {
                Header = "每行列数",
                MinWidth = 180,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            combo.Items.Add("3 列");
            combo.Items.Add("4 列");
            combo.SelectedIndex = _settings.ColumnsLimit == AppSettings.ColumnsLimitMin ? 0 : 1;

            var content = new StackPanel { Spacing = 12, MinWidth = 340 };
            content.Children.Add(new TextBlock
            {
                Text = $"窗口按每行 3 或 4 个排列，放不下的自动换到下一行。" +
                       $"范围 {AppSettings.ColumnsLimitMin}–{AppSettings.ColumnsLimitMax} 列。",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            content.Children.Add(combo);

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "左右并排设置",
                Content = content,
                PrimaryButtonText = "保存",
                SecondaryButtonText = "恢复默认",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            // 恢复默认只改弹窗里的值，点保存才写入
            dialog.SecondaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                combo.SelectedIndex = 1;
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var value = combo.SelectedIndex == 0
                ? AppSettings.ColumnsLimitMin
                : AppSettings.ColumnsLimitMax;
            _settings.ColumnsLimit = value;
            SettingsStore.Save(_settings);
            UpdateStatus($"左右并排：每行最多 {value} 列", StatusLevel.Info);
        }
        catch (Exception ex)
        {
            UpdateStatus("打开设置失败：" + ex.Message, StatusLevel.Error);
        }
        finally
        {
            _prompting = false;
        }
    }

    private Button CreateRestoreCard()
    {
        const string action = "Restore";
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new FontIcon
        {
            Glyph = "\uE7A7",
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        });

        stack.Children.Add(new TextBlock
        {
            Text = "还原布局",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });

        stack.Children.Add(new TextBlock
        {
            Text = "把窗口放回上一次排列之前的位置",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MinHeight = 32,
        });

        var (keycap, keycapText) = CreateKeycap(GetComboText(action));
        _keycapTexts[action] = keycapText;
        stack.Children.Add(keycap);

        var button = new Button
        {
            Content = stack,
            Style = (Style)Application.Current.Resources["LayoutCardButtonStyle"],
        };
        ToolTipService.SetToolTip(button, "还原上一次布局　" + GetComboText(action));
        button.Click += (_, _) => RestoreLayout();
        return button;
    }

    private static UIElement BuildPreview(LayoutKind kind)
    {
        const double width = 96;
        const double height = 54;

        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        foreach (var rect in PreviewRects(kind))
        {
            var window = CreatePreviewWindow(rect.Width, rect.Height);
            Canvas.SetLeft(window, rect.X);
            Canvas.SetTop(window, rect.Y);
            canvas.Children.Add(window);
        }

        return canvas;
    }

    /// <summary>卡片缩略图里的“小窗口”：浅色底 + 细描边 + 一条标题栏，五个布局共用同一套画法。</summary>
    private static FrameworkElement CreatePreviewWindow(double width, double height)
    {
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

        var root = new Grid
        {
            Width = Math.Max(width, 4),
            Height = Math.Max(height, 4),
        };

        root.Children.Add(new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(2.5),
            Opacity = 0.18,
        });

        root.Children.Add(new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(0.8),
            CornerRadius = new CornerRadius(2.5),
            Opacity = 0.5,
        });

        if (width >= 18 && height >= 12)
        {
            root.Children.Add(new Border
            {
                Background = accent,
                CornerRadius = new CornerRadius(1.2),
                Height = 2.6,
                Width = Math.Max(width * 0.42, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2.4, 2.4, 0, 0),
                Opacity = 0.65,
            });
        }

        return root;
    }

    private static List<Rect> PreviewRects(LayoutKind kind)
    {
        const double width = 96;
        const double height = 54;
        var rects = new List<Rect>();

        switch (kind)
        {
            case LayoutKind.Cascade:
                for (var i = 0; i < 3; i++)
                {
                    rects.Add(new Rect(4 + i * 9, 4 + i * 6, 60, 38));
                }

                break;

            case LayoutKind.Columns:
                rects.Add(new Rect(0, 0, 30, height));
                rects.Add(new Rect(33, 0, 30, height));
                rects.Add(new Rect(66, 0, 30, height));
                break;

            case LayoutKind.Rows:
                rects.Add(new Rect(0, 0, width, 16));
                rects.Add(new Rect(0, 19, width, 16));
                rects.Add(new Rect(0, 38, width, 16));
                break;

            case LayoutKind.Grid:
                rects.Add(new Rect(0, 0, 58, 32));
                rects.Add(new Rect(61, 0, 35, 32));
                rects.Add(new Rect(0, 35, 46, 19));
                rects.Add(new Rect(49, 35, 47, 19));
                break;

            case LayoutKind.MainLeft:
                rects.Add(new Rect(0, 0, 58, height));
                rects.Add(new Rect(61, 0, 35, 16));
                rects.Add(new Rect(61, 19, 35, 16));
                rects.Add(new Rect(61, 38, 35, 16));
                break;
        }

        return rects;
    }

    private static (Border Border, TextBlock Text) CreateKeycap(string text)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var border = new Border
        {
            Style = (Style)Application.Current.Resources["KeycapBorderStyle"],
            Child = textBlock,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        return (border, textBlock);
    }

    // ---------------- 选项 ----------------

    private void ApplyOptionsToUi()
    {
        GapSlider.Value = Math.Clamp(_settings.Gap, 0, 40);
        GapValueText.Text = $"{(int)Math.Round(GapSlider.Value)} px";
        ScopeCombo.SelectedIndex = _settings.Scope == LayoutScope.AllMonitors ? 1 : 0;

        var startupEnabled = _settings.StartWithWindows || StartupRegistration.IsEnabled();
        _settings.StartWithWindows = startupEnabled;
        StartupToggle.IsOn = startupEnabled;

        // 打包（MSIX）应用不能提权：管理员模式启动在商店版里不可用
        ElevatedToggle.IsEnabled = !HostInfo.IsPackaged;
        ElevatedToggle.IsOn = _settings.RunAsAdmin && !HostInfo.IsPackaged;
        ToolTipService.SetToolTip(
            ElevatedToggle,
            HostInfo.IsPackaged
                ? "MSIX 打包版不能请求管理员权限"
                : "启动时请求管理员权限（UAC）");

        UpdateToggleText(StartupStateText, StartupToggle);
        UpdateToggleText(ElevatedStateText, ElevatedToggle);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.SelectedItemContainer?.Tag as string ?? "home";
        HomePanel.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        PrefsPanel.Visibility = tag == "prefs" ? Visibility.Visible : Visibility.Collapsed;
        HotkeysPanel.Visibility = tag == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        ContentScroller.ChangeView(null, 0, null, true);
    }

    private void SetVersionText()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = version is null
            ? "版本 0.1.0"
            : $"版本 {version.Major}.{version.Minor}.{version.Build}";
    }

    private void GapSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (GapValueText != null)
        {
            GapValueText.Text = $"{(int)Math.Round(e.NewValue)} px";
        }

        if (_initializing)
        {
            return;
        }

        _settings.Gap = (int)Math.Round(e.NewValue);
        SettingsStore.Save(_settings);
    }

    private void ScopeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.Scope = ScopeCombo.SelectedIndex == 1
            ? LayoutScope.AllMonitors
            : LayoutScope.CursorMonitor;
        SettingsStore.Save(_settings);
        UpdateStatus(
            "作用范围：" + (_settings.Scope == LayoutScope.AllMonitors ? "所有显示器" : "鼠标所在显示器"),
            StatusLevel.Info);
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateToggleText(StartupStateText, StartupToggle);

        if (_initializing || _syncingToggle)
        {
            return;
        }

        try
        {
            StartupRegistration.SetEnabled(StartupToggle.IsOn);
            _settings.StartWithWindows = StartupToggle.IsOn;
            SettingsStore.Save(_settings);
            UpdateStatus(StartupToggle.IsOn ? "已设置为开机启动" : "已取消开机启动", StatusLevel.Info);
        }
        catch (Exception ex)
        {
            _syncingToggle = true;
            StartupToggle.IsOn = !StartupToggle.IsOn;
            _syncingToggle = false;
            UpdateStatus("设置开机启动失败：" + ex.Message, StatusLevel.Error);
        }
    }

    private void ElevatedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateToggleText(ElevatedStateText, ElevatedToggle);

        if (_initializing)
        {
            return;
        }

        _settings.RunAsAdmin = ElevatedToggle.IsOn;
        SettingsStore.Save(_settings);
        UpdateStatus(
            ElevatedToggle.IsOn
                ? "管理员模式启动：下次启动会请求管理员权限（UAC）"
                : "已关闭管理员模式启动",
            StatusLevel.Info);
    }

    /// <summary>
    /// 悬停时整张“启动”卡片变灰（和 Windows 设置里的可展开卡片一致）。
    /// Expander 标题本身是个按钮，会把指针事件吃掉，所以在根节点用 PointerMoved 判断位置。
    /// </summary>
    private void HookStartupCardHover()
    {
        RootGrid.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler((_, e) => UpdateStartupCardHover(e.GetCurrentPoint(RootGrid).Position)),
            true);
        RootGrid.AddHandler(
            UIElement.PointerExitedEvent,
            new PointerEventHandler((_, _) => SetStartupCardHover(false)),
            true);
    }

    private void UpdateStartupCardHover(Point position)
    {
        if (StartupCard.ActualWidth <= 0 || StartupCard.ActualHeight <= 0)
        {
            return;
        }

        var transform = StartupCard.TransformToVisual(RootGrid);
        var bounds = transform.TransformBounds(
            new Rect(0, 0, StartupCard.ActualWidth, StartupCard.ActualHeight));
        SetStartupCardHover(bounds.Contains(position));
    }

    private void SetStartupCardHover(bool hover)
    {
        var key = hover
            ? "CardBackgroundFillColorSecondaryBrush"
            : "CardBackgroundFillColorDefaultBrush";
        if (Application.Current.Resources[key] is Brush brush)
        {
            StartupCard.Background = brush;
        }
    }

    /// <summary>开关右侧的状态文字（Windows 设置里是「关/开 + 开关」的顺序）。</summary>
    private void BuildAboutLinks()
    {
        AddAboutLink("\uE8A7", "GitHub 仓库", "https://github.com/xuzhunzhi/fluent-zones");
        AddAboutLink("\uE736", "README（功能与已知限制）", "https://github.com/xuzhunzhi/fluent-zones/blob/main/README.md");
        AddAboutLink("\uE73E", "MIT License", "https://github.com/xuzhunzhi/fluent-zones/blob/main/LICENSE");
    }

    private void AddAboutLink(string glyph, string text, string url)
    {
        var row = new Grid
        {
            ColumnSpacing = 12,
            Padding = new Thickness(16, 12, 12, 12),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon { Glyph = glyph, FontSize = 16 };
        icon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        row.Children.Add(icon);

        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 12 };
        chevron.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        Grid.SetColumn(chevron, 2);
        row.Children.Add(chevron);

        var hover = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
        var transparent = new SolidColorBrush(Colors.Transparent);

        var button = new Button
        {
            Content = row,
            Background = transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(6),
        };

        button.Click += async (_, _) =>
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
            catch
            {
                // 打不开浏览器就忽略
            }
        };

        button.PointerEntered += (_, _) => button.Background = hover;
        button.PointerExited += (_, _) => button.Background = transparent;

        AboutLinksHost.Children.Add(button);
    }

    private static void UpdateToggleText(TextBlock label, ToggleSwitch toggle)
    {        label.Text = toggle.IsOn ? "开" : "关";
    }

    /// <summary>展开「启动」卡片时，内容淡入并从上方滑入；卡片之间的位移由 RepositionThemeTransition 补。</summary>
    private void AnimateStartupContent()
    {
        if (StartupContent.RenderTransform is not TranslateTransform slide)
        {
            return;
        }

        StartupContent.Opacity = 0;
        slide.Y = 12;

        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, StartupContent);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var move = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(move, slide);
        Storyboard.SetTargetProperty(move, "Y");

        storyboard.Children.Add(fade);
        storyboard.Children.Add(move);
        storyboard.Begin();
    }

    // ---------------- 热键表 ----------------

    private void BuildHotkeyRows()
    {
        HotkeyRowsHost.Children.Clear();
        _rows.Clear();

        var first = true;
        foreach (var setting in _settings.Hotkeys)
        {
            if (!HotkeyAction.TryParse(setting.Action, out var action))
            {
                continue;
            }

            if (!first)
            {
                HotkeyRowsHost.Children.Add(new Rectangle
                {
                    Height = 1,
                    Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                });
            }

            first = false;

            var row = new Grid
            {
                Padding = new Thickness(0, 8, 0, 8),
                ColumnSpacing = 12,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = action.DisplayName });
            var hint = new TextBlock
            {
                Text = "未注册",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            label.Children.Add(hint);

            row.Children.Add(label);

            var (keycap, keycapText) = CreateKeycap(setting.Combo);
            Grid.SetColumn(keycap, 1);
            row.Children.Add(keycap);

            var toggle = new ToggleSwitch
            {
                IsOn = setting.Enabled,
                OnContent = string.Empty,
                OffContent = string.Empty,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(toggle, 2);
            row.Children.Add(toggle);

            var editButton = new Button
            {
                Content = "修改",
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(editButton, 3);
            row.Children.Add(editButton);

            HotkeyRowsHost.Children.Add(row);

            var hotkeyRow = new HotkeyRow(setting, action.DisplayName, keycapText, hint, toggle, editButton);
            _rows[setting.Action] = hotkeyRow;
            _keycapTexts[setting.Action] = keycapText;

            toggle.Toggled += (_, _) => OnHotkeyEnabledChanged(hotkeyRow, toggle.IsOn);
            editButton.Click += (_, _) => BeginCapture(setting.Action);
        }
    }

    private void RegisterAllHotkeys()
    {
        foreach (var row in _rows.Values)
        {
            if (!row.Setting.Enabled)
            {
                SetHint(row, "已禁用", false);
                continue;
            }

            if (TryRegisterSetting(row.Setting, out var error))
            {
                SetHint(row, "生效中", false);
            }
            else
            {
                SetHint(row, "注册失败：" + (error ?? "未知原因"), true);
            }
        }
    }

    private bool TryRegisterSetting(HotkeySetting setting, out string? error)
    {
        error = null;

        if (!HotkeyAction.TryParse(setting.Action, out var action))
        {
            error = "动作无效";
            return false;
        }

        if (!HotkeyParser.TryParse(setting.Combo, out var combo, out var parseError))
        {
            error = parseError ?? "热键格式不正确";
            return false;
        }

        var id = _nextHotkeyId++;
        if (!_hotkeys.TryRegister(id, combo, action, out error))
        {
            error ??= "注册失败";
            return false;
        }

        _layoutHotkeyIds[setting.Action] = id;

        // 自动附加 Shift 变体：作用于所有显示器
        if (!action.IsRestore &&
            action.Scope == LayoutScope.CursorMonitor &&
            (combo.Modifiers & HotkeyCombo.ModShift) == 0)
        {
            var shiftId = _nextHotkeyId++;
            var shiftCombo = new HotkeyCombo(combo.Modifiers | HotkeyCombo.ModShift, combo.VirtualKey);
            var shiftAction = new HotkeyAction(false, action.Kind, LayoutScope.AllMonitors);
            if (_hotkeys.TryRegister(shiftId, shiftCombo, shiftAction, out _))
            {
                _allMonitorHotkeyIds[setting.Action] = shiftId;
            }
        }

        return true;
    }

    private void UnregisterSetting(string action)
    {
        if (_layoutHotkeyIds.Remove(action, out var id))
        {
            _hotkeys.Unregister(id);
        }

        if (_allMonitorHotkeyIds.Remove(action, out var shiftId))
        {
            _hotkeys.Unregister(shiftId);
        }
    }

    private void OnHotkeyEnabledChanged(HotkeyRow row, bool enabled)
    {
        if (_initializing || _syncingToggle)
        {
            return;
        }

        row.Setting.Enabled = enabled;

        if (!_hotkeysAvailable)
        {
            row.Setting.Enabled = false;
            _syncingToggle = true;
            row.Toggle.IsOn = false;
            _syncingToggle = false;
            SetHint(row, "热键服务不可用", true);
            return;
        }

        if (enabled)
        {
            if (!TryRegisterSetting(row.Setting, out var error))
            {
                row.Setting.Enabled = false;
                _syncingToggle = true;
                row.Toggle.IsOn = false;
                _syncingToggle = false;
                SetHint(row, "注册失败：" + error, true);
                UpdateStatus($"「{row.Description}」启用失败：{error}", StatusLevel.Error);
                SettingsStore.Save(_settings);
                return;
            }

            SetHint(row, "生效中", false);
            UpdateStatus($"「{row.Description}」已启用：{row.Setting.Combo}", StatusLevel.Success);
        }
        else
        {
            UnregisterSetting(row.Setting.Action);
            SetHint(row, "已禁用", false);
            UpdateStatus($"「{row.Description}」热键已禁用", StatusLevel.Info);
        }

        SettingsStore.Save(_settings);
    }

    private void BeginCapture(string action)
    {
        if (!_rows.TryGetValue(action, out var row))
        {
            return;
        }

        if (_editingAction != null)
        {
            CancelCapture(silent: true);
        }

        _editingAction = action;
        row.EditButton.Content = "按键…";
        row.EditButton.Focus(FocusState.Programmatic);
        UpdateStatus($"正在设置「{row.Description}」：请按下新的组合键，Esc 取消", StatusLevel.Info);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_editingAction == null)
        {
            return;
        }

        var virtualKey = (uint)e.Key;
        e.Handled = true;

        if (virtualKey == 0x1B) // Esc
        {
            CancelCapture();
            return;
        }

        if (HotkeyCombo.IsModifierKey(virtualKey))
        {
            return;
        }

        var combo = HotkeyCombo.FromParts(
            IsDown(VirtualKey.Control),
            IsDown(VirtualKey.Menu),
            IsDown(VirtualKey.Shift),
            IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows),
            virtualKey);

        if (!combo.IsValid)
        {
            UpdateStatus("热键必须包含 Ctrl、Alt 或 Win 之一", StatusLevel.Warn);
            return;
        }

        ApplyCapturedCombo(combo);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void ApplyCapturedCombo(HotkeyCombo combo)
    {
        var action = _editingAction;
        if (action == null || !_rows.TryGetValue(action, out var row))
        {
            return;
        }

        _editingAction = null;
        row.EditButton.Content = "修改";

        var oldCombo = row.Setting.Combo;
        var enabled = row.Setting.Enabled;
        UnregisterSetting(action);
        row.Setting.Combo = combo.ToDisplayString();

        if (!enabled)
        {
            row.ComboText.Text = row.Setting.Combo;
            UpdateKeycap(action);
            SettingsStore.Save(_settings);
            SetHint(row, "已禁用", false);
            UpdateStatus($"热键改为 {row.Setting.Combo}（该动作当前为禁用状态）", StatusLevel.Info);
            return;
        }

        if (!TryRegisterSetting(row.Setting, out var error))
        {
            row.Setting.Combo = oldCombo;
            TryRegisterSetting(row.Setting, out _);
            SetHint(row, "注册失败：" + error, true);
            UpdateStatus($"热键无效：{error}", StatusLevel.Error);
            return;
        }

        row.ComboText.Text = row.Setting.Combo;
        UpdateKeycap(action);
        SetHint(row, "生效中", false);
        SettingsStore.Save(_settings);
        UpdateStatus($"「{row.Description}」热键已改为 {row.Setting.Combo}", StatusLevel.Success);
    }

    private void CancelCapture(bool silent = false)
    {
        if (_editingAction == null)
        {
            return;
        }

        if (_rows.TryGetValue(_editingAction, out var row))
        {
            row.EditButton.Content = "修改";
        }

        _editingAction = null;
        if (!silent)
        {
            UpdateStatus("已取消热键修改", StatusLevel.Info);
        }
    }

    // ---------------- 执行 ----------------

    private enum FitChoice
    {
        Minimize,
        Cascade,
        Cancel,
    }

    private enum MainSlotChoice
    {
        UseMain,
        Cascade,
        Cancel,
    }

    private async void ApplyLayout(LayoutKind kind) =>
        await ApplyLayoutAsync(kind, CurrentOptions().Scope);

    private async Task ApplyLayoutAsync(LayoutKind kind, LayoutScope scope, IntPtr forcedMain = default)
    {
        // 分区能力开启时：选布局 = 按该布局进入工作区（层叠没有可拖的分隔线，仍按一次性排）
        if (kind != LayoutKind.Cascade && (ZoneToggle.IsOn || _zones.IsActive))
        {
            EnterZones(scope, kind);
            return;
        }

        if (_zones.IsActive)
        {
            _zones.Exit();
        }

        try
        {
            var options = new LayoutOptions(
                (int)Math.Round(GapSlider.Value), scope, _settings.GridLimit, _settings.ColumnsLimit);
            var result = _layouts.Apply(kind, options, mainSlot: forcedMain);

            // 网格装不下，但把声明需求最大的窗口换到主窗就能排：先弹窗问，用户点头才重排，
            // 不擅自改变窗口位置（也不能退回"均分竖条"那种跟网格无关的排法）
            if (result.NeedsMainSlot && forcedMain == IntPtr.Zero &&
                result.SuggestedMain is { } candidate)
            {
                switch (await AskMainSlotAsync(kind, candidate))
                {
                    case MainSlotChoice.UseMain:
                        await ApplyLayoutAsync(kind, scope, candidate.Handle);
                        return;
                    case MainSlotChoice.Cascade:
                        result = _layouts.Apply(LayoutKind.Cascade, options);
                        break;
                    default:
                        UpdateStatus("已取消排列", StatusLevel.Info);
                        return;
                }
            }

            if (result.NeedsChoice)
            {
                var victims = result.SuggestedMinimize?.Count ?? 0;
                var choice = await AskFitAsync(kind, victims);

                switch (choice)
                {
                    case FitChoice.Minimize:
                        result = _layouts.Apply(
                            kind, options, minimizeOldest: victims, mainSlot: forcedMain);
                        break;
                    case FitChoice.Cascade:
                        result = _layouts.Apply(LayoutKind.Cascade, options);
                        break;
                    default:
                        UpdateStatus("已取消排列", StatusLevel.Info);
                        return;
                }
            }

            ShowResult(result);
            if (result.Success)
            {
                HighlightCard(result.Kind ?? kind);

                if (ZoneToggle.IsOn && kind == LayoutKind.Cascade)
                {
                    UpdateStatus(result.Message + "（层叠没有可拖的分隔线，按一次性排列）", StatusLevel.Success);
                }

                // 有些应用会在收到尺寸变化后自己微调窗口，过一会儿再扶正一次
                await Task.Delay(600);
                var fixedCount = _layouts.RealignDrifted();
                if (fixedCount > 0)
                {
                    UpdateStatus(
                        result.Message + $"（已扶正 {fixedCount} 个自行偏移的窗口）",
                        StatusLevel.Success);
                }
            }

            var blocked = result.Unmovable?.Where(w => w.CannotMove).ToList();
            if (blocked is { Count: > 0 } && !_elevationPrompted)
            {
                _elevationPrompted = true;
                await AskElevateAsync(kind, blocked);
            }
        }
        catch (Exception ex)
        {
            UpdateStatus("排列失败：" + ex.Message, StatusLevel.Error);
        }
    }

    private async Task<FitChoice> AskFitAsync(LayoutKind kind, int victims)
    {
        if (_prompting)
        {
            return FitChoice.Cancel;
        }

        _prompting = true;

        try
        {
            BringWindowToFront();

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "这个布局放不下",
                Content =
                    $"有窗口声明的最小尺寸太大，装不进「{LayoutNames.Display(kind)}」。\n\n" +
                    $"可以把最久未使用（Z 序最底层）的 {victims} 个窗口最小化，再排列剩下的窗口。",
                PrimaryButtonText = $"最小化 {victims} 个并排列",
                SecondaryButtonText = "改用层叠",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync();
            return result switch
            {
                ContentDialogResult.Primary => FitChoice.Minimize,
                ContentDialogResult.Secondary => FitChoice.Cascade,
                _ => FitChoice.Cancel,
            };
        }
        finally
        {
            _prompting = false;
        }
    }

    /// <summary>
    /// 网格里某个窗口声明的最小尺寸塞不进它分到的格子，但换到主窗就排得下。
    /// 不擅自搬窗口，交给用户确认（放到主窗 / 改用层叠 / 取消）。
    /// </summary>
    private async Task<MainSlotChoice> AskMainSlotAsync(LayoutKind kind, WindowInfo candidate)
    {
        if (_prompting)
        {
            return MainSlotChoice.Cancel;
        }

        _prompting = true;

        try
        {
            BringWindowToFront();

            var declared = candidate.MinWidth > 0 && candidate.MinHeight > 0
                ? $"它声明的最小尺寸是 {candidate.MinWidth}×{candidate.MinHeight}，"
                : string.Empty;

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "这个窗口更适合放到主窗",
                Content =
                    $"{declared}装不进「{LayoutNames.Display(kind)}」给它分的格子。\n\n" +
                    "把它放到主窗（最大的一块）通常是最合适的做法。",
                PrimaryButtonText = "放到主窗",
                SecondaryButtonText = "改用层叠",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync();
            return result switch
            {
                ContentDialogResult.Primary => MainSlotChoice.UseMain,
                ContentDialogResult.Secondary => MainSlotChoice.Cascade,
                _ => MainSlotChoice.Cancel,
            };
        }
        finally
        {
            _prompting = false;
        }
    }

    private void BringWindowToFront()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            Activate();
        }
        catch
        {
            // 提不到前台也不影响弹窗
        }
    }

    private async Task AskElevateAsync(LayoutKind kind, IReadOnlyList<WindowInfo> blocked)
    {
        if (_prompting)
        {
            return;
        }

        _prompting = true;

        try
        {
            BringWindowToFront();

            var names = string.Join("、", blocked.Select(w => w.Title).Take(4));
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "有窗口动不了",
                Content =
                    $"{names} 以管理员权限运行，本工具权限不足，系统（UIPI）不允许移动它们的窗口。\n\n" +
                    "要以管理员身份重启本工具吗？重启后会自动重试这次排列。",
                PrimaryButtonText = "以管理员身份重启",
                CloseButtonText = "先不用",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                RestartElevated(kind);
            }
        }
        finally
        {
            _prompting = false;
        }
    }

    private void RestartElevated(LayoutKind kind)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                UpdateStatus("找不到程序路径，无法以管理员身份重启", StatusLevel.Error);
                return;
            }

            Process.Start(new ProcessStartInfo(exe, $"--apply {kind}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });

            Close();
        }
        catch (Exception ex)
        {
            UpdateStatus("以管理员身份重启失败：" + ex.Message, StatusLevel.Error);
        }
    }

    /// <summary>带 --apply 参数启动时，加载完自动排一次。</summary>
    public void ApplyOnStartup(LayoutKind kind)
    {
        RootGrid.Loaded += async (_, _) => await ApplyLayoutAsync(kind, CurrentOptions().Scope);
    }

    private void RestoreLayout()
    {
        if (_zones.IsActive)
        {
            _zones.Exit();
        }

        try
        {
            ShowResult(_layouts.Restore());
        }
        catch (Exception ex)
        {
            UpdateStatus("还原失败：" + ex.Message, StatusLevel.Error);
        }
    }

    // ---------------- 分区模式 ----------------

    private void ZoneToggle_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateToggleText(ZoneStateText, ZoneToggle);

        if (_syncingZoneToggle)
        {
            return;
        }

        // 开关只表示「开启分区能力」：打开时不动窗口，等你选一个布局再进工作区
        if (ZoneToggle.IsOn)
        {
            if (!_zones.IsActive)
            {
                UpdateStatus("分区模式已开启：选一个布局（卡片或 Ctrl+Alt+1..5）进入工作区", StatusLevel.Info);
            }

            return;
        }

        if (_zones.IsActive)
        {
            _zones.Exit();
            UpdateStatus("分区模式已关闭，窗口保持当前布局；Ctrl+Alt+0 可还原", StatusLevel.Info);
        }
        else
        {
            UpdateStatus("分区模式已关闭", StatusLevel.Info);
        }
    }

    /// <summary>
    /// 热键 Ctrl+Alt+Z：只开关「分区模式」能力，不直接铺窗口；
    /// 进入工作区仍然靠选布局（卡片或 Ctrl+Alt+1..5）。
    /// </summary>
    private void ToggleZones()
    {
        if (ZoneToggle.IsOn)
        {
            SetZoneToggle(false);
            if (_zones.IsActive)
            {
                _zones.Exit();
                UpdateStatus("分区模式已关闭，窗口保持当前布局；Ctrl+Alt+0 可还原", StatusLevel.Info);
            }
            else
            {
                UpdateStatus("分区模式已关闭", StatusLevel.Info);
            }

            return;
        }

        SetZoneToggle(true);
        UpdateStatus("分区模式已开启：选一个布局（卡片或 Ctrl+Alt+1..5）进入工作区", StatusLevel.Info);
    }

    /// <summary>
    /// 进入工作区；已经在工作区里就按新布局重建。
    /// <paramref name="kind"/> 为空时按窗口数量自动选（≤3 左右并排，否则网格）。
    /// 这里只做窗口筛选、首次快照和状态栏；切分树 / 边框 / 拖拽 / 窗口事件都在 <see cref="ZoneSession"/>。
    /// </summary>
    private void EnterZones(LayoutScope scope, LayoutKind? kind = null)
    {
        try
        {
            var all = _layouts.GetWindows(scope);

            // 提权窗口（UIPI）动不了：不要把它算进槽位，否则会留下一个空格子，
            // 它的对话框还会压在别的分区上（布局模式在 LayoutService 里也是这么排除的）。
            var unmovable = all.Where(w => w.CannotMove).ToList();
            var movable = all.Where(w => !w.CannotMove).ToList();
            if (movable.Count < 2)
            {
                UpdateStatus(
                    unmovable.Count > 0
                        ? "可移动的窗口不足两个：提权窗口需要以管理员身份运行"
                        : "至少需要两个窗口才能进入分区模式",
                    StatusLevel.Warn);
                return;
            }

            var monitor = movable[0].Monitor;
            var windows = movable.Where(w => w.Monitor == monitor).ToList();
            if (windows.Count < 2)
            {
                windows = movable.ToList();
                monitor = windows[0].Monitor;
            }

            var effective = kind ?? (windows.Count <= 3 ? LayoutKind.Columns : LayoutKind.Grid);

            // 只记第一次进入前的窗口位置：Ctrl+Alt+0 永远回到非工作区状态
            if (!_zones.IsActive)
            {
                _layouts.CaptureSnapshot(windows);
            }

            _zones.Enter(effective, windows, monitor);

            SetZoneToggle(true);
            HighlightCard(effective);
            var blockedNote = unmovable.Count > 0
                ? $"；无法移动：{string.Join("、", unmovable.Select(w => w.Title))}（管理员权限）"
                : string.Empty;
            UpdateStatus(
                $"分区模式（{LayoutNames.Display(effective)}）：拖动分隔线同时调整两侧窗口" +
                $"（{windows.Count} 个），Ctrl+Alt+0 还原并退出{blockedNote}",
                StatusLevel.Info);
        }
        catch (Exception ex)
        {
            UpdateStatus("进入分区模式失败：" + ex.Message, StatusLevel.Error);
            _zones.Exit();
        }
    }

    /// <summary>程序改开关状态时不要再触发一次进入/退出。</summary>
    private void SetZoneToggle(bool on)
    {
        _syncingZoneToggle = true;
        ZoneToggle.IsOn = on;
        _syncingZoneToggle = false;
        UpdateToggleText(ZoneStateText, ZoneToggle);
    }

    /// <summary>工作区里窗口的可用区域：四边留出画边框的边距。</summary>
    private WindowRect ZoneArea(IntPtr monitor)
    {
        var area = _layouts.GetMonitorArea(monitor);
        return new WindowRect(
            area.Left + ZoneMargin,
            area.Top + ZoneMargin,
            Math.Max(area.Width - ZoneMargin * 2, 80),
            Math.Max(area.Height - ZoneMargin * 2, 80));
    }

    private async Task<bool> AskZoneOverflowAsync(int victims, IReadOnlyList<WindowInfo> newcomers)
    {
        if (_prompting)
        {
            return false;
        }

        _prompting = true;

        try
        {
            CancelCapture(silent: true);
            BringWindowToFront();

            var newcomer = newcomers.FirstOrDefault()?.Title ?? "新窗口";
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "工作区放不下新窗口",
                Content =
                    $"「{newcomer}」加入后，有窗口的最小尺寸超过了分到的格子。\n\n" +
                    $"可以最小化最久未使用的 {victims} 个窗口腾出空间，或者让它先留在工作区外。",
                PrimaryButtonText = $"最小化 {victims} 个并加入",
                CloseButtonText = "先不加入",
                DefaultButton = ContentDialogButton.Primary,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _prompting = false;
        }
    }

    /// <summary>把拖拽钩子 / 窗口事件的回调送回 UI 线程（与原来 DispatcherQueue.TryEnqueue 的时序一致）。</summary>
    private void PostToUi(Action action) => DispatcherQueue.TryEnqueue(() => action());

    /// <summary>ZoneSession 的状态栏消息映射到界面的 <see cref="StatusLevel"/>。</summary>
    private void OnZoneStatus(string message, ZoneStatusLevel level) =>
        UpdateStatus(message, level switch
        {
            ZoneStatusLevel.Success => StatusLevel.Success,
            ZoneStatusLevel.Warn => StatusLevel.Warn,
            ZoneStatusLevel.Error => StatusLevel.Error,
            _ => StatusLevel.Info,
        });

    private LayoutOptions CurrentOptions() => new(
        (int)Math.Round(GapSlider.Value),
        ScopeCombo.SelectedIndex == 1 ? LayoutScope.AllMonitors : LayoutScope.CursorMonitor);

    private void OnHotkeyPressed(object? sender, HotkeyEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleHotkey(e.Action);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => HandleHotkey(e.Action));
        }
    }

    private void HandleHotkey(HotkeyAction action)
    {
        // 录制热键时不响应全局热键，否则改键会顺手把窗口排一遍
        if (_editingAction != null)
        {
            return;
        }

        if (action.IsZones)
        {
            ToggleZones();
            return;
        }

        if (action.IsRestore)
        {
            RestoreLayout();
            return;
        }

        _ = ApplyLayoutAsync(action.Kind, action.Scope);
    }

    private void ShowResult(LayoutResult result)
    {
        UpdateStatus(result.Message, result.Success ? StatusLevel.Success : StatusLevel.Warn);
        if (_restoreCard != null)
        {
            _restoreCard.IsEnabled = _layouts.CanRestore;
        }
    }

    private void HighlightCard(LayoutKind kind)
    {
        if (!_cards.TryGetValue(kind, out var card))
        {
            return;
        }

        if (_highlightedCard != null && _highlightedCard != card)
        {
            _highlightedCard.BorderThickness = new Thickness(1);
            _highlightedCard.BorderBrush =
                (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        }

        card.BorderThickness = new Thickness(2);
        card.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _highlightedCard = card;
    }

    // ---------------- 状态栏 ----------------

    private void UpdateStatus(string message, StatusLevel level)
    {
        StatusText.Text = message;

        StatusIcon.Glyph = level switch
        {
            StatusLevel.Success => "\uE73E",
            StatusLevel.Warn => "\uE7BA",
            StatusLevel.Error => "\uEA39",
            _ => "\uE946",
        };

        var brushKey = level switch
        {
            StatusLevel.Success => "SystemFillColorSuccessBrush",
            StatusLevel.Warn => "SystemFillColorCautionBrush",
            StatusLevel.Error => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        };

        StatusIcon.Foreground = (Brush)Application.Current.Resources[brushKey];
    }

    // ---------------- 小工具 ----------------

    private string GetComboText(string action)
    {
        var setting = _settings.Hotkeys.FirstOrDefault(
            h => string.Equals(h.Action, action, StringComparison.OrdinalIgnoreCase));
        return setting?.Combo ?? "未设置";
    }

    private void UpdateKeycap(string action)
    {
        if (_keycapTexts.TryGetValue(action, out var text))
        {
            text.Text = GetComboText(action);
        }
    }

    private void SetHint(HotkeyRow row, string text, bool isError)
    {
        row.HintText.Text = text;
        row.HintText.Foreground = (Brush)Application.Current.Resources[
            isError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
    }

    private sealed class HotkeyRow
    {
        public HotkeyRow(
            HotkeySetting setting,
            string description,
            TextBlock comboText,
            TextBlock hintText,
            ToggleSwitch toggle,
            Button editButton)
        {
            Setting = setting;
            Description = description;
            ComboText = comboText;
            HintText = hintText;
            Toggle = toggle;
            EditButton = editButton;
        }

        public HotkeySetting Setting { get; }

        public string Description { get; }

        public TextBlock ComboText { get; }

        public TextBlock HintText { get; }

        public ToggleSwitch Toggle { get; }

        public Button EditButton { get; }
    }
}
