// 屏幕亮线（ScreenStreaks）—— 屏幕纵向线条缺陷模拟工具
// Copyright (C) 2026 hcllmsx
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenStreaks.Infrastructure;
using ScreenStreaks.Models;
using ScreenStreaks.Services;

namespace ScreenStreaks.Views;

/// <summary>
/// 设置面板。参数改动通过 150ms 防抖后推给覆盖层，实现「边调边看」。
/// 拖动滑杆时每帧都重建可视树会明显掉帧，防抖是必要的。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppController _controller;
    private readonly DispatcherTimer _previewTimer;

    private bool _loading = true;
    private bool _labelsReady;

    public SettingsWindow(AppController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _previewTimer.Tick += OnPreviewTick;

        InitializeComponent();

        _labelsReady = true;
        LoadValues();
        _loading = false;

        Loaded += OnLoadedLayoutProbe;
    }

    /// <summary>
    /// 布局自检：确认标题与分隔线不重叠。
    /// 这类问题构建期查不出来，只能量实际渲染出来的矩形。
    /// </summary>
    private void OnLoadedLayoutProbe(object sender, RoutedEventArgs e)
    {
        try
        {
            Rect title = TitleText.TransformToAncestor(this)
                .TransformBounds(new Rect(0, 0, TitleText.ActualWidth, TitleText.ActualHeight));

            Rect divider = HeaderDivider.TransformToAncestor(this)
                .TransformBounds(new Rect(0, 0, HeaderDivider.ActualWidth, HeaderDivider.ActualHeight));

            bool overlap = title.Bottom > divider.Top;

            string message =
                $"[settings-layout] title Y={title.Top:0.#}..{title.Bottom:0.#} " +
                $"divider Y={divider.Top:0.#}..{divider.Bottom:0.#} overlap={overlap}";

            if (overlap)
            {
                Logger.Warn(message);
            }
            else
            {
                Logger.Info(message);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("布局自检失败: " + ex.Message);
        }
    }

    // ================================================================ 载入与同步

    private void LoadValues()
    {
        StreakOptions options = _controller.Settings.Streaks;

        DensitySlider.Value = Clamp(options.Density, DensitySlider.Minimum, DensitySlider.Maximum);
        IntensitySlider.Value = Clamp(options.Intensity, IntensitySlider.Minimum, IntensitySlider.Maximum);
        BlinkSlider.Value = Clamp(options.BlinkDepth, BlinkSlider.Minimum, BlinkSlider.Maximum);
        CountSlider.Value = Clamp(options.MaxPerRegion, CountSlider.Minimum, CountSlider.Maximum);
        SeedBox.Text = options.Seed.ToString(CultureInfo.InvariantCulture);

        BrightToggle.IsChecked = options.EnableBright;
        FlickerToggle.IsChecked = options.EnableFlicker;
        DarkToggle.IsChecked = options.EnableDark;
        ColorStuckToggle.IsChecked = options.EnableColorStuck;

        UpdateValueLabels();
        UpdateHotkeyDisplay();
        UpdateRegionSummary();
        AutoStartToggle.IsChecked = _controller.IsAutoStartEnabled();
    }

    private void UpdateValueLabels()
    {
        if (!_labelsReady)
        {
            return;
        }

        DensityValue.Text = DensitySlider.Value.ToString("0.0");
        IntensityValue.Text = IntensitySlider.Value.ToString("0.00");
        BlinkValue.Text = (BlinkSlider.Value * 100.0).ToString("0") + " %";
        CountValue.Text = ((int)Math.Round(CountSlider.Value)).ToString();
    }

    private void UpdateHotkeyDisplay()
    {
        HotkeyDefinition hotkey = _controller.Settings.ToggleHotkey;

        if (!hotkey.IsValid)
        {
            HotkeyBox.Text = "未设置";
            HotkeyHint.Text = "当前没有热键。点击输入框按下组合键即可设置；未设置时请用托盘菜单切换展示态。";
            return;
        }

        HotkeyBox.Text = hotkey.ToString();

        HotkeyHint.Text = _controller.Hotkey.Current is null
            ? "当前热键未生效（很可能被别的程序占用了），换一个组合试试。"
            : "点击输入框后按下想要的组合键，Esc 放弃修改。";
    }

    private void UpdateRegionSummary()
    {
        int count = _controller.Settings.Regions.Count;
        if (count == 0)
        {
            RegionSummary.Text = "当前没有任何区域。回到主面板点击「开始框选区域」即可创建。";
            return;
        }

        var perMonitor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (StreakRegion region in _controller.Settings.Regions)
        {
            perMonitor.TryGetValue(region.MonitorId, out int existing);
            perMonitor[region.MonitorId] = existing + 1;
        }

        var parts = new List<string>(perMonitor.Count);
        foreach (KeyValuePair<string, int> pair in perMonitor)
        {
            parts.Add($"{pair.Key} × {pair.Value}");
        }

        RegionSummary.Text = $"共 {count} 个区域 —— {string.Join("，", parts)}";
    }

    // ================================================================ 参数改动

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_labelsReady)
        {
            return;
        }

        UpdateValueLabels();

        if (_loading)
        {
            return;
        }

        QueuePreview();
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !_labelsReady)
        {
            return;
        }

        bool anyKindEnabled =
            BrightToggle.IsChecked == true ||
            FlickerToggle.IsChecked == true ||
            DarkToggle.IsChecked == true ||
            ColorStuckToggle.IsChecked == true;

        if (!anyKindEnabled)
        {
            // 一种都不选等于屏幕上什么都不画，直接回退并给出提示
            BrightToggle.IsChecked = true;
            KindHint.Visibility = Visibility.Visible;
        }
        else
        {
            KindHint.Visibility = Visibility.Collapsed;
        }

        QueuePreview();
    }

    private void QueuePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        ApplyToModel();
        _controller.ApplyStreakOptions();
    }

    private void ApplyToModel()
    {
        StreakOptions options = _controller.Settings.Streaks;

        options.Density = DensitySlider.Value;
        options.Intensity = IntensitySlider.Value;
        options.BlinkDepth = BlinkSlider.Value;
        options.MaxPerRegion = (int)Math.Round(CountSlider.Value);
        options.EnableBright = BrightToggle.IsChecked == true;
        options.EnableFlicker = FlickerToggle.IsChecked == true;
        options.EnableDark = DarkToggle.IsChecked == true;
        options.EnableColorStuck = ColorStuckToggle.IsChecked == true;

        options.Normalize();
    }

    private void OnResetStreakOptionsClick(object sender, RoutedEventArgs e)
    {
        _controller.Settings.Streaks = new StreakOptions();
        _controller.Settings.Streaks.Normalize();

        _loading = true;
        try
        {
            LoadValues();
        }
        finally
        {
            _loading = false;
        }

        _controller.ApplyStreakOptions();
    }

    // ================================================================ 随机种子

    private void OnSeedBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        CommitSeed();
        Keyboard.ClearFocus();
    }

    private void OnSeedBoxCommit(object sender, KeyboardFocusChangedEventArgs e) => CommitSeed();

    private void CommitSeed()
    {
        if (_loading || !_labelsReady)
        {
            return;
        }

        StreakOptions options = _controller.Settings.Streaks;

        if (!int.TryParse(SeedBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
        {
            // 输入不是整数就还原显示，不打断用户
            SeedBox.Text = options.Seed.ToString(CultureInfo.InvariantCulture);
            return;
        }

        if (options.Seed == seed)
        {
            return;
        }

        options.Seed = seed;
        _controller.ApplyStreakOptions();
    }

    private void OnRerollSeedClick(object sender, RoutedEventArgs e)
    {
        StreakOptions options = _controller.Settings.Streaks;
        options.Seed = Random.Shared.Next(1, int.MaxValue);
        SeedBox.Text = options.Seed.ToString(CultureInfo.InvariantCulture);
        _controller.ApplyStreakOptions();
    }

    // ================================================================ 热键

    private void OnHotkeyBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotkeyBox.SelectAll();
        HotkeyHint.Text = "请按下组合键…（Esc 放弃）";
    }

    private void OnHotkeyBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdateHotkeyDisplay();
    }

    private void OnHotkeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        // 按 Alt 组合键时 WPF 把键放在 SystemKey 里
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.None)
        {
            return;
        }

        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            UpdateHotkeyDisplay();
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        uint flags = 0;
        if ((modifiers & ModifierKeys.Control) != 0) flags |= HotkeyDefinition.ModifierControl;
        if ((modifiers & ModifierKeys.Alt) != 0) flags |= HotkeyDefinition.ModifierAlt;
        if ((modifiers & ModifierKeys.Shift) != 0) flags |= HotkeyDefinition.ModifierShift;
        if ((modifiers & ModifierKeys.Windows) != 0) flags |= HotkeyDefinition.ModifierWin;

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            return;
        }

        var definition = new HotkeyDefinition
        {
            Modifiers = flags,
            VirtualKey = (uint)virtualKey,
        };

        Keyboard.ClearFocus();

        if (_controller.ApplyHotkey(definition))
        {
            UpdateHotkeyDisplay();
            HotkeyHint.Text = "热键已更新。";
        }
        else
        {
            UpdateHotkeyDisplay();
            HotkeyHint.Text = "这个组合被别的程序占用了，请换一个。";
        }
    }

    /// <summary>
    /// 清除热键。「默认」现在就是「不启用」，
    /// 所以这个按钮是清空而不是恢复成某个组合键。
    /// </summary>
    private void OnResetHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (_controller.ApplyHotkey(HotkeyDefinition.CreateEmpty()))
        {
            UpdateHotkeyDisplay();
            HotkeyHint.Text = "已清除热键，展示态改用托盘菜单切换。";
        }
    }

    // ================================================================ 其他

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _controller.SetAutoStart(AutoStartToggle.IsChecked == true);
        AutoStartToggle.IsChecked = _controller.IsAutoStartEnabled();
    }

    private void OnClearRegionsClick(object sender, RoutedEventArgs e)
    {
        if (_controller.Settings.Regions.Count == 0)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            "将删除全部已选区域，展示态也会一并退出。确定继续吗？",
            AppInfo.DisplayName,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        _controller.ClearAllRegions();
        UpdateRegionSummary();
    }

    /// <summary>整个窗口任意空白处都能拖动（挂在根 Border 上）</summary>
    private void OnWindowDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已抬起
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTick;
        base.OnClosed(e);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return min;
        }

        return Math.Min(max, Math.Max(min, value));
    }
}
