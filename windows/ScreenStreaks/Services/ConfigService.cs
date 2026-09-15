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
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenStreaks.Infrastructure;
using ScreenStreaks.Models;

namespace ScreenStreaks.Services;

/// <summary>
/// 配置读写。落盘位置 %APPDATA%\ScreenStreaks\config.json。
/// 写入采用「临时文件 + 覆盖移动」，避免断电/崩溃写出半个 JSON。
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string FilePath => AppPaths.ConfigFile;

    public AppSettings Settings { get; private set; } = new();

    /// <summary>配置文件存在但解析失败</summary>
    public bool LoadFailed { get; private set; }

    public AppSettings Load()
    {
        AppPaths.EnsureDataDirectory();

        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                string json = File.ReadAllText(AppPaths.ConfigFile);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (loaded is not null)
                    {
                        Settings = loaded;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoadFailed = true;
            Logger.Error("读取配置失败，已回退为默认配置", ex);
            BackupCorruptFile();
        }

        Settings.Normalize();
        return Settings;
    }

    public void Save()
    {
        AppPaths.EnsureDataDirectory();

        try
        {
            string json = JsonSerializer.Serialize(Settings, JsonOptions);
            string temp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.ConfigFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Error("写入配置失败", ex);
        }
    }

    public void ResetToDefault()
    {
        Settings = new AppSettings();
        Settings.Normalize();
        Save();
    }

    private void BackupCorruptFile()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFile))
            {
                return;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backup = Path.Combine(AppPaths.DataDirectory, $"config.corrupt-{stamp}.json");
            File.Move(AppPaths.ConfigFile, backup, overwrite: true);
            Logger.Warn($"损坏的配置已备份到 {backup}");
        }
        catch (Exception ex)
        {
            Logger.Error("备份损坏配置失败", ex);
        }
    }
}
