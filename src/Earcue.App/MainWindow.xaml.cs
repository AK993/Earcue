using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Earcue.Audio.Sources;
using Earcue.Brain.Context;
using Earcue.Brain.Llm;
using Earcue.Core.Configuration;
using Earcue.Core.Logging;
using Earcue.Core.Models;
using Earcue.Overlay;
using Earcue.Speech.Asr;
using Forms = System.Windows.Forms;

namespace Earcue.App;

public partial class MainWindow : Window
{
    private readonly EarcueConfig _config;
    private readonly string _configPath;
    private readonly OverlayManager _overlayManager;
    private WasapiLoopbackSource? _loopbackSource;
    private SherpaAsrEngine? _asrEngine;
    private readonly DialogueContextBuffer _contextBuffer;
    private OpenAiCompatibleLlmClient _llmClient;
    private readonly DispatcherTimer _autoAnswerTimer;

    private Forms.NotifyIcon? _notifyIcon;
    private bool _reallyExit;
    private bool _hasShownMinimizeTip;

    private long _frameCount = 0;
    private bool _asrErrorLogged;
    private bool _missingKeyNotified;
    private bool _isClosing;
    private bool _overlayEditMode;
    private DateTime _captureWallStart;
    private DateTime _lastSpeechAt;
    private DateTime _lastAnswerAt = DateTime.MinValue;
    private int _pendingChars;
    private string _latestSentence = string.Empty;
    private bool _isAnswering;
    private CancellationTokenSource? _answerCts;
    private readonly System.Diagnostics.Stopwatch _uptime = new();

    public MainWindow()
    {
        InitializeComponent();

        AppLogger.Initialize();
        AppLogger.Instance.Information("Earcue 应用程序主窗口初始化开始");

        // 加载或初始化配置
        _configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        _config = EarcueConfig.LoadFromFile(_configPath);
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

        InitializeTrayIcon();

        System.Windows.Application.Current.SessionEnding += (s, e) =>
        {
            _reallyExit = true;
        };

        AppLogger.Instance.Information("Earcue 应用程序主窗口初始化完成");
    }

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

        BtnEditOverlay.Content = enabled ? "✅ 完成" : "🖱 调整";
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
            TxtLlmKeyStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        else if (_config.Llm.HasApiKey)
        {
            TxtLlmKeyStatus.Text = "API Key: 已加密保存但无法解密，请重新填写";
            TxtLlmKeyStatus.Foreground = System.Windows.Media.Brushes.Orange;
        }
        else if (LlmEndpoints.IsLoopback(_config.Llm.BaseUrl))
        {
            TxtLlmKeyStatus.Text = "API Key: 未配置（本地服务可留空）";
            TxtLlmKeyStatus.Foreground = System.Windows.Media.Brushes.Gray;
        }
        else
        {
            TxtLlmKeyStatus.Text = "API Key: 未配置，点击【设置】";
            TxtLlmKeyStatus.Foreground = System.Windows.Media.Brushes.Orange;
        }
    }

    private void BtnModelSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenModelSettings();
    }

    private void OpenModelSettings()
    {
        var originalOverlay = _config.Overlay;
        var previousAsrModel = _config.Asr.LoopbackModel;
        var dialog = new SettingsWindow(_config.Llm, _config.Overlay, _config.AutoAnswer, _config.Asr) { Owner = this };

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
        _missingKeyNotified = false;
        RefreshLlmUi();

        var keyState = _config.Llm.HasApiKey ? $"API Key {_config.Llm.MaskedApiKey}" : "未配置 API Key";
        var autoState = _config.AutoAnswer.Enabled
            ? $"自动应答 {_config.AutoAnswer.SilenceMs / 1000.0:0.0}s"
            : "自动应答关闭";

        var asrModelChanged = !string.Equals(previousAsrModel, _config.Asr.LoopbackModel, StringComparison.OrdinalIgnoreCase);
        var asrModelName = AsrModelCatalog.Find(_config.Asr.LoopbackModel)?.DisplayName ?? _config.Asr.LoopbackModel;
        if (asrModelChanged)
        {
            TxtAsrModel.Text = $"ASR: {asrModelName}";
            AppendTranscript($"[系统] ASR 语音模型已切换为：{asrModelName}（下一次启动采集时生效）");
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

        BtnToggleCapture.IsEnabled = false;
        TxtAudioStatus.Text = "状态: 正在加载本地 ASR 模型…";
        TxtAudioStatus.Foreground = System.Windows.Media.Brushes.Gold;

        try
        {
            // 模型加载约 1 秒，放到后台线程避免卡住 UI
            var loaded = await Task.Run(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var engine = AsrEngineFactory.Create(_config, AudioRole.Remote);
                return (Engine: engine, ElapsedMs: watch.ElapsedMilliseconds);
            });

            if (_isClosing)
            {
                loaded.Engine.Dispose();
                return;
            }

            _asrEngine = loaded.Engine;
            _asrErrorLogged = false;
            TxtAsrModel.Foreground = System.Windows.Media.Brushes.LightGray;

            var model = AsrModelCatalog.Find(_asrEngine.ModelId);
            TxtAsrModel.Text =
                $"ASR: {model?.DisplayName ?? _asrEngine.ModelId} · " +
                $"{(_asrEngine.Files.IsInt8 ? "int8" : "float")} · {loaded.ElapsedMs}ms";
            TxtAsrModel.ToolTip = _asrEngine.Files.ModelDirectory;

            AppendTranscript(
                $"[系统] 本地 ASR 已加载：{_asrEngine.ModelId} · {_asrEngine.Files.EncoderFileName} · {loaded.ElapsedMs}ms");

            _loopbackSource = new WasapiLoopbackSource();
            _loopbackSource.FrameReady += OnAudioFrameReady;
            _captureWallStart = DateTime.UtcNow;
            _lastSpeechAt = DateTime.UtcNow;
            _lastAnswerAt = DateTime.MinValue;
            _pendingChars = 0;
            _latestSentence = string.Empty;
            _loopbackSource.Start();
            _uptime.Restart();
            _frameCount = 0;
            _autoAnswerTimer.Start();

            BtnToggleCapture.Content = "⏹ 停止采集与识别";
            TxtAudioStatus.Text = "状态: 正在监听并识别系统声音 (16kHz Mono)";
            TxtAudioStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            AppendTranscript("[系统] WASAPI 环回采集已启动，本地 ASR 正在识别会议音频...");
            var autoHint = _config.AutoAnswer.Enabled
                ? $"静音 {_config.AutoAnswer.SilenceMs / 1000.0:0.0}s 后自动把最近内容发给大模型"
                : "自动应答当前关闭（可在【模型设置】中开启）";
            AppendTranscript($"[系统] 自动应答：{autoHint}。");
        }
        catch (Exception ex)
        {
            _asrEngine?.Dispose();
            _asrEngine = null;
            _loopbackSource?.Dispose();
            _loopbackSource = null;

            TxtAsrModel.Text = "ASR: 加载失败";
            TxtAsrModel.Foreground = System.Windows.Media.Brushes.Orange;
            TxtAudioStatus.Text = "状态: 启动失败";
            TxtAudioStatus.Foreground = System.Windows.Media.Brushes.Orange;

            var message = ex is FileNotFoundException
                ? $"{ex.Message}\n\n下载命令：\n  powershell -ExecutionPolicy Bypass -File tools\\download-asr-models.ps1"
                : $"加载本地 ASR 失败：\n{ex.Message}";
            MessageBox.Show(message, "本地 ASR 错误", MessageBoxButton.OK, MessageBoxImage.Error);
            AppLogger.Instance.Error(ex, "启动本地 ASR 失败");
        }
        finally
        {
            BtnToggleCapture.IsEnabled = true;
        }
    }

    private void StopCaptureAndFlush()
    {
        _autoAnswerTimer.Stop();
        _answerCts?.Cancel();

        _loopbackSource?.Stop();
        _loopbackSource?.Dispose();
        _loopbackSource = null;
        _uptime.Stop();

        FlushAsr();

        _asrEngine?.Dispose();
        _asrEngine = null;
        TxtLiveAsr.Text = string.Empty;
        TxtAsrModel.Text = "ASR: 已停止";
        TxtAsrModel.Foreground = System.Windows.Media.Brushes.Gray;

        BtnToggleCapture.Content = "▶ 启动采集与本地识别";
        TxtAudioStatus.Text = "状态: 已停止";
        TxtAudioStatus.Foreground = System.Windows.Media.Brushes.Orange;
        AppendTranscript("[系统] 采集与本地识别已停止。");
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

    private void OnAudioFrameReady(AudioFrame frame)
    {
        Interlocked.Increment(ref _frameCount);

        // 每秒更新一次 UI 指标
        if (_frameCount % 100 == 0)
        {
            var count = _frameCount;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var driftMs = Math.Abs((_uptime.Elapsed.TotalSeconds * 100) - count) * 10;
                TxtSampleCount.Text = $"累计帧数: {count} 帧 (10ms/帧) | 漂移估算: {driftMs:0}ms";
            }));
        }

        var engine = _asrEngine;
        if (engine is null)
        {
            return;
        }

        try
        {
            var events = engine.Process(frame);
            foreach (var partial in events)
            {
                var item = partial;
                if (item.IsFinal)
                {
                    Dispatcher.BeginInvoke(new Action(() => OnAsrFinal(item)));
                }
                else if (item.Text.Length > 0)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!Dispatcher.HasShutdownStarted)
                        {
                            TxtLiveAsr.Text = $"[对方] {item.Text}";
                            _lastSpeechAt = DateTime.UtcNow;
                        }
                    }));
                }
            }
        }
        catch (Exception ex)
        {
            if (!_asrErrorLogged)
            {
                _asrErrorLogged = true;
                AppLogger.Instance.Error(ex, "本地 ASR 处理音频帧失败（后续同类错误不再重复记录）");
            }
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
            "ASR final: {Text} · audio {Start:0.00}-{End:0.00}s · finalLag {Lag:0}ms",
            partial.Text,
            partial.StartTime.TotalSeconds,
            partial.EndTime.TotalSeconds,
            lagMs);

        HandleRecognizedSentence(partial.Text);
    }

    private void HandleRecognizedSentence(string sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence))
        {
            return;
        }

        AppendTranscript($"[对方] {sentence}");
        _contextBuffer.AddTurn(AudioRole.Remote, sentence);

        // 不再判断"是不是疑问句"：所有识别内容都进入上下文，静音超时后统一交给大模型
        _latestSentence = sentence;
        _pendingChars += sentence.Length;
        _lastSpeechAt = DateTime.UtcNow;

        AppLogger.Instance.Information(
            "ASR 句子已加入上下文: {Sentence} (待应答字数={Pending})",
            sentence,
            _pendingChars);
    }

    private void OnAutoAnswerTick(object? sender, EventArgs e)
    {
        if (_isClosing || _isAnswering || _asrEngine is null)
        {
            return;
        }

        var options = _config.AutoAnswer;
        if (!options.Enabled)
        {
            return;
        }

        if (_pendingChars < Math.Max(1, options.MinChars) || string.IsNullOrWhiteSpace(_latestSentence))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var silence = now - _lastSpeechAt;
        if (silence.TotalMilliseconds < Math.Max(500, options.SilenceMs))
        {
            return;
        }

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
        _pendingChars = 0;
        _lastAnswerAt = now;

        _ = GenerateAnswerAsync(latest, auto: true, silence);
    }

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

    private async Task GenerateAnswerAsync(string latest, bool auto, TimeSpan silence)
    {
        if (_isAnswering || !EnsureLlmReady(userInitiated: !auto))
        {
            return;
        }

        _isAnswering = true;
        _lastAnswerAt = DateTime.UtcNow;
        if (!auto)
        {
            _pendingChars = 0;
        }

        _answerCts?.Dispose();
        _answerCts = new CancellationTokenSource();
        var token = _answerCts.Token;

        try
        {
            var excerpt = latest.Length <= 60 ? latest : latest[..60] + "…";
            var mode = auto
                ? $"自动应答（静音 {silence.TotalSeconds:0.0}s）"
                : "手动提问";
            AppendTranscript($"[触发] {mode}：\"{excerpt}\"，正在把最近内容发给大模型...");

            var card = new CardSuggestion
            {
                Question = latest,
                Answer = "思考中...",
                CreatedAt = DateTime.UtcNow
            };

            // 立即在左下角弹出半透明卡片
            _overlayManager.ShowSuggestion(card);

            var prompt = new Prompt
            {
                Mode = auto ? PromptMode.AutoResponse : PromptMode.Question,
                Role = _config.Profile.RoleValue,
                UserRoleDescription = _config.Llm.UserRoleDescription,
                UserStance = _config.Llm.UserStance,
                ResumeText = _config.Profile.Resume,
                MaxResumeChars = _config.Profile.MaxResumeChars,
                RecentContext = _contextBuffer.GetRecentTurns(),
                TriggerQuestion = latest
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var answerBuilder = new System.Text.StringBuilder();
            try
            {
                await foreach (var chunk in _llmClient.AskAsync(prompt, token))
                {
                    token.ThrowIfCancellationRequested();
                    answerBuilder.Append(chunk);
                    _overlayManager.UpdateAnswerStream(answerBuilder.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                _overlayManager.Hide();
                AppLogger.Instance.Information("自动应答已取消（采集停止或窗口关闭）");
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warning(ex, "流式应答异常");
            }

            stopwatch.Stop();
            var finalAnswer = answerBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(finalAnswer))
            {
                finalAnswer = auto
                    ? "我先确认一下细节，稍后补充。"
                    : "这个我不确定，建议跟产品和架构团队确认具体细节。";
                _overlayManager.UpdateAnswerStream(finalAnswer);
            }

            card.Answer = finalAnswer;
            card.LatencyMs = stopwatch.ElapsedMilliseconds;
            AddCardToHistory(card);

            if (auto)
            {
                var preview = finalAnswer.Length <= 60 ? finalAnswer : finalAnswer[..60] + "…";
                AppendTranscript($"[自动应答] {preview}（{stopwatch.ElapsedMilliseconds / 1000.0:0.0}s）");
            }
        }
        finally
        {
            _isAnswering = false;
            _answerCts?.Dispose();
            _answerCts = null;
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
        AddCardToHistory(testCard);
    }

    private void BtnManualAsk_Click(object sender, RoutedEventArgs e)
    {
        var question = TxtManualQuestion.Text.Trim();
        if (string.IsNullOrWhiteSpace(question)) return;

        _ = GenerateAnswerAsync(question, auto: false, TimeSpan.Zero);
    }

    private void AddCardToHistory(CardSuggestion card)
    {
        Dispatcher.Invoke(() =>
        {
            var border = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 10),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(1)
            };

            var sp = new System.Windows.Controls.StackPanel();
            var txtQ = new System.Windows.Controls.TextBlock
            {
                Text = $"[对方] {card.Question}  ({card.LatencyMs / 1000.0:0.0}s)",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            var txtA = new System.Windows.Controls.TextBlock
            {
                Text = card.Answer,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            };

            sp.Children.Add(txtQ);
            sp.Children.Add(txtA);
            border.Child = sp;

            PanelCards.Children.Insert(0, border);
        });
    }

    private void AppendTranscript(string line)
    {
        Dispatcher.Invoke(() =>
        {
            TxtTranscript.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
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

    private void RestoreMainWindow()
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
