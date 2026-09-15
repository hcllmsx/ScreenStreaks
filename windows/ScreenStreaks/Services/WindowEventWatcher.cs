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
using ScreenStreaks.Infrastructure;
using ScreenStreaks.Interop;

namespace ScreenStreaks.Services;

/// <summary>
/// 全局窗口事件监听：有窗口显示出来、或前台窗口切换时通知订阅者。
/// 覆盖层靠它把「被弹出窗口压住」的时间压到一帧以内。
///
/// 为什么做成进程级单例 + 静态委托，而不是每个覆盖层各挂一份：
///
/// WinEvent 钩子在系统侧保存的是一个**函数指针**。一旦对应的委托被 GC 回收，
/// 系统下次回调就会跳进已释放的内存，运行时会直接 Environment.FailFast 终止进程。
/// 这种崩溃连 DispatcherUnhandledException 都拦不住，日志里一条错误都不会留下。
///
/// 之前按窗口注册的写法踩了两个坑：注册了两个钩子只注销了一个，
/// 而且在注销完成前就把委托引用置空了。结果只要随后再来一次前台切换事件，
/// 进程就当场死掉 —— 表现为「重新框选后按确认，有一定概率闪退」。
///
/// 现在委托是静态字段，生命周期与进程一致，从结构上就不可能被回收；
/// 钩子也只注册一次，注销逻辑不复存在，坑自然消失。
/// </summary>
public static class WindowEventWatcher
{
    private static readonly object Gate = new();
    private static readonly List<Action<uint>> Subscribers = new();

    /// <summary>静态字段 = 与进程同生命周期，保证系统回调期间委托一定有效</summary>
    private static readonly NativeMethods.WinEventProc Callback = OnWindowEvent;

    private static IntPtr _hookShow = IntPtr.Zero;

    /// <summary>
    /// 前台切换钩子句柄。
    /// 不主动注销 —— 钩子与进程同生命周期，注销反而多一次出错机会；
    /// 保留句柄只是为了让「是否已注册」这件事可查。
    /// </summary>
    private static IntPtr _hookForeground = IntPtr.Zero;

    /// <summary>钩子是否已成功注册（注册失败时覆盖层会退回轮询）</summary>
    public static bool IsListening => _hookShow != IntPtr.Zero;

    /// <summary>订阅窗口事件，回调参数是事件发生时刻（GetTickCount 基准，毫秒）</summary>
    public static void Subscribe(Action<uint> handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (Gate)
        {
            if (!Subscribers.Contains(handler))
            {
                Subscribers.Add(handler);
            }

            EnsureHooked();
        }
    }

    public static void Unsubscribe(Action<uint> handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (Gate)
        {
            Subscribers.Remove(handler);
        }

        // 钩子本身保持注册：委托是静态的，不存在回收风险，
        // 反复注册/注销反而又多一次出错机会。
    }

    private static void EnsureHooked()
    {
        if (_hookShow != IntPtr.Zero)
        {
            return;
        }

        uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;

        _hookShow = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW,
            NativeMethods.EVENT_OBJECT_SHOW,
            IntPtr.Zero,
            Callback,
            0,
            0,
            flags);

        if (_hookShow == IntPtr.Zero)
        {
            Logger.Warn("窗口显示事件钩子注册失败，遮挡恢复退回轮询模式（被压住后会有短暂延迟）");
            return;
        }

        // 前台切换必须单独注册：事件范围不能跨得太宽，否则会收到海量无关事件
        _hookForeground = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            Callback,
            0,
            0,
            flags);

        Logger.Info("全局窗口事件监听已启动，遮挡恢复为事件驱动");
    }

    private static void OnWindowEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        // 只关心窗口级事件；菜单项、光标之类的子对象事件直接忽略
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0)
        {
            return;
        }

        Action<uint>[] snapshot;

        lock (Gate)
        {
            if (Subscribers.Count == 0)
            {
                return;
            }

            snapshot = Subscribers.ToArray();
        }

        // 在锁外调用，避免订阅者代码回调进 Subscribe/Unsubscribe 造成死锁
        foreach (Action<uint> handler in snapshot)
        {
            try
            {
                handler(dwmsEventTime);
            }
            catch (Exception ex)
            {
                Logger.Error("窗口事件回调处理失败", ex);
            }
        }
    }
}
