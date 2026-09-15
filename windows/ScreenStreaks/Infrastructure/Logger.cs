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
using System.Text;

namespace ScreenStreaks.Infrastructure;

/// <summary>
/// 极简文件日志。用于排障，不做任何 UI 呈现。
/// 单文件超过 <see cref="MaxBytes"/> 时轮转为 app.old.log。
/// </summary>
public static class Logger
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static bool _failed;

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        if (_failed)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                RotateIfNeeded();

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(AppPaths.LogFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响主流程
            _failed = true;
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(AppPaths.LogFile);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            string backup = Path.Combine(AppPaths.LogDirectory, "app.old.log");
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(AppPaths.LogFile, backup);
        }
        catch
        {
            // 轮转失败时忽略，继续追加
        }
    }
}
