# DynamicIsland 🏝️

Windows 上的灵动岛 —— 屏幕顶部居中的浮动胶囊，集成系统状态、媒体控制、瞬时提醒。

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4) ![WPF](https://img.shields.io/badge/WPF-Windows-0078D6) ![License](https://img.shields.io/badge/license-MIT-green)

---

## 功能

### 显示状态

| 状态 | 触发 | 效果 |
|---|---|---|
| **收起** | 默认 | 200×40 半透明胶囊，显示当前优先级最高的信息 |
| **悬停** | 鼠标移入 | 轻微放大 + 边框高亮增亮 |
| **展开** | 点击 | 460×160 组合仪表盘：分页面板 + 系统资源条 |
| **提醒** | 瞬时事件 | 420×40 胶囊弹出 + 心跳动画 + 高光扫过，数秒后收回 |

### 数据源

| 源 | 收起态显示 | 展开态 |
|---|---|---|
| 🕒 **时钟** | 当前时间 | 日期 + 秒 |
| 🎵 **媒体控制** | 歌曲名/艺人 | 封面 + 进度条 + 播放控制 |
| 🖥 **系统资源** | CPU/RAM 占用（阈值触发） | CPU/RAM/GPU/VRAM/NET 仪表盘条 |
| 🔋 **电量变化** | 瞬时提醒 | — |
| 📋 **剪贴板复制** | 瞬时提醒 | — |
| 🔌 **USB 插拔** | 瞬时提醒 | — |
| 📡 **蓝牙连接** | 瞬时提醒 | — |
| 🌐 **网络变化** | 瞬时提醒 | — |
| ⬇ **下载完成** | 瞬时提醒（带「打开」按钮） | — |

### 特性

- **多显示器支持** — 设置窗选择目标显示器，热拔插自动迁移
- **全屏抑制** — 全屏应用运行时自动隐藏，不遮挡画面
- **深色/浅色主题** — 跟随 Windows 主题自动切换
- **跑马灯** — 长标题自动横向滚动
- **提醒统计** — 每种提醒的出现次数，跨重启持久化
- **系统通知** — 瞬时提醒同时推送到 Windows 通知中心（可选）
- **托盘图标** — 备用入口（设置 / 测试 / 退出）

---

## 截图

*(稍后补充)*

---

## 系统要求

- Windows 10 (19041+) / Windows 11
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（开发）
- 音频会话需要 SMTC 兼容的播放器（网易云音乐 / B 站 / Spotify 等）

## 快速开始

```bash
# 克隆
git clone https://github.com/YOUR_USERNAME/DynamicIsland.git
cd DynamicIsland

# 构建
dotnet build

# 运行
dotnet run --project DynamicIsland
```

首次运行后，在任务栏托盘图标右键 →「设置」可配置目标显示器、显隐项、阈值等。

## 项目结构

```
DynamicIsland/
├── MainWindow.xaml(.cs)    # 主窗口：状态机 + 动画 + 交互
├── IslandDashboard.xaml(.cs) # 展开态组合仪表盘
├── MarqueeText.cs           # 跑马灯文本控件
├── TrayIcon.cs              # 系统托盘
├── AutoStart.cs             # 开机自启管理
├── DisplaySettings.cs       # 设置模型（单例）
├── MonitorInfo.cs           # 显示器信息
├── FullScreenDetector.cs    # 全屏检测
│
├── Island/                  # 核心抽象
│   ├── IIslandSource.cs     # 数据源接口
│   ├── IIslandPanel.cs      # 展开面板接口
│   ├── IIslandAlert.cs      # 提醒模型
│   ├── IslandHost.cs        # 源仲裁
│   ├── AlertHost.cs         # 提醒队列
│   ├── AlertStats.cs        # 提醒统计
│   └── SystemNotifier.cs    # Windows 通知推送
│
├── Sources/                 # 数据源实现
│   ├── ClockSource.cs
│   ├── MediaSource.cs
│   ├── SystemResourceSource.cs
│   ├── MediaPanel.cs
│   ├── NotificationPanel.cs
│   ├── StatsPanel.cs
│   └── ...
│
├── Alerts/                  # 提醒源实现
│   ├── BatteryAlertSource.cs
│   ├── ClipboardAlertSource.cs
│   ├── UsbAlertSource.cs
│   ├── BluetoothAlertSource.cs
│   ├── NetworkAlertSource.cs
│   ├── DownloadAlertSource.cs
│   ├── AlertView.xaml(.cs)
│   └── NotificationListView.xaml(.cs)
│
└── SettingsWindow.xaml(.cs) # 设置窗
```

## 技术架构

- **WPF** (.NET 10, Windows TFM `10.0.19041.0`)
- **Window 层**：`AllowsTransparency=True` 无边框窗口，贴顶居中
- **数据流**：`IIslandSource` → `IslandHost`（按优先级仲裁）→ `MainWindow`（渲染）
- **提醒流**：`AlertSource` → `AlertHost`（优先级队列）→ `MainWindow`（抢占式展示）
- **展开态**：`IslandDashboard` 分页容器，滚轮翻页 + 底部系统资源条固定
- **分辨率适配**：按屏幕高度等比缩放 `[0.7, 2.0]`

## 构建

```bash
dotnet build
dotnet publish -c Release -o publish
```

## 许可证

MIT
