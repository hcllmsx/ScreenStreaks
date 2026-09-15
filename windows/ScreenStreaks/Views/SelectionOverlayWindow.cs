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
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenStreaks.Infrastructure;
using ScreenStreaks.Interop;
using ScreenStreaks.Models;
using ScreenStreaks.Rendering;
using ScreenStreaks.Services;

namespace ScreenStreaks.Views;

/// <summary>
/// 框选编辑器：每个显示器一个半透明遮罩窗口。
///
/// 支持多次拖拽落区域（不是一笔画完），并且可以继续修改已落下的区域：
///   拖空白  新建区域
///   拖区域  整体移动
///   拖角    缩放
///   右键/Del 删除
///   Enter   确认   Esc 取消
///
/// 所有几何量内部一律以 DIP 计算，仅在写回模型时换算成物理像素。
/// </summary>
public sealed class SelectionOverlayWindow : Window
{
    private const double HandleSize = 9.0;
    private const double MinRegionDip = 5.0;
    private const double HandleTolerance = 10.0;

    private static readonly int[] BoundsRecoveryDelaysMs = { 120, 450, 1200 };

    private enum DragMode
    {
        None,
        Creating,
        Moving,
        Resizing,
    }

    private static readonly Color AccentColor = Color.FromRgb(0x4C, 0xC2, 0xFF);
    private static readonly Color SelectedColor = Color.FromRgb(0xFF, 0xD1, 0x66);

    private readonly MonitorInfo _monitor;
    private readonly SelectionSession _session;
    private readonly StreakOptions _options;
    private readonly bool _isPrimaryMonitor;

    private readonly Canvas _regionLayer = new();
    private readonly TextBlock _statusLine = new();

    private DragMode _dragMode = DragMode.None;
    private StreakRegion? _active;
    private StreakRegion? _selected;
    private int _resizeCorner = -1;

    private Point _dragStartDip;
    private Rect _originalDip;
    private Rect _previewDip = Rect.Empty;

    private double _scale = 1.0;
    private IntPtr _handle;
    private HwndSource? _source;
    private bool _closed;
    private bool _geometryLogged;

    public MonitorInfo Monitor => _monitor;

    public bool IsPrimaryMonitor => _isPrimaryMonitor;

    public event Action? ConfirmRequested;
    public event Action? CancelRequested;
    public event Action? ClearRequested;

    public SelectionOverlayWindow(
        MonitorInfo monitor,
        SelectionSession session,
        StreakOptions options,
        bool isPrimaryMonitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? new StreakOptions();
        _isPrimaryMonitor = isPrimaryMonitor;

        // 遮罩底色：40% 黑，既压暗背景让选区突出，又能看清底层屏幕内容
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        ShowInTaskbar = false;
        ShowActivated = isPrimaryMonitor;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Focusable = true;
        FocusVisualStyle = null;
        Title = "ScreenStreaks.Select." + monitor.DeviceId;

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(_regionLayer);
        root.Children.Add(BuildHintBar());
        if (isPrimaryMonitor)
        {
            root.Children.Add(BuildActionBar());
        }

        Content = root;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonDown += OnMouseRightButtonDown;
        PreviewKeyDown += OnPreviewKeyDown;

        _session.Changed += OnSessionChanged;
    }

    // ---------------------------------------------------------------- 顶部提示栏

    private UIElement BuildHintBar()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var keys = new StackPanel { Orientation = Orientation.Horizontal };
        keys.Children.Add(HintChip("拖拽空白", "新建区域"));
        keys.Children.Add(HintChip("拖拽区域", "整体移动"));
        keys.Children.Add(HintChip("拖拽四角", "缩放"));
        keys.Children.Add(HintChip("右键 / Del", "删除"));
        keys.Children.Add(HintChip("Enter", "确认"));
        keys.Children.Add(HintChip("Esc", "取消"));
        stack.Children.Add(keys);

        _statusLine.FontSize = 11.5;
        _statusLine.Margin = new Thickness(2, 7, 0, 0);
        _statusLine.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x97, 0xAB));
        stack.Children.Add(_statusLine);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x0B, 0x0E, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x4C, 0xC2, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 11, 16, 11),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 26, 0, 0),
            Child = stack,
            IsHitTestVisible = false,
        };

        return border;
    }

    private static UIElement HintChip(string key, string description)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 16, 0),
        };

        panel.Children.Add(new TextBlock
        {
            Text = key,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(AccentColor),
        });

        panel.Children.Add(new TextBlock
        {
            Text = " " + description,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xD3, 0xE4)),
        });

        return panel;
    }

    // ---------------------------------------------------------------- 底部操作栏（仅主显示器）

    private UIElement BuildActionBar()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        panel.Children.Add(CreateButton("确认", "PrimaryButton", () => ConfirmRequested?.Invoke()));
        panel.Children.Add(CreateButton("清空全部", "GhostButton", () => ClearRequested?.Invoke(), new Thickness(10, 0, 0, 0)));
        panel.Children.Add(CreateButton("取消", "GhostButton", () => CancelRequested?.Invoke(), new Thickness(10, 0, 0, 0)));

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x0B, 0x0E, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0x26, 0x30, 0x41)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 38),
            Child = panel,
        };
    }

    private static UIElement CreateButton(string text, string styleKey, Action onClick, Thickness? margin = null)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            Margin = margin ?? new Thickness(0),
            Focusable = false,
        };

        if (Application.Current?.TryFindResource(styleKey) is Style style)
        {
            button.Style = style;
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    // ---------------------------------------------------------------- 窗口生命周期

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;

        int exStyle = NativeMethods.GetWindowLong(_handle, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~(NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE);
        NativeMethods.SetWindowLong(_handle, NativeMethods.GWL_EXSTYLE, exStyle);

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);

        RefreshScale();
        ApplyPhysicalBounds();
        SyncDipSize();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshScale();
        ApplyPhysicalBounds();
        SyncDipSize();
        Render();
        UpdateStatus(_session.ForMonitor(_monitor.DeviceId).Count);
        VerifyGeometry("loaded");

        // 与展示层同样的兜底：窗口刚落地时 DPI 与尺寸可能还会被系统改写一次
        foreach (int delay in BoundsRecoveryDelaysMs)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_closed || !IsLoaded)
                {
                    return;
                }

                double before = _scale;
                RefreshScale();
                ApplyPhysicalBounds();
                SyncDipSize();
                VerifyGeometry($"recovery{delay}");

                if (Math.Abs(before - _scale) > 0.001)
                {
                    Render();
                }
            };
            timer.Start();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _session.Changed -= OnSessionChanged;

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DPICHANGED && !_closed)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshScale();
                ApplyPhysicalBounds();
                SyncDipSize();
                Render();
                VerifyGeometry("dpichanged");
            }), DispatcherPriority.Loaded);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 取「物理像素 → DIP」的换算比例，来源必须是 WPF 自己的合成缩放。
    /// 窗口矩形由 SetWindowPos 以物理像素钉死，内容由 WPF 按它的缩放渲染，
    /// 只有同源才能保证「拖拽记录的区域」与「画出来的区域」严格是同一块。
    /// </summary>
    private void RefreshScale()
    {
        double scale = 1.0;

        Matrix toDevice = _source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        if (toDevice.M11 > 0.01)
        {
            scale = toDevice.M11;
        }

        _scale = scale;
    }

    private void ApplyPhysicalBounds()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HWND_TOPMOST,
            _monitor.Left,
            _monitor.Top,
            _monitor.Width,
            _monitor.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void SyncDipSize()
    {
        if (_handle == IntPtr.Zero || _scale <= 0.001)
        {
            return;
        }

        double widthDip = _monitor.Width / _scale;
        double heightDip = _monitor.Height / _scale;

        if (Math.Abs(Width - widthDip) > 0.5)
        {
            Width = widthDip;
        }

        if (Math.Abs(Height - heightDip) > 0.5)
        {
            Height = heightDip;
        }
    }

    /// <summary>校验窗口是否精确覆盖目标显示器，不一致则重贴，并记录实测数据</summary>
    private void VerifyGeometry(string stage)
    {
        if (_handle == IntPtr.Zero || _closed)
        {
            return;
        }

        if (!NativeMethods.GetWindowRect(_handle, out NativeMethods.RECT rect))
        {
            return;
        }

        int actualWidth = rect.Right - rect.Left;
        int actualHeight = rect.Bottom - rect.Top;

        bool sizeOk = Math.Abs(actualWidth - _monitor.Width) <= 2
                      && Math.Abs(actualHeight - _monitor.Height) <= 2;
        bool posOk = Math.Abs(rect.Left - _monitor.Left) <= 2
                     && Math.Abs(rect.Top - _monitor.Top) <= 2;

        if (!_geometryLogged || !sizeOk || !posOk)
        {
            _geometryLogged = true;

            string message =
                $"[geo:select:{_monitor.DeviceId}] stage={stage} " +
                $"monitor=({_monitor.Left},{_monitor.Top} {_monitor.Width}x{_monitor.Height}) " +
                $"window=({rect.Left},{rect.Top} {actualWidth}x{actualHeight}) " +
                $"wpfScale={_scale:0.###} windowDpi={NativeMethods.GetDpiForWindow(_handle)} " +
                $"dipSize={ActualWidth:0.#}x{ActualHeight:0.#} ok={sizeOk && posOk}";

            if (sizeOk && posOk)
            {
                Logger.Info(message);
            }
            else
            {
                Logger.Warn(message + " -> 重新贴合");
                ApplyPhysicalBounds();
            }
        }
    }

    // ---------------------------------------------------------------- 坐标换算

    private double CanvasWidthDip => ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : _monitor.Width);

    private double CanvasHeightDip => ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : _monitor.Height);

    private Rect ToDipRect(StreakRegion region) => new(
        region.X / _scale,
        region.Y / _scale,
        region.Width / _scale,
        region.Height / _scale);

    private Point ClampToCanvas(Point point) => new(
        Math.Clamp(point.X, 0, Math.Max(0, CanvasWidthDip)),
        Math.Clamp(point.Y, 0, Math.Max(0, CanvasHeightDip)));

    private Rect ClampRect(Rect rect)
    {
        double maxX = Math.Max(0, CanvasWidthDip);
        double maxY = Math.Max(0, CanvasHeightDip);

        double width = Math.Min(rect.Width, maxX);
        double height = Math.Min(rect.Height, maxY);
        double x = Math.Clamp(rect.X, 0, Math.Max(0, maxX - width));
        double y = Math.Clamp(rect.Y, 0, Math.Max(0, maxY - height));

        return new Rect(x, y, width, height);
    }

    private void ApplyRect(StreakRegion region, Rect dip)
    {
        region.X = (int)Math.Round(dip.X * _scale);
        region.Y = (int)Math.Round(dip.Y * _scale);
        region.Width = Math.Max(2, (int)Math.Round(dip.Width * _scale));
        region.Height = Math.Max(2, (int)Math.Round(dip.Height * _scale));
    }

    private static Rect MakeRect(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));

    // ---------------------------------------------------------------- 绘制

    private void OnSessionChanged()
    {
        if (_closed)
        {
            return;
        }

        Render();
        UpdateStatus(_session.ForMonitor(_monitor.DeviceId).Count);
    }

    private void UpdateStatus(int count)
    {
        if (count == 0)
        {
            _statusLine.Text = "还没有选中任何区域 —— 按住左键拖出一个矩形即可";
            return;
        }

        int lines = 0;
        foreach (StreakRegion region in _session.Regions)
        {
            lines += StreakGenerator.EstimateLineCount(region, _options);
        }

        _statusLine.Text =
            $"本屏已选 {count} 个区域，全部共 {_session.Count} 个，预计生成 {lines} 条线条" +
            $"（密度 {_options.Density:0.#} 条/千像素可调）";
    }

    private void Render()
    {
        if (_closed)
        {
            return;
        }

        _regionLayer.Children.Clear();

        IReadOnlyList<StreakRegion> regions = _session.ForMonitor(_monitor.DeviceId);
        for (int i = 0; i < regions.Count; i++)
        {
            AddRegionVisuals(regions[i], i + 1);
        }

        if (_dragMode == DragMode.Creating && _previewDip.Width > 0.5 && _previewDip.Height > 0.5)
        {
            AddPreviewVisuals(_previewDip);
        }
    }

    private void AddRegionVisuals(StreakRegion region, int index)
    {
        Rect dip = ToDipRect(region);
        bool selected = ReferenceEquals(region, _selected);
        Color stroke = selected ? SelectedColor : AccentColor;

        var box = new Rectangle
        {
            Width = Math.Max(1, dip.Width),
            Height = Math.Max(1, dip.Height),
            Fill = new SolidColorBrush(Color.FromArgb(
                selected ? (byte)0x38 : (byte)0x26, stroke.R, stroke.G, stroke.B)),
            Stroke = new SolidColorBrush(Color.FromArgb(0xE6, stroke.R, stroke.G, stroke.B)),
            StrokeThickness = 1.5,
            RadiusX = 3,
            RadiusY = 3,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(box, dip.X);
        Canvas.SetTop(box, dip.Y);
        _regionLayer.Children.Add(box);

        int estimate = StreakGenerator.EstimateLineCount(region, _options);

        var label = new TextBlock
        {
            Text = $"#{index}   {region.Width} × {region.Height}   ≈ {estimate} 条",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, stroke.R, stroke.G, stroke.B)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(label, dip.X + 7);
        Canvas.SetTop(label, dip.Y + 6);
        _regionLayer.Children.Add(label);

        foreach (Point corner in GetCorners(dip))
        {
            var handle = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = new SolidColorBrush(Color.FromArgb(0xFF, stroke.R, stroke.G, stroke.B)),
                RadiusX = 2,
                RadiusY = 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(handle, corner.X - (HandleSize / 2));
            Canvas.SetTop(handle, corner.Y - (HandleSize / 2));
            _regionLayer.Children.Add(handle);
        }
    }

    private void AddPreviewVisuals(Rect dip)
    {
        var box = new Rectangle
        {
            Width = Math.Max(1, dip.Width),
            Height = Math.Max(1, dip.Height),
            Fill = new SolidColorBrush(Color.FromArgb(0x1F, AccentColor.R, AccentColor.G, AccentColor.B)),
            Stroke = new SolidColorBrush(AccentColor),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            RadiusX = 3,
            RadiusY = 3,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(box, dip.X);
        Canvas.SetTop(box, dip.Y);
        _regionLayer.Children.Add(box);

        int previewEstimate = StreakGenerator.EstimateLineCount(
            new StreakRegion
            {
                MonitorId = _monitor.DeviceId,
                Width = Math.Max(1, (int)Math.Round(dip.Width * _scale)),
                Height = Math.Max(1, (int)Math.Round(dip.Height * _scale)),
            },
            _options);

        var size = new TextBlock
        {
            Text = $"{(int)Math.Round(dip.Width * _scale)} × {(int)Math.Round(dip.Height * _scale)}   ≈ {previewEstimate} 条",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF7)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(size, dip.X + 7);
        Canvas.SetTop(size, dip.Y + 6);
        _regionLayer.Children.Add(size);
    }

    private static IEnumerable<Point> GetCorners(Rect rect)
    {
        yield return new Point(rect.Left, rect.Top);
        yield return new Point(rect.Right, rect.Top);
        yield return new Point(rect.Left, rect.Bottom);
        yield return new Point(rect.Right, rect.Bottom);
    }

    // ---------------------------------------------------------------- 命中测试

    private StreakRegion? HitTestRegion(Point dip)
    {
        IReadOnlyList<StreakRegion> regions = _session.ForMonitor(_monitor.DeviceId);

        // 后加入的绘制在上层，命中测试也要从后往前
        for (int i = regions.Count - 1; i >= 0; i--)
        {
            Rect rect = ToDipRect(regions[i]);
            if (rect.Width < 1 || rect.Height < 1)
            {
                // 极小区域放宽命中范围，否则根本点不到
                rect.Inflate(3, 3);
            }

            if (rect.Contains(dip))
            {
                return regions[i];
            }
        }

        return null;
    }

    private static int HitTestCorner(Rect rect, Point dip)
    {
        Point[] corners =
        {
            new(rect.Left, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Left, rect.Bottom),
            new(rect.Right, rect.Bottom),
        };

        for (int i = 0; i < corners.Length; i++)
        {
            if (Math.Abs(dip.X - corners[i].X) <= HandleTolerance &&
                Math.Abs(dip.Y - corners[i].Y) <= HandleTolerance)
            {
                return i;
            }
        }

        return -1;
    }

    // ---------------------------------------------------------------- 鼠标交互

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        Point dip = ClampToCanvas(e.GetPosition(_regionLayer));

        // 1. 优先判断是否抓住已选中区域的控制点
        if (_selected is not null)
        {
            int corner = HitTestCorner(ToDipRect(_selected), dip);
            if (corner >= 0)
            {
                _dragMode = DragMode.Resizing;
                _active = _selected;
                _resizeCorner = corner;
                _dragStartDip = dip;
                _originalDip = ToDipRect(_selected);
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // 2. 命中某个已有区域 -> 整体移动
        StreakRegion? hit = HitTestRegion(dip);
        if (hit is not null)
        {
            _selected = hit;
            _active = hit;
            _dragMode = DragMode.Moving;
            _dragStartDip = dip;
            _originalDip = ToDipRect(hit);
            CaptureMouse();
            Render();
            e.Handled = true;
            return;
        }

        // 3. 空白处 -> 开新框
        _selected = null;
        _active = null;
        _dragMode = DragMode.Creating;
        _dragStartDip = dip;
        _previewDip = new Rect(dip, new Size(0, 0));
        CaptureMouse();
        Render();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None)
        {
            return;
        }

        Point dip = ClampToCanvas(e.GetPosition(_regionLayer));

        switch (_dragMode)
        {
            case DragMode.Creating:
                _previewDip = MakeRect(_dragStartDip, dip);
                Render();
                break;

            case DragMode.Moving:
                {
                    if (_active is null)
                    {
                        return;
                    }

                    Rect moved = _originalDip;
                    moved.Offset(dip.X - _dragStartDip.X, dip.Y - _dragStartDip.Y);
                    ApplyRect(_active, ClampRect(moved));

                    // 交给会话广播，各显示器窗口统一重绘，也顺带同步顶部计数
                    _session.NotifyGeometryChanged();
                    break;
                }

            case DragMode.Resizing:
                {
                    if (_active is null)
                    {
                        return;
                    }

                    Rect baseRect = _originalDip;
                    Point topLeft;
                    Point bottomRight;

                    switch (_resizeCorner)
                    {
                        case 0: // 左上
                            topLeft = dip;
                            bottomRight = baseRect.BottomRight;
                            break;
                        case 1: // 右上
                            topLeft = new Point(baseRect.Left, dip.Y);
                            bottomRight = new Point(dip.X, baseRect.Bottom);
                            break;
                        case 2: // 左下
                            topLeft = new Point(dip.X, baseRect.Top);
                            bottomRight = new Point(baseRect.Right, dip.Y);
                            break;
                        default: // 右下
                            topLeft = baseRect.TopLeft;
                            bottomRight = dip;
                            break;
                    }

                    ApplyRect(_active, ClampRect(MakeRect(topLeft, bottomRight)));
                    _session.NotifyGeometryChanged();
                    break;
                }
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == DragMode.None)
        {
            return;
        }

        ReleaseMouseCapture();

        if (_dragMode == DragMode.Creating)
        {
            Rect preview = _previewDip;
            _previewDip = Rect.Empty;
            _dragMode = DragMode.None;
            _active = null;

            if (preview.Width >= MinRegionDip && preview.Height >= MinRegionDip)
            {
                StreakRegion created = _session.Add(
                    _monitor.DeviceId,
                    (int)Math.Round(preview.X * _scale),
                    (int)Math.Round(preview.Y * _scale),
                    Math.Max(2, (int)Math.Round(preview.Width * _scale)),
                    Math.Max(2, (int)Math.Round(preview.Height * _scale)));

                _selected = created;
                Render();
            }
            else
            {
                Render();
            }

            e.Handled = true;
            return;
        }

        _dragMode = DragMode.None;
        _active = null;
        Render();
        e.Handled = true;
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point dip = ClampToCanvas(e.GetPosition(_regionLayer));
        StreakRegion? hit = HitTestRegion(dip);
        if (hit is null)
        {
            return;
        }

        if (ReferenceEquals(hit, _selected))
        {
            _selected = null;
        }

        if (ReferenceEquals(hit, _active))
        {
            _active = null;
        }

        _session.Remove(hit);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ConfirmRequested?.Invoke();
                e.Handled = true;
                break;

            case Key.Escape:
                CancelRequested?.Invoke();
                e.Handled = true;
                break;

            case Key.Delete:
            case Key.Back:
                if (_selected is not null)
                {
                    _session.Remove(_selected);
                    _selected = null;
                    e.Handled = true;
                }

                break;
        }
    }
}
