# 开发与验证约定

> 目标：改代码时**不打扰用户的桌面**。所有验证必须无头完成。

## 禁止

- 不用 `Start-Process` 打开 HTML、原型、exe 或任何窗口；
- 不启动 GUI 程序（FluentZones.exe、演示页面）；
- 不注入鼠标/键盘（SendInput / mouse_event / SetCursorPos）；
- 不在用户桌面上移动窗口来测试布局。

## 允许 / 推荐

### C# 侧

- `dotnet build`（只编译，不运行）；
- 单元测试 / 控制台工具：逻辑写进 `LayoutMath`、`ZoneWorkspace` 这类纯函数，用测试工程跑；
- 需要读真实窗口信息时只读（`EnumWindows`、`GetWindowRect`），不改动。

### HTML 原型

用 **headless Edge** 渲染截图，不弹窗口：

```powershell
& "$env:ProgramFiles(x86)\Microsoft\Edge\Application\msedge.exe" `
  --headless=new --disable-gpu --hide-scrollbars `
  --window-size=1600,1000 `
  --screenshot="$env:TEMP\demo.png" `
  "file:///C:/Users/XuZhunzhi/Documents/默认项目/docs/demo/index.html"
```

- 截图后直接读图检查布局；
- 需要交互验证时，用无头浏览器的脚本能力（如 Playwright / Puppeteer）而不是桌面自动化；
- 无头截图不显示在用户屏幕上，是安全的。

### 需要用户确认的

- 真实的"拖拽手感""窗口吸附""提权窗口"这类必须真机操作的，**由用户自己试**，我只提供步骤；
- 需要看真实效果时，先问，不擅自启动。

## 提交前检查

1. `dotnet build` 通过（0 warning / 0 error）；
2. 文档与实现一致（有偏差先改文档）；
3. 未运行任何 GUI 程序、未改动用户桌面。
