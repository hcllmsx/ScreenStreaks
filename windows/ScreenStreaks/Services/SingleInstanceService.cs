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
using System.Threading;
using ScreenStreaks.Infrastructure;

namespace ScreenStreaks.Services;

/// <summary>
/// 单实例守卫 + 二次启动唤起。
/// 第二个进程不会启动第二套覆盖层，而是给第一个进程发信号让它弹出主面板。
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\ScreenStreaks.SingleInstance.Mutex";
    private const string SignalName = @"Local\ScreenStreaks.SingleInstance.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _signal;
    private Thread? _listener;
    private volatile bool _stopping;

    public bool IsFirstInstance { get; private set; }

    /// <summary>收到「唤起主面板」请求</summary>
    public event Action? ActivationRequested;

    /// <summary>尝试成为唯一实例。返回 false 说明已有实例在跑。</summary>
    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            IsFirstInstance = createdNew;

            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
            }

            return IsFirstInstance;
        }
        catch (Exception ex)
        {
            Logger.Error("单实例互斥体创建失败，将按多实例放行", ex);
            IsFirstInstance = true;
            return true;
        }
    }

    public void StartListening()
    {
        try
        {
            _signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SignalName);
        }
        catch (Exception ex)
        {
            Logger.Error("创建单实例信号量失败，二次启动唤起将不可用", ex);
            return;
        }

        _listener = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "ScreenStreaks.SingleInstance",
        };
        _listener.Start();
    }

    private void ListenLoop()
    {
        while (!_stopping)
        {
            try
            {
                EventWaitHandle? signal = _signal;
                if (signal is null)
                {
                    break;
                }

                if (!signal.WaitOne(500))
                {
                    continue;
                }

                if (_stopping)
                {
                    break;
                }

                ActivationRequested?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error("单实例监听循环异常", ex);
                break;
            }
        }
    }

    /// <summary>由第二个进程调用：唤醒已运行的实例</summary>
    public static void SignalRunningInstance()
    {
        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(SignalName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // 第一个实例还没建好信号量，忽略即可
        }
        catch (Exception ex)
        {
            Logger.Warn("唤起已运行实例失败: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _stopping = true;

        try
        {
            _signal?.Set();
        }
        catch
        {
            // 忽略
        }

        try
        {
            if (_mutex is not null && IsFirstInstance)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // 忽略
        }

        _mutex?.Dispose();
        _signal?.Dispose();
        _mutex = null;
        _signal = null;
    }
}
