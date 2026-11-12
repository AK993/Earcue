# ADR-008: 本地流式 ASR（sherpa-onnx + 双模型）

## 背景

M2 的出口标准是“30 分钟会议音频能持续滚动出识别文本，时间轴不漂”。原方案（ADR-002）已选定 sherpa-onnx 流式 Zipformer，但代码里只有 `MockAsrEngine`，`asr.loopbackModel` / `asr.micModel` 只是配置占位，主窗口采集后没有真正识别。

同时有三个现实约束：

1. 会议场景要求**真流式**、低延迟、离线，不能每句话攒段后跑 Whisper。
2. 模型文件较大（70 MB+），不能塞进源码仓库，也不适合每次构建复制。
3. 环回（对方）与麦克风（我方）需要不同模型：环回重准确率，麦克风重资源占用（ADR-004）。

## 决策

### 1. 运行时与版本

- 使用官方 NuGet 包 **`org.k2fsa.sherpa.onnx` 2.0.1**（managed 绑定 + `org.k2fsa.sherpa.onnx.runtime.win-x64` 原生库）。
- `Earcue.App` 固定 `RuntimeIdentifier=win-x64`、`SelfContained=false`、`AppendRuntimeIdentifierToOutputPath=false`，只输出 Windows x64 原生库（`onnxruntime.dll`、`sherpa-onnx-c-api.dll`），避免把 Android/Linux/macOS 运行时打进安装包。
- 识别完全在进程内 CPU 执行，推理线程数默认 2（`asr.numThreads`），识别期间不联网。

### 2. 模型与安装位置

- 默认模型：**`sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23`**（中文流式 Zipformer，14M 参数，优先 `.int8.onnx`）。
- 可选模型：**`sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20`**（中英混说，约 487 MB，适合英文术语多的会议）。
- 默认安装根目录：`%LOCALAPPDATA%\Earcue\models\{modelId}`，可用 `asr.modelRoot` 覆盖；相对路径以程序目录为基准。
- 模型清单与 SHA256 统一放在 `src/Earcue.Speech/Asr/asr-models.json`，作为**嵌入资源**供程序读取，同时供 `tools/download-asr-models.ps1` 解析，避免两处漂移。
- 下载脚本校验 SHA256 后才解压；解压使用 Windows 自带 `tar.exe`（Win10 1803+ 支持 bz2），失败时给出明确提示。

### 3. 输入、端点与断句

- 输入固定为 **16 kHz / 单声道 / 16-bit PCM**，与 `WasapiLoopbackSource` 的 10 ms 帧完全对齐；`SherpaAsrEngine.Process` 直接同步处理一帧，不额外排队。实测 RTF ≈ 0.023（2 线程），远低于 1.0，不会拖垮采集线程。
- 端点检测启用 `EnableEndpoint=1`：
  - `Rule1MinTrailingSilence = 1.5s`（一直没出字时）
  - `Rule2MinTrailingSilence = segment.silenceMs`（默认 0.6s）
  - `Rule3MinUtteranceLength = segment.maxMs`（默认 10s）
- 因此 **ASR 端点就是断句依据**，主窗口不再把 `TextSegmenter` 放在识别链路里。`TextSegmenter` 仍保留给 Mock/非端点式引擎使用，但当前生产路径不使用它。
- `AsrPartial.Text` 语义统一为“当前句子的累计文本”：草稿事件用于实时字幕（整行替换），`IsFinal=true` 时表示整句结束，直接送 `QuestionDetector` / 上下文缓冲，不再做增量拼接，避免流式模型的回改造成重复字。

### 4. 双流模型

- `AsrEngineFactory.Create(config, role)` 根据 `AudioRole.Remote/Local` 选择 `asr.loopbackModel` / `asr.micModel`，两个模型可以独立下载与配置。
- 当前 UI 只接入了**环回**采集链路；麦克风采集源、回声门控与 AEC 仍按 M3/M5 的计划推进。工厂与配置已经为双流准备好，接入麦克风时不需要改识别引擎。

### 5. 失败与降级

- 模型目录缺失、文件不全、路径穿越、SHA 不匹配都会返回中文可操作错误；启动时弹出下载命令。
- 原生库加载失败（缺 VC++ 运行库、架构不符）会包装成 `加载本地 ASR 模型失败`，不会让进程无提示崩溃。
- 推理异常按帧记录一次日志后继续采集，避免一次坏帧终止整场会议。

## 后果与限制

- **准确率**：14M 中文模型在官方测试音频上可读，但专名/近音字会错（实测“介绍”识别为“件事”、“动荡”识别为“动量/动能”）。英文术语多的会议建议切换双语模型，或在后续接入热词表。
- **模型体积**：中文 int8 约 25 MB（浮点约 56 MB），双英模型约 487 MB 压缩包；安装包策略留到 M7（内置或首次下载）。
- **首次加载**：本机实测 0.7–1.0 s；放在后台线程加载，UI 不卡顿，但点击启动到出字会有这段一次性等待。
- **麦克风**：双流配置已就绪，但麦克风采集/AEC/回声门控尚未实现，当前只识别系统环回。
- **热词**：`asr.hotwords` 字段保留，尚未写入 sherpa-onnx 的 hotwords 文件；后续接入时要注意 sherpa 的热词格式与打分。
- **模型不可打包进 git**：`tools/models/`、`*.onnx` 继续被 `.gitignore` 忽略。

## 涉及代码

- `src/Earcue.Speech/Asr/SherpaAsrEngine.cs`：sherpa-onnx 流式识别、端点、Flush、PCM 转换。
- `src/Earcue.Speech/Asr/AsrModelLocator.cs`：模型目录/文件发现、int8 优先、路径校验。
- `src/Earcue.Speech/Asr/AsrModelCatalog.cs` + `asr-models.json`：模型清单与 SHA256。
- `src/Earcue.Speech/Asr/AsrEngineFactory.cs`：按角色创建环回/麦克风引擎。
- `src/Earcue.App/MainWindow.xaml{,.cs}`：采集启动时后台加载模型、实时草稿字幕、最终句送理解层。
- `tools/download-asr-models.ps1`：下载、SHA256 校验、解压、安装。
- `tools/Spikes/ASR_Probe`：离线 WAV 探针，验证模型与 RTF。
