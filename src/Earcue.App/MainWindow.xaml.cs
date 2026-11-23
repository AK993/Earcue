using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Earcue.Audio.Sources;
using Earcue.Brain.Context;
using Earcue.Brain.Llm;
using Earcue.Brain.Understanding;
using Earcue.Core.Configuration;
using Earcue.Core.Logging;
using Earcue.Core.Models;
using Earcue.Overlay;
using Earcue.Speech.Asr;
using Earcue.Speech.Vad;
using Forms = System.Windows.Forms;

namespace Earcue.App;

public partial class MainWindow : Window
{
    private readonly EarcueConfig _config;
    private readonly string _configPath;
    private readonly OverlayManager _overlayManager;
    private WasapiLoopbackSource? _loopbackSource;
    private SherpaAsrEngine? _asrEngine;
    private WasapiLoopbackSource? _micSource;
    private SherpaAsrEngine? _micAsrEngine;
    private readonly AutoAnswerGate _micGate = new();
    private readonly Queue<(string Text, DateTime Timestamp)> _possibleEchoes = new();
    private readonly DialogueContextBuffer _contextBuffer;
    private OpenAiCompatibleLlmClient _llmClient;
    private readonly DispatcherTimer _autoAnswerTimer;

    private Forms.NotifyIcon? _notifyIcon;
    private bool _reallyExit;
    private bool _hasShownMinimizeTip;

    private long _frameCount = 0;
    private int _asrFailed;
    private int _micAsrFailed;
    private readonly AutoAnswerGate _answerGate = new();
    private bool _missingKeyNotified;
    private bool _isClosing;
    private bool _isStartingCapture;
    private bool _overlayEditMode;
    private DateTime _captureWallStart;
    private DateTime _lastAnswerAt = DateTime.MinValue;
    private int _pendingChars;
    private readonly PendingSpeechBuffer _pendingSpeech = new();
    private bool _autoAnswerPausedAfterError;
    private bool _autoAnswerPausedByUser;
    private int _captureSessionId;
    private AnswerCardView? _activeAnswerCard;
    private const int MaxHistoryCards = 100;
    private const int MaxTranscriptChars = 100_000;
    private string _latestSentence = string.Empty;
    private bool _isAnswering;
    private CancellationTokenSource? _answerCts;
    private ContinuousAnswerRetry _answerRetry = new();
    private string? _answerRetryStatus;
    private readonly System.Diagnostics.Stopwatch _uptime = new();
    private sealed record AnswerRequest(string SourceText, bool Automatic, int CaptureSession,
        PendingSpeechSnapshot? Pending, IReadOnlyList<DialogueTurn> Context, string? QuestionOverride = null);

    public MainWindow()
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(760, workArea.Width - 32));
        MinHeight = Math.Min(MinHeight, Math.Max(520, workArea.Height - 32));
        Width = Math.Min(Width, workArea.Width - 32);
        Height = Math.Min(Height, workArea.Height - 32);

        AppLogger.Initialize();
        AppLogger.Instance.Information("Earcue 应用程序主窗口初始化开始");

        // 加载或初始化配置
        _configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        _config = EarcueConfig.LoadFromFile(_configPath, out var configWarning);
        if (!_config.Llm.TryLoadApiKey() && !LlmEndpoints.IsLoopback(_config.Llm.BaseUrl))
        {
            AppLogger.Instance.Warning("未找到可用的 LLM API Key，请在【模型设置】中配置");
        }

        // 初始化组件
        _overlayManager = new OverlayManager(_config.Overlay);
        _overlayManager.LayoutChanged += OnOverlayLayoutChanged;
        _overlayManager.Show();

        _contextBuffer = new DialogueContextBuffer();
        _llmClient = new OpenAiCompatibleLlmClient(_config.Llm);

        _autoAnswerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _autoAnswerTimer.Tick += OnAutoAnswerTick;

        Closed += OnWindowClosed;

        RefreshLlmUi();
        RefreshProfileUi();
        TxtAsrModel.Text = AsrModelCatalog.Find(_config.Asr.LoopbackModel)?.DisplayName ?? _config.Asr.LoopbackModel;
        RefreshOverlayStatus();
        RefreshAnswerState();

        InitializeTrayIcon();

        if (configWarning is not null)
            AppendTranscript($"[系统] {configWarning}");

        System.Windows.Application.Current.SessionEnding += (s, e) =>
        {
            _reallyExit = true;
        };

        AppLogger.Instance.Information("Earcue 应用程序主窗口初始化完成");
    }

    private System.Windows.Media.Brush UiBrush(string key) => (System.Windows.Media.Brush)FindResource(key);

    private void RefreshOverlayStatus() => TxtOverlayStatus.Text = _config.Overlay.ExcludeFromCapture
        ? "字幕卡已启用共享捕获排除" : "字幕卡允许被屏幕捕获";

    /// <summary>把当前面试角色 / 简历长度同步到入口按钮的提示。</summary>
    private void RefreshProfileUi()
    {
        var role = InterviewRolePresets.Find(_config.Profile.Role);
        var resumeLength = _config.Profile.Resume?.Length ?? 0;
        BtnProfile.ToolTip = $"{role.DisplayName} · 简历 {resumeLength} 字";
    }

    private void BtnProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileWindow(_config.Profile, _config.Llm.UserRoleDescription) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _config.Profile = dialog.ProfileResult;
        _config.Llm.UserRoleDescription = dialog.UserRoleDescription;

        try
        {
            _config.SaveToFile(_configPath);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warning(ex, "保存面试资料到 config.json 失败");
            MessageBox.Show(
                $"面试资料已在本次运行中生效，但写入配置文件失败：\n{ex.Message}",
                "配置保存失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        RefreshProfileUi();
        var role = InterviewRolePresets.Find(_config.Profile.Role);
        AppendTranscript(
            $"[系统] 面试资料已更新：{role.DisplayName} · 简历 {(_config.Profile.Resume?.Length ?? 0)} 字");
    }

    private void BtnEditOverlay_Click(object sender, RoutedEventArgs e)
    {
        SetOverlayEditMode(!_overlayEditMode);
    }

    private void SetOverlayEditMode(bool enabled)
    {
        _overlayEditMode = enabled;
        _overlayManager.SetEditMode(enabled);

        BtnEditOverlay.Content = enabled ? "完成调整" : "调整位置";
        BtnEditOverlay.ToolTip = enabled
            ? "退出调整模式，恢复点击穿透"
            : "打开后可以鼠标拖动/缩放字幕卡";

        if (enabled)
        {
            AppendTranscript("[系统] 字幕卡调整模式：拖动卡片移动位置，拖右下角手柄调整大小；调整完点击【完成】。");
        }
        else
        {
            AppendTranscript("[系统] 字幕卡调整模式已关闭，恢复点击穿透。");
        }
    }

    private void OnOverlayLayoutChanged(OverlayLayout layout)
    {
        _config.Overlay.Position = OverlayPositionPresets.Custom;
        _config.Overlay.PositionX = layout.Left;
        _config.Overlay.PositionY = layout.Top;
        _config.Overlay.Width = layout.Width;
        _config.Overlay.MinHeight = layout.MinHeight;

        try
        {
            _config.SaveToFile(_configPath);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warning(ex, "保存字幕卡布局到 config.json 失败");
        }

        AppLogger.Instance.Information(
            "字幕卡布局已更新: left={Left:0}, top={Top:0}, width={Width:0}, minHeight={MinHeight:0}",
            layout.Left,
            layout.Top,
            layout.Width,
            layout.MinHeight);
    }

    /// <summary>把当前配置同步到主界面上的模型 / Key 状态展示。</summary>
    private void RefreshLlmUi()
    {
        var provider = LlmProviderPresets.ResolveDisplay(_config.Llm.Provider, _config.Llm.BaseUrl);
        var providerName = provider.DisplayName;
        TxtLlmStatus.Text = $"{providerName} · {_config.Llm.Model}";

        var keyLoaded = _config.Llm.TryLoadApiKey();
        if (keyLoaded)
        {
            TxtLlmKeyStatus.Text = $"API Key: {_config.Llm.MaskedApiKey} · 回答长度不限";
            TxtLlmKeyStatus.Foreground = UiBrush("SuccessBrush");
        }
        else if (_config.Llm.HasApiKey)
        {
            TxtLlmKeyStatus.Text = "API Key: 已加密保存但无法解密，请重新填写";
            TxtLlmKeyStatus.Foreground = UiBrush("WarningBrush");
        }
        else if (LlmEndpoints.IsLoopback(_config.Llm.BaseUrl))
        {
            TxtLlmKeyStatus.Text = "API Key: 未配置（本地服务可留空）";
            TxtLlmKeyStatus.Foreground = UiBrush("SubtleBrush");
        }
        else
        {
            TxtLlmKeyStatus.Text = "API Key: 未配置，点击【设置】";
            TxtLlmKeyStatus.Foreground = UiBrush("WarningBrush");
        }
    }

    private void BtnModelSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenModelSettings();
    }

    private void OpenModelSettings(bool speechTab = false)
    {
        var originalOverlay = _config.Overlay;
        var previousAsrModel = _config.Asr.LoopbackModel;
        var dialog = new SettingsWindow(_config.Llm, _config.Overlay, _config.AutoAnswer, _config.Asr,
            _config.Profile.Resume, _config.Mic.CaptureForContext) { Owner = this };
        if (speechTab) dialog.SelectSpeechTab();

        // 设置页拖动透明度/字号/颜色时，实时应用到真实字幕卡
        dialog.OverlayPreviewChanged += options => _overlayManager.UpdateOptions(options);

        if (dialog.ShowDialog() != true)
        {
            // 取消时恢复调整前的外观
            _overlayManager.UpdateOptions(originalOverlay);
            return;
        }

        _config.Llm = dialog.Result;
        _config.Overlay = dialog.OverlayResult;
        _config.AutoAnswer = dialog.AutoAnswerResult;
        _config.Asr = dialog.AsrResult;
        if (_config.Mic.CaptureForContext != dialog.RememberMyAnswers)
        {
            _config.Mic.CaptureForContext = dialog.RememberMyAnswers;
            if (dialog.RememberMyAnswers)
                AppendTranscript("[系统] 下次开始聆听时会启用麦克风，记住你的回答。");
            else
            {
                _answerCts?.Cancel();
                StopMicrophone();
                CommitRemoteSpeech(DateTime.UtcNow, flush: true);
                _contextBuffer.RemoveLocalTurns();
                AppendTranscript("[系统] 已关闭麦克风，并从回答上下文中清除本次我方发言。");
            }
        }

        try
        {
            _config.SaveToFile(_configPath);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warning(ex, "保存 config.json 失败");
            MessageBox.Show(
                $"设置已在本次运行中生效，但写入配置文件失败：\n{ex.Message}",
                "配置保存失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        // 用新配置重建客户端、立即套用字幕卡外观
        _llmClient = new OpenAiCompatibleLlmClient(_config.Llm);
        _overlayManager.UpdateOptions(_config.Overlay);
        RefreshOverlayStatus();
        _missingKeyNotified = false;
        _autoAnswerPausedAfterError = false;
        _autoAnswerPausedByUser = false;
        RefreshLlmUi();
        RefreshAnswerState();

        var keyState = _config.Llm.HasApiKey ? $"API Key {_config.Llm.MaskedApiKey}" : "未配置 API Key";
        var autoState = _config.AutoAnswer.Enabled
            ? $"自动应答 {_config.AutoAnswer.SilenceMs / 1000.0:0.0}s"
            : "自动应答关闭";

        var asrModelChanged = !string.Equals(previousAsrModel, _config.Asr.LoopbackModel, StringComparison.OrdinalIgnoreCase);
        var asrModelName = AsrModelCatalog.Find(_config.Asr.LoopbackModel)?.DisplayName ?? _config.Asr.LoopbackModel;
        if (asrModelChanged)
        {
            var runningName = _asrEngine is null ? null : AsrModelCatalog.Find(_asrEngine.ModelId)?.DisplayName;
            TxtAsrModel.Text = runningName is null
                ? $"ASR: 已选择 {asrModelName}"
                : $"ASR: {runningName}（待切换：{asrModelName}）";
            AppendTranscript($"[系统] 已选择 ASR 模型：{asrModelName}。下次启动采集时生效，无需重启程序。");
        }

        AppendTranscript($"[系统] 设置已更新：{_config.Llm.Model} · {keyState} · {autoState} · ASR: {asrModelName}");
        AppLogger.Instance.Information(
            "设置已更新: provider={Provider}, host={Host}, model={Model}, hasKey={HasKey}, autoAnswer={Auto}, silenceMs={Silence}",
            _config.Llm.Provider,
            Earcue.Brain.Llm.LlmEndpoints.DescribeHost(_config.Llm.BaseUrl),
            _config.Llm.Model,
            _config.Llm.HasApiKey,
            _config.AutoAnswer.Enabled,
            _config.AutoAnswer.SilenceMs);
    }

    private async void BtnToggleCapture_Click(object sender, RoutedEventArgs e)
    {
        // 托盘可以直接调用此方法，不能只依赖按钮的 IsEnabled。
        if (_isClosing || _isStartingCapture) return;
        if (_loopbackSource != null && _loopbackSource.IsRunning)
        {
            StopCaptureAndFlush();
            return;
        }

        // 会议开始前退出调整模式，恢复点击穿透
        if (_overlayEditMode)
        {
            SetOverlayEditMode(false);
        }

        var recovering = CaptureErrorBanner.Visibility == Visibility.Visible;
        if (!AsrModelLocator.TryLocate(_config.Asr.LoopbackModel, _config.Asr.ModelRoot, _config.Asr.UseInt8, out _, out _))
        {
            OpenModelSettings(speechTab: true);
            return;
        }
        _isStartingCapture = true;
        BtnToggleCapture.IsEnabled = false;
        BtnRetryCapture.IsEnabled = false;
        TxtAudioStatus.Text = "正在加载语音模型…";
        TxtAudioStatus.Foreground = UiBrush("WarningBrush");

        try
        {
            var loadMicrophone = _config.Mic.CaptureForContext;
            var selectedModel = _config.Asr.LoopbackModel;
            // Two recognizers own separate streams, so acoustic samples cannot cross speaker roles.
            var loaded = await Task.Run(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var engine = AsrEngineFactory.Create(_config, AudioRole.Remote, selectedModel);
                SherpaAsrEngine? microphone = null;
                if (loadMicrophone)
                {
                    try { microphone = AsrEngineFactory.Create(_config, AudioRole.Local, selectedModel); }
                    catch (Exception ex) { AppLogger.Instance.Warning(ex, "麦克风 ASR 加载失败"); }
                }
                return (Engine: engine, Microphone: microphone, ElapsedMs: watch.ElapsedMilliseconds);
            });

            if (_isClosing)
            {
                loaded.Engine.Dispose();
                loaded.Microphone?.Dispose();
                return;
            }

            _asrEngine = loaded.Engine;
            _micAsrEngine = loaded.Microphone;
            Interlocked.Exchange(ref _asrFailed, 0);
            TxtAsrModel.Foreground = UiBrush("InkBrush");

            var model = AsrModelCatalog.Find(_asrEngine.ModelId);
            TxtAsrModel.Text = model?.DisplayName ?? _asrEngine.ModelId;
            TxtAsrModel.ToolTip = $"{_asrEngine.Files.ModelDirectory}\n加载耗时 {loaded.ElapsedMs}ms";

            AppendTranscript(
                $"[系统] 本地 ASR 已加载：{_asrEngine.ModelId} · {_asrEngine.Files.EncoderFileName} · {loaded.ElapsedMs}ms");
            if (_config.Asr.Hotwords.Count > 0)
                AppendTranscript($"[系统] 专业词：解码增强 {_asrEngine.Hotwords.Applied} 个，其他 {_asrEngine.Hotwords.Skipped} 个仅供问题校正参考。词表不会替换识别原文。");

            var source = new WasapiLoopbackSource();
            _loopbackSource = source;
            source.FrameReady += frame => OnAudioFrameReady(source, frame);
            source.StatusChanged += status => OnCaptureStatusChanged(source, status);
            source.CaptureFailed += error => OnCaptureFailed(source, error);
            _captureWallStart = DateTime.UtcNow;
            _answerGate.Reset(DateTime.UtcNow);
            _captureSessionId++;
            if (!recovering)
            {
                _lastAnswerAt = DateTime.MinValue;
                _pendingChars = 0;
                _pendingSpeech.Clear();
                _possibleEchoes.Clear();
                _autoAnswerPausedAfterError = false;
                _autoAnswerPausedByUser = false;
                _contextBuffer.Clear();
                _latestSentence = string.Empty;
            }
            _loopbackSource.Start();
            if (_config.Mic.CaptureForContext)
                StartMicrophone();
            else
                StopMicrophone();
            _uptime.Restart();
            _frameCount = 0;
            _autoAnswerTimer.Start();

            BtnToggleCapture.Content = "停止聆听";
            TxtAudioStatus.Text = "正在聆听";
            TxtAudioStatus.Foreground = UiBrush("SuccessBrush");
            CaptureErrorBanner.Visibility = Visibility.Collapsed;
            AppendTranscript("[系统] WASAPI 环回采集已启动，本地 ASR 正在识别会议音频...");
            if (recovering) AppendTranscript("[系统] 已恢复聆听，继续使用故障前的对话上下文和待回答内容。");
            var autoHint = _config.AutoAnswer.Enabled
                ? $"静音 {_config.AutoAnswer.SilenceMs / 1000.0:0.0}s 后自动把最近内容发给大模型"
                : "自动应答当前关闭（可在【模型设置】中开启）";
            AppendTranscript($"[系统] 自动应答：{autoHint}。");
        }
        catch (Exception ex)
        {
            StopMicrophone();
            _asrEngine?.Dispose();
            _asrEngine = null;
            _loopbackSource?.Dispose();
            _loopbackSource = null;

            TxtAsrModel.Text = "识别引擎加载失败";
            TxtAsrModel.Foreground = UiBrush("WarningBrush");
            TxtAudioStatus.Text = "启动失败";
            TxtAudioStatus.Foreground = UiBrush("WarningBrush");
            ShowCaptureError("启动失败，请检查音频设备或语音模型后重试。");

            var message = ex is FileNotFoundException
                ? $"{ex.Message}\n\n下载命令：\n  powershell -ExecutionPolicy Bypass -File tools\\download-asr-models.ps1"
                : $"加载本地 ASR 失败：\n{ex.Message}";
            MessageBox.Show(message, "本地 ASR 错误", MessageBoxButton.OK, MessageBoxImage.Error);
            AppLogger.Instance.Error(ex, "启动本地 ASR 失败");
        }
        finally
        {
            _isStartingCapture = false;
            BtnToggleCapture.IsEnabled = true;
            BtnRetryCapture.IsEnabled = true;
            RefreshAnswerState();
        }
    }

    private void OnCaptureStatusChanged(WasapiLoopbackSource source, string status)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || source != _loopbackSource) return;
            TxtAudioStatus.Text = $"{status}";
            RefreshAnswerState();
            AppendTranscript($"[系统] {status}");
        });
    }

    private void OnCaptureFailed(WasapiLoopbackSource source, Exception error)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || source != _loopbackSource) return;
            StopCaptureAndFlush(flush: false);
            ShowCaptureError("音频采集已断开。检查输出设备后，点击重新开始。");
            AppendTranscript($"[系统] {error.Message}");
        });
    }

    private void StopCaptureAndFlush(bool flush = true)
    {
        _autoAnswerTimer.Stop();
        _answerCts?.Cancel();
        _captureSessionId++;

        _loopbackSource?.Stop();
        _loopbackSource?.Dispose();
        _loopbackSource = null;
        _uptime.Stop();

        StopMicrophone(flush);
        CommitRemoteSpeech(DateTime.UtcNow, flush: true);
        if (flush) FlushAsr();

        _asrEngine?.Dispose();
        _asrEngine = null;
        TxtLiveAsr.Text = string.Empty;
        TxtAsrModel.Text = "识别引擎已停止";
        TxtAsrModel.Foreground = UiBrush("SubtleBrush");

        BtnToggleCapture.Content = "开始聆听";
        TxtAudioStatus.Text = "已停止";
        TxtAudioStatus.Foreground = UiBrush("WarningBrush");
        AppendTranscript("[系统] 采集与本地识别已停止。");
        RefreshAnswerState();
    }

    private void StartMicrophone()
    {
        var engine = _micAsrEngine;
        if (engine is null)
        {
            AppendTranscript("[系统] 麦克风识别未启动，对方语音仍可识别。");
            return;
        }

        var source = WasapiLoopbackSource.CreateMicrophone();
        _micSource = source;
        Interlocked.Exchange(ref _micAsrFailed, 0);
        _micGate.Reset(DateTime.UtcNow);
        source.FrameReady += frame => OnMicrophoneFrame(source, engine, frame);
        source.StatusChanged += status => Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || source != _micSource) return;
            _micGate.Reset(DateTime.UtcNow);
            AppendTranscript($"[系统] {status}");
        });
        source.CaptureFailed += error => OnMicrophoneFailed(source, error);
        try
        {
            source.Start();
            AppendTranscript("[系统] 已开启「记住我的回答」：麦克风发言会标为 [我]，用于后续追问。");
        }
        catch (Exception ex)
        {
            StopMicrophone();
            AppLogger.Instance.Warning(ex, "无法启动麦克风采集");
            AppendTranscript("[系统] 无法打开默认麦克风，对方语音仍可识别。检查输入设备和麦克风权限后重新开始聆听。");
        }
    }

    private void StopMicrophone(bool flush = false)
    {
        var source = _micSource;
        _micSource = null;
        source?.Dispose();
        var engine = _micAsrEngine;
        _micAsrEngine = null;
        if (engine is null) return;
        using (engine)
        {
            if (!flush) return;
            foreach (var partial in engine.Flush())
                HandleRecognizedSentence(AudioRole.Local, partial.Text, DateTime.UtcNow);
        }
    }

    private void OnMicrophoneFailed(WasapiLoopbackSource source, Exception error)
    {
        if (!ReferenceEquals(source, _micSource) || Dispatcher.HasShutdownStarted ||
            Interlocked.Exchange(ref _micAsrFailed, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || source != _micSource) return;
            StopMicrophone();
            AppLogger.Instance.Warning(error, "麦克风识别停止");
            AppendTranscript("[系统] 麦克风识别已停止，对方语音仍可识别。检查输入设备后重新开始聆听。");
        });
    }

    private void OnMicrophoneFrame(WasapiLoopbackSource source, SherpaAsrEngine engine, AudioFrame frame)
    {
        if (!ReferenceEquals(source, _micSource) || Dispatcher.HasShutdownStarted || Volatile.Read(ref _micAsrFailed) != 0) return;
        try
        {
            var now = DateTime.UtcNow;
            _micGate.ObserveAudio(frame, now);
            foreach (var partial in engine.Process(frame))
            {
                if (partial.IsFinal)
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!_isClosing && ReferenceEquals(source, _micSource))
                            HandleRecognizedSentence(AudioRole.Local, partial.Text, now);
                    });
                _micGate.ObserveRecognition(partial.IsFinal, now);
            }
        }
        catch (Exception ex)
        {
            OnMicrophoneFailed(source, ex);
        }
    }

    private void ShowCaptureError(string message)
    {
        TxtCaptureError.Text = message;
        CaptureErrorBanner.Visibility = Visibility.Visible;
        TxtAudioStatus.Text = "已停止 · 需要重试";
        TxtAudioStatus.Foreground = UiBrush("WarningBrush");
        RefreshAnswerState();
    }

    private void BtnRetryCapture_Click(object sender, RoutedEventArgs e) => BtnToggleCapture_Click(sender, e);

    private void OnAsrFailed(WasapiLoopbackSource source, Exception error)
    {
        if (Interlocked.Exchange(ref _asrFailed, 1) != 0) return;
        AppLogger.Instance.Error(error, "本地 ASR 处理失败，停止采集并等待用户重试");
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || !ReferenceEquals(source, _loopbackSource)) return;
            StopCaptureAndFlush(flush: false);
            TxtAsrModel.Text = "识别引擎发生错误";
            TxtAsrModel.Foreground = UiBrush("WarningBrush");
            ShowCaptureError("本地语音识别发生错误，已停止聆听。点击重新开始以重新加载模型。");
            AppendTranscript("[错误] 本地识别已停止，可点击【重新开始】恢复；已识别的文字仍然保留。");
        });
    }

    private void FlushAsr()
    {
        if (_asrEngine is null)
        {
            return;
        }

        try
        {
            foreach (var partial in _asrEngine.Flush())
            {
                if (partial.IsFinal)
                {
                    OnAsrFinal(partial);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warning(ex, "ASR Flush 失败");
        }
    }

    private void OnAudioFrameReady(WasapiLoopbackSource source, AudioFrame frame)
    {
        if (_isClosing || !ReferenceEquals(source, _loopbackSource) || Volatile.Read(ref _asrFailed) != 0) return;
        Interlocked.Increment(ref _frameCount);

        // 每秒更新一次 UI 指标（仅在控制台可见时更新）
        if (_frameCount % 100 == 0 && IsVisible)
        {
            var count = _frameCount;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible)
                {
                    var driftMs = Math.Abs((_uptime.Elapsed.TotalSeconds * 100) - count) * 10;
                    TxtSampleCount.Text = "已聆听 " + _uptime.Elapsed.ToString(@"hh\:mm\:ss");
                    TxtSampleCount.ToolTip = $"累计 {count} 帧 · 漂移估算 {driftMs:0}ms";
                }
            }));
        }

        var engine = _asrEngine;
        if (engine is null)
        {
            return;
        }

        try
        {
            _answerGate.ObserveAudio(frame, DateTime.UtcNow);
            var events = engine.Process(frame);
            foreach (var partial in events)
            {
                var item = partial;
                if (item.IsFinal)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!_isClosing && ReferenceEquals(source, _loopbackSource) && Volatile.Read(ref _asrFailed) == 0) OnAsrFinal(item);
                    }));
                }
                else if (item.Text.Length > 0)
                {
                    // 仅当主窗口可见时，才唤醒 UI 线程刷新实时 ASR 草稿文本
                    if (IsVisible)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!_isClosing && ReferenceEquals(source, _loopbackSource) && !Dispatcher.HasShutdownStarted && IsVisible)
                            {
                                var sourceLabel = _micSource?.IsReceivingAudio == true ? "系统播放" : "对方";
                                TxtLiveAsr.Text = $"[{sourceLabel}] {item.Text}";
                            }
                        }));
                    }
                }
                _answerGate.ObserveRecognition(item.IsFinal, DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            OnAsrFailed(source, ex);
        }
    }

    private void OnAsrFinal(AsrPartial partial)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        TxtLiveAsr.Text = string.Empty;

        var lagMs = (DateTime.UtcNow - _captureWallStart - partial.EndTime).TotalMilliseconds;
        AppLogger.Instance.Information(
            "ASR final: chars={Chars} · audio {Start:0.00}-{End:0.00}s · finalLag {Lag:0}ms",
            partial.Text.Length,
            partial.StartTime.TotalSeconds,
            partial.EndTime.TotalSeconds,
            lagMs);

        var now = DateTime.UtcNow;
        if (_micSource?.IsReceivingAudio == true && _config.Mic.EchoGate)
            _possibleEchoes.Enqueue((partial.Text, now));
        else
        {
            CommitRemoteSpeech(now, flush: true);
            HandleRecognizedSentence(AudioRole.Remote, partial.Text, now);
        }
    }

    private void CommitRemoteSpeech(DateTime now, bool flush = false)
    {
        while (_possibleEchoes.TryPeek(out var segment))
        {
            if (!flush && _micSource?.IsReceivingAudio == true &&
                (now - segment.Timestamp).TotalMilliseconds < 500) return;

            _possibleEchoes.Dequeue();
            if (!_contextBuffer.IsLikelyLocalPlayback(segment.Text, segment.Timestamp))
                HandleRecognizedSentence(AudioRole.Remote, segment.Text, segment.Timestamp);
        }
    }

    private void HandleRecognizedSentence(AudioRole role, string sentence, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(sentence))
        {
            return;
        }

        AppendTranscript($"{(role == AudioRole.Local ? "[我]" : "[对方]")} {sentence}");
        _contextBuffer.AddTurn(role, sentence, timestamp);

        if (role == AudioRole.Remote)
        {
            _latestSentence = sentence;
            _pendingSpeech.Add(sentence);
            _pendingChars = _pendingSpeech.CharacterCount;
        }

        if (role == AudioRole.Remote && _config.Privacy.SaveTranscript)
            AppLogger.Instance.Information("ASR 转录（已开启保存）: {Sentence}", sentence);
    }

    private void OnAutoAnswerTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        CommitRemoteSpeech(now);
        if (_isClosing || _isAnswering || _asrEngine is null || _autoAnswerPausedAfterError || _autoAnswerPausedByUser ||
            Volatile.Read(ref _asrFailed) != 0 || _loopbackSource?.IsReceivingAudio != true || _possibleEchoes.Count != 0)
        {
            return;
        }

        var options = _config.AutoAnswer;
        if (!options.Enabled)
        {
            return;
        }

        if (_pendingChars == 0 || (_pendingChars < Math.Max(1, options.MinChars) && !IsShortFollowUp(_latestSentence)))
        {
            return;
        }

        if (!_answerGate.CanAnswer(now, options.SilenceMs, out var silence))
        {
            return;
        }

        if (_micSource?.IsReceivingAudio == true && !_micGate.CanAnswer(now, 500, out _)) return;

        if (_lastAnswerAt != DateTime.MinValue &&
            (now - _lastAnswerAt).TotalMilliseconds < Math.Max(0, options.CooldownMs))
        {
            return;
        }

        if (!EnsureLlmReady(userInitiated: false))
        {
            return;
        }

        var latest = _latestSentence;
        _lastAnswerAt = now;

        _ = GenerateAnswerAsync(latest, auto: true, silence);
    }

    private static bool IsShortFollowUp(string sentence) =>
        sentence.Trim().TrimEnd('？', '?', '。') is
            "为什么" or "为什么呢" or "怎么做" or "怎么测" or "怎么选" or "怎么验证" or "具体呢" or "然后呢" or "后来呢" or "还有呢";

    private bool EnsureLlmReady(bool userInitiated)
    {
        if (_config.Llm.TryLoadApiKey() || LlmEndpoints.IsLoopback(_config.Llm.BaseUrl))
        {
            return true;
        }

        if (userInitiated)
        {
            var choice = MessageBox.Show(
                "尚未配置模型 API Key，无法生成回答。\n现在打开【模型与 API Key 设置】吗？",
                "需要配置模型",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes)
            {
                OpenModelSettings();
            }
        }
        else if (!_missingKeyNotified)
        {
            _missingKeyNotified = true;
            AppendTranscript("[系统] 未配置模型 API Key，自动应答已暂停；请点击【设置】配置后再试。");
            RefreshAnswerState();
        }

        if (!_config.Llm.TryLoadApiKey())
        {
            if (userInitiated)
            {
                AppendTranscript("[系统] 未配置可用的模型 API Key，已跳过本次回答。");
            }

            return false;
        }

        return true;
    }

    private void RefreshAnswerState()
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        var paused = _autoAnswerPausedAfterError || _autoAnswerPausedByUser;
        TxtAutoAnswerStatus.Text = _isAnswering ? _answerRetryStatus ?? "正在生成回答…"
            : !_config.AutoAnswer.Enabled ? "自动应答已关闭 · 可手动提问"
            : _autoAnswerPausedAfterError ? "自动应答已暂停 · 回答失败，可重试或恢复"
            : _autoAnswerPausedByUser ? "自动应答已暂停 · 已停止生成"
            : _missingKeyNotified ? "自动应答等待模型配置"
            : _loopbackSource?.IsRunning == true ? "自动应答已开启 · 等待对方说完"
            : "自动应答等待开始聆听";
        TxtAutoAnswerStatus.Foreground = UiBrush(paused || _missingKeyNotified || _answerRetryStatus is not null ? "WarningBrush" : "SubtleBrush");
        BtnResumeAuto.Visibility = paused && _config.AutoAnswer.Enabled ? Visibility.Visible : Visibility.Collapsed;
        BtnResumeAuto.IsEnabled = !_isAnswering && _loopbackSource?.IsRunning == true;
        BtnManualAsk.IsEnabled = !_isAnswering;
        foreach (var view in PanelCards.Children.OfType<AnswerCardView>())
            view.SetBusy(_isAnswering, _isAnswering && ReferenceEquals(view, _activeAnswerCard));
    }

    private void BtnResumeAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_isAnswering || _loopbackSource?.IsRunning != true || !EnsureLlmReady(userInitiated: true)) return;
        _autoAnswerPausedAfterError = false;
        _autoAnswerPausedByUser = false;
        _lastAnswerAt = DateTime.UtcNow;
        RefreshAnswerState();
        AppendTranscript("[系统] 自动应答已恢复，保留的内容会在下一次满足停顿条件后重试。");
    }

    private void StopAnswer()
    {
        if (!_isAnswering) return;
        _autoAnswerPausedByUser = true;
        _answerRetryStatus = "正在停止生成与自动重试…";
        _answerCts?.Cancel();
        RefreshAnswerState();
        _activeAnswerCard?.SetStopping();
    }

    private Task GenerateAnswerAsync(string latest, bool auto, TimeSpan silence)
    {
        var pending = auto ? _pendingSpeech.Snapshot() : null;
        var source = pending is not null && !string.IsNullOrWhiteSpace(pending.Text) ? pending.Text : latest;
        return GenerateAnswerCoreAsync(new AnswerRequest(source, auto, _captureSessionId,
            pending, _contextBuffer.GetRecentTurns()), silence);
    }

    private async Task GenerateAnswerCoreAsync(AnswerRequest request, TimeSpan silence)
    {
        var latest = request.SourceText;
        var auto = request.Automatic;
        if (_isAnswering || !EnsureLlmReady(userInitiated: !auto))
        {
            return;
        }

        _isAnswering = true;
        _lastAnswerAt = DateTime.UtcNow;
        var extractQuestion = auto && request.QuestionOverride is null && _config.AutoAnswer.ExtractAndCorrectQuestion;
        QuestionAnswerStream? preparedStream = null;
        QuestionExtraction? shownExtraction = null;
        var shownFallback = false;
        _autoAnswerPausedAfterError = false;
        _autoAnswerPausedByUser = false;
        _answerRetryStatus = null;

        _answerCts?.Dispose();
        var requestCts = new CancellationTokenSource();
        _answerCts = requestCts;
        var token = requestCts.Token;
        var answerBuilder = new System.Text.StringBuilder();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var view = AddAnswerCard(request);
        _activeAnswerCard = view;
        view.PrepareQuestion(extractQuestion);
        view.Update("", extractQuestion ? "正在提取并校正问题…" : "正在思考，等待模型返回…");
        RefreshAnswerState();

        try
        {
            var excerpt = latest.Length <= 60 ? latest : latest[..60] + "…";
            var mode = auto
                ? $"自动应答（静音 {silence.TotalSeconds:0.0}s）"
                : "手动提问";
            AppendTranscript($"[触发] {mode}：\"{excerpt}\"，正在把最近内容发给大模型...");

            var card = new CardSuggestion
            {
                Question = request.QuestionOverride ?? latest,
                Answer = extractQuestion ? "正在提取并校正问题…" : "思考中...",
                CreatedAt = DateTime.UtcNow
            };

            // 立即在左下角弹出半透明卡片
            _overlayManager.ShowSuggestion(card);

            var prompt = new Prompt
            {
                Mode = auto && request.QuestionOverride is null ? PromptMode.AutoResponse : PromptMode.Question,
                Role = _config.Profile.RoleValue,
                UserRoleDescription = _config.Llm.UserRoleDescription,
                UserStance = _config.Llm.UserStance,
                ResumeText = _config.Profile.Resume,
                MaxResumeChars = _config.Profile.MaxResumeChars,
                RecentContext = _config.Mic.CaptureForContext ? request.Context :
                    request.Context.Where(turn => turn.Role == AudioRole.Remote).ToArray(),
                TriggerQuestion = request.QuestionOverride ?? latest,
                ExtractAndCorrectQuestion = extractQuestion,
                ProfessionalTerms = _config.Asr.Hotwords
            };

            long? firstTokenMs = null;
            var retryCount = 0;
            try
            {
                await _answerRetry.RunAsync(async (attempt, cancellationToken) =>
                {
                    retryCount = attempt;
                    _answerRetryStatus = attempt > 0 ? $"正在自动重试（第 {attempt} 次）…" : null;
                    preparedStream = extractQuestion ? new QuestionAnswerStream() : null;
                    shownExtraction = null;
                    shownFallback = false;
                    answerBuilder.Clear();
                    firstTokenMs = null;
                    var attemptWatch = System.Diagnostics.Stopwatch.StartNew();
                    view.PrepareQuestion(extractQuestion);
                    view.Update("", extractQuestion ? "正在提取并校正问题…" : "正在思考，等待模型返回…");
                    card.Question = request.QuestionOverride ?? latest;
                    card.Answer = extractQuestion ? "正在提取并校正问题…" : "思考中...";
                    if (attempt > 0) _overlayManager.ShowSuggestion(card);
                    RefreshAnswerState();
                    await foreach (var chunk in _llmClient.AskAsync(prompt, token))
                    {
                        token.ThrowIfCancellationRequested();
                        if (_isClosing || Dispatcher.HasShutdownStarted)
                        {
                            requestCts.Cancel();
                            token.ThrowIfCancellationRequested();
                        }
                        if (preparedStream is not null)
                        {
                            preparedStream.Push(chunk);
                            answerBuilder.Clear().Append(preparedStream.Answer);
                            if (preparedStream.Extraction != shownExtraction || preparedStream.UsedFallback != shownFallback)
                            {
                                shownExtraction = preparedStream.Extraction;
                                shownFallback = preparedStream.UsedFallback;
                                view.SetQuestionExtraction(shownExtraction, shownFallback);
                                card.Question = shownExtraction?.Questions.Count > 0 ? shownExtraction.Text : latest;
                                card.Answer = "正在组织回答…";
                                _overlayManager.ShowSuggestion(card);
                            }
                        }
                        else answerBuilder.Append(chunk);
                        var answerText = answerBuilder.ToString();
                        if (!string.IsNullOrWhiteSpace(answerText))
                        {
                            firstTokenMs ??= attemptWatch.ElapsedMilliseconds;
                            view.Update(answerText, "正在生成… 可随时停止");
                            _overlayManager.UpdateAnswerStream(answerText);
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    preparedStream?.Complete();
                    if (preparedStream is not null)
                    {
                        answerBuilder.Clear().Append(preparedStream.Answer);
                        view.SetQuestionExtraction(preparedStream.Extraction, preparedStream.UsedFallback);
                    }
                    if (string.IsNullOrWhiteSpace(answerBuilder.ToString()))
                        throw new InvalidDataException("模型未返回有效回答。");
                }, (error, attempt, delay) =>
                {
                    if (_isClosing || Dispatcher.HasShutdownStarted) requestCts.Cancel();
                    token.ThrowIfCancellationRequested();
                    var message = error is TimeoutException or System.Net.Http.HttpRequestException or InvalidDataException
                        ? error.Message : "模型请求失败，请检查模型设置。";
                    _answerRetryStatus = $"回答失败 · {delay.TotalSeconds:0} 秒后自动重试（第 {attempt} 次）";
                    view.Update(answerBuilder.ToString(), $"{message}\n{delay.TotalSeconds:0} 秒后自动重试（第 {attempt} 次），可点击停止生成。");
                    _overlayManager.UpdateAnswerStream($"{message}\n{delay.TotalSeconds:0} 秒后自动重试（第 {attempt} 次）");
                    AppendTranscript($"[重试] {message} {delay.TotalSeconds:0} 秒后进行第 {attempt} 次自动重试。");
                    var failureStage = error is TimeoutException
                        ? (error.Message.Contains("首字") ? "first_signal_timeout" : "total_timeout")
                        : error is InvalidDataException ? "response_format" : "request_failure";
                    AppLogger.Instance.Warning("模型应答失败，继续自动重试: error={ErrorType}, stage={Stage}, retry={Retry}, delaySeconds={Delay}",
                        error.GetType().Name, failureStage, attempt, delay.TotalSeconds);
                    RefreshAnswerState();
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (!_isClosing && !Dispatcher.HasShutdownStarted)
                {
                    _overlayManager.Hide();
                    if (extractQuestion && shownExtraction is null) view.SetQuestionExtraction(null, true);
                    view.Update(answerBuilder.ToString(), answerBuilder.Length > 0
                        ? "已停止 · 已保留生成内容，可重新回答" : "已停止 · 可重新回答");
                }
                AppLogger.Instance.Information("模型应答已取消");
                return;
            }
            catch (Exception ex)
            {
                if (_isClosing || Dispatcher.HasShutdownStarted) return;
                _autoAnswerPausedAfterError = true;
                if (extractQuestion && shownExtraction is null) view.SetQuestionExtraction(null, true);
                var message = ex is TimeoutException or System.Net.Http.HttpRequestException or InvalidDataException
                    ? ex.Message : "模型请求失败，请检查网络与模型设置。";
                AppLogger.Instance.Warning("模型应答失败: {ErrorType}", ex.GetType().Name);
                view.Update(answerBuilder.ToString(), $"回答失败：{message} 点击【重新回答】重试。");
                _overlayManager.UpdateAnswerStream($"回答失败：{message}");
                AppendTranscript($"[错误] {message} 本次内容已保留；可点击【重新回答】或【恢复自动应答】。");
                if (auto && string.IsNullOrWhiteSpace(TxtManualQuestion.Text))
                    TxtManualQuestion.Text = latest;
                return;
            }

            stopwatch.Stop();
            var finalAnswer = answerBuilder.ToString().Trim();

            if (request.Pending is not null && request.CaptureSession == _captureSessionId)
            {
                _pendingSpeech.Acknowledge(request.Pending);
                _pendingChars = _pendingSpeech.CharacterCount;
            }

            card.Answer = finalAnswer;
            card.LatencyMs = stopwatch.ElapsedMilliseconds;
            var retryLabel = retryCount > 0 ? $" · 重试 {retryCount} 次" : "";
            view.Update(finalAnswer, $"{DateTime.Now:HH:mm} · 本次首字 {(firstTokenMs ?? stopwatch.ElapsedMilliseconds) / 1000.0:0.0} 秒 · 总计 {card.LatencyMs / 1000.0:0.0} 秒{retryLabel}");

            if (auto)
            {
                var preview = finalAnswer.Length <= 60 ? finalAnswer : finalAnswer[..60] + "…";
                AppendTranscript($"[自动应答] {preview}（{stopwatch.ElapsedMilliseconds / 1000.0:0.0}s）");
            }
        }
        finally
        {
            _isAnswering = false;
            _answerRetryStatus = null;
            _activeAnswerCard = null;
            requestCts.Dispose();
            if (ReferenceEquals(_answerCts, requestCts)) _answerCts = null;
            RefreshAnswerState();
        }
    }

    private void BtnTestOverlay_Click(object sender, RoutedEventArgs e)
    {
        var testCard = new CardSuggestion
        {
            Question = "你们这个灰度策略怎么保证回滚？",
            Answer = "按 5% 分批，每批观察 30 分钟，异常率超 1% 立即回滚上一版本",
            LatencyMs = 1200
        };

        _overlayManager.ShowSuggestion(testCard);
    }

    private void BtnManualAsk_Click(object sender, RoutedEventArgs e)
    {
        var question = TxtManualQuestion.Text.Trim();
        if (string.IsNullOrWhiteSpace(question)) return;

        _ = GenerateAnswerAsync(question, auto: false, TimeSpan.Zero);
    }

    private AnswerCardView AddAnswerCard(AnswerRequest request)
    {
        var view = new AnswerCardView(request.SourceText, request.Automatic);
        if (request.QuestionOverride is not null) view.SetQuestionOverride(request.QuestionOverride);
        view.StopRequested += StopAnswer;
        view.RetryRequested += () =>
        {
            if (_isClosing || _isAnswering) return;
            // Keep the original snapshot: retry can acknowledge it, never later speech.
            _ = GenerateAnswerCoreAsync(request with { QuestionOverride = view.QuestionForRetry }, TimeSpan.Zero);
        };
        PanelCards.Children.Insert(0, view);
        EmptyCardsState.Visibility = Visibility.Collapsed;
        while (PanelCards.Children.Count > MaxHistoryCards)
            PanelCards.Children.RemoveAt(PanelCards.Children.Count - 1);
        ScrollerCards.ScrollToTop();
        return view;
    }

    private void AppendTranscript(string line)
    {
        Dispatcher.Invoke(() =>
        {
            TxtTranscript.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
            if (TxtTranscript.Text.Length > MaxTranscriptChars)
            {
                var text = TxtTranscript.Text;
                var cut = text.IndexOf('\n', text.Length - MaxTranscriptChars * 3 / 4);
                TxtTranscript.Text = cut >= 0 ? text[(cut + 1)..] : text[^MaxTranscriptChars..];
                TxtTranscript.CaretIndex = TxtTranscript.Text.Length;
            }
            ScrollerTranscript.ScrollToEnd();
        });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();

            if (!_hasShownMinimizeTip && _notifyIcon != null)
            {
                _hasShownMinimizeTip = true;
                try
                {
                    _notifyIcon.ShowBalloonTip(
                        3000,
                        "附耳 Earcue · 后台持续运行中",
                        "控制台已隐藏至系统托盘，继续在后台为您提供会议提词服务。\n单击托盘图标即可重新打开控制台，右键可进行快捷控制或彻底退出。",
                        Forms.ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    AppLogger.Instance.Warning(ex, "弹出托盘气泡提示失败");
                }
            }
            return;
        }

        base.OnClosing(e);
    }

    public void RestoreMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (!IsVisible)
            {
                Show();
            }
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        });
    }

    private void ExitApplication()
    {
        Dispatcher.Invoke(() =>
        {
            _reallyExit = true;
            Close();
            System.Windows.Application.Current.Shutdown();
        });
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _notifyIcon = new Forms.NotifyIcon
            {
                Text = "附耳 Earcue · 会议实时提词器",
                Visible = true
            };

            // 优先从输出目录的 Assets\app.ico 加载
            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                try
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
                }
                catch (Exception ex)
                {
                    AppLogger.Instance.Warning(ex, "从 Assets/app.ico 加载托盘图标失败");
                }
            }

            if (_notifyIcon.Icon == null)
            {
                try
                {
                    var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Instance.Warning(ex, "从主程序提取关联图标失败");
                }
            }

            if (_notifyIcon.Icon == null)
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            var contextMenu = new Forms.ContextMenuStrip();

            var itemConsole = new Forms.ToolStripMenuItem("显示控制台", null, (s, e) =>
            {
                if (IsVisible)
                {
                    Hide();
                }
                else
                {
                    RestoreMainWindow();
                }
            });

            var itemCapture = new Forms.ToolStripMenuItem("开启采集与识别", null, (s, e) =>
            {
                Dispatcher.Invoke(() => BtnToggleCapture_Click(this, new RoutedEventArgs()));
            });

            var itemOverlay = new Forms.ToolStripMenuItem("测试提词卡显示", null, (s, e) =>
            {
                Dispatcher.Invoke(() => BtnTestOverlay_Click(this, new RoutedEventArgs()));
            });

            var itemExit = new Forms.ToolStripMenuItem("彻底退出 (Exit)", null, (s, e) =>
            {
                ExitApplication();
            });

            contextMenu.Opening += (s, e) =>
            {
                itemCapture.Enabled = !_isStartingCapture;
                itemConsole.Text = IsVisible ? "隐藏控制台" : "显示控制台";
                var isCapturing = _loopbackSource != null && _loopbackSource.IsRunning;
                itemCapture.Text = isCapturing ? "停止采集与识别" : "开启采集与识别";
            };

            contextMenu.Items.Add(itemConsole);
            contextMenu.Items.Add(itemCapture);
            contextMenu.Items.Add(itemOverlay);
            contextMenu.Items.Add(new Forms.ToolStripSeparator());
            contextMenu.Items.Add(itemExit);

            _notifyIcon.ContextMenuStrip = contextMenu;

            // 单击或双击托盘图标恢复控制台
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                {
                    RestoreMainWindow();
                }
            };
            _notifyIcon.DoubleClick += (s, e) =>
            {
                RestoreMainWindow();
            };
            _notifyIcon.BalloonTipClicked += (s, e) =>
            {
                RestoreMainWindow();
            };
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "初始化系统托盘图标失败");
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _autoAnswerTimer.Stop();
        _answerCts?.Cancel();
        StopMicrophone();
        _loopbackSource?.Stop();
        _loopbackSource?.Dispose();
        _loopbackSource = null;
        _asrEngine?.Dispose();
        _asrEngine = null;
        _overlayManager.Close();

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_reallyExit)
        {
            System.Windows.Application.Current.Shutdown();
        }
    }
}
