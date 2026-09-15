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
using Microsoft.Win32;
using ScreenStreaks.Infrastructure;

namespace ScreenStreaks.Services;

/// <summary>
/// 开机自启。写 HKCU\...\Run，不需要管理员权限，
/// 也因此只对当前用户生效 —— 这正是我们想要的。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScreenStreaks";

    /// <summary>自启时携带的参数：不弹主面板，直接进入缺陷展示态</summary>
    public const string SilentArgument = "--silent";

    public static string BuildCommand(string exePath) => $"\"{exePath}\" {SilentArgument}";

    /// <summary>读取注册表中当前登记的命令行，未登记返回 null</summary>
    public static string? GetRegisteredCommand()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex)
        {
            Logger.Warn("读取开机自启注册表失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>注册表中的自启项是否指向「当前这个 exe」</summary>
    public static bool IsEnabledForCurrentExecutable()
    {
        string? command = GetRegisteredCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        string exePath = AppPaths.ExecutablePath;
        return command.Contains(exePath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Enable(string? exePath = null)
    {
        string target = string.IsNullOrWhiteSpace(exePath) ? AppPaths.ExecutablePath : exePath;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                     ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                Logger.Warn("无法打开 Run 注册表项，开机自启设置失败");
                return false;
            }

            key.SetValue(ValueName, BuildCommand(target), RegistryValueKind.String);
            Logger.Info($"已开启开机自启: {BuildCommand(target)}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("设置开机自启失败", ex);
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return true;
            }

            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Logger.Info("已关闭开机自启");
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("关闭开机自启失败", ex);
            return false;
        }
    }
}
