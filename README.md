# Fluent Zones

<img src="src/FluentZones.UI/Assets/icon-preview.png" width="88" align="right" alt="icon" />

Fluent 风格的 Windows 窗口分区工具：用**全局热键**一键层叠、左右并排、上下堆叠、网格平铺、主窗在左，外加一键还原。后续会加入可拖拽的 zone（分区）概念。

前端是 **WinUI 3**（未打包 / unpackaged），内核是纯 Win32 互操作，不依赖 PowerShell、不需要管理员权限。

```
Ctrl+Alt+1   层叠窗口        Ctrl+Alt+4   网格平铺
Ctrl+Alt+2   左右并排        Ctrl+Alt+5   主窗在左
Ctrl+Alt+3   上下堆叠        Ctrl+Alt+0   还原上一次排列

Ctrl+Alt+Shift+数字  =  同一布局作用于所有显示器
```

## 功能

- **5 种布局 + 还原**：每次排列前记录窗口原位置，`Ctrl+Alt+0` 回退。
- **按最小尺寸智能分配**：先向每个窗口要它自己声明的最小尺寸（`WM_GETMINMAXINFO`），放不下就自动分行 / 分列；再放不下就**弹窗让你选**——最小化最久未使用（Z 序最底层）的几个窗口后重排、改用层叠，或取消。
- **分区模式（zone）**：打开「分区模式」开关（或按 `Ctrl+Alt+Z`）= 只启用工作区能力，**不会立刻铺窗口**；之后选任一布局（卡片或 `Ctrl+Alt+1..5`）才按该布局进入工作区。进入后窗口按分区摆好，**每扇窗套一圈 Win11 原生边框（DWM，画在窗口自己身上，不压内容）**，窗口之间留 8px 间隙，拖间隙即可同时调整两侧窗口（拖到某侧最小尺寸会停住）；工作区开启期间**新出现的窗口会自动加入**（`SetWinEventHook` 监听窗口 SHOW / DESTROY，事件触发重排，不轮询），关掉的窗口自动让位；新窗口最小尺寸塞不进分到的格子时会弹窗询问「最小化最久未使用的 N 个并加入 / 先不加入」；`Ctrl+Alt+0` 退出工作区并整体还原（开关保持开启，下次选布局再次进入）。
- **全局热键**：窗口最小化/不在前台时依然生效；关窗即退出。
- **多显示器**：默认只排列「鼠标所在显示器」上的窗口；按住 Shift 或在下拉框里切换为「所有显示器」，此时每台显示器各自排列自己的窗口。
- **窗口间距**：0–40px，只对并排/网格生效，太挤时自动放弃间距。
- **卡片设置**：网格齿轮调「最多窗口数」（2–6），左右并排齿轮调「每行列数」（3–4，下拉选择，超出自动换行）；网格超出的窗口按 Z 序最小化，`Ctrl+Alt+0` 一起还原。
- **热键可改**：点每行的「修改」，按下新组合即可（Esc 取消）。占用的组合会显示「注册失败」，不会静默失效。
- **启动**：开机启动（写入 `HKCU\...\Run`，`--minimized` 静默启动）+ 管理员模式启动（每次启动请求 UAC，能操作 Windhawk 这类提权窗口；MSIX 版不支持，可随时在设置里关掉）。
- **现代化界面**：Mica 背景、自绘标题栏、跟随系统深浅色、布局缩略图。

## 运行

```bat
build.cmd        :: 编译 Release（需要 .NET 8 SDK）
run.cmd          :: 启动（普通权限）
run-admin.cmd    :: 以管理员身份启动（要操作管理员程序的窗口时用）
```

也可以直接：

```powershell
# 推荐（build.cmd 用的就是这条）
dotnet build src\FluentZones.UI\FluentZones.UI.csproj -c Release -p:Platform=x64

# 或者用解决方案（Visual Studio 里直接打开 FluentZones.sln 也可以）
dotnet build FluentZones.sln -c Release

src\FluentZones.UI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\FluentZones.exe
```

设置保存在 `%LOCALAPPDATA%\FluentZones\settings.json`，删掉即可恢复默认热键。

## 环境要求

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 1809+ / Windows 11（Mica 需要 Win11） |
| .NET | .NET 8 SDK（编译用）；运行时随 .NET 8 桌面运行时 |
| Windows App SDK | 1.8 运行时（框架依赖，本机已安装） |

> 编译前请先退出正在运行的本程序：Windows 会锁定 `exe` / `dll`，否则会报 `MSB3021`。

**不需要 Visual Studio**：WinUI 3 的 XAML 编译器由 `Microsoft.WindowsAppSDK` NuGet 包提供，`dotnet build` 即可完成。`build.cmd` 会优先使用 `%USERPROFILE%\.dotnet\dotnet.exe`（本机安装的用户级 SDK），否则回退到 PATH 里的 `dotnet`。

## 实现要点

```
src/FluentZones.Core/        纯 C#，无 UI 依赖
  NativeMethods.cs            Win32 声明（EnumWindows / SetWindowPos / RegisterHotKey…）
  WindowCollector.cs          挑选"值得排列"的顶层窗口
  LayoutService.cs            布局几何 + 快照/还原
  HotkeyManager.cs            全局热键（独立线程 + 隐藏窗口 + 消息循环）
  HotkeyCombo.cs              热键文本解析 / 格式化
  Settings.cs                 JSON 设置 + 开机启动
src/FluentZones.UI/          WinUI 3 前端
  App.xaml / MainWindow.xaml  界面与样式
```

窗口筛选规则（避免把系统窗口卷进来）：

- 必须可见、未最小化、有标题；
- 排除 `WS_EX_TOOLWINDOW` / `WS_EX_NOACTIVATE`（工具窗、悬浮层）；
- 通过 DWM `DWMWA_CLOAKED` 判断，跳过其它虚拟桌面的窗口和被挂起的 UWP 窗口；
- 排除桌面、任务栏等已知系统类名，以及本程序自己的窗口。

### 管理员权限的窗口（例：Windhawk）

有些程序以管理员身份运行。UIPI 会拦掉普通权限进程对它们窗口的全部操作：`SetWindowPos` 返回 `False` 且不报错，`WM_GETMINMAXINFO` 也读不到 —— 现象就是"怎么排都不动"，日志里那一行的 `ok=False` 就是它。

处理方式：

- 这些窗口会被识别（进程令牌读不到 / TokenIsElevated=1）并**从布局中排除**，不再连累其他窗口；
- 状态栏写「无法移动：Windhawk（管理员权限）」，并弹窗询问是否以管理员身份重启（重启后自动重试这次排列）；
- 也可以直接用 `run-admin.cmd` 启动。

注意：开机启动写的是 `HKCU\...\Run`，属于普通权限；如果希望开机后也能操作管理员程序的窗口，需要用「任务计划程序」建一个"使用最高权限运行"的任务。

排列细节：

- 用 `SetWindowPos(..., SWP_NOZORDER | SWP_NOACTIVATE)`，**不改变 Z 序、不抢焦点**。
- 按 **DWM 可见边框**（`DWMWA_EXTENDED_FRAME_BOUNDS`）对齐而不是窗口矩形：普通窗口外面有约 9px 的透明阴影边距，自绘边框的应用（如 Windhawk）没有，按矩形对齐就会差几像素。
- 分配算法先用每个窗口声明的最小尺寸做下限，再按"舒适宽度 400 / 舒适高度 320 有效像素"的分配下限分剩余空间，总量精确等于分区可用区域，避免切出一排细条。
- 分区可用区域在显示器工作区四边各内缩 4px（留给窗口边框），一次性布局与分区模式使用同一份区域。
- 左右并排：按舒适宽度找"每行最多放几个"、放不下自动换行，每行列数还能在卡片齿轮里设成 3 或 4（默认 4）；上下堆叠为单列纵向堆叠；`Grid` 用马赛克构图（主窗 + 右侧列 + 底部行），几何与分区模式共用 `ZoneWorkspace`，格数上限由卡片齿轮设置（2–6）。整组窗口的最小尺寸实在排不下时：网格先询问（换主窗 / 最小化 N 个 / 改用层叠），其它布局按 Z 序从最底层开始少排几个、放不下的窗口**保持原位**（状态栏注明）。
- 排完会回读实际尺寸：被系统按最小尺寸撑大的窗口，会用实测值重排一次。
- 排完 600ms 后再校验一次：有些应用（Chromium/Electron 系）收到尺寸变化后会自己微调窗口，偏移超过 2px 的会被放回格子。
- 放不下时不会擅自降级：核心返回"最少需要最小化几个（从 Z 序最底层数起）"，界面弹窗询问；选最小化的话，`Ctrl+Alt+0` 还原时会把它们取消最小化。
- 分区模式用一棵切分树描述布局（`ZoneNode`/`ZoneWorkspace`），拖分隔线就是改某个节点的比例，再按 DWM 可见边框重摆两侧窗口；覆盖层是一个置顶、不激活的分层窗口，非分隔线区域鼠标直接穿透。
- 取消最大化用 `SetWindowPlacement(SW_SHOWNOACTIVATE)`（不抢焦点），并在排完后把 `DWMWA_WINDOW_CORNER_PREFERENCE` 设回圆角 —— 避免 Win11 把"最大化时的方角"留在普通窗口上；应用自己声明方角（`DONOTROUND`）的不动。

## 打包与分发

```bat
package-all.cmd
```

一次生成三件产物（都在 `dist\`）：

- `fluent-zones-v0.1.0-win-x64.zip`：便携版，自包含 .NET 8 + Windows App SDK，解压双击 `FluentZones.exe` 即可运行；
- `FluentZones-0.1.0-Setup-x64.exe`：EXE 安装包（装到 `%LOCALAPPDATA%\Programs`，不需要管理员），开始菜单带「Fluent Zones（管理员）」快捷方式；
- `FluentZones-0.1.0-x64.msix`：MSIX，用于 Microsoft Store 上架（¥12 赞助费）或 GitHub 侧载（需自行签名）。

两条渠道的分工、商店上架步骤、MSIX 签名与管理员模式说明见 [发布与分发](docs/RELEASING.md)。

> MSIX 打包应用始终以普通令牌运行，无法提权，Windhawk 这类高完整性窗口就动不了；
> 所以需要管理员能力时用 EXE 安装包或便携版。`package.cmd` 保留为只出便携版的旧入口。

## 文档

- [发布与分发](docs/RELEASING.md)：商店 / GitHub 两条渠道、MSIX 签名、管理员模式与许可证提醒
- [路线图](docs/ROADMAP.md)：版本规划（v0.1 布局 → v0.2 分区工作区 → v0.3 工作区条 → v0.4 替代任务栏的窗口切换）
- [分区工作区规格](docs/ZONES.md)：槽位、队列、踢出、拖拽分隔线的完整行为与验收清单
- [架构](docs/ARCHITECTURE.md)：模块分层、线程模型、坐标约定、诊断方式
- [首次使用引导](docs/ONBOARDING.md)：引导页步骤与写入的偏好（v0.3）
- [布局卡片设置](docs/LAYOUT_SETTINGS.md)：卡片齿轮、上限与参数、持久化（齿轮与网格格数已实现）
- [智能网格](docs/GRID.md)：一个主窗 + 环绕小窗的马赛克构图与上限语义（已实现，主窗占比/位置设置未做）
- [滚动工作区构想](docs/SCROLLING.md)：左右并排 → 无限循环的横向窗口带（v0.5）
- [工作区即桌面](docs/DESKTOPS.md)：接管 Alt+Tab / Win+Tab、自绘任务视图、虚拟桌面替代（v0.6）

> 当前状态：v0.1 已发布；v0.2 分区工作区按上述规格重写中。

## 已知限制

- 最小化的窗口不参与排列，也不会被还原。
- 每次排列都会写一份过程日志到 `%LOCALAPPDATA%\FluentZones\layout.log`（前台窗口、参与/排除的窗口、`SetWindowPos` 的请求值与实际值），排查"某个窗口为什么没动"时看它。
- 布局放不下时会弹窗询问：最小化最久未使用的 N 个窗口后重排 / 改用层叠 / 取消。
- 快照只保留最近一次，连续切换布局后 `Ctrl+Alt+0` 回到的是上一次排列之前。
- 目标进程假死时，`SetWindowPos` 可能会短暂等待。
- 关闭窗口即退出（热键随进程结束）；要常驻请保持窗口最小化，或用开机启动。

## 许可

[MIT License](LICENSE)。
