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
using System.Text.Json.Serialization;

namespace ScreenStreaks.Models;

/// <summary>
/// 缺陷生成参数。
///
/// 建模原则：只保留「真实面板故障确实会有的差异」，凡是纯装饰性的效果一律不做。
/// 因此这里没有线宽范围、没有辉光开关、没有是否贯穿全屏、没有漂移 ——
/// 那些要么物理上不可能，要么是屏幕截图里才有的观感，放在这里只会让人一眼看出是假的。
///
/// 所有取值都会在 <see cref="Normalize"/> 中被夹到安全区间，因为用户可以手改 config.json。
/// </summary>
public sealed class StreakOptions
{
    /// <summary>全局随机种子</summary>
    public int Seed { get; set; } = 20260915;

    /// <summary>密度：每一千物理像素宽度内期望出现的缺陷列数</summary>
    public double Density { get; set; } = 25.0;

    /// <summary>
    /// 发光强度 0.3 ~ 1.0。
    /// 1.0 = 该列被完全拉死，以面板最大亮度常亮，这是最典型的故障形态；
    /// 调低则模拟「半失效」的列（仍然不透明，只是发光更弱）。
    /// </summary>
    public double Intensity { get; set; } = 1.0;

    /// <summary>
    /// 闪烁深度 0 ~ 0.95。
    /// 接触不良的列会在「正常发光」与「完全不发光」之间跳变，而不是变透明，
    /// 所以这个值控制的是熄灭时的黑度，不是透明度。
    /// </summary>
    public double BlinkDepth { get; set; } = 0.75;

    /// <summary>单个区域最多生成多少条缺陷，纯性能护栏</summary>
    public int MaxPerRegion { get; set; } = 120;

    public bool EnableBright { get; set; } = true;
    public bool EnableFlicker { get; set; }
    public bool EnableDark { get; set; } = true;
    public bool EnableColorStuck { get; set; } = true;

    /// <summary>至少启用一种类型，否则屏幕上什么都不会出现</summary>
    [JsonIgnore]
    public bool HasAnyKind => EnableBright || EnableFlicker || EnableDark || EnableColorStuck;

    public void Normalize()
    {
        Density = Clamp(Density, 0.5, 60.0);
        Intensity = Clamp(Intensity, 0.3, 1.0);
        BlinkDepth = Clamp(BlinkDepth, 0.0, 0.95);
        MaxPerRegion = Math.Clamp(MaxPerRegion, 1, 400);

        if (!HasAnyKind)
        {
            EnableBright = true;
        }
    }

    public StreakOptions Clone() => new()
    {
        Seed = Seed,
        Density = Density,
        Intensity = Intensity,
        BlinkDepth = BlinkDepth,
        MaxPerRegion = MaxPerRegion,
        EnableBright = EnableBright,
        EnableFlicker = EnableFlicker,
        EnableDark = EnableDark,
        EnableColorStuck = EnableColorStuck,
    };

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return min;
        }

        return Math.Min(max, Math.Max(min, value));
    }
}

/// <summary>
/// 落盘到 %APPDATA%\ScreenStreaks\config.json 的完整配置。
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>上次运行时的程序版本，仅作记录</summary>
    public string LastVersion { get; set; } = string.Empty;

    /// <summary>启动时若已存在区域，是否直接进入缺陷展示态</summary>
    public bool StartInDisplayMode { get; set; } = true;

    /// <summary>是否暂停展示（暂停时保留区域但不绘制）</summary>
    public bool PauseDisplay { get; set; }

    /// <summary>切换展示态的全局热键。默认未设置，即不启用。</summary>
    public HotkeyDefinition ToggleHotkey { get; set; } = HotkeyDefinition.CreateEmpty();

    /// <summary>缺陷生成参数</summary>
    public StreakOptions Streaks { get; set; } = new();

    /// <summary>已选中的屏幕区域</summary>
    public List<StreakRegion> Regions { get; set; } = new();

    public void Normalize()
    {
        Streaks ??= new StreakOptions();
        Streaks.Normalize();

        ToggleHotkey ??= HotkeyDefinition.CreateEmpty();

        Regions ??= new List<StreakRegion>();
        Regions.RemoveAll(static r => r is null || !r.IsUsable);

        if (SchemaVersion <= 0)
        {
            SchemaVersion = 1;
        }
    }

    public AppSettings Clone()
    {
        var copy = new AppSettings
        {
            SchemaVersion = SchemaVersion,
            LastVersion = LastVersion,
            StartInDisplayMode = StartInDisplayMode,
            PauseDisplay = PauseDisplay,
            ToggleHotkey = ToggleHotkey.Clone(),
            Streaks = Streaks.Clone(),
            Regions = new List<StreakRegion>(Regions.Count),
        };

        foreach (StreakRegion region in Regions)
        {
            copy.Regions.Add(region.Clone());
        }

        return copy;
    }
}
