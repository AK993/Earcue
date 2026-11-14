# 附耳 Earcue

Windows 桌面语音助手：在本机识别系统声音，自动提取、校正问题，再通过配置的回答模型生成实时字幕卡。

## 下载与使用

前往 [Releases](https://github.com/AK993/Earcue/releases/latest) 下载 `Earcue-v0.1.8-win-x64.zip`。这是 **Windows x64 免安装包**，包含 .NET 8 运行时与三套 ASR 模型，下载约 835 MiB，解压后约 1 GiB。

1. 使用 Windows 10 2004 或更新版本 / Windows 11 64 位，完整解压 ZIP。
2. 双击 `Earcue.App.exe`，进入设置，填写自己的模型服务地址、API Key 和模型名称，并测试连接。
3. 按需填写背景资料，点击「开始聆听」。程序采集默认输出设备的系统声音；当前版本不采集麦克风。
4. 在「语音识别」设置中切换 ASR 模型；停止并重新开始聆听后生效。

关闭主窗口会隐藏到托盘；托盘右键「彻底退出」结束程序。升级前先退出旧版，同一电脑、同一 Windows 用户可复制旧目录的 `config.json` 迁移设置。新下载的 ZIP 不包含个人 API Key、简历或会议文本。

详细操作见 [使用说明](docs/使用说明.txt)。

## 功能

- 本地流式 ASR：中文 Zipformer 14M、中英双语 Zipformer、中英双语 Paraformer INT8，可在设置中切换。
- 自动合并分段转录、提取和校正问题，显示识别原文及整理结果，支持编辑问题后重新回答。
- 支持 OpenAI 兼容的模型接口，流式显示回答；调用失败后固定等待 2 秒持续重试，点击「停止生成」可取消请求和重试。
- 浮动字幕卡支持拖动、缩放、点击穿透，背景与文字不透明度可独立调整。
- 支持手动提问、复制回答、背景资料、系统托盘及音频异常重连。

## 隐私

音频在本机识别，不保存音频。提取问题和生成回答时，会向你配置的模型服务发送相关转录文本、对话上下文及背景资料。

API Key 使用 Windows DPAPI 加密，背景资料以明文保存在本机 `config.json`。默认日志不记录转录正文，日志位于 `%LOCALAPPDATA%\Earcue\logs`。不要分享自己的配置文件或日志。字幕卡启用 Windows 捕获排除，使用前请在具体会议软件中确认共享画面效果。

## 从源码运行

需要 Windows 和 .NET 8 SDK：

```powershell
dotnet restore Earcue.sln
dotnet build Earcue.sln --configuration Release
dotnet test Earcue.sln --configuration Release
powershell -ExecutionPolicy Bypass -File tools\download-asr-models.ps1 -Model all
dotnet run --project src\Earcue.App\Earcue.App.csproj
```

下载脚本默认将模型放到 `%LOCALAPPDATA%\Earcue\models`，并按项目清单校验 SHA256。源码仓库不包含模型权重和发布包。

## 打包

将三套模型准备在 `tools\models`，再运行打包脚本：

```powershell
powershell -ExecutionPolicy Bypass -File tools\download-asr-models.ps1 -Model all -InstallRoot tools\models
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Version 0.1.8
```

输出目录为 `publish`，包含免安装目录、ZIP 和 SHA256 校验文件。脚本为 ZIP 生成空白用户配置；目标版本已存在时会停止，请使用新版本号或先整理已有包。

主要目录：`src` 为应用及核心模块，`tests` 为自动化测试，`tools` 为模型下载与打包脚本，`docs` 为设计和使用文档。

## 第三方组件与模型

语音识别使用 [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)，音频采集使用 [NAudio](https://github.com/naudio/NAudio)。模型来源、固定下载地址及校验值见 [ASR 模型清单](src/Earcue.Speech/Asr/asr-models.json)。第三方组件与模型分别适用其上游许可；发布包附带的上游说明也请一并查阅。
