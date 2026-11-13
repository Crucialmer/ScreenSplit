# ScreenSplit

> 纯绿色便携运行 · 无菜单栏干扰 · Windows 动态平铺分屏工具

[![GitHub Release](https://img.shields.io/github/v/release/Crucialmer/ScreenSplit?style=flat-square)](https://github.com/Crucialmer/ScreenSplit/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](LICENSE)

**ScreenSplit** 是专为 Windows 10 / Windows 11 设计的轻量级、全便携动态平铺窗口管理与自定义分屏工具。它基于 FancyWM 深度改造与优化，去除了所有干扰性的顶部浮动菜单栏，实现了 100% 绿色便携存储，让你的桌面窗口布局井然有序、高效无感。

---

## 🌟 核心特性与改造优化

1. **纯绿色便携（100% Portable）**
   - 不写入系统注册表，不在用户的 `%APPDATA%` 或其他系统路径产生残留。
   - 软件所有配置数据（`Settings.json`）、日志文件（`Logs/`）统一存放在软件自身根目录的 `./Data/` 文件夹中，随拷随走。

2. **纯粹分屏，移除多余菜单栏（Zero Clutter）**
   - 彻底移除了原版的顶部浮动菜单栏与多余的屏幕顶部边距占用（0 额外像素消耗）。
   - 保留最纯粹的屏幕自定义平铺逻辑，完全融入 Windows 原生窗口体验。

3. **三种核心面板模式，支持无限嵌套**
   - **水平面板 (Horizontal)**：窗口左右横向排列。
   - **垂直面板 (Vertical)**：窗口上下纵向堆叠。
   - **堆叠面板 (Stack)**：类似多标签页重叠，适合多窗口快速切换。
   - 支持自由嵌套（如左侧一个大窗口，右侧上下分栏，子栏内再堆叠）。

4. **双通道混合操作（键盘 + 鼠标）**
   - 支持强大的全局快捷键控制窗口移动、交换、分屏类型切换。
   - 同时也原生支持鼠标直接拖拽窗口边框调整大小、拖放重组面板。

5. **智能自适应与低资源消耗**
   - 尊重 Windows 窗口最小尺寸（`WM_GETMINMAXINFO`），不破坏程序显示，遇到对话框等弹窗自动浮动（Float）。
   - 原生 .NET 桌面进程运行，空闲时 CPU 占用 **< 1%**，内存占用轻量。

---

## 🚀 快速上手与快捷键指南

软件运行后会常驻在系统右下角托盘区（如果托盘隐藏，请点击 `^` 箭头查看）。

### 常用默认快捷键

激活热键默认为：`[Shift]` + `[Win]`（可随时在设置中修改）

| 快捷键操作 | 功能说明 |
| :--- | :--- |
| `[Shift] + [Win]` 然后按 `[H]` | 创建水平分屏面板 (Horizontal) |
| `[Shift] + [Win]` 然后按 `[V]` | 创建垂直分屏面板 (Vertical) |
| `[Shift] + [Win]` 然后按 `[S]` | 创建堆叠面板 (Stack) |
| `[Shift] + [Win]` 然后按 `[方向键]` | 将当前活动窗口向指定方向移动/分屏 |
| `[Shift] + [Win]` 然后按 `[F11]` | 全局开启 / 暂停平铺窗口管理 |
| `[Shift] + [Win]` 然后按 `[R]` | 刷新当前工作区布局 |
| `[Shift] + [Win]` 然后按 `[Esc]` | 取消当前的按键序列 |

> 💡 **提示**：按下激活组合键 `[Shift] + [Win]` 后，屏幕中央会自动弹出半透明的快捷键指引，直接按下对应按键即可完成操作。

---

## 📦 下载与运行

前往 [Releases 页面](https://github.com/Crucialmer/ScreenSplit/releases) 下载最新的绿色便携包：

- **下载文件**：`ScreenSplit.zip`
- **运行方法**：
  1. 解压 `ScreenSplit.zip` 到任意英文路径（如 `D:\Tools\ScreenSplit`）。
  2. 双击运行 `ScreenSplit.exe` 即可。
  3. 初次启动将在托盘弹出气泡通知，并在程序目录自动生成 `./Data` 文件夹。

---

## 🛠️ 从源码编译

```powershell
# 1. 克隆本仓库及子模块
git clone --recursive https://github.com/Crucialmer/ScreenSplit.git
cd ScreenSplit

# 2. 编译自包含绿色便携版本
dotnet publish FancyWM/FancyWM.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o dist/ScreenSplit
```

---

## 📄 开源许可与致谢

- 本项目基于 [FancyWM](https://github.com/fancywm/fancywm) 进行了绿色化与纯净分屏定制改造。
- 底层窗口管理基于 [WinMan](https://github.com/fancywm/winman) 及 [WinMan.Windows](https://github.com/fancywm/winman-windows)。
- 本项目遵循 MIT 开源许可协议。
