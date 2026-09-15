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
using System.Drawing;
using System.IO;
using System.Windows;
using ScreenStreaks.Infrastructure;
using WinForms = System.Windows.Forms;

namespace ScreenStreaks.Services;

/// <summary>
/// 托盘图标与右键菜单。
/// 这里用 WinForms 的 NotifyIcon 而非自己 P/Invoke Shell_NotifyIcon，
/// 原因是它已经处理好了任务栏重建、DPI、以及菜单定位这些琐碎细节。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ToolStripMenuItem? _modeItem;
    private WinForms.ToolStripMenuItem? _pauseItem;
    private WinForms.ToolStripMenuItem? _autoStartItem;
    private Icon? _icon;
    private MemoryStream? _iconStream;
    private bool _ownsIcon;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? SelectRequested;
    public event Action? ToggleDisplayRequested;
    public event Action? TogglePauseRequested;
    public event Action? SettingsRequested;
    public event Action? AutoStartToggled;
    public event Action? ClearRegionsRequested;
    public event Action? ExitRequested;

    /// <summary>程序内部同步开关状态时置位，避免把同步动作当成用户点击</summary>
    private bool _suppressAutoStartEvent;

    public bool AutoStartChecked => _autoStartItem?.Checked ?? false;

    public void Initialize()
    {
        _icon = LoadIcon();

        var menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
        };

        _modeItem = new WinForms.ToolStripMenuItem("进入展示态");
        _modeItem.Click += (_, _) => ToggleDisplayRequested?.Invoke();

        var selectItem = new WinForms.ToolStripMenuItem("开始框选区域");
        selectItem.Click += (_, _) => SelectRequested?.Invoke();

        _pauseItem = new WinForms.ToolStripMenuItem("暂停显示") { CheckOnClick = false };
        _pauseItem.Click += (_, _) => TogglePauseRequested?.Invoke();

        var settingsItem = new WinForms.ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        _autoStartItem = new WinForms.ToolStripMenuItem("开机自启") { CheckOnClick = false };
        _autoStartItem.Click += (_, _) =>
        {
            if (_suppressAutoStartEvent)
            {
                return;
            }

            _autoStartItem.Checked = !_autoStartItem.Checked;
            AutoStartToggled?.Invoke();
        };

        var clearItem = new WinForms.ToolStripMenuItem("清空全部区域");
        clearItem.Click += (_, _) => ClearRegionsRequested?.Invoke();

        var openItem = new WinForms.ToolStripMenuItem("打开主面板");
        openItem.Click += (_, _) => OpenRequested?.Invoke();

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(openItem);
        menu.Items.Add(_modeItem);
        menu.Items.Add(selectItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(clearItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = $"{AppInfo.DisplayName} {AppInfo.VersionWithPrefix}",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                OpenRequested?.Invoke();
            }
        };
    }

    /// <summary>把当前状态同步到托盘菜单与气泡提示</summary>
    public void UpdateState(AppMode mode, bool paused, bool autoStart, int regionCount, string hotkeyText)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        if (_modeItem is not null)
        {
            _modeItem.Text = mode == AppMode.Displaying ? "退出展示态" : "进入展示态";
        }

        if (_pauseItem is not null)
        {
            _pauseItem.Checked = paused;
        }

        if (_autoStartItem is not null)
        {
            _suppressAutoStartEvent = true;
            _autoStartItem.Checked = autoStart;
            _suppressAutoStartEvent = false;
        }

        string state = mode switch
        {
            AppMode.Displaying => paused ? "已暂停" : "展示中",
            AppMode.Selecting => "框选中",
            _ => "待机",
        };

        string text = $"{AppInfo.DisplayName} {AppInfo.VersionWithPrefix} · {state} · {regionCount} 个区域 · {hotkeyText}";
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowBalloon(string text, bool warning = false)
    {
        try
        {
            _notifyIcon?.ShowBalloonTip(
                2200,
                AppInfo.DisplayName,
                text,
                warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Logger.Warn("托盘气泡提示失败: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 图标加载

    private Icon? LoadIcon()
    {
        // 1) 首选：直接取当前 exe 的关联图标 —— 构建时 ApplicationIcon 已经嵌进去了
        try
        {
            Icon? extracted = Icon.ExtractAssociatedIcon(AppPaths.ExecutablePath);
            if (extracted is not null)
            {
                _ownsIcon = true;
                return extracted;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("从可执行文件提取图标失败: " + ex.Message);
        }

        // 2) 回退：从程序集资源里读 app.ico
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            System.Windows.Resources.StreamResourceInfo? info = Application.GetResourceStream(uri);
            if (info?.Stream is not null)
            {
                _iconStream = new MemoryStream();
                info.Stream.CopyTo(_iconStream);
                _iconStream.Position = 0;
                _ownsIcon = true;
                return new Icon(_iconStream);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("读取内嵌图标资源失败: " + ex.Message);
        }

        // 3) 兜底：系统默认图标
        try
        {
            return SystemIcons.Application;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _iconStream?.Dispose();
        _iconStream = null;

        // SystemIcons.Application 是共享对象，绝不能释放
        if (_ownsIcon)
        {
            _icon?.Dispose();
        }

        _icon = null;
    }
}
