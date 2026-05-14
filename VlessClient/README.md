# VlessClient — WinUI 3 + VLESS + hev-socks5-tunnel

Windows 平台 VLESS 代理客户端，支持：
- **VLESS over WebSocket** 代理（单端口同时支持 SOCKS5 和 HTTP）
- **TUN 全局模式**（通过 hev-socks5-tunnel 接管所有流量）
- **系统托盘**（最小化到托盘、右键菜单控制）
- **开机自启动**（注册表，支持 `--minimized` 静默启动）
- **配置导入/导出**（vless:// URI 格式，支持批量导入）

---

## 环境要求

- Windows 10 1903+ / Windows 11
- .NET 8 SDK
- 以**管理员身份运行**（TUN 模式需要创建虚拟网卡）

---

## 项目结构

```
VlessClient/
├── Core/
│   ├── VlessProxyService.cs      # VLESS over WebSocket 代理核心
│   └── TunService.cs             # hev-socks5-tunnel 进程管理
├── Models/
│   ├── VlessConfig.cs            # 配置模型 + vless:// URI 解析/导出
│   └── AppSettings.cs            # 持久化设置模型
├── Services/
│   ├── ProxyManager.cs           # 代理 + TUN 统一调度（状态机）
│   ├── SettingsService.cs        # JSON 配置读写（%AppData%）
│   └── AutoStartService.cs       # 开机自启（注册表操作）
├── App.xaml / App.xaml.cs        # 应用入口 + 系统托盘 + 全局异常处理
├── MainWindow.xaml               # 主界面（WinUI 3）
└── MainWindow.xaml.cs            # 主界面逻辑
```

---

## 依赖包

在 `VlessClient.csproj` 中声明：

| 包 | 版本 | 用途 |
|---|---|---|
| Microsoft.WindowsAppSDK | 1.5 | WinUI 3 运行时 |
| WinUIEx | 2.3.4 | WinUI 窗口扩展 |
| H.NotifyIcon.WinUI | 2.1.3 | 系统托盘图标 |

---

## 原生文件

将 `hev-socks5-tunnel.exe` 及相关 DLL 放到对应目录：

```
VlessClient/runtimes/
├── win-x64/
│   ├── hev-socks5-tunnel.exe
│   ├── wintun.dll
│   └── msys-2.0.dll
├── win-x86/
│   └── ...
└── win-arm64/
    └── ...
```

- `hev-socks5-tunnel.exe` — 源码：https://github.com/heiher/hev-socks5-tunnel
- `wintun.dll` — 下载：https://www.wintun.net/

托盘图标放置：

```
VlessClient/Assets/tray.ico
```

---

## 编译运行

```powershell
# 自包含部署必须指定平台架构

# Debug
dotnet build -c Debug -p:Platform=x64

# Release
dotnet build -c Release -p:Platform=x64

# 直接运行
dotnet run -c Debug -p:Platform=x64
```

项目无 .sln 文件，直接编译 .csproj 即可。


---

## TUN 全局模式原理

```
所有流量 → TUN 虚拟网卡 (198.18.0.1/15)
         → hev-socks5-tunnel
         → SOCKS5 (127.0.0.1:本地端口)
         → VlessProxyService
         → VLESS over WebSocket
         → 远端服务器
```

---

## vless:// URI 格式

导入示例：
```
vless://uuid@server:443?encryption=none&security=tls&sni=xxx&type=ws&host=xxx&path=/?ed=2560#名称
```

支持批量导入，每行一个。

---

## 注意事项

- 应用以**自包含**方式部署（`WindowsAppSDKSelfContained=true`），无需额外安装 Windows App SDK 运行时
- 始终以管理员权限运行（`app.manifest` 中声明）
- 配置保存在 `%AppData%/VlessClient/settings.json`
- 崩溃日志写入桌面 `VlessClient_crash.log`
- 关闭窗口默认退出；开启「最小化到托盘」后关闭窗口仅隐藏
