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

namespace ScreenStreaks.Models;

/// <summary>
/// 缺陷类型。只保留真实面板上会出现的四类。
/// </summary>
public enum StreakKind
{
    /// <summary>常亮亮线：该列被拉起后无法关闭</summary>
    Bright,

    /// <summary>闪烁线：驱动排线接触不良，在正常与熄灭之间跳变</summary>
    Flicker,

    /// <summary>暗线 / 死列：该列不出光</summary>
    Dark,

    /// <summary>子像素卡死：单个子像素被拉到某个极值，表现为纯红/纯绿/纯蓝等</summary>
    ColorStuck,
}

/// <summary>
/// 一条纵向缺陷列的完整描述。
///
/// 几何量均为「相对区域左上角的物理像素」。
/// 线宽是整数，因为面板缺陷必然是整数个子像素列 —— 不存在零点几像素宽的坏线。
/// </summary>
public sealed class StreakDefect
{
    /// <summary>线芯左边缘相对区域左边缘的偏移（物理像素）</summary>
    public double OffsetX { get; set; }

    /// <summary>线宽（物理像素，整数，1~3 列）</summary>
    public int Width { get; set; } = 1;

    public StreakKind Kind { get; set; }

    /// <summary>发光颜色。不透明 —— 坏列是自己发光的，不是盖在画面上的半透明色块</summary>
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }

    /// <summary>闪烁循环频率（Hz）</summary>
    public double BlinkHz { get; set; } = 1.0;

    /// <summary>闪烁起始相位（秒），用于错开各条线的节奏</summary>
    public double PhaseSeconds { get; set; }

    /// <summary>闪烁亮度序列（离散关键帧，0 = 完全熄灭），预先生成以保证确定性</summary>
    public double[] BlinkPattern { get; set; } = new[] { 1.0 };
}
