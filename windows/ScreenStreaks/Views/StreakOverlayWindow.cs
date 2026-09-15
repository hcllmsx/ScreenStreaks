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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenStreaks.Infrastructure;
using ScreenStreaks.Interop;
using ScreenStreaks.Models;
using ScreenStreaks.Rendering;
using ScreenStreaks.Services;

namespace ScreenStreaks.Views;

/// <summary>
/// 缺陷展示层：每个显示器一个窗口。
///
/// 窗口级特性（全部靠扩展样式实现）：
///   WS_EX_LAYERED     逐像素透明，背景完全透出桌面
///   WS_EX_TRANSPARENT 鼠标穿透，点击直接落到桌面/下层窗口
///   WS_EX_TOOLWINDOW  不出现在 Alt+Tab 与任务栏
///   WS_EX_NOACTIVATE  不抢焦点，不会打断用户正在做的事
///
/// 由于鼠标被穿透，展示态的唯一退出通道是全局热键或托盘菜单。
/// </summary>
public sealed class StreakOverlayWindow : Window
{
    private static readonly int[] BoundsRecoveryDelaysMs = { 120, 450, 1200 };

    /// <summary>
    /// Z 序自愈的轮询间隔。
    /// 300ms 只是「查一下自己上面有没有窗口」，GetWindow 是微秒级调用，
    /// 只有真的被压下去时才会调 SetWindowPos，因此常态开销可以忽略。
    /// </summary>
    private static readonly TimeSpan TopmostPollInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 沿 z 序向上最多探查多少个窗口。
    /// 纯粹是防止异常情况下死循环，正常几十个足够。
    /// </summary>
    private const int MaxZOrderHops = 40;

    /// <summary>小于这个尺寸的窗口不算遮挡者（Shell 有大量 1x1 的幽灵窗口）</summary>
    private const int MinOccluderSize = 2;

    /// <summary>同一个遮挡窗口连续压不过去多少次后开始冷却</summary>
    private const int MaxStrikesPerBlocker = 3;

    /// <summary>压不过去时的冷却时长（秒），避免持续空转</summary>
    private const int RetryCooldownSeconds = 5;

    private readonly MonitorInfo _monitor;
    private readonly Canvas _canvas;
    private readonly List<StreakLayer> _layers = new();

    private IReadOnlyList<StreakRegion> _regions = Array.Empty<StreakRegion>();
    private StreakOptions? _options;

    private IntPtr _handle;
    private HwndSource? _source;
    private DispatcherTimer? _topmostTimer;
    private double _scale = 1.0;
    private bool _ready;
    private bool _paused;
    private bool _disposed;
    private bool _geometryLogged;
    private bool _contentLogged;
    private int _topmostRestackCount;
    private IntPtr _topmostBlocker = IntPtr.Zero;
    private int _topmostStrikes;
    private long _topmostRetryAfterTicks;
    private Action<uint>? _windowEventHandler;

    public MonitorInfo Monitor => _monitor;

    public StreakOverlayWindow(MonitorInfo monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "ScreenStreaks.Overlay." + monitor.DeviceId;

        _canvas = new Canvas
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            ClipToBounds = true,
            SnapsToDevicePixels = false,
        };

        Content = _canvas;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // ---------------------------------------------------------------- 对外接口

    public void SetContent(IReadOnlyList<StreakRegion> regions, StreakOptions options)
    {
        _regions = regions ?? Array.Empty<StreakRegion>();
        _options = options;
        Rebuild();
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            return;
        }

        _paused = paused;

        if (paused)
        {
            ClearLayers();
            Hide();
        }
        else
        {
            Show();
            ApplyPhysicalBounds();
            Rebuild();
        }
    }

    /// <summary>重新读取 DPI、重新贴合显示器边界并重建内容</summary>
    public void ForceRefresh()
    {
        RefreshScale();
        ApplyPhysicalBounds();
        SyncDipSize();
        Rebuild();
        VerifyGeometry("force");
    }

    // ---------------------------------------------------------------- 窗口生命周期

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);

        ApplyExtendedStyles();
        RefreshScale();
        ApplyPhysicalBounds();
        SyncDipSize();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ready = true;
        RefreshScale();
        ApplyPhysicalBounds();
        SyncDipSize();
        Rebuild();
        VerifyGeometry("loaded");
        StartTopmostGuard();

        // 窗口从创建到真正落到目标显示器之间，系统的 DPI 与尺寸可能还会被改写一次，
        // 这里做几次幂等的兜底校正 —— 代价极低，但能躲掉一整类“覆盖层错位”问题。
        foreach (int delay in BoundsRecoveryDelaysMs)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_disposed || !IsLoaded)
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
                    Rebuild();
                }
            };
            timer.Start();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_windowEventHandler is not null)
        {
            WindowEventWatcher.Unsubscribe(_windowEventHandler);
            _windowEventHandler = null;
        }

        if (_topmostTimer is not null)
        {
            _topmostTimer.Stop();
            _topmostTimer.Tick -= OnTopmostTick;
            _topmostTimer = null;
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        ClearLayers();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DPICHANGED)
        {
            // 系统改了窗口 DPI，必须在下一轮布局前重新贴合，否则会出现半屏错位
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed)
                {
                    return;
                }

                RefreshScale();
                ApplyPhysicalBounds();
                SyncDipSize();
                Rebuild();
                VerifyGeometry("dpichanged");
            }), DispatcherPriority.Loaded);
        }

        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- 窗口样式与几何

    private void ApplyExtendedStyles()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        int exStyle = NativeMethods.GetWindowLong(_handle, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_LAYERED
                   | NativeMethods.WS_EX_TOOLWINDOW
                   | NativeMethods.WS_EX_NOACTIVATE
                   | NativeMethods.WS_EX_TRANSPARENT;

        NativeMethods.SetWindowLong(_handle, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    /// <summary>
    /// 取「物理像素 → DIP」的换算比例。
    ///
    /// 关键：必须用 WPF 自己的合成缩放（CompositionTarget.TransformToDevice），
    /// 而不是 GetDpiForWindow。因为窗口坐标由 SetWindowPos 以物理像素钉死，
    /// 而画布内容是 WPF 用它的缩放比例渲染的 —— 两者只有用同一个来源才不会打架。
    /// 若用 GetDpiForWindow，一旦它与 WPF 的取值不一致（分层窗口下确实会不一致），
    /// 内容就会被整体缩放并挤到左上角。
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

    /// <summary>用物理像素精确覆盖目标显示器</summary>
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

    /// <summary>
    /// 让 WPF 的尺寸认知与物理尺寸对齐。
    /// 由于 _scale 来自 WPF 自己，Width * _scale 必然回到显示器物理宽度，不会来回打架。
    /// </summary>
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

    /// <summary>
    /// 校验窗口的实际矩形是否精确覆盖目标显示器。不一致就重新贴一次，
    /// 并把真实数据写进日志 —— DPI 相关的错位只能靠实测数据定位，靠猜没用。
    /// </summary>
    private void VerifyGeometry(string stage)
    {
        if (_handle == IntPtr.Zero || _disposed)
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
                $"[geo:{_monitor.DeviceId}] stage={stage} " +
                $"monitor=({_monitor.Left},{_monitor.Top} {_monitor.Width}x{_monitor.Height}) " +
                $"window=({rect.Left},{rect.Top} {actualWidth}x{actualHeight}) " +
                $"wpfScale={_scale:0.###} windowDpi={NativeMethods.GetDpiForWindow(_handle)} " +
                $"systemDpi={NativeMethods.GetDpiForSystem()} " +
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

    // ---------------------------------------------------------------- Z 序自愈

    private void StartTopmostGuard()
    {
        if (_topmostTimer is not null)
        {
            return;
        }

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TopmostPollInterval,
        };

        _topmostTimer.Tick += OnTopmostTick;
        _topmostTimer.Start();

        _windowEventHandler = OnWindowEvent;
        WindowEventWatcher.Subscribe(_windowEventHandler);

        if (!WindowEventWatcher.IsListening)
        {
            Logger.Warn("全局窗口事件监听不可用，遮挡恢复只能依赖轮询");
        }
    }

    private void OnTopmostTick(object? sender, EventArgs e) => ReassertTopmost("轮询");

    /// <summary>
    /// 事件驱动的遮挡恢复。
    ///
    /// 单靠轮询不够：右键菜单弹出的瞬间会盖住线条，等到下一次轮询才被顶回去，
    /// 中间那段时间（最长一个轮询周期）就是肉眼可见的「闪一下」。
    /// 订阅全局窗口事件后，新窗口一出现就能立刻反应。
    ///
    /// 钩子由 <see cref="WindowEventWatcher"/> 统一管理（进程级静态委托）。
    /// 切勿改回「每个窗口自己注册 + 自己保存委托引用」的写法：
    /// 那样只要漏掉一个注销、或提前释放了委托引用，系统回调就会跳进已回收的委托，
    /// 运行时会直接 Environment.FailFast 终止进程，而且一条日志都不会留下。
    /// </summary>
    private void OnWindowEvent(uint eventTime)
    {
        if (_disposed || _paused)
        {
            return;
        }

        ReassertTopmost("窗口事件", eventTime);
    }

    /// <summary>
    /// 保持自己压在置顶带最前面。
    ///
    /// 覆盖层声明了 WS_EX_NOACTIVATE，永远不会被激活；
    /// 而右键菜单、开始菜单这类弹出窗口会把自己插进置顶带的最前面。
    /// 普通窗口被压住后，用户一点它就会被重新提到前面 —— 但我们的窗口永远不会被点，
    /// 于是就被永久压在菜单下面，线条看着就"不置顶"了。
    ///
    /// 这里主动检查「自己上面还有没有别的可见窗口」，有就重新置顶一次。
    /// 因为窗口是鼠标穿透的，即使压住任何界面也不影响点击它 —— 视觉上这才是对的：
    /// 真实的屏幕缺陷长在面板上，本来就该横跨在所有界面之上，包括本程序自己的窗口。
    /// </summary>
    private void ReassertTopmost(string source, uint eventTime = 0)
    {
        if (_disposed || _handle == IntPtr.Zero || _paused || !IsVisible)
        {
            return;
        }

        if (!TryFindRealOccluderAbove(out IntPtr blocker))
        {
            _topmostBlocker = IntPtr.Zero;
            _topmostStrikes = 0;
            return;
        }

        if (blocker == _topmostBlocker)
        {
            // 同一个窗口反复压不过去，说明它在比 topmost 更高的窗口带里（系统 UI），
            // 再每 300ms 试一次只是白烧 CPU，冷却一段时间
            if (Environment.TickCount64 < _topmostRetryAfterTicks)
            {
                return;
            }

            _topmostStrikes++;
            if (_topmostStrikes >= MaxStrikesPerBlocker)
            {
                _topmostStrikes = 0;
                _topmostRetryAfterTicks = Environment.TickCount64 + (RetryCooldownSeconds * 1000L);
                return;
            }
        }
        else
        {
            _topmostBlocker = blocker;
            _topmostStrikes = 0;
        }

        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        _topmostRestackCount++;

        // 常态下不该触发；触发说明确实有窗口盖住了我们，记几笔便于排查
        if (_topmostRestackCount <= 5)
        {
            NativeMethods.GetClassName(blocker, out string blockerClass);

            // dwmsEventTime 是事件发生的时刻（GetTickCount 基准），
            // 与当前时刻相减就是「从窗口出现到我们顶回去」的反应耗时。
            string latency = eventTime == 0
                ? "n/a"
                : $"{unchecked((uint)Environment.TickCount - eventTime)}ms";

            Logger.Info(
                $"[topmost:{_monitor.DeviceId}] {source}：上方存在遮挡窗口 class='{blockerClass}' " +
                $"pid={GetWindowProcessId(blocker)}，已重新置顶（第 {_topmostRestackCount} 次，反应 {latency}）");
        }
    }

    /// <summary>
    /// 沿 z 序往上找第一个「真的挡住了我们」的窗口。
    ///
    /// 这里有两个坑，都必须绕开：
    ///
    /// 1. 不能只看紧邻的那一个。Shell 会把自己的一堆隐藏辅助窗口
    ///    （ForegroundStaging、Default IME、MSCTFIME UI…）放在很靠前的位置，
    ///    只看紧邻窗口的话一碰上就直接返回，**完全检测不到真正的遮挡者**。
    ///    所以要继续往上找。
    ///
    /// 2. 找到可见的也不一定是遮挡者。Shell 还会放一些 <b>1x1 像素</b>却
    ///    IsWindowVisible = true 的幽灵窗口（如 ThumbnailDeviceHelperWnd），
    ///    它们处在比 topmost 更高的窗口带，我们永远压不过去。
    ///    若按「可见」就当成遮挡者，就会陷入每 300ms 重贴一次的死循环。
    ///
    /// 因此判定条件是：可见 + 至少 2x2 像素 + 与本显示器有实际交叠。
    /// </summary>
    private bool TryFindRealOccluderAbove(out IntPtr blocker)
    {
        blocker = IntPtr.Zero;

        IntPtr cursor = NativeMethods.GetWindow(_handle, NativeMethods.GW_HWNDPREV);

        for (int hop = 0; hop < MaxZOrderHops && cursor != IntPtr.Zero; hop++)
        {
            if (IsRealOccluder(cursor))
            {
                blocker = cursor;
                return true;
            }

            cursor = NativeMethods.GetWindow(cursor, NativeMethods.GW_HWNDPREV);
        }

        return false;
    }

    private bool IsRealOccluder(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            return false;
        }

        if (rect.Right - rect.Left < MinOccluderSize || rect.Bottom - rect.Top < MinOccluderSize)
        {
            return false;
        }

        // 与本显示器求交，排除落在其它屏幕上的窗口
        int left = Math.Max(rect.Left, _monitor.Left);
        int top = Math.Max(rect.Top, _monitor.Top);
        int right = Math.Min(rect.Right, _monitor.Right);
        int bottom = Math.Min(rect.Bottom, _monitor.Bottom);

        return right - left >= MinOccluderSize && bottom - top >= MinOccluderSize;
    }

    private static uint GetWindowProcessId(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    // ---------------------------------------------------------------- 内容

    private void Rebuild()
    {
        ClearLayers();

        if (_disposed || _paused || !_ready || _options is null)
        {
            return;
        }

        foreach (StreakRegion region in _regions)
        {
            if (region is null || !region.IsUsable)
            {
                continue;
            }

            if (!string.Equals(region.MonitorId, _monitor.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var layer = new StreakLayer(_canvas);
            layer.Build(region, _options, _scale, _monitor.Height);
            _layers.Add(layer);
        }

        if (!_contentLogged)
        {
            _contentLogged = true;

            Rect bounds = Rect.Empty;
            int elementCount = 0;

            foreach (StreakLayer layer in _layers)
            {
                elementCount += layer.ElementCount;
                if (layer.ContentBounds.IsEmpty)
                {
                    continue;
                }

                bounds = bounds.IsEmpty ? layer.ContentBounds : Rect.Union(bounds, layer.ContentBounds);
            }

            // 把包围盒换算回物理像素，方便和「框选时选的区域」直接比对
            string boundsPhysical = bounds.IsEmpty
                ? "n/a"
                : $"({bounds.Left * _scale:0},{bounds.Top * _scale:0})-({bounds.Right * _scale:0},{bounds.Bottom * _scale:0})";

            Logger.Info(
                $"[content:{_monitor.DeviceId}] scale={_scale:0.###} " +
                $"canvas={_canvas.ActualWidth:0.#}x{_canvas.ActualHeight:0.#} " +
                $"regions={_regions.Count} layers={_layers.Count} elements={elementCount} " +
                $"contentPhysical={boundsPhysical} " +
                $"monitorHeight={_monitor.Height}");
        }
    }

    private void ClearLayers()
    {
        foreach (StreakLayer layer in _layers)
        {
            layer.Dispose();
        }

        _layers.Clear();
        _canvas.Children.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearLayers();
    }
}
