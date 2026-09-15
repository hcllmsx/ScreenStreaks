// ScreenStreaks —— 屏幕纵向线条缺陷模拟工具
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
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenStreaks.Infrastructure;
using ScreenStreaks.Models;
using ScreenStreaks.Services;

namespace ScreenStreaks.Views;

/// <summary>
/// 主面板。只做「展示状态 + 触发动作」，所有状态迁移都交给 <see cref="AppController"/>。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>作者主页</summary>
    private const string AuthorUrl = "https://space.bilibili.com/255947051";

    private readonly AppController _controller;
    private bool _syncing;

    public MainWindow(AppController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

        InitializeComponent();

        VersionText.Text = AppInfo.VersionWithPrefix;
        TaglineText.Text = AppInfo.Tagline;

        TryLoadLogo();
        RefreshState();
    }

    /// <summary>
    /// 载入应用图标作为左上角徽标。
    ///
    /// 图标已入库，正常情况下不会缺失；但 csproj 是按 Exists 条件包含该资源的，
    /// 文件一旦被删就不会打进程序集，所以这里仍必须容错：
    /// 资源不存在时保持字母徽标，而不是让窗口构造抛异常。
    /// （XAML 里直接写 Source 的话，缺文件会在 InitializeComponent 阶段就炸掉。）
    /// </summary>
    private void TryLoadLogo()
    {
        try
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri("pack://application:,,,/Assets/app.png", UriKind.Absolute);
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            source.Freeze();

            LogoImage.Source = source;
            LogoImage.Visibility = Visibility.Visible;
            LogoFrame.Visibility = Visibility.Visible;
            LogoFallback.Visibility = Visibility.Collapsed;

            Logger.Info($"已载入界面徽标 app.png ({source.PixelWidth}x{source.PixelHeight})");
        }
        catch (Exception ex)
        {
            Logger.Warn("应用图标加载失败，回退为字母徽标: " + ex.Message);
        }
    }

    /// <summary>把控制器状态同步到界面。控制器会在状态变化时调用。</summary>
    public void RefreshState()
    {
        _syncing = true;

        try
        {
            AppMode mode = _controller.Mode;
            int regionCount = _controller.Settings.Regions.Count;
            bool paused = _controller.Settings.PauseDisplay;

            (StatusText.Text, StatusDot.Fill) = mode switch
            {
                AppMode.Displaying when !paused => ("展示中", Brush("SuccessBrush")),
                AppMode.Displaying => ("已暂停", Brush("TextMutedBrush")),
                AppMode.Selecting => ("框选中", Brush("AccentBrush")),
                _ => ("待机", Brush("TextMutedBrush")),
            };

            RegionText.Text = regionCount == 0
                ? "还没有选中任何区域。点击「开始框选区域」，在屏幕上拖出一个或多个矩形。"
                : $"已选中 {regionCount} 个区域，分布在 {CountMonitors()} 个显示器上。";

            HotkeyDefinition hotkey = _controller.Settings.ToggleHotkey;
            HotkeyText.Text = hotkey.IsValid
                ? $"按 {hotkey} 可随时切换展示态。"
                : "未设置全局热键。展示态下点击会被穿透，请用托盘菜单切换。";

            DisplayButton.Content = mode == AppMode.Displaying ? "退出展示态" : "进入展示态";
            DisplayButton.IsEnabled = regionCount > 0 || mode == AppMode.Displaying;
            SelectButton.IsEnabled = mode != AppMode.Selecting;

            AutoStartToggle.IsChecked = _controller.IsAutoStartEnabled();
        }
        finally
        {
            _syncing = false;
        }
    }

    private Brush Brush(string key) =>
        TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Gray;

    private int CountMonitors()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StreakRegion region in _controller.Settings.Regions)
        {
            ids.Add(region.MonitorId);
        }

        return Math.Max(1, ids.Count);
    }

    // ================================================================ 事件

    /// <summary>
    /// 整个窗口任意空白处都能拖动。
    /// 按钮、开关、滑杆这类控件会自己把 MouseLeftButtonDown 标记为已处理，
    /// 所以挂在根 Border 上不会影响它们。
    /// </summary>
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
            // 极少数情况下鼠标已经抬起，忽略
        }
    }

    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        _controller.BeginSelection();
        RefreshState();
    }

    private void OnDisplayClick(object sender, RoutedEventArgs e)
    {
        if (_controller.Mode == AppMode.Displaying)
        {
            _controller.ExitDisplayMode();
        }
        else
        {
            _controller.EnterDisplayMode();
        }

        RefreshState();
    }

    private void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _controller.SetAutoStart(AutoStartToggle.IsChecked == true);
        RefreshState();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _controller.ShowSettings();

    private void OnCloseClick(object sender, RoutedEventArgs e) => _controller.HideMainWindow();

    private void OnExitClick(object sender, RoutedEventArgs e) => _controller.Shutdown();

    /// <summary>
    /// 打开作者主页。UseShellExecute = true 才会交给系统默认浏览器处理，
    /// 这是 .NET Core 之后打开 URL 的正确姿势（否则会被当成可执行文件去跑）。
    /// </summary>
    private void OnAuthorClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Logger.Info("打开作者主页: " + AuthorUrl);
            Process.Start(new ProcessStartInfo(AuthorUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("打开作者主页失败: " + AuthorUrl, ex);
        }
    }
}
