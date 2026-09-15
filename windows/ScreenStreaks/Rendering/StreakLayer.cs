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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ScreenStreaks.Infrastructure;
using ScreenStreaks.Models;

namespace ScreenStreaks.Rendering;

/// <summary>
/// 把一组 <see cref="StreakDefect"/> 变成真正的 WPF 视觉元素，挂到覆盖层画布上。
///
/// 渲染原则（都是为了「以假乱真」）：
///  - 实心不透明填充。坏列是自己发光的，不是盖在画面上的半透明色块；
///    半透明会让桌面内容透出来，一眼就穿帮。
///  - 不画辉光。真实的面板坏线边缘就是硬的，光晕是截图或滤镜里才有的东西。
///  - 每条线从显示器顶端到底端。
///  - 闪烁是「亮 ↔ 黑」的跳变：底下垫一条恒黑的同宽矩形，
///    上面那条亮线的透明度在 1 与 0 之间跳。这样熄灭时露出的是黑，
///    而不是让桌面透出来。
///
/// 闪烁动画交给 WPF 的 Storyboard 引擎，在合成线程跑，
/// 不在 UI 线程逐帧重算，因此长时间挂着也不吃 CPU。
/// </summary>
public sealed class StreakLayer : IDisposable
{
    private readonly Canvas _host;
    private readonly List<FrameworkElement> _elements = new();
    private bool _disposed;
    private Rect _contentBounds = Rect.Empty;

    public StreakLayer(Canvas host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public int ElementCount => _elements.Count;

    /// <summary>本层所有线条在画布上占据的 DIP 包围盒，仅用于诊断与自检</summary>
    public Rect ContentBounds => _contentBounds;

    /// <summary>
    /// 重建该区域的全部线条。
    /// </summary>
    /// <param name="region">区域，坐标是相对显示器左上角的物理像素</param>
    /// <param name="options">生成参数</param>
    /// <param name="scale">物理像素 → DIP 的除数</param>
    /// <param name="monitorHeightPhysical">所属显示器的物理高度，缺陷列贯穿整屏</param>
    public void Build(
        StreakRegion region,
        StreakOptions options,
        double scale,
        double monitorHeightPhysical)
    {
        Clear();

        if (_disposed || region is null || options is null)
        {
            return;
        }

        if (scale <= 0.01)
        {
            scale = 1.0;
        }

        if (monitorHeightPhysical <= 0)
        {
            monitorHeightPhysical = region.Height;
        }

        _contentBounds = Rect.Empty;

        double heightDip = monitorHeightPhysical / scale;

        int count1 = 0;
        int count2 = 0;
        int count3 = 0;
        int flickerCount = 0;
        int defectCount = 0;

        foreach (StreakDefect defect in StreakGenerator.Generate(region, options))
        {
            defectCount++;

            switch (defect.Width)
            {
                case 1: count1++; break;
                case 2: count2++; break;
                default: count3++; break;
            }

            // 画布坐标 = 区域在显示器上的原点 + 缺陷在区域内的偏移
            double leftDip = (region.X + defect.OffsetX) / scale;
            double widthDip = defect.Width / scale;

            if (defect.Kind == StreakKind.Flicker)
            {
                flickerCount++;

                // 底层恒黑：模拟这一列熄灭时的状态。这样闪烁是「亮 ↔ 黑」的跳变，
                // 而不是「亮 ↔ 透明」—— 后者会让桌面内容透出来，一眼就是假的。
                AddSolid(leftDip, 0.0, widthDip, heightDip, Colors.Black);

                Rectangle bright = AddSolid(
                    leftDip,
                    0.0,
                    widthDip,
                    heightDip,
                    Color.FromRgb(defect.R, defect.G, defect.B));

                ApplyFlicker(defect, bright);
            }
            else
            {
                AddSolid(
                    leftDip,
                    0.0,
                    widthDip,
                    heightDip,
                    Color.FromRgb(defect.R, defect.G, defect.B));
            }
        }

        Logger.Info(
            $"[layer:{region.MonitorId}] 坏列={defectCount} 宽1px={count1} 宽2px={count2} 宽3px={count3} " +
            $"闪烁={flickerCount} 高度={monitorHeightPhysical:0}px");
    }

    public void Clear()
    {
        foreach (FrameworkElement element in _elements)
        {
            StopAnimations(element);
            _host.Children.Remove(element);
        }

        _elements.Clear();
        _contentBounds = Rect.Empty;
    }

    // ---------------------------------------------------------------- 构建

    private Rectangle AddSolid(double left, double top, double width, double height, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var element = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = brush,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
        };

        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);

        _host.Children.Add(element);
        _elements.Add(element);

        var bounds = new Rect(left, top, width, height);
        _contentBounds = _contentBounds.IsEmpty ? bounds : Rect.Union(_contentBounds, bounds);

        return element;
    }

    // ---------------------------------------------------------------- 动画

    private static void ApplyFlicker(StreakDefect defect, FrameworkElement element)
    {
        double[] pattern = defect.BlinkPattern;
        if (pattern is null || pattern.Length < 2)
        {
            return;
        }

        double cycleSeconds = 1.0 / Math.Max(0.1, defect.BlinkHz);

        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromSeconds(cycleSeconds)),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(Math.Max(0.0, defect.PhaseSeconds)),
            FillBehavior = FillBehavior.HoldEnd,
        };

        double step = cycleSeconds / pattern.Length;
        for (int i = 0; i < pattern.Length; i++)
        {
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                Math.Clamp(pattern[i], 0.0, 1.0),
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(i * step))));
        }

        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static void StopAnimations(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
    }

    // ---------------------------------------------------------------- 释放

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }
}
