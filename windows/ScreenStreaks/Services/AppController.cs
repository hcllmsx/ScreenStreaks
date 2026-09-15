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
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using ScreenStreaks.Infrastructure;
using ScreenStreaks.Models;
using ScreenStreaks.Rendering;
using ScreenStreaks.Views;

namespace ScreenStreaks.Services;

/// <summary>程序的三态</summary>
public enum AppMode
{
    /// <summary>待机：只有托盘与主面板，屏幕上没有覆盖层</summary>
    Idle,

    /// <summary>框选中：各显示器铺满遮罩，等待用户划区域</summary>
    Selecting,

    /// <summary>展示中：覆盖层处于鼠标穿透状态，正在模拟线条缺陷</summary>
    Displaying,
}

/// <summary>
/// 应用主控。所有状态迁移都收敛在这里，
/// 窗口与服务只负责各自的具体行为，不互相直接调用。
///
///   Idle ──开始框选──▶ Selecting ──Enter──▶ Displaying
///     ▲                    │                   │
///     └────── Esc ─────────┘                   │
///     └──────────── 热键 / 托盘 ◀─────────────┘
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly ConfigService _config;
    private readonly HotkeyService _hotkey;
    private readonly TrayIconService _tray;
    private readonly SingleInstanceService _singleInstance;

    private readonly List<StreakOverlayWindow> _displayWindows = new();
    private readonly List<string> _displayMonitorKeys = new();
    private readonly List<SelectionOverlayWindow> _selectionWindows = new();

    /// <summary>启动参数：直接进入框选模式</summary>
    public const string SelectArgument = "--select";

    /// <summary>启动参数：直接打开设置面板</summary>
    public const string SettingsArgument = "--settings";

    private SelectionSession? _session;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private bool _displaySettingsHooked;
    private bool _disposed;

    public AppController(
        ConfigService config,
        HotkeyService hotkey,
        TrayIconService tray,
        SingleInstanceService singleInstance)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _singleInstance = singleInstance ?? throw new ArgumentNullException(nameof(singleInstance));
    }

    public AppMode Mode { get; private set; } = AppMode.Idle;

    public AppSettings Settings => _config.Settings;

    public ConfigService Config => _config;

    public HotkeyService Hotkey => _hotkey;

    public bool IsAutoStartEnabled()
    {
        try
        {
            return AutoStartService.IsEnabledForCurrentExecutable();
        }
        catch
        {
            return false;
        }
    }

    // ================================================================ 启动

    public void Initialize()
    {
        AppPaths.EnsureDataDirectory();

        _config.Load();
        _config.Settings.LastVersion = AppInfo.Version;
        _config.Save();

        // ---- 托盘
        _tray.OpenRequested += ShowMainWindow;
        _tray.SelectRequested += BeginSelection;
        _tray.ToggleDisplayRequested += ToggleDisplay;
        _tray.TogglePauseRequested += TogglePause;
        _tray.SettingsRequested += ShowSettings;
        _tray.AutoStartToggled += OnTrayAutoStartToggled;
        _tray.ClearRegionsRequested += ClearAllRegions;
        _tray.ExitRequested += Shutdown;
        _tray.Initialize();

        // ---- 全局热键（默认未设置，由用户自行决定是否指定）
        _hotkey.Pressed += ToggleDisplay;

        HotkeyDefinition toggleHotkey = _config.Settings.ToggleHotkey;
        if (toggleHotkey.IsValid)
        {
            if (!_hotkey.Register(toggleHotkey))
            {
                Logger.Warn("启动时热键注册失败，用户仍可通过托盘菜单操作");
            }
        }
        else
        {
            Logger.Info("未设置全局热键，展示态请通过托盘菜单切换");
        }

        // ---- 二次启动唤起（信号来自监听线程，需要切回 UI 线程）
        _singleInstance.ActivationRequested += OnActivationRequested;

        // ---- 显示器热插拔：分辨率/排布变化后必须重建覆盖层，否则会错位
        try
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _displaySettingsHooked = true;
        }
        catch (Exception ex)
        {
            Logger.Warn("订阅显示器变更事件失败: " + ex.Message);
        }

        RaiseStateChanged();
    }

    /// <summary>根据启动参数决定首屏行为</summary>
    /// <param name="silent">开机自启：不弹主面板</param>
    /// <param name="selectMode">直接进入框选模式</param>
    /// <param name="settingsMode">直接打开设置面板</param>
    public void HandleStartup(bool silent, bool selectMode = false, bool settingsMode = false)
    {
        if (selectMode)
        {
            BeginSelection();
            return;
        }

        if (settingsMode)
        {
            ShowSettings();
            return;
        }

        bool hasRegions = _config.Settings.Regions.Count > 0;

        if (hasRegions && (silent || _config.Settings.StartInDisplayMode))
        {
            EnterDisplayMode();
            return;
        }

        if (silent)
        {
            // 自启但没有区域，也不该弹窗打扰用户 —— 直接静默待机
            return;
        }

        ShowMainWindow();
    }

    // ================================================================ 窗口

    public void ShowMainWindow()
    {
        if (Mode == AppMode.Selecting)
        {
            return;
        }

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(this);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        _mainWindow.RefreshState();

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Focus();
    }

    public void HideMainWindow()
    {
        _mainWindow?.Hide();
    }

    public void ShowSettings()
    {
        if (Mode == AppMode.Selecting)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ================================================================ 框选

    public void BeginSelection()
    {
        if (Mode == AppMode.Selecting)
        {
            return;
        }

        Logger.Info("进入框选模式");

        Mode = AppMode.Selecting;
        RaiseStateChanged();

        CloseSelectionWindows();
        CloseDisplayWindows();

        _mainWindow?.Hide();
        _settingsWindow?.Hide();

        _session = new SelectionSession(_config.Settings.Regions);

        // 历史区域可能因为改动分辨率而跑到显示器外面，先夹回来
        foreach (StreakRegion region in _session.Regions)
        {
            MonitorInfo? monitor = MonitorService.FindById(region.MonitorId);
            if (monitor is not null)
            {
                MonitorService.ClampRegion(region, monitor);
            }
        }

        IReadOnlyList<MonitorInfo> monitors = MonitorService.GetMonitors();
        MonitorInfo primary = MonitorService.GetPrimary();

        foreach (MonitorInfo monitor in monitors)
        {
            bool isPrimary = string.Equals(monitor.DeviceId, primary.DeviceId, StringComparison.OrdinalIgnoreCase);
            var window = new SelectionOverlayWindow(monitor, _session, _config.Settings.Streaks, isPrimary);
            window.ConfirmRequested += ConfirmSelection;
            window.CancelRequested += CancelSelection;
            window.ClearRequested += ClearSelection;
            _selectionWindows.Add(window);
        }

        foreach (SelectionOverlayWindow window in _selectionWindows)
        {
            window.Show();
        }

        SelectionOverlayWindow? focusTarget =
            _selectionWindows.FirstOrDefault(static w => w.IsPrimaryMonitor)
            ?? _selectionWindows.FirstOrDefault();

        focusTarget?.Activate();
        focusTarget?.Focus();
    }

    public void ConfirmSelection()
    {
        SelectionSession? session = _session;
        if (session is null)
        {
            return;
        }

        _config.Settings.Regions = session.Snapshot();
        _config.Save();

        Logger.Info($"框选确认，共 {_config.Settings.Regions.Count} 个区域");

        CloseSelectionWindows();

        if (_config.Settings.Regions.Count == 0)
        {
            Mode = AppMode.Idle;
            RaiseStateChanged();
            ShowMainWindow();
            return;
        }

        EnterDisplayMode();

        // 立刻回报实际生成了多少条线 —— 否则用户很容易把「这个区域只出了一条暗线」
        // 误判成「这个区域什么都没画」
        if (Mode == AppMode.Displaying)
        {
            _tray.ShowBalloon(
                $"已在 {_config.Settings.Regions.Count} 个区域生成 {EstimateTotalLines()} 条线条");
        }
    }

    /// <summary>按当前密度估算所有区域合计会生成多少条线</summary>
    public int EstimateTotalLines()
    {
        int total = 0;

        foreach (StreakRegion region in _config.Settings.Regions)
        {
            total += StreakGenerator.EstimateLineCount(region, _config.Settings.Streaks);
        }

        return total;
    }

    public void CancelSelection()
    {
        Logger.Info("框选取消，保留原有区域");

        CloseSelectionWindows();

        if (_config.Settings.Regions.Count > 0)
        {
            EnterDisplayMode();
        }
        else
        {
            Mode = AppMode.Idle;
            RaiseStateChanged();
            ShowMainWindow();
        }
    }

    private void ClearSelection() => _session?.Clear();

    public void ClearAllRegions()
    {
        _config.Settings.Regions.Clear();
        _config.Save();

        CloseSelectionWindows();
        CloseDisplayWindows();

        Mode = AppMode.Idle;
        RaiseStateChanged();
        ShowMainWindow();
        _tray.ShowBalloon("已清空全部区域");
    }

    // ================================================================ 展示

    public void EnterDisplayMode()
    {
        if (_config.Settings.Regions.Count == 0)
        {
            Logger.Info("没有区域，无法进入展示态");
            Mode = AppMode.Idle;
            RaiseStateChanged();
            return;
        }

        EnsureDisplayWindows();

        foreach (StreakOverlayWindow window in _displayWindows)
        {
            window.SetContent(CollectRegionsFor(window.Monitor), _config.Settings.Streaks);
            window.SetPaused(_config.Settings.PauseDisplay);
        }

        if (Mode != AppMode.Displaying)
        {
            Logger.Info($"进入展示态，覆盖 {_displayWindows.Count} 个显示器");
        }

        Mode = AppMode.Displaying;
        RaiseStateChanged();
    }

    public void ExitDisplayMode()
    {
        CloseDisplayWindows();

        if (Mode != AppMode.Selecting)
        {
            Mode = AppMode.Idle;
        }

        RaiseStateChanged();
    }

    public void ToggleDisplay()
    {
        if (Mode == AppMode.Selecting)
        {
            return;
        }

        if (Mode == AppMode.Displaying)
        {
            ExitDisplayMode();
            _tray.ShowBalloon("已退出展示态");
            return;
        }

        if (_config.Settings.Regions.Count == 0)
        {
            _tray.ShowBalloon("还没有选中任何区域，先框选一个吧");
            BeginSelection();
            return;
        }

        EnterDisplayMode();

        if (Mode == AppMode.Displaying)
        {
            _tray.ShowBalloon($"已进入展示态 · {_config.Settings.Regions.Count} 个区域");
        }
    }

    public void TogglePause()
    {
        _config.Settings.PauseDisplay = !_config.Settings.PauseDisplay;
        _config.Save();

        foreach (StreakOverlayWindow window in _displayWindows)
        {
            window.SetPaused(_config.Settings.PauseDisplay);
        }

        RaiseStateChanged();
    }

    /// <summary>把当前设置应用到覆盖层（设置面板实时预览用）</summary>
    public void ApplyStreakOptions(bool persist = true)
    {
        _config.Settings.Streaks.Normalize();

        if (persist)
        {
            _config.Save();
        }

        if (Mode != AppMode.Displaying || _displayWindows.Count == 0)
        {
            return;
        }

        foreach (StreakOverlayWindow window in _displayWindows)
        {
            window.SetContent(CollectRegionsFor(window.Monitor), _config.Settings.Streaks);
        }
    }

    public bool ApplyHotkey(HotkeyDefinition definition)
    {
        if (definition is null)
        {
            return false;
        }

        // 清空热键是合法操作（等于「不启用」），不能当成失败
        if (!definition.IsValid)
        {
            _hotkey.Unregister();
            _config.Settings.ToggleHotkey = HotkeyDefinition.CreateEmpty();
            _config.Save();
            Logger.Info("已清除全局热键");
            RaiseStateChanged();
            return true;
        }

        // 先真正注册成功，再落盘。否则会把一个不可用的热键写进配置，
        // 下次启动还得指望用户自己发现「热键没反应」。
        if (!_hotkey.Register(definition))
        {
            // 回滚：原配置本身为空时，Register 会保持「无注册」状态
            _hotkey.Register(_config.Settings.ToggleHotkey);
            return false;
        }

        _config.Settings.ToggleHotkey = definition.Clone();
        _config.Save();
        RaiseStateChanged();
        return true;
    }

    public bool SetAutoStart(bool enabled)
    {
        bool ok = enabled ? AutoStartService.Enable() : AutoStartService.Disable();
        RaiseStateChanged();
        return ok;
    }

    // ================================================================ 内部

    private List<StreakRegion> CollectRegionsFor(MonitorInfo monitor) =>
        _config.Settings.Regions
            .Where(r => string.Equals(r.MonitorId, monitor.DeviceId, StringComparison.OrdinalIgnoreCase))
            .Select(static r => r.Clone())
            .ToList();

    private void EnsureDisplayWindows()
    {
        IReadOnlyList<MonitorInfo> monitors = MonitorService.GetMonitors();

        List<string> keys = monitors
            .Select(static m => $"{m.DeviceId}:{m.Left},{m.Top},{m.Width},{m.Height}")
            .ToList();

        bool unchanged = _displayWindows.Count == monitors.Count
                         && keys.SequenceEqual(_displayMonitorKeys, StringComparer.OrdinalIgnoreCase);

        if (unchanged)
        {
            return;
        }

        CloseDisplayWindows();

        _displayMonitorKeys.Clear();
        _displayMonitorKeys.AddRange(keys);

        foreach (MonitorInfo monitor in monitors)
        {
            var window = new StreakOverlayWindow(monitor);
            _displayWindows.Add(window);
            window.Show();
        }
    }

    private void CloseDisplayWindows()
    {
        foreach (StreakOverlayWindow window in _displayWindows)
        {
            try
            {
                window.Dispose();
                window.Close();
            }
            catch (Exception ex)
            {
                Logger.Error("关闭展示层失败", ex);
            }
        }

        _displayWindows.Clear();
    }

    private void CloseSelectionWindows()
    {
        foreach (SelectionOverlayWindow window in _selectionWindows)
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                Logger.Error("关闭框选层失败", ex);
            }
        }

        _selectionWindows.Clear();
        _session = null;
    }

    private void OnTrayAutoStartToggled()
    {
        bool enabled = _tray.AutoStartChecked;
        bool ok = enabled ? AutoStartService.Enable() : AutoStartService.Disable();

        if (!ok)
        {
            _tray.ShowBalloon("开机自启设置失败，请检查系统权限", warning: true);
        }

        RaiseStateChanged();
    }

    private void OnActivationRequested()
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(new Action(ShowMainWindow));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            Logger.Info("检测到显示器配置变化，重建覆盖层");

            if (Mode == AppMode.Displaying)
            {
                _displayMonitorKeys.Clear();
                EnterDisplayMode();
            }
            else
            {
                CloseDisplayWindows();
                _displayMonitorKeys.Clear();
            }
        }));
    }

    private void RaiseStateChanged()
    {
        HotkeyDefinition hotkey = _config.Settings.ToggleHotkey;

        _mainWindow?.RefreshState();
        _tray.UpdateState(
            Mode,
            _config.Settings.PauseDisplay,
            IsAutoStartEnabled(),
            _config.Settings.Regions.Count,
            hotkey.IsValid ? hotkey.ToString() : "无热键");
    }

    // ================================================================ 退出

    public void Shutdown()
    {
        try
        {
            Logger.Info("退出程序");

            CloseSelectionWindows();
            CloseDisplayWindows();

            _config.Save();

            if (_displaySettingsHooked)
            {
                try
                {
                    SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                }
                catch
                {
                    // 忽略
                }

                _displaySettingsHooked = false;
            }

            _settingsWindow?.Close();
            _settingsWindow = null;
            _mainWindow?.Close();
            _mainWindow = null;
        }
        catch (Exception ex)
        {
            Logger.Error("退出清理时发生异常", ex);
        }
        finally
        {
            Application.Current?.Shutdown();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkey.Dispose();
        _tray.Dispose();
        _singleInstance.Dispose();
    }
}
