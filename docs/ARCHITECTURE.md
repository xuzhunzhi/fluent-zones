# 架构

## 分层

```
src/FluentZones.Core/         纯 C#，无 UI 依赖，可单测
  NativeMethods.cs            Win32 声明
  WindowCollector.cs          窗口枚举与筛选、最小尺寸、权限判断
  LayoutService.cs            一次性布局的外层策略（格数上限、主窗槽位、放不下提示）、摆放与快照/还原
  LayoutNames.cs              布局名称、热键动作解析
  HotkeyManager.cs            全局热键（独立线程 + 隐藏窗口 + 消息循环）
  HotkeyCombo.cs              热键解析/格式化
  Elevation.cs                进程完整性级别判断（UIPI）
  Settings.cs                 JSON 设置、开机启动
  LayoutLog.cs                过程日志
  Zones/                      v0.2 分区工作区（重写中）
    ZoneNode.cs               切分树：叶子=窗口，节点=方向+比例
    ZoneWorkspace.cs          切分树 → 矩形；拖拽改比例
    ZoneDragHook.cs           低层鼠标钩子：拖分隔线改比例
    ZoneSession.cs            工作区状态机：进入/退出、窗口跟踪、边框、规划（UI 只做胶水）
    ZoneOverlay.cs            置顶分层覆盖层：画分隔线、命中、拖拽

src/FluentZones.UI/           WinUI 3 前端（未打包）
  App.xaml(.cs)               应用入口、单实例启动参数、崩溃日志
  MainWindow.xaml(.cs)        布局卡片、选项、热键表、状态栏、分区模式入口
```

## 线程模型

| 线程 | 职责 | 注意 |
|---|---|---|
| UI 线程（WinUI） | 界面、热键回调分发、布局执行 | `DispatcherQueue`，所有 UI 变更必须回到这里 |
| 热键线程 | 隐藏窗口 + `RegisterHotKey` + 消息循环 | 注册/注销必须在该线程执行（跨线程会失败） |
| 覆盖层线程 | 分层窗口、绘制、鼠标拖拽 | 拖拽回调通过 `DispatcherQueue` 回 UI 线程 |
| 窗口跟踪线程 | `SetWinEventHook`（v0.2 新增） | 事件防抖，只处理工作区所在显示器 |

## 坐标与 DPI

- 全部使用**物理像素**；进程为 Per-Monitor V2（`app.manifest`）；
- 对齐基准是 **DWM 可见边框**（`DWMWA_EXTENDED_FRAME_BOUNDS`），窗口矩形需按阴影边距补偿后再 `SetWindowPos`；
- 覆盖层绘制/命中要区分**屏幕坐标**（槽位矩形）与**客户区坐标**（`WM_PAINT`、鼠标消息）。

## 关键状态

- `LayoutService._snapshot`：上一次排列前的窗口矩形（`Ctrl+Alt+0` 用）；
- `LayoutService._lastPlan`：最近一次请求的目标矩形（600ms 后校验偏移用）；
- `ZoneWorkspace.Root`：切分树，拖拽只改 `Ratio`；
- 设置：`%LOCALAPPDATA%\FluentZones\settings.json`。

## v0.2 Zones 重写设计

现有 `ZoneNode`/`ZoneWorkspace` 只解决"几何"，缺"槽位归属 + 队列 + 窗口跟踪"，按 `docs/ZONES.md` 重写为：

```
ZoneSlot        { 区域, 当前窗口, 队列 }        ← 可查询、可序列化
ZoneWorkspace   { 布局, 槽位[], 切分树 }        ← 几何 + 归属
ZoneController  { 进入/退出、入槽、踢出、轮换 }  ← 事件编排
WinEventWatcher { SHOW / DESTROY / MINIMIZE / FOREGROUND }
ZoneOverlay     分层窗口：画线 + 命中 + 拖拽
```

> `ZoneController` 的落地形态是 `src/FluentZones.Core/Zones/ZoneSession.cs`：切分树、窗口句柄、显示器、布局类型、边框、拖拽钩子与窗口监听都归它管；`MainWindow` 只注入窗口枚举/摆放（`LayoutService`）、状态栏、对话框、卡片高亮与快照。

一次性布局也复用同一份几何：`LayoutService` 的左右并排 / 上下堆叠 / 网格 / 主窗在左都走 `ZoneWorkspace.Create` / `PlanGrid` + `FitToMinimumSizes()`，区域统一用 `ZoneWorkspace.InsetArea`（四边内缩 `ZoneMargin=4`）。因此同一组窗口在“进工作区前 / 进工作区后”的矩形逐像素一致。

事件流：

```
新窗口 SHOW ──► 过滤 ──► 选槽位 ──► 踢出最久未激活者（层叠到角落）
                                   └─► 重排布局 ──► 覆盖层刷新
Ctrl+Alt+Tab ──► 槽位队列轮换 ──► 置顶/换主槽 ──► 重排 ──► 刷新
拖拽分隔线   ──► 改 Ratio（钳制最小尺寸）──► 两侧重排 ──► 重绘
```

## 诊断

- `%LOCALAPPDATA%\FluentZones\layout.log`：每次排列/进入/退出/踢出/轮换的过程日志（含 `SetWindowPos` 请求值与实际值）；
- `%LOCALAPPDATA%\FluentZones\startup.log`：启动异常堆栈；
- 临时诊断工具（不随仓库发布）：`%TEMP%\opencode\wltest`，支持 `--probe` / `--explain` / `--plan` / `--drag` / `--register-only`。
