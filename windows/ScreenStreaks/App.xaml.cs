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
using System.Windows;
using System.Windows.Threading;
using ScreenStreaks.Infrastructure;
using ScreenStreaks.Services;

namespace ScreenStreaks;

/// <summary>
/// 应用入口。这里只做「单实例判定 + 依赖装配 + 全局异常兜底」，
/// 真正的状态机在 <see cref="AppController"/> 里。
/// </summary>
public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    private ConfigService? _config;
    private HotkeyService? _hotkey;
    private TrayIconService? _tray;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        AppPaths.EnsureDataDirectory();
        Logger.Info($"==== {AppInfo.DisplayName} {AppInfo.VersionWithPrefix} 启动，参数: [{string.Join(" ", e.Args)}] ====");

        bool silent = Array.Exists(
            e.Args,
            static arg => string.Equals(arg, AutoStartService.SilentArgument, StringComparison.OrdinalIgnoreCase));

        bool selectMode = Array.Exists(
            e.Args,
            static arg => string.Equals(arg, AppController.SelectArgument, StringComparison.OrdinalIgnoreCase));

        bool settingsMode = Array.Exists(
            e.Args,
            static arg => string.Equals(arg, AppController.SettingsArgument, StringComparison.OrdinalIgnoreCase));

        // ---- 单实例：第二个进程只负责把第一个唤到前台
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.TryAcquire())
        {
            Logger.Info("已有实例在运行，发送唤起信号后退出");
            SingleInstanceService.SignalRunningInstance();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        _singleInstance.StartListening();

        // ---- 装配
        _config = new ConfigService();
        _hotkey = new HotkeyService();
        _tray = new TrayIconService();

        _controller = new AppController(_config, _hotkey, _tray, _singleInstance);

        try
        {
            _controller.Initialize();
            _controller.HandleStartup(silent, selectMode, settingsMode);
        }
        catch (Exception ex)
        {
            Logger.Error("启动流程失败", ex);
            MessageBox.Show(
                "ScreenStreaks 启动失败：\n" + ex.Message + "\n\n详细日志：" + AppPaths.LogFile,
                AppInfo.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        Logger.Info("==== 已退出 ====");
        base.OnExit(e);
    }

    // ================================================================ 全局异常

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("未处理的 UI 线程异常", e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Logger.Error("未处理的后台线程异常: " + e.ExceptionObject);
    }
}
