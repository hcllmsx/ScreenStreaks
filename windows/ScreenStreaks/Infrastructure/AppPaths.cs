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
using System.IO;

namespace ScreenStreaks.Infrastructure;

/// <summary>
/// 应用使用到的所有路径。统一在这里定义，避免各处拼接字符串。
/// </summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\ScreenStreaks</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenStreaks");

    /// <summary>%APPDATA%\ScreenStreaks\config.json</summary>
    public static string ConfigFile { get; } = Path.Combine(DataDirectory, "config.json");

    /// <summary>%APPDATA%\ScreenStreaks\logs</summary>
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>%APPDATA%\ScreenStreaks\logs\app.log</summary>
    public static string LogFile { get; } = Path.Combine(LogDirectory, "app.log");

    /// <summary>当前进程的可执行文件完整路径</summary>
    public static string ExecutablePath
    {
        get
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            return Path.Combine(AppContext.BaseDirectory, "ScreenStreaks.exe");
        }
    }

    /// <summary>确保数据目录存在</summary>
    public static void EnsureDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
        }
        catch
        {
            // 目录创建失败不应阻断启动，后续写入会各自处理异常
        }
    }
}
