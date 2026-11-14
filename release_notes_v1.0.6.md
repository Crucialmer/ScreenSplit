# ScreenSplit v1.0.6 正式版发布

> 纯绿色便携 · Windows 边缘智能吸附分屏与多窗口管理器  
> 纯鼠标直觉交互 · 240Hz 电竞级超流畅动画 · 窗口尺寸智能记忆 · 极致安全稳定

---

### 🎉 版本优化说明
**ScreenSplit v1.0.6** 针对运行时出现的闪退问题进行了全链路深度排查与彻底根治，全面实施线程隔离与全局钩子安全加固，确保在任何复杂多任务场景下永不闪退、坚如磐石！

---

### ✨ 本次更新内容

#### 1. 运行时闪退全链路深度排查与彻底根治
- **闪退根本原因深度剖析与修复**：
  排查发现 v1.0.5 引入托盘外部点击自动收起逻辑时，底层系统级鼠标钩子运行于独立的原生消息泵线程（`LowLevelMouseHookThread`）。由于在钩子回调中直接访问了主 UI 线程创建的 WPF 对象 `m_contextMenu.IsOpen`，导致抛出 `InvalidOperationException`（跨线程调用违规）。在 .NET 10 核心运行时中，后台线程的未捕获异常会直接触发进程异常退出（闪退）；
- **线程安全状态隔离（Volatile Thread Synchronization）**：
  引入线程安全的 `volatile bool m_isContextMenuOpen` 状态标记，底层钩子线程仅执行快速原子判断，严格禁止跨线程直接访问 UI 控件；所有 WPF 上下文状态判断与窗口 Rect 计算均经由 UI 线程 `Dispatcher.BeginInvoke` 异步安全执行；
- **全局系统钩子全链路防御加固（Hook Exception Shield）**：
  全面重构 `LowLevelMouseHook` 与 `LowLevelKeyboardHook`，在系统级回调函数 `HookProc` 以及按键、鼠标事件分发代码外层建立强力异常防线（Exception Shield），即便外部调用发生异常亦能被安全捕获与隔离，杜绝因钩子中断引发的 CLR 崩溃；
- **异步后台任务与未观测异常兜底**：
  加固 DWM 沉降延迟异步任务中的异常保护，并在应用程序全局挂载 `TaskScheduler.UnobservedTaskException` 与 `DispatcherUnhandledException` 双重容灾兜底机制，杜绝后台任务未观测异常外泄。

#### 2. 托盘右键菜单失焦自动收起极致顺畅
- 托盘右键菜单展开后，在桌面空白处、任务栏或其他任意应用程序窗口点击鼠标，托盘菜单均能 0 延迟平滑关闭，无残留无卡顿；
- 窗口焦点切换或键盘取消操作均能自动收起菜单，符合原生 Windows 交互直觉。

#### 3. 全局版本号同步升级
- 程序集版本、关于页面、设置窗口标题栏、启动引导页全面同步升级至 **v1.0.6 正式版**。

---

### 📦 资产下载与校验
- **发行包**：`ScreenSplit.zip` (~8.8 MB)
- **使用说明**：解压至任意目录，双击运行 `ScreenSplit.exe` 即可直接使用！
