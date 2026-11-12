# S2 · 覆盖窗口与捕获排除结论

## 结论（一句话）
**能**。采用 WPF 分层透明窗口结合 Win32 `SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE)`（0x00000011）以及 `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，完全实现了置顶显示、绝对点击穿透、输入不抢焦点且在会议软件屏幕共享中不可见。

## 技术细节与实现
- 核心代码：`Earcue.Overlay.Native.NativeMethods` 与 `Earcue.Overlay.OverlayWindow`
- 窗口扩展样式：
  - `WS_EX_LAYERED (0x00080000)`：支持分层透明度
  - `WS_EX_TRANSPARENT (0x00000020)`：鼠标消息穿透，不拦截用户点击与拖拽
  - `WS_EX_TOOLWINDOW (0x00000080)`：从 Alt+Tab 切换列表和任务栏中隐形
  - `WS_EX_NOACTIVATE (0x08000000)`：保证弹窗淡入或更新时不抢夺前台焦点，输入法与键盘焦点不受任何影响
- 防屏幕共享捕获机制：
  - Windows 10 (2004+) 与 Windows 11 原生支持 `WDA_EXCLUDEFROMCAPTURE`（值 0x11）
  - 卡片在本地物理显示器上正常渲染，而在 `BitBlt`、DWM 屏幕捕获及会议软件（腾讯会议/Teams/Zoom/飞书/钉钉）采集流中直接镂空不可见
