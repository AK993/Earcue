# ADR-009: 静音自动应答与字幕卡个性化

## 背景

M3 的疑问句规则版（`QuestionDetector`）存在一个产品级缺陷：它只会响应带疑问词/问号的句子。真实会议里大量需要接话的内容不是疑问句，例如：

- “让我自我介绍一下”
- “你这边负责什么”
- “你先说说看法”
- “下一个议题到你”

这些句子不会命中疑问词，导致卡片完全不出现。继续加关键词只能缓解，不能根治，而且会越来越难维护。

同时，原来的浮层固定右下角、字体和字号写死在 XAML 里，用户无法按自己的屏幕/视力/会议软件布局调整。

## 决策

### 1. 取消“疑问句”硬门槛

- 所有 ASR final 句都写入 `DialogueContextBuffer`（最近 90 秒）。
- 不再调用 `QuestionDetector` 决定是否回答；识别到内容就进入“待应答”状态。
- `QuestionDetector` / `TriggerCooldownManager` 保留在 Brain 库中，但不再位于生产链路。

### 2. 静音超时触发

新增 `autoAnswer` 配置：

```json
"autoAnswer": {
  "enabled": true,
  "silenceMs": 2000,
  "minChars": 6,
  "cooldownMs": 3000
}
```

触发条件（250ms 轮询）：

1. 正在采集、自动应答开启；
2. 距离最后一次 ASR 文本 ≥ `silenceMs`（默认 2 秒，ASR 端点本身的 0.6s 静音另计）；
3. 上次应答后新增字数 ≥ `minChars`，避免“嗯/好”触发；
4. 距离上次应答 ≥ `cooldownMs`；
5. 当前没有正在流式生成的回答。

触发后把最近 90 秒的**全部对话**（带 `[对方]` 标注）发给 LLM。

### 3. Prompt 增加自动应答模式

`Prompt.Mode = PromptMode.AutoResponse` 时，系统提示词明确说明：

- 对方不一定在提问，也可能是在点名让我发言、让我自我介绍、征求意见或等我接话；
- 仍然只输出第一人称、口语化的回应，把必要信息说完整；不再硬性限制 40 字；
- 如果只是陈述且不需要我说话，给一句最简短的确认/追问，不编造事实。

这样“让我自我介绍一下”会得到类似“好的，我先简单介绍一下我负责的研发工作”的接话，而不是被过滤掉。

### 4. 防止重复/刷屏

- `_pendingChars` 在每次应答后清零；
- `_isAnswering` 期间不重复触发；
- 停止采集会取消正在进行的流式请求；
- 没有 LLM Key 时自动应答只提示一次，不弹模态框。

### 5. 字幕卡个性化

`OverlayOptions` 新增：

```json
"position": "BottomLeft",
"marginX": 24,
"marginY": 24,
"fontFamily": "Microsoft YaHei UI",
"questionFontSize": 13,
"answerFontSize": 15,
"autoHide": false,
"autoFadeoutSeconds": 20
```

- 默认 **左下角**；
- 支持左下/右下/左上/右上四角 + 水平/垂直边距；
- 字体从系统已安装字体中选择；
- 问题字号、答案字号分别可调；
- `opacity` 控制整体不透明度；
- **默认不自动隐藏**：卡片保留到下一次回答替换，避免用户还没看完就消失；
- 需要定时隐藏时勾选 `autoHide` 并选择等待秒数；
- 流式输出每个分片都会重置自动隐藏计时，防止回答写到一半淡出；
- 每次显示卡片会重新声明 topmost，避免被其他置顶窗口盖住；
- 设置页保存后通过 `OverlayManager.UpdateOptions` 立即生效，不需要重启。

由于浮层使用 `WDA_EXCLUDEFROMCAPTURE`，屏幕截图/录屏工具默认看不到它（这正是防共享泄露的目的）；调试外观时需临时把 `overlay.excludeFromCapture` 设为 `false`。

## 备选方案

- **继续扩疑问词表**：无法覆盖“让我自我介绍”这类祈使/陈述式接话；否决。
- **每次 ASR final 都立刻请求 LLM**：请求量太大、答案碎片化；改用静音合并。
- **固定右下角**：与用户会议软件布局冲突，且不符合本次需求；改左下 + 可配置。
- **只提供配置文件修改**：对普通用户不可用；增加设置页控件与实时预览。

## 后果

- **LLM 调用量上升**：每次静音都可能触发一次请求；`cooldownMs` 与 `minChars` 用于控制成本，用户可关闭自动应答。
- **可能答非所问**：没有疑问句门槛后，模型会对陈述句也给出接话；Prompt 要求“不需要说话时给最简确认/追问”，但仍需真实会议调优。
- **上下文窗口**：目前只发送最近 90 秒；超长会议需要继续压缩/摘要策略。
- **答案长度**：不再限制字数；卡片最大显示约 2/3 屏，字体放大后卡片会向上生长（左下角锚定）。
- **设置页较长**：模型设置与字幕卡设置在同一滚动页；小屏需要滚动。

## 涉及代码

- `src/Earcue.Core/Configuration/EarcueConfig.cs`：`AutoAnswerOptions`、`OverlayOptions` 扩展。
- `src/Earcue.Core/Configuration/OverlayPositionPresets.cs`：四角预设。
- `src/Earcue.Core/Models/Prompt.cs`：`PromptMode.AutoResponse`。
- `src/Earcue.App/MainWindow.xaml.cs`：静音轮询、自动应答、流式取消。
- `src/Earcue.Overlay/OverlayWindow.xaml.cs`：左下定位、字体/字号/透明度应用。
- `src/Earcue.App/SettingsWindow.xaml{,.cs}`：字幕卡与自动应答设置、步进按钮、预览。
