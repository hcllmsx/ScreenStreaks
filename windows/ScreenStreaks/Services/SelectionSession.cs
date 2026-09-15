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
using System.Collections.Generic;
using ScreenStreaks.Models;

namespace ScreenStreaks.Services;

/// <summary>
/// 一次框选会话期间的工作副本。
/// 所有编辑器窗口（每显示器一个）共享同一份数据，
/// 只有点击「确认」才会提交回 <see cref="AppSettings"/>，
/// 因此「取消」天然是无损的。
/// </summary>
public sealed class SelectionSession
{
    private readonly List<StreakRegion> _regions = new();

    public SelectionSession(IEnumerable<StreakRegion>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (StreakRegion region in source)
        {
            if (region.IsUsable)
            {
                _regions.Add(region.Clone());
            }
        }
    }

    /// <summary>任一区域发生增删改都会触发，用于各显示器窗口重绘</summary>
    public event Action? Changed;

    public IReadOnlyList<StreakRegion> Regions => _regions;

    public int Count => _regions.Count;

    public IReadOnlyList<StreakRegion> ForMonitor(string monitorId)
    {
        var result = new List<StreakRegion>();
        foreach (StreakRegion region in _regions)
        {
            if (string.Equals(region.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(region);
            }
        }

        return result;
    }

    public StreakRegion Add(string monitorId, int x, int y, int width, int height)
    {
        var region = new StreakRegion
        {
            MonitorId = monitorId,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Seed = Random.Shared.Next(1, int.MaxValue),
        };

        _regions.Add(region);
        Raise();
        return region;
    }

    public void Remove(StreakRegion region)
    {
        if (_regions.Remove(region))
        {
            Raise();
        }
    }

    public void Clear()
    {
        if (_regions.Count == 0)
        {
            return;
        }

        _regions.Clear();
        Raise();
    }

    public void ClearMonitor(string monitorId)
    {
        int removed = _regions.RemoveAll(
            r => string.Equals(r.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            Raise();
        }
    }

    /// <summary>用于提交给 AppSettings 的深拷贝快照</summary>
    public List<StreakRegion> Snapshot()
    {
        var copy = new List<StreakRegion>(_regions.Count);
        foreach (StreakRegion region in _regions)
        {
            copy.Add(region.Clone());
        }

        return copy;
    }

    /// <summary>几何属性被直接修改后，主动通知重绘</summary>
    public void NotifyGeometryChanged() => Raise();

    private void Raise() => Changed?.Invoke();
}
