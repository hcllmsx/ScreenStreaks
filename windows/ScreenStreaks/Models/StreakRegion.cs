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

using System.Text.Json.Serialization;

namespace ScreenStreaks.Models;

/// <summary>
/// 一个被选中的屏幕区域。
/// 坐标使用「物理像素」且相对于所属显示器的左上角，
/// 这样显示器重新排列、主屏切换后区域依然贴合原来的屏幕。
/// </summary>
public sealed class StreakRegion
{
    /// <summary>所属显示器标识，形如 \\.\DISPLAY1</summary>
    public string MonitorId { get; set; } = string.Empty;

    /// <summary>相对显示器左边缘的物理像素</summary>
    public int X { get; set; }

    /// <summary>相对显示器上边缘的物理像素</summary>
    public int Y { get; set; }

    /// <summary>宽度（物理像素）</summary>
    public int Width { get; set; }

    /// <summary>高度（物理像素）</summary>
    public int Height { get; set; }

    /// <summary>该区域的随机种子，保证同一区域每次生成的缺陷形态一致</summary>
    public int Seed { get; set; }

    [JsonIgnore]
    public bool IsUsable => Width >= 2 && Height >= 2 && !string.IsNullOrEmpty(MonitorId);

    public StreakRegion Clone() => new()
    {
        MonitorId = MonitorId,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        Seed = Seed,
    };

    public override string ToString() => $"{MonitorId} [{X},{Y} {Width}x{Height}]";
}
