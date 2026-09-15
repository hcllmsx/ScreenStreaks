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
using System.Runtime.InteropServices;
using System.Windows.Interop;
using ScreenStreaks.Infrastructure;
using ScreenStreaks.Interop;
using ScreenStreaks.Models;

namespace ScreenStreaks.Services;

/// <summary>
/// 全局热键。挂在消息专用窗口上，与主窗口生命周期解耦 ——
/// 这点很重要：展示态下所有覆盖层都鼠标穿透，热键是唯一的退出途径。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x5353;
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;
    private bool _registered;
    private HotkeyDefinition? _current;
    private bool _disposed;

    public event Action? Pressed;

    public HotkeyService()
    {
        try
        {
            var parameters = new HwndSourceParameters("ScreenStreaks.HotkeySink")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,
                ParentWindow = HwndMessage,
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            Logger.Error("热键消息窗口创建失败", ex);
            _source = null;
        }
    }

    /// <summary>当前生效的热键定义，注册失败时为 null</summary>
    public HotkeyDefinition? Current => _current?.Clone();

    private bool _lastRegisterFailed;

    /// <summary>上一次注册是否失败（用于给用户提示，例如热键被别的程序占用）</summary>
    public bool LastRegisterFailed => _lastRegisterFailed;

    public bool Register(HotkeyDefinition? definition)
    {
        Unregister();
        _lastRegisterFailed = false;

        if (definition is null || !definition.IsValid)
        {
            // 「未设置热键」是合法状态，不是错误，所以只记 Info
            Logger.Info("未设置全局热键，当前不注册任何热键");
            return false;
        }

        HwndSource? source = _source;
        if (source is null)
        {
            _lastRegisterFailed = true;
            return false;
        }

        uint modifiers = definition.Modifiers | NativeMethods.MOD_NOREPEAT;
        if (NativeMethods.RegisterHotKey(source.Handle, HotkeyId, modifiers, definition.VirtualKey))
        {
            _registered = true;
            _current = definition.Clone();
            Logger.Info($"全局热键已注册: {_current}");
            return true;
        }

        int error = Marshal.GetLastWin32Error();
        _lastRegisterFailed = true;
        Logger.Warn($"全局热键注册失败: {definition}，Win32 错误码 {error}（可能被其它程序占用）");
        return false;
    }

    public void Unregister()
    {
        if (_registered && _source is not null)
        {
            try
            {
                NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            }
            catch (Exception ex)
            {
                Logger.Error("注销全局热键失败", ex);
            }

            _registered = false;
        }

        _current = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try
            {
                Pressed?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error("处理热键回调异常", ex);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
