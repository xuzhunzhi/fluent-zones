using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WindowLayout.Core;

namespace WindowLayout.UI;

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
    private bool _hotkeysAvailable;
    private bool _prompting;
    private bool _elevationPrompted;

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

        _settings = SettingsStore.Load();

        Title = "窗口布局";
        AppWindow.Title = "窗口布局";
        RootGrid.Loaded += (_, _) => FitWindowToWorkArea();

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TryStyleTitleBar();

        BuildLayoutCards();
        ApplyOptionsToUi();
        BuildHotkeyRows();

        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);

        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        try
        {
            _hotkeys.Start();
            _hotkeysAvailable = true;
            RegisterAllHotkeys();
            UpdateStatus("就绪：试试 Ctrl+Alt+1 层叠窗口", StatusLevel.Info);
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
            var width = Math.Min((int)Math.Round(860 * scale), (int)(work.Width * 0.95));
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
            Grid.SetColumn(card, i % 3);
            Grid.SetRow(card, i / 3);
            LayoutCardsHost.Children.Add(card);
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

        var brush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var rects = PreviewRects(kind);

        for (var i = 0; i < rects.Count; i++)
        {
            var rect = new Rectangle
            {
                Width = Math.Max(rects[i].Width - 1.5, 2),
                Height = Math.Max(rects[i].Height - 1.5, 2),
                RadiusX = 2,
                RadiusY = 2,
                Fill = brush,
                Opacity = kind == LayoutKind.Cascade ? 0.35 + i * 0.15 : 0.6,
            };
            Canvas.SetLeft(rect, rects[i].X + 0.75);
            Canvas.SetTop(rect, rects[i].Y + 0.75);
            canvas.Children.Add(rect);
        }

        return canvas;
    }

    private static List<Rect> PreviewRects(LayoutKind kind)
    {
        const double width = 96;
        const double height = 54;
        var rects = new List<Rect>();

        switch (kind)
        {
            case LayoutKind.Cascade:
                for (var i = 0; i < 4; i++)
                {
                    rects.Add(new Rect(6 + i * 9, 6 + i * 6, 62, 34));
                }

                break;

            case LayoutKind.Columns:
                for (var i = 0; i < 4; i++)
                {
                    rects.Add(new Rect(i * width / 4.0, 0, width / 4.0, height));
                }

                break;

            case LayoutKind.Rows:
                for (var i = 0; i < 4; i++)
                {
                    rects.Add(new Rect(0, i * height / 4.0, width, height / 4.0));
                }

                break;

            case LayoutKind.Grid:
                for (var row = 0; row < 2; row++)
                {
                    for (var col = 0; col < 2; col++)
                    {
                        rects.Add(new Rect(col * width / 2.0, row * height / 2.0, width / 2.0, height / 2.0));
                    }
                }

                break;

            case LayoutKind.MainLeft:
                rects.Add(new Rect(0, 0, width / 2.0, height));
                for (var i = 0; i < 3; i++)
                {
                    rects.Add(new Rect(width / 2.0, i * height / 3.0, width / 2.0, height / 3.0));
                }

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

    private async void ApplyLayout(LayoutKind kind) =>
        await ApplyLayoutAsync(kind, CurrentOptions().Scope);

    private async Task ApplyLayoutAsync(LayoutKind kind, LayoutScope scope)
    {
        try
        {
            var options = new LayoutOptions((int)Math.Round(GapSlider.Value), scope);
            var result = _layouts.Apply(kind, options);

            if (result.NeedsChoice)
            {
                var victims = result.SuggestedMinimize?.Count ?? 0;
                var choice = await AskFitAsync(kind, victims);

                switch (choice)
                {
                    case FitChoice.Minimize:
                        result = _layouts.Apply(kind, options, minimizeOldest: victims);
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
        try
        {
            ShowResult(_layouts.Restore());
        }
        catch (Exception ex)
        {
            UpdateStatus("还原失败：" + ex.Message, StatusLevel.Error);
        }
    }

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
        if (action.IsRestore)
        {
            try
            {
                ShowResult(_layouts.Restore());
            }
            catch (Exception ex)
            {
                UpdateStatus("还原失败：" + ex.Message, StatusLevel.Error);
            }

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
