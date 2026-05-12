# VlessClient — WinUI 3 + VLESS + hev-socks5-tunnel

Windows 平台 VLESS 代理客户端，支持：
- **VLESS over WebSocket** 代理（SOCKS5 + HTTP 双协议）
- **TUN 全局模式**（通过 hev-socks5-tunnel）
- **系统托盘**（最小化到托盘、右键菜单）
- **开机自启动**
- **配置导入/导出**（vless:// URI 格式）

---

## 项目结构

```
VlessClient/
├── Core/
│   ├── VlessProxyService.cs      # VLESS WebSocket 代理核心（你的原始逻辑）
│   └── TunService.cs             # hev-socks5-tunnel 封装
├── Models/
│   ├── VlessConfig.cs            # 配置模型 + vless:// URI 解析
│   └── AppSettings.cs            # 持久化设置
├── Services/
│   ├── ProxyManager.cs           # 代理 + TUN 统一管理
│   ├── SettingsService.cs        # JSON 配置持久化
│   └── AutoStartService.cs       # 开机自启（注册表）
├── App.xaml / App.xaml.cs        # 应用入口 + 系统托盘
├── MainWindow.xaml               # 主界面 XAML
└── MainWindow.xaml.cs            # 主界面逻辑
```

---

## 环境要求

- Windows 10 1903+ / Windows 11
- .NET 8 SDK
- Visual Studio 2022 (带 WinUI / Windows App SDK 工作负载)
- 以**管理员身份运行**（TUN 模式需要）

---

## 安装依赖

在 `VlessClient.csproj` 中已声明：
```
Microsoft.WindowsAppSDK 1.5
WinUIEx 2.3.4
H.NotifyIcon.WinUI 2.1.3
```

---

## 放置原生库

### hev-socks5-tunnel

将你编译好的库文件放到对应目录：

```
VlessClient/
└── runtimes/
    ├── win-x64/
    │   └── native/
    │       ├── hev-socks5-tunnel.dll   ← 从 .so 转换/编译的 Windows DLL
    │       └── wintun.dll              ← 从 wintun.zip 解压
    ├── win-x86/
    │   └── native/
    │       ├── hev-socks5-tunnel.dll
    │       └── wintun.dll
    └── win-arm64/
        └── native/
            ├── hev-socks5-tunnel.dll
            └── wintun.dll
```

> **重要**：你的 `.so` 文件是 Linux/Android 格式，Windows 需要编译 `.dll` 版本。
> hev-socks5-tunnel 官方支持 Windows，参见：
> https://github.com/heiher/hev-socks5-tunnel
>
> **wintun.dll** 下载：https://www.wintun.net/

### 托盘图标

```
VlessClient/Assets/tray.ico   ← 放置你的图标文件
```

---

## 编译运行

```powershell
# 以管理员身份打开 PowerShell
cd VlessClient
dotnet restore
dotnet build -c Release

# 或用 Visual Studio 2022 打开 .sln 后 F5
```

---

## hev-socks5-tunnel Windows 编译说明

如果你只有 `.so` 文件（Linux/Android），需要在 Windows 重新编译：

```bash
# 在 MSYS2 / MinGW64 中
git clone https://github.com/heiher/hev-socks5-tunnel
cd hev-socks5-tunnel
git submodule update --init
make CROSS_PREFIX=x86_64-w64-mingw32- TARGET=windows
```

编译产物 `hev-socks5-tunnel.dll` 复制到上述 runtimes 目录。

---

## vless:// URI 格式

导入示例：
```
vless://55a95ae1-4ae8-4461-8484-457279821b40@broad.aicms.dpdns.org:443?encryption=none&security=tls&sni=broad.aicms.dpdns.org&type=ws&host=broad.aicms.dpdns.org&path=/?ed=2560#broad.aicms.dpdns.org
```

---

## TUN 全局模式原理

```
所有流量 → TUN 网卡 (198.18.0.1/15)
         → hev-socks5-tunnel
         → SOCKS5 代理 (127.0.0.1:10808)
         → VlessProxyService
         → VLESS over WebSocket
         → 远端服务器
```

路由设置（自动）：
- `0.0.0.0/1` 和 `128.0.0.0/1` → TUN（覆盖默认路由）
- 服务器 IP → 原网关（防止回环）

---

## 已知限制 / 待办

- [ ] UDP 透明代理（hev-socks5-tunnel 支持，需配置）
- [ ] 分应用代理（PAC 模式）
- [ ] 延迟测试
- [ ] 流量统计图表
- [ ] 多语言

---

## 核心代码来源

`Core/VlessProxyService.cs` 直接基于你上传的 `VlessProxyClient.cs`，
逻辑完全一致：SOCKS5 握手 → early data 收集 → VLESS Header 构建 → WebSocket 中继。
