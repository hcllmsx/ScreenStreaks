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
using System.Linq;
using ScreenStreaks.Infrastructure;
using ScreenStreaks.Models;

namespace ScreenStreaks.Services;

/// <summary>单个显示器的物理像素信息</summary>
public sealed class MonitorInfo
{
    /// <summary>形如 \\.\DISPLAY1</summary>
    public string DeviceId { get; init; } = string.Empty;

    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsPrimary { get; init; }

    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public bool Contains(int physicalX, int physicalY) =>
        physicalX >= Left && physicalX < Right && physicalY >= Top && physicalY < Bottom;

    public override string ToString() =>
        $"{DeviceId} ({Left},{Top} {Width}x{Height}){(IsPrimary ? " [主]" : string.Empty)}";
}

/// <summary>
/// 显示器枚举。因为进程声明了 PerMonitorV2 DPI 感知，
/// WinForms 的 Screen.Bounds 返回的就是真实物理像素。
/// </summary>
public static class MonitorService
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();

        try
        {
            foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
            {
                list.Add(new MonitorInfo
                {
                    DeviceId = screen.DeviceName,
                    Left = screen.Bounds.Left,
                    Top = screen.Bounds.Top,
                    Width = screen.Bounds.Width,
                    Height = screen.Bounds.Height,
                    IsPrimary = screen.Primary,
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("枚举显示器失败", ex);
        }

        if (list.Count == 0)
        {
            // 兜底：至少有一个显示器，否则整个 UI 无从摆放
            list.Add(new MonitorInfo
            {
                DeviceId = @"\\.\DISPLAY1",
                Width = 1920,
                Height = 1080,
                IsPrimary = true,
            });
        }

        return list;
    }

    public static MonitorInfo GetPrimary()
    {
        IReadOnlyList<MonitorInfo> monitors = GetMonitors();
        return monitors.FirstOrDefault(static m => m.IsPrimary) ?? monitors[0];
    }

    public static MonitorInfo? FindById(string? monitorId)
    {
        if (string.IsNullOrEmpty(monitorId))
        {
            return null;
        }

        return GetMonitors().FirstOrDefault(
            m => string.Equals(m.DeviceId, monitorId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把落在虚拟桌面之外的历史区域重新贴回显示器内部</summary>
    public static void ClampRegion(StreakRegion region, MonitorInfo monitor)
    {
        region.X = Math.Clamp(region.X, 0, Math.Max(0, monitor.Width - 4));
        region.Y = Math.Clamp(region.Y, 0, Math.Max(0, monitor.Height - 4));
        region.Width = Math.Clamp(region.Width, 4, monitor.Width - region.X);
        region.Height = Math.Clamp(region.Height, 4, monitor.Height - region.Y);
    }
}
