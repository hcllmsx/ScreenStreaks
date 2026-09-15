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
using ScreenStreaks.Models;

namespace ScreenStreaks.Rendering;

/// <summary>
/// 依据区域和配置推导出一组纵向缺陷列。
///
/// 建模原则：
///  - 线宽是整数物理像素。面板缺陷必然坏掉整数个子像素列，不存在 0.6 像素宽的坏线。
///  - 颜色即发光色，不透明度恒为 255。坏列是自己发光的，不是盖在画面上的半透明色块。
///  - 随机性完全由 (全局种子, 区域种子, 区域几何) 决定，同一区域每次渲染结果一致，
///    否则每次切换展示态线条都会「跳位置」，一眼就穿帮。
/// </summary>
public static class StreakGenerator
{
    /// <summary>子像素卡死：单列被拉到极值，所以宽度必然是 1 个物理像素</summary>
    private static readonly (byte R, byte G, byte B)[] ColorStuckPalette =
    {
        (255, 0, 0),
        (0, 255, 0),
        (0, 0, 255),
        (255, 255, 0),
        (0, 255, 255),
        (255, 0, 255),
    };

    /// <summary>
    /// 常亮亮线的色温。全拉死的列三个子像素都满开，所以就是纯白；
    /// 这里留一丁点偏色，避免所有线看起来完全一样。
    /// </summary>
    private static readonly (byte R, byte G, byte B)[] BrightPalette =
    {
        (255, 255, 255),
        (255, 255, 253),
        (253, 255, 255),
        (255, 253, 255),
    };

    public static List<StreakDefect> Generate(StreakRegion region, StreakOptions options)
    {
        var result = new List<StreakDefect>();

        if (region is null || !region.IsUsable || options is null)
        {
            return result;
        }

        options.Normalize();
        if (!options.HasAnyKind)
        {
            return result;
        }

        var rng = new Random(BuildSeed(region, options));
        int count = ResolveCount(region, options, rng);
        if (count <= 0)
        {
            return result;
        }

        // 把区域横向切成 count 个槽位，每条线在自己的槽内抖动，
        // 这样既随机又不会全部挤在一起。
        double slotWidth = region.Width / (double)count;

        for (int i = 0; i < count; i++)
        {
            double laneLeft = i * slotWidth;
            double center = laneLeft + (slotWidth * (0.14 + (rng.NextDouble() * 0.72)));

            StreakKind kind = PickKind(rng, options);
            int width = ResolveWidth(kind, rng);

            // 坏列必须在物理像素网格上，所以左边缘取整
            double left = Math.Round(center - (width / 2.0));
            left = Math.Clamp(left, 0.0, Math.Max(0.0, region.Width - width));

            var defect = new StreakDefect
            {
                Kind = kind,
                OffsetX = left,
                Width = width,
            };

            Configure(defect, options, rng);
            result.Add(defect);
        }

        return result;
    }

    /// <summary>
    /// 按密度估算区域会生成多少条线。不含随机抖动，仅供界面提示用。
    /// </summary>
    public static int EstimateLineCount(StreakRegion region, StreakOptions options)
    {
        if (region is null || !region.IsUsable || options is null)
        {
            return 0;
        }

        double density = Math.Clamp(options.Density, 0.5, 60.0);
        double expected = region.Width / 1000.0 * density;
        int max = Math.Clamp(options.MaxPerRegion, 1, 400);

        return Math.Clamp((int)Math.Round(expected, MidpointRounding.AwayFromZero), 1, max);
    }

    // ---------------------------------------------------------------- 内部

    private static int BuildSeed(StreakRegion region, StreakOptions options)
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + options.Seed;
            hash = (hash * 31) + region.Seed;
            hash = (hash * 31) + region.X;
            hash = (hash * 31) + region.Y;
            hash = (hash * 31) + region.Width;
            hash = (hash * 31) + region.Height;
            return hash;
        }
    }

    private static int ResolveCount(StreakRegion region, StreakOptions options, Random rng)
    {
        double expected = region.Width / 1000.0 * options.Density;
        int count = (int)Math.Round(expected, MidpointRounding.AwayFromZero);

        // ±1 的抖动，避免每个区域都严丝合缝地按密度出线
        count += rng.Next(0, 2);
        if (count > 1 && rng.NextDouble() < 0.3)
        {
            count--;
        }

        // 下限恒为 1：任何可用区域都必须至少出一条线。
        return Math.Clamp(count, 1, options.MaxPerRegion);
    }

    /// <summary>
    /// 宽度分布刻意偏向 1 列。真实面板上绝大多数故障就是坏掉一列，
    /// 一次坏掉三四列的情况要少得多。
    /// </summary>
    private static int ResolveWidth(StreakKind kind, Random rng)
    {
        // 子像素卡死必然是单列
        if (kind == StreakKind.ColorStuck)
        {
            return 1;
        }

        double roll = rng.NextDouble();
        if (roll < 0.72)
        {
            return 1;
        }

        return roll < 0.93 ? 2 : 3;
    }

    private static StreakKind PickKind(Random rng, StreakOptions options)
    {
        Span<StreakKind> buffer = stackalloc StreakKind[19]; // 权重总和上限 7+2+4+4
        int n = 0;

        // 权重刻意偏向稳定线条：真实面板上最常见的是一条恒亮的线或暗线，
        // 闪烁属于劣化后期的少数情况，比例调高会一眼假。
        if (options.EnableBright)
        {
            for (int i = 0; i < 7; i++)
            {
                buffer[n++] = StreakKind.Bright;
            }
        }

        if (options.EnableFlicker)
        {
            buffer[n++] = StreakKind.Flicker;
            buffer[n++] = StreakKind.Flicker;
        }

        if (options.EnableDark)
        {
            for (int i = 0; i < 4; i++)
            {
                buffer[n++] = StreakKind.Dark;
            }
        }

        if (options.EnableColorStuck)
        {
            for (int i = 0; i < 4; i++)
            {
                buffer[n++] = StreakKind.ColorStuck;
            }
        }

        if (n == 0)
        {
            return StreakKind.Bright;
        }

        return buffer[rng.Next(n)];
    }

    private static void Configure(StreakDefect defect, StreakOptions options, Random rng)
    {
        double intensity = options.Intensity;

        switch (defect.Kind)
        {
            case StreakKind.Bright:
                {
                    (byte r, byte g, byte b) = BrightPalette[rng.Next(BrightPalette.Length)];
                    defect.R = Scale(r, intensity);
                    defect.G = Scale(g, intensity);
                    defect.B = Scale(b, intensity);
                    break;
                }

            case StreakKind.Dark:
                {
                    // 该列不出光。黑就是黑，亮度系数对它没有意义。
                    defect.R = 0;
                    defect.G = 0;
                    defect.B = 0;
                    break;
                }

            case StreakKind.ColorStuck:
                {
                    (byte r, byte g, byte b) = ColorStuckPalette[rng.Next(ColorStuckPalette.Length)];
                    defect.R = Scale(r, intensity);
                    defect.G = Scale(g, intensity);
                    defect.B = Scale(b, intensity);
                    break;
                }

            case StreakKind.Flicker:
                {
                    (byte r, byte g, byte b) = BrightPalette[rng.Next(BrightPalette.Length)];
                    defect.R = Scale(r, intensity);
                    defect.G = Scale(g, intensity);
                    defect.B = Scale(b, intensity);

                    // 接触不良的列是「一段时间正常、偶尔整段熄灭」，
                    // 频率低（0.12~2.4 Hz）。像 PWM 那样每秒闪好几次反而不像故障。
                    defect.BlinkHz = Lerp(0.12, 2.4, Math.Pow(rng.NextDouble(), 1.5));
                    defect.PhaseSeconds = rng.NextDouble() / Math.Max(0.2, defect.BlinkHz);
                    defect.BlinkPattern = CreateBlinkPattern(rng, 1.0 - options.BlinkDepth);
                    break;
                }
        }
    }

    /// <summary>
    /// 预生成闪烁亮度序列，0 表示这一列此刻完全不发光。
    /// 分布刻意做成「大部分时间满亮 + 偶发整段跌落」，
    /// 而不是对称的明暗脉动。
    /// </summary>
    private static double[] CreateBlinkPattern(Random rng, double lowValue)
    {
        const int steps = 14;
        double low = Math.Clamp(lowValue, 0.0, 1.0);
        var pattern = new double[steps];

        for (int i = 0; i < steps; i++)
        {
            double roll = rng.NextDouble();
            pattern[i] = roll switch
            {
                < 0.74 => 1.0,
                < 0.92 => Lerp(low, 1.0, rng.NextDouble()),
                _ => low,
            };
        }

        return pattern;
    }

    private static byte Scale(byte channel, double intensity) =>
        (byte)Math.Round(Math.Clamp(channel * intensity, 0.0, 255.0));

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
