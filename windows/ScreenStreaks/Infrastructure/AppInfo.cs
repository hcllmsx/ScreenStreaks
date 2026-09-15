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
using System.Reflection;

namespace ScreenStreaks.Infrastructure;

/// <summary>
/// 程序元信息。版本号来自 csproj，而 csproj 又是直接读仓库根目录的 VERSION 文件，
/// 所以这里的版本与实际构建版本永远一致。
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// 中文显示名。所有面向用户的界面 —— 窗口标题、托盘提示、消息框 —— 统一用它。
    /// </summary>
    public const string DisplayName = "屏幕亮线";

    /// <summary>
    /// 英文名。只作为次要标注出现，**不是标识符**。
    ///
    /// exe 文件名、注册表自启项、%APPDATA% 数据目录仍一律使用 ScreenStreaks：
    /// 那些地方一旦改动，会导致已有配置丢失、自启项变孤儿。不要为了「统一」去动。
    /// </summary>
    public const string EnglishName = "ScreenStreaks";

    /// <summary>副标题，与英文名一起显示在标题下方</summary>
    public const string Tagline = "屏幕纵向线条缺陷模拟";

    public static string Version { get; } = ResolveVersion();

    public static string VersionWithPrefix => "v" + Version;

    private static string ResolveVersion()
    {
        try
        {
            Assembly assembly = typeof(AppInfo).Assembly;

            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // 去掉源码版本元数据后缀（+abcdef）
                int plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            Version? version = assembly.GetName().Version;
            return version is null ? "0.0.0" : version.ToString(3);
        }
        catch
        {
            return "0.0.0";
        }
    }
}
