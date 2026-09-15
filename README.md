# ScreenStreaks

> 屏幕纵向线条缺陷模拟工具 —— 把「面板亮线故障」做成可自由布置的屏幕特效

在屏幕上划出任意区域，程序在这些区域里生成纵向的**亮线、暗线、闪烁线、子像素卡死线**，
模拟显示器面板劣化时的典型视觉特征。覆盖层逐像素透明、鼠标穿透、不抢焦点、不进任务栏，
挂后台几乎无感。

- 当前版本：见根目录 `VERSION`（唯一版本号来源）
- 技术栈：.NET 8 + WPF，仅 Windows

## 功能

| 能力 | 说明 |
|---|---|
| 自由框选 | 任意显示器上拖出**多个**矩形，可继续移动 / 缩放 / 删除，不是一笔画完 |
| 多显示器 | 每屏一个独立覆盖层，Per-Monitor V2 DPI 感知，混合缩放比例也不错位 |
| 四类缺陷 | 常亮亮线 / 暗线（死列）/ 子像素卡死 / 闪烁线 |
| 可复现随机 | 相同种子 + 相同区域 = 完全相同的结果，切换展示态不会「跳位置」 |
| 实时调参 | 设置面板拖动滑杆即时生效 |
| 鼠标穿透 | 展示态下所有点击直接穿透到桌面 |
| 全局热键 | **默认不启用**（不占用任何按键），需要时在设置里指定 |
| 开机自启 | 写当前用户启动项，开机静默进入展示态，不需要管理员权限 |

## 环境要求

**运行**（框架依赖版，约 0.35 MB）

- Windows 10 1607+ / Windows 11
- [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（Desktop Runtime，不是 ASP.NET 那份）

**构建**

```powershell
winget install Microsoft.DotNet.SDK.8
```

## 构建

```powershell
dotnet publish .\windows\ScreenStreaks\ScreenStreaks.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:DebugType=embedded `
  -o dist
```

产物：`dist\ScreenStreaks.exe`（框架依赖单文件，约 0.35 MB）。

**版本号不需要传参** —— csproj 会自己读根目录的 `VERSION`。

想做成免安装分发，改用自包含：

```powershell
dotnet publish .\windows\ScreenStreaks\ScreenStreaks.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=embedded -o dist
```

产物约 70 MB，目标机器无需安装 .NET 运行时。

> 仓库只提供源码构建方式。作者本地的构建与打包脚本不随仓库分发。

## 运行

```powershell
.\dist\ScreenStreaks.exe            # 有区域则直接进展示态，否则弹出主面板
.\dist\ScreenStreaks.exe --silent   # 静默模式：不弹主面板
.\dist\ScreenStreaks.exe --select   # 启动后直接进入框选模式
.\dist\ScreenStreaks.exe --settings # 启动后直接打开设置面板
```

## 使用

1. 启动后弹出**主面板** → 点击「开始框选区域」
2. 屏幕变暗，按住左键拖出矩形，松开即落下一个区域
3. 重复第 2 步，可落任意多个区域
4. `Enter` 确认，立即进入展示态；`Esc` 放弃本次改动

**框选模式**

| 操作 | 效果 |
|---|---|
| 空白处拖拽 | 新建区域 |
| 区域上拖拽 | 整体移动 |
| 拖拽四角 | 缩放 |
| 右键 / `Del` | 删除光标下的区域 |

**展示态**

覆盖层是鼠标穿透的，点不到也碰不到，所以退出只能靠：

- 全局热键（默认没有，需先在设置 → 交互里指定）
- 托盘图标右键 →「进入 / 退出展示态」
- 托盘图标左键 → 打开主面板

托盘菜单另提供：开始框选 / 暂停显示 / 设置 / 开机自启 / 清空全部区域 / 退出。

## 版本号管理

`VERSION` 是**全项目唯一来源**：`ScreenStreaks.csproj` 直接读取它生成
`Version` / `AssemblyVersion` / `FileVersion`，改完立即生效，不存在「忘记同步」的可能。
内容必须严格是 `x.y.z` 三段数字（四段格式自动补 `.0`）。

改版本号就是直接编辑根目录的 `VERSION` 文件：

```
1.0.0
```

改完重新构建即可 —— 不需要改动 csproj，也不需要运行任何同步步骤。

## 目录结构

```
ScreenStreaks/
├─ VERSION                    # 唯一版本号来源
├─ windows/ScreenStreaks/     # WPF 应用
│  ├─ Infrastructure/  Interop/  Models/
│  ├─ Rendering/              # 缺陷生成 + WPF 视觉层
│  ├─ Services/               # 主控、配置、自启、热键、托盘、显示器
│  └─ Views/                  # 主面板、设置、框选层、展示层、主题
└─ android/                   # Android 版占位（后续开发）
```

界面与 exe 的图标 `app.ico` / `app.png` 是入库资源，直接参与构建，无需额外生成步骤。

## 配置

`%APPDATA%\ScreenStreaks\config.json`（首次运行自动生成），日志在同目录 `logs\app.log`（自动轮转）。

主要项：

| 项 | 默认 | 说明 |
|---|---|---|
| `seed` | 20260915 | 全局随机种子，相同种子结果一致 |
| `density` | 25 | 每 1000 物理像素期望出现的缺陷列数（0.5 ~ 60） |
| `intensity` | 1.0 | 发光强度（0.3 ~ 1.0），调低即模拟「半失效」的列 |
| `blinkDepth` | 0.75 | 闪烁熄灭深度（0 ~ 0.95） |
| `maxPerRegion` | 120 | 单区域缺陷上限，纯性能护栏 |
| `enableFlicker` | **false** | 闪烁线**默认关闭**，需要时在设置里打开 |
| `regions` | — | 区域列表，以**物理像素 + 显示器标识**存储 |

兜底：文件损坏会自动备份为 `config.corrupt-<时间戳>.json` 并回退默认值；数值越界会被夹到
安全区间；区域坐标若因分辨率变化跑到屏幕外，会自动贴回显示器内。

## 开机自启

通过主面板或设置面板的开关控制。实现为注册表
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `ScreenStreaks` 项，
**不需要管理员权限**，只对当前用户生效，携带 `--silent` 参数开机静默进入展示态。

手动卸载：

```powershell
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "ScreenStreaks"
```

> 移动了 exe 的位置会让自启项失效。程序能识别这种「指向旧路径」的状态并把开关显示为关闭，
> 重新打开一次即可。

## 已知限制

- **仅 Windows**：覆盖层依赖 WPF 分层窗口与 Win32 扩展样式，没有跨平台实现
- **无法覆盖 Windows 系统 UI**：开始菜单、搜索、操作中心、音量/网络面板、Alt+Tab 切换器位于
  比 `HWND_TOPMOST` 更高的「窗口带」，普通进程压不过去 —— 这是系统为保证自身 UI 始终可达做的
  设计，签名也解决不了。桌面右键菜单、任务栏菜单、普通窗口、其它 topmost 程序都不受影响
- 展示态没有「点击退出」通道，必须用热键或托盘 —— 鼠标穿透的必然结果
- 区域存储在显示器坐标系内，改分辨率后可能需要在框选模式里微调

## 常见问题

**线条太少？**
数量 ≈ `区域宽度(物理像素) ÷ 1000 × 密度`。注意单位是**物理像素** —— 4K@150% 下一块看起来
「占屏宽三分之一」的区域其实约 1280 物理像素，也就是十几条。想更密就在设置里调高**线条密度**。
框选时区域标签、拖拽预览框和确认后的托盘提示都会显示实际条数。

**为什么默认没有热键？**
全局热键会独占一个组合键，很容易和别的软件打架。与其硬塞一个默认值、让你去排查「为什么某个
快捷键突然不灵了」，不如默认什么都不占用。托盘菜单是始终可用的退路。

**右键菜单会盖住线条？**
已处理。覆盖层声明了 `WS_EX_NOACTIVATE`（不抢焦点，这是鼠标穿透的前提），所以永远不会被激活；
而右键菜单这类弹出窗口会插到置顶带最前面。现在靠 `EVENT_OBJECT_SHOW` /
`EVENT_SYSTEM_FOREGROUND` 两个 WinEvent 钩子即时顶回去（实测同一毫秒内），另有 300ms
轮询兜底防漏事件。

**为什么线条是实心不透明的，半透明不是更自然吗？**
反过来才对。真实的坏列是**自己发光**的，不是叠加层：做成半透明的话，深色背景上线条会发灰、
有窗口时还会透出窗口内容，一眼就假。所以不透明度恒为 100%，只有**颜色亮度**可调。

**占用资源高吗？**
闪烁动画由 WPF 合成线程驱动，UI 线程不参与逐帧计算。不放心可调低缺陷密度或单区域上限。

**运行没反应但进程还在？**
开的是单实例，重复启动只会把已运行的实例唤到前台。日志见
`%APPDATA%\ScreenStreaks\logs\app.log`。

## Roadmap

**Windows** —— 已完成：多区域框选与编辑、四类缺陷 + 确定性随机、多显示器 DPI、全局热键 /
开机自启 / 托盘常驻、VERSION 单源版本管理与一键构建。

待办：缺陷预设（「轻微 / 严重」一键套用）、按显示器保存多套区域方案、自启延迟启动。

**Android**（`android/` 目录，后续开发）—— 计划用 **Kotlin + Compose** 原生实现，与 Windows 版
保持同一套交互语义：悬浮窗用 `TYPE_APPLICATION_OVERLAY` +
`FLAG_NOT_TOUCHABLE | FLAG_NOT_FOCUSABLE` 实现同样的「可见但穿透」；区域数据结构与
`config.json` 对齐以便将来配置互通；需要 `SYSTEM_ALERT_WINDOW` 权限；Android 没有全局热键
概念，改用常驻通知栏的动作按钮切换展示态。详见 [`android/README.md`](android/README.md)。

## 许可

本项目采用 **GNU General Public License v3.0**（GPL-3.0），完整条款见 [`LICENSE`](LICENSE)。

```
Copyright (C) 2026 hcllmsx
```

你可以自由地使用、研究、修改和再分发本程序，但**分发时（无论源码还是二进制）必须同样
以 GPL-3.0 授权并提供完整对应源码**，且保留版权声明。本程序**不提供任何担保**。
