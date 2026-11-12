using System.Windows;
using Earcue.Audio.Sources;
using Earcue.Brain.Context;
using Earcue.Brain.Llm;
using Earcue.Brain.Understanding;
using Earcue.Core.Configuration;
using Earcue.Core.Logging;
using Earcue.Core.Models;
using Earcue.Overlay;
using Earcue.Speech.Segmentation;
using Earcue.Speech.Vad;

namespace Earcue.App;

public partial class MainWindow : Window
{
    private readonly EarcueConfig _config;
    private readonly OverlayManager _overlayManager;
    private WasapiLoopbackSource? _loopbackSource;
    private readonly QuestionDetector _questionDetector;
    private readonly TriggerCooldownManager _cooldownManager;
    private readonly DialogueContextBuffer _contextBuffer;
    private readonly TextSegmenter _segmenter;
    private readonly EnergyVad _vad;
    private readonly OpenAiCompatibleLlmClient _llmClient;

    private long _frameCount = 0;
    private readonly System.Diagnostics.Stopwatch _uptime = new();

    public MainWindow()
    {
        InitializeComponent();

        AppLogger.Initialize();
        AppLogger.Instance.Information("Earcue 应用程序主窗口初始化开始");

        // 加载或初始化配置
        var configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        _config = EarcueConfig.LoadFromFile(configPath);

        // 初始化组件
        _overlayManager = new OverlayManager(_config.Overlay);
        _overlayManager.Show();

        _questionDetector = new QuestionDetector(_config.Trigger);
        _cooldownManager = new TriggerCooldownManager(_config.Trigger);
        _contextBuffer = new DialogueContextBuffer();
        _segmenter = new TextSegmenter(_config.Segment);
        _vad = new EnergyVad();
        _llmClient = new OpenAiCompatibleLlmClient(_config.Llm);

        _segmenter.SentenceCompleted += OnSentenceCompleted;
        Closed += OnWindowClosed;

        AppLogger.Instance.Information("Earcue 应用程序主窗口初始化完成");
    }

    private void BtnToggleCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_loopbackSource != null && _loopbackSource.IsRunning)
        {
            // 停止采集
            _loopbackSource.Stop();
            _loopbackSource.Dispose();
            _loopbackSource = null;
            _uptime.Stop();

            BtnToggleCapture.Content = "▶ 启动 WASAPI 环回采集";
            TxtAudioStatus.Text = "状态: 已停止";
            TxtAudioStatus.Foreground = System.Windows.Media.Brushes.Orange;
            AppendTranscript("[系统] WASAPI 环回采集已停止。");
        }
        else
        {
            try
            {
                _loopbackSource = new WasapiLoopbackSource();
                _loopbackSource.FrameReady += OnAudioFrameReady;
                _loopbackSource.Start();
                _uptime.Restart();
                _frameCount = 0;

                BtnToggleCapture.Content = "⏹ 停止 WASAPI 环回采集";
                TxtAudioStatus.Text = "状态: 正在监听系统声音 (16kHz Mono)";
                TxtAudioStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                AppendTranscript("[系统] WASAPI 环回采集已启动，正在监听会议音频...");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动音频采集失败: {ex.Message}", "音频错误", MessageBoxButton.OK, MessageBoxImage.Error);
                AppLogger.Instance.Error(ex, "启动音频采集发生异常");
            }
        }
    }

    private void OnAudioFrameReady(AudioFrame frame)
    {
        Interlocked.Increment(ref _frameCount);

        // 每秒更新一次 UI 指标
        if (_frameCount % 100 == 0)
        {
            Dispatcher.Invoke(() =>
            {
                var driftMs = Math.Abs((_uptime.Elapsed.TotalSeconds * 100) - _frameCount) * 10;
                TxtSampleCount.Text = $"累计帧数: {_frameCount} 帧 (10ms/帧) | 漂移估算: {driftMs:0}ms";
            });
        }

        // 能量 VAD 检测
        bool isSpeech = _vad.ProcessFrame(frame);

        // 将音频状态通知断句器
        _segmenter.PushText(string.Empty, !isSpeech);
    }

    private void OnSentenceCompleted(string sentence)
    {
        // 收到完整断句
        Dispatcher.Invoke(() =>
        {
            AppendTranscript($"[对方] {sentence}");
            _contextBuffer.AddTurn(AudioRole.Remote, sentence);

            // 评估是否为疑问句
            if (_questionDetector.IsQuestion(sentence, out string matchReason))
            {
                if (_cooldownManager.ShouldTrigger(sentence, out string blockReason))
                {
                    TriggerQuestionHandling(sentence);
                }
                else
                {
                    AppLogger.Instance.Information("问题触发被冷却抑制: {Question} 原因: {Reason}", sentence, blockReason);
                }
            }
        });
    }

    private async void TriggerQuestionHandling(string question)
    {
        AppendTranscript($"[触发] 识别到有效提问: \"{question}\"，正在生成 40 字速答...");

        var card = new CardSuggestion
        {
            Question = question,
            Answer = "思考中...",
            CreatedAt = DateTime.UtcNow
        };

        // 立即弹出半透明悬浮卡片
        _overlayManager.ShowSuggestion(card);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 构造 Prompt
        var prompt = new Prompt
        {
            UserRoleDescription = _config.Llm.UserRoleDescription,
            UserStance = _config.Llm.UserStance,
            RecentContext = _contextBuffer.GetRecentTurns(),
            TriggerQuestion = question
        };

        // 流式调用 LLM
        var answerBuilder = new System.Text.StringBuilder();
        try
        {
            await foreach (var chunk in _llmClient.AskAsync(prompt))
            {
                answerBuilder.Append(chunk);
                _overlayManager.UpdateAnswerStream(answerBuilder.ToString());
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warning(ex, "流式应答异常");
        }

        stopwatch.Stop();
        var finalAnswer = answerBuilder.ToString();
        if (string.IsNullOrWhiteSpace(finalAnswer))
        {
            finalAnswer = "这个我不确定，建议跟产品和架构团队确认具体细节。";
            _overlayManager.UpdateAnswerStream(finalAnswer);
        }

        card.Answer = finalAnswer;
        card.LatencyMs = stopwatch.ElapsedMilliseconds;

        AddCardToHistory(card);
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

        TriggerQuestionHandling(question);
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

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _loopbackSource?.Stop();
        _loopbackSource?.Dispose();
        _overlayManager.Close();
    }
}
