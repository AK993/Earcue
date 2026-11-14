using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Earcue.Brain.Llm;
using Earcue.Core.Configuration;
using Earcue.Core.Models;
using Earcue.Core.Security;
using Earcue.Speech.Asr;

namespace Earcue.App;

public partial class SettingsWindow : Window
{
    private enum StatusTone
    {
        Info,
        Success,
        Error
    }

    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0x71, 0x81, 0x79));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x7D, 0x64));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xB1, 0x4E, 0x3A));

    private readonly LlmOptions _draft;
    private OverlayOptions _overlayDraft;
    private readonly AutoAnswerOptions _autoDraft;
    private readonly AsrOptions _asrDraft;
    private readonly ObservableCollection<ModelItem> _models = new();
    private readonly ICollectionView _modelsView;

    private bool _initializing = true;
    private bool _overlayInitializing = true;
    private bool _busy;
    private bool _closed;
    private CancellationTokenSource? _busyCts;

    public SettingsWindow(LlmOptions current, OverlayOptions overlay, AutoAnswerOptions autoAnswer, AsrOptions? asr = null)
    {
        InitializeComponent();

        _draft = Clone(current);
        _overlayDraft = Clone(overlay);
        _autoDraft = Clone(autoAnswer);
        _asrDraft = Clone(asr ?? new AsrOptions());
        _modelsView = CollectionViewSource.GetDefaultView(_models);
        _modelsView.Filter = FilterModel;
        LstModels.ItemsSource = _modelsView;

        CmbProvider.ItemsSource = LlmProviderPresets.All;
        LoadDraft();
        LoadOverlayDraft();
        LoadAsrDraft();

        // 小屏 / 高缩放下让窗口不超出工作区，内容区可滚动，底部按钮始终可见
        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(640, workArea.Width - 32);
        var maxHeight = Math.Max(480, workArea.Height - 32);
        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        if (Width > MaxWidth)
        {
            Width = MaxWidth;
        }

        if (Height > MaxHeight)
        {
            Height = MaxHeight;
        }

        Closed += (_, _) =>
        {
            _closed = true;
            _busyCts?.Cancel();
        };
    }

    /// <summary>保存后的完整配置副本，由 MainWindow 替换到 EarcueConfig 上。</summary>
    public LlmOptions Result => _draft;

    public OverlayOptions OverlayResult => _overlayDraft;

    public AutoAnswerOptions AutoAnswerResult => _autoDraft;

    public AsrOptions AsrResult => _asrDraft;

    /// <summary>设置页每次调整外观时触发，用于主窗口实时应用到真实浮层。</summary>
    public event Action<OverlayOptions>? OverlayPreviewChanged;

    private void LoadDraft()
    {
        var preset = LlmProviderPresets.ResolveDisplay(_draft.Provider, _draft.BaseUrl);

        CmbProvider.SelectedItem = preset;
        TxtBaseUrl.Text = _draft.BaseUrl;
        TxtModel.Text = _draft.Model;
        PwdApiKey.Password = _draft.ApiKey;
        TxtApiKeyVisible.Text = _draft.ApiKey;

        ShowSuggestedModels(preset);
        UpdateProviderHint(preset);
        UpdateKeyHint();

        _initializing = false;
        UpdateModelCount();
    }

    private void LoadOverlayDraft()
    {
        _overlayInitializing = true;

        CmbOverlayPosition.ItemsSource = OverlayPositionPresets.All;
        CmbOverlayPosition.SelectedItem = OverlayPositionPresets.Find(_overlayDraft.Position);

        PopulateFontPicker();

        SldAnswerFontSize.Value = Math.Clamp(_overlayDraft.AnswerFontSize, 12, 40);
        SldQuestionFontSize.Value = Math.Clamp(_overlayDraft.QuestionFontSize, 10, 30);
        SldBackgroundOpacity.Value = _overlayDraft.BackgroundOpacity * 100;
        SldTextOpacity.Value = _overlayDraft.TextOpacity * 100;
        SldMarginX.Value = Math.Clamp(_overlayDraft.MarginX, 0, 200);
        SldMarginY.Value = Math.Clamp(_overlayDraft.MarginY, 0, 200);
        TxtAnswerColor.Text = string.IsNullOrWhiteSpace(_overlayDraft.AnswerColor)
            ? "#00F0FF"
            : _overlayDraft.AnswerColor;

        ChkAutoAnswer.IsChecked = _autoDraft.Enabled;
        ChkCorrectQuestion.IsChecked = _autoDraft.ExtractAndCorrectQuestion;
        SldAutoSilence.Value = Math.Clamp(_autoDraft.SilenceMs / 1000.0, 1, 10);
        ChkAutoHide.IsChecked = _overlayDraft.AutoHide;
        CmbAutoHideSeconds.IsEnabled = _overlayDraft.AutoHide;
        CmbAutoHideSeconds.SelectedIndex = AutoHideIndex(_overlayDraft.AutoFadeoutSeconds);

        _overlayInitializing = false;
        UpdateOverlayValueLabels();
        UpdateOverlayPreview();
    }

    private void LoadAsrDraft()
    {
        var list = new List<AsrModelViewItem>();
        foreach (var descriptor in AsrModelCatalog.All)
        {
            bool installed = AsrModelLocator.TryLocate(descriptor.Id, _asrDraft.ModelRoot, _asrDraft.UseInt8, out _, out _);
            var status = installed ? "已就绪" : "需下载";
            list.Add(new AsrModelViewItem
            {
                Id = descriptor.Id,
                DisplayName = $"{status} · {descriptor.DisplayName} ({descriptor.SizeDisplay})",
                Description = descriptor.Description,
                IsInstalled = installed
            });
        }

        CmbAsrModel.ItemsSource = list;
        var selected = list.FirstOrDefault(m => string.Equals(m.Id, _asrDraft.LoopbackModel, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault();
        CmbAsrModel.SelectedItem = selected;
        UpdateAsrStatusText(selected);
    }

    private void CmbAsrModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbAsrModel.SelectedItem is AsrModelViewItem item)
        {
            _asrDraft.LoopbackModel = item.Id;
            _asrDraft.MicModel = item.Id;
            UpdateAsrStatusText(item);
        }
    }

    private void UpdateAsrStatusText(AsrModelViewItem? item)
    {
        if (item is null) return;
        if (item.IsInstalled)
        {
            TxtAsrModelStatus.Text = "模型已就绪。保存后重新启动采集即可切换，无需退出程序。";
            TxtAsrModelStatus.Foreground = SuccessBrush;
        }
        else
        {
            TxtAsrModelStatus.Text = "模型文件不完整，请安装后再选择。完整版包含全部三套模型。";
            TxtAsrModelStatus.Foreground = InfoBrush;
        }
    }

    private static int AutoHideIndex(int seconds)
    {
        var options = new[] { 10, 20, 30, 60, 120 };
        for (var i = 0; i < options.Length; i++)
        {
            if (seconds <= options[i])
            {
                return i;
            }
        }

        return options.Length - 1;
    }

    private void PopulateFontPicker()
    {
        var names = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList();

        if (names.Count == 0)
        {
            names.Add("Microsoft YaHei UI");
        }

        var current = names.FirstOrDefault(n =>
            string.Equals(n, _overlayDraft.FontFamily, StringComparison.OrdinalIgnoreCase));

        if (current is null)
        {
            current = string.IsNullOrWhiteSpace(_overlayDraft.FontFamily)
                ? "Microsoft YaHei UI"
                : _overlayDraft.FontFamily;
            names.Insert(0, current);
        }

        CmbOverlayFont.ItemsSource = names;
        CmbOverlayFont.SelectedItem = current;
    }

    private void OverlayCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_overlayInitializing)
        {
            OnOverlaySettingsChanged();
        }
    }

    private void OverlaySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_overlayInitializing)
        {
            OnOverlaySettingsChanged();
        }
    }

    private void OverlayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_overlayInitializing)
        {
            CmbAutoHideSeconds.IsEnabled = ChkAutoHide.IsChecked == true;
            OnOverlaySettingsChanged();
        }
    }

    private void OverlayStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        switch (tag)
        {
            case "AnswerFontDown": Step(SldAnswerFontSize, -1); break;
            case "AnswerFontUp": Step(SldAnswerFontSize, +1); break;
            case "QuestionFontDown": Step(SldQuestionFontSize, -1); break;
            case "QuestionFontUp": Step(SldQuestionFontSize, +1); break;
            case "BackgroundOpacityDown": Step(SldBackgroundOpacity, -5); break;
            case "BackgroundOpacityUp": Step(SldBackgroundOpacity, +5); break;
            case "TextOpacityDown": Step(SldTextOpacity, -5); break;
            case "TextOpacityUp": Step(SldTextOpacity, +5); break;
            case "MarginXDown": Step(SldMarginX, -4); break;
            case "MarginXUp": Step(SldMarginX, +4); break;
            case "MarginYDown": Step(SldMarginY, -4); break;
            case "MarginYUp": Step(SldMarginY, +4); break;
            case "AutoSilenceDown": Step(SldAutoSilence, -0.5); break;
            case "AutoSilenceUp": Step(SldAutoSilence, +0.5); break;
        }
    }

    private static void Step(Slider slider, double delta)
    {
        slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
    }

    private void OnOverlaySettingsChanged()
    {
        UpdateOverlayValueLabels();
        UpdateOverlayPreview();
        OverlayPreviewChanged?.Invoke(BuildOverlayDraftFromControls());
    }

    private void AnswerColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            TxtAnswerColor.Text = color;
        }
    }

    private void AnswerColor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_overlayInitializing)
        {
            OnOverlaySettingsChanged();
        }
    }

    private void UpdateOverlayValueLabels()
    {
        TxtAnswerFontSizeValue.Text = $"{SldAnswerFontSize.Value:0}";
        TxtQuestionFontSizeValue.Text = $"{SldQuestionFontSize.Value:0}";
        TxtBackgroundOpacityValue.Text = $"{SldBackgroundOpacity.Value:0}%";
        TxtTextOpacityValue.Text = $"{SldTextOpacity.Value:0}%";
        TxtMarginXValue.Text = $"{SldMarginX.Value:0}";
        TxtMarginYValue.Text = $"{SldMarginY.Value:0}";
        TxtAutoSilenceValue.Text = $"{SldAutoSilence.Value:0.0} 秒";
    }

    private void UpdateOverlayPreview()
    {
        var fontName = CmbOverlayFont.SelectedItem as string ?? _overlayDraft.FontFamily;
        var family = ResolveFontFamily(fontName);

        TxtPreviewQuestion.FontFamily = family;
        TxtPreviewQuestion.FontSize = SldQuestionFontSize.Value;
        TxtPreviewQuestion.LineHeight = Math.Round(SldQuestionFontSize.Value * 1.5);

        TxtPreviewAnswer.FontFamily = family;
        TxtPreviewAnswer.FontSize = SldAnswerFontSize.Value;
        TxtPreviewAnswer.LineHeight = Math.Round(SldAnswerFontSize.Value * 1.5);

        if (TryResolveColor(TxtAnswerColor.Text, out var answerColor))
        {
            TxtPreviewAnswer.Foreground = new SolidColorBrush(answerColor);
        }

        PreviewBackground.Opacity = SldBackgroundOpacity.Value / 100;
        PreviewText.Opacity = SldTextOpacity.Value / 100;
    }

    private static bool TryResolveColor(string? text, out Color color)
    {
        color = Color.FromRgb(0x00, 0xF0, 0xFF);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(text.Trim()) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static bool TryNormalizeColor(string? text, out string normalized)
    {
        normalized = "#00F0FF";
        if (!TryResolveColor(text, out var color))
        {
            return false;
        }

        normalized = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        return true;
    }

    private static FontFamily ResolveFontFamily(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return SystemFonts.MessageFontFamily;
        }

        try
        {
            return new FontFamily(name);
        }
        catch
        {
            return SystemFonts.MessageFontFamily;
        }
    }

    private void CmbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || CmbProvider.SelectedItem is not LlmProviderPreset preset)
        {
            return;
        }

        if (preset.BaseUrl.Length > 0)
        {
            TxtBaseUrl.Text = preset.BaseUrl;
        }

        ShowSuggestedModels(preset);
        UpdateProviderHint(preset);
        UpdateKeyHint();
        SetStatus($"已切换到 {preset.DisplayName}，点击【获取可用模型】验证 Key 并查看真实模型列表。", StatusTone.Info);
    }

    private void UpdateProviderHint(LlmProviderPreset preset)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(preset.Note))
        {
            parts.Add(preset.Note);
        }

        if (!string.IsNullOrWhiteSpace(preset.KeyHint))
        {
            parts.Add($"Key 格式：{preset.KeyHint}。");
        }

        TxtProviderNote.Text = string.Join(" ", parts);
        BtnOpenKeyPage.IsEnabled = !string.IsNullOrWhiteSpace(preset.KeyUrl);
    }

    private void UpdateKeyHint()
    {
        if (_draft.ProtectedApiKey.Length > 0 && string.IsNullOrWhiteSpace(_draft.ApiKey))
        {
            TxtKeyHint.Text = "检测到已加密保存的 Key，但当前 Windows 账户无法解密（换电脑/换用户后会出现），请重新填写。";
            return;
        }

        if (LlmEndpoints.IsLoopback(TxtBaseUrl.Text))
        {
            TxtKeyHint.Text = "本地服务通常无需 Key，可留空；填写后同样会加密保存，仅在本机使用。";
            return;
        }

        var preset = SelectedPreset();
        TxtKeyHint.Text = preset.RequiresApiKey
            ? "Key 只在本机使用，使用 Windows DPAPI 加密后保存到 config.json；请勿把该文件发给他人。"
            : "该服务可不填 Key；填写后同样会加密保存，仅在本机使用。";
    }

    private void TxtBaseUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        UpdateKeyHint();
    }

    private void ChkShowKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (ChkShowKey.IsChecked == true)
        {
            TxtApiKeyVisible.Text = PwdApiKey.Password;
            TxtApiKeyVisible.Visibility = Visibility.Visible;
            PwdApiKey.Visibility = Visibility.Collapsed;
            TxtApiKeyVisible.Focus();
            TxtApiKeyVisible.CaretIndex = TxtApiKeyVisible.Text.Length;
        }
        else
        {
            PwdApiKey.Password = TxtApiKeyVisible.Text;
            PwdApiKey.Visibility = Visibility.Visible;
            TxtApiKeyVisible.Visibility = Visibility.Collapsed;
            PwdApiKey.Focus();
        }
    }

    private void BtnClearKey_Click(object sender, RoutedEventArgs e)
    {
        PwdApiKey.Clear();
        TxtApiKeyVisible.Clear();
        SetStatus("已清空 API Key（点击【保存并应用】后生效）。", StatusTone.Info);
    }

    private async void BtnFetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !TryReadConnection(out var baseUrl, out var apiKey))
        {
            return;
        }

        if (apiKey.Length == 0 && !LlmEndpoints.IsLoopback(baseUrl))
        {
            SetStatus("请先填写 API Key：模型列表是按 Key 权限返回的。", StatusTone.Error);
            FocusKeyInput();
            return;
        }

        BeginBusy();
        SetStatus("正在请求模型列表…", StatusTone.Info);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            _busyCts = cts;
            using var catalog = new OpenAiCompatibleModelCatalog();
            var result = await catalog.ListModelsAsync(baseUrl, apiKey, cts.Token);
            if (_closed)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                SetStatus(result.Message, StatusTone.Error);
                return;
            }

            ShowFetchedModels(result.Models);
            if (string.IsNullOrWhiteSpace(TxtModel.Text) && result.Models.Count > 0)
            {
                TxtModel.Text = result.Models[0].Id;
            }

            var endpoint = string.IsNullOrWhiteSpace(result.Endpoint) ? string.Empty : $" · {result.Endpoint}";
            SetStatus($"{result.Message} · 用时 {result.Elapsed.TotalMilliseconds:0} ms{endpoint}", StatusTone.Success);
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                SetStatus("获取模型列表超时（25 秒），请检查网络、代理或 Base URL。", StatusTone.Error);
            }
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                SetStatus($"获取模型列表失败：{ex.Message}", StatusTone.Error);
            }
        }
        finally
        {
            _busyCts = null;
            EndBusy();
        }
    }

    private async void BtnTestModel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var model = TxtModel.Text.Trim();
        if (model.Length == 0)
        {
            SetStatus("请先选择或输入模型 ID，再测试调用。", StatusTone.Error);
            TxtModel.Focus();
            return;
        }

        if (!TryReadConnection(out var baseUrl, out var apiKey))
        {
            return;
        }

        if (apiKey.Length == 0 && !LlmEndpoints.IsLoopback(baseUrl))
        {
            SetStatus("请先填写 API Key，再测试模型调用。", StatusTone.Error);
            FocusKeyInput();
            return;
        }

        BeginBusy();
        SetStatus($"正在调用 {model}…", StatusTone.Info);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            _busyCts = cts;
            using var catalog = new OpenAiCompatibleModelCatalog();
            var result = await catalog.TestChatAsync(baseUrl, apiKey, model, cts.Token);
            if (_closed)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                SetStatus(result.Message, StatusTone.Error);
                return;
            }

            var reply = result.Reply.Length <= 40 ? result.Reply : result.Reply[..40] + "…";
            SetStatus($"流式连接可用 · 首字 {result.FirstTextMs ?? result.LatencyMs} ms · 总计 {result.LatencyMs} ms · 回复：{reply}", StatusTone.Success);
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                SetStatus("流式测试已取消或超时，请检查网络、代理或模型是否可用。", StatusTone.Error);
            }
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                SetStatus($"测试调用失败：{ex.Message}", StatusTone.Error);
            }
        }
        finally
        {
            _busyCts = null;
            EndBusy();
        }
    }

    private void TxtModelFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _modelsView.Refresh();
        UpdateModelCount();
    }

    private void LstModels_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (LstModels.SelectedItem is ModelItem item)
        {
            TxtModel.Text = item.Id;
        }
    }

    private void BtnOpenKeyPage_Click(object sender, RoutedEventArgs e)
    {
        var preset = SelectedPreset();
        if (string.IsNullOrWhiteSpace(preset.KeyUrl))
        {
            SetStatus("该服务商没有内置申请地址，请到其控制台创建 API Key。", StatusTone.Info);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(preset.KeyUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"无法打开浏览器：{ex.Message}", StatusTone.Error);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !TryReadConnection(out var baseUrl, out var apiKey))
        {
            return;
        }

        var model = TxtModel.Text.Trim();
        if (model.Length == 0)
        {
            SetStatus("请选择或输入模型 ID。", StatusTone.Error);
            TxtModel.Focus();
            return;
        }

        if (apiKey.Length > 0 && !ApiKeyProtector.IsSupported)
        {
            MessageBox.Show(
                "当前系统不支持 Windows DPAPI，无法安全保存 API Key。",
                "无法保存",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (apiKey.Length == 0 && !LlmEndpoints.IsLoopback(baseUrl))
        {
            var choice = MessageBox.Show(
                "尚未填写 API Key，保存后无法调用云端模型。仍要保存吗？",
                "确认保存",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _draft.Provider = ResolveProviderId(baseUrl);
        _draft.BaseUrl = baseUrl;
        _draft.Model = model;
        _draft.SetApiKey(apiKey);

        if (apiKey.Length > 0 && string.IsNullOrEmpty(_draft.ProtectedApiKey))
        {
            SetStatus("API Key 加密失败（DPAPI 不可用），未保存。", StatusTone.Error);
            return;
        }

        if (!TryApplyOverlayDraft())
        {
            return;
        }

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void BeginBusy()
    {
        _busy = true;
        BtnFetchModels.IsEnabled = false;
        BtnTestModel.IsEnabled = false;
        BtnSave.IsEnabled = false;
        Cursor = Cursors.Wait;
    }

    private void EndBusy()
    {
        _busy = false;
        BtnFetchModels.IsEnabled = true;
        BtnTestModel.IsEnabled = true;
        BtnSave.IsEnabled = true;
        Cursor = null;
    }

    private void SetStatus(string message, StatusTone tone)
    {
        TxtStatus.Text = message;
        TxtStatus.Foreground = tone switch
        {
            StatusTone.Success => SuccessBrush,
            StatusTone.Error => ErrorBrush,
            _ => InfoBrush
        };
    }

    private void ShowSuggestedModels(LlmProviderPreset preset)
    {
        _models.Clear();
        foreach (var id in preset.SuggestedModels)
        {
            _models.Add(new ModelItem
            {
                Id = id,
                Detail = $"{preset.DisplayName} · 预设建议（以获取到的真实列表为准）",
                IsSuggested = true
            });
        }

        TxtModelFilter.Text = string.Empty;
        _modelsView.Refresh();
        UpdateModelCount();
    }

    private void ShowFetchedModels(IReadOnlyList<LlmModelInfo> models)
    {
        _models.Clear();
        foreach (var model in models)
        {
            var detail = model.OwnerDisplay;
            if (!string.IsNullOrEmpty(model.CreatedDisplay))
            {
                detail += " · " + model.CreatedDisplay;
            }

            _models.Add(new ModelItem { Id = model.Id, Detail = detail });
        }

        TxtModelFilter.Text = string.Empty;
        _modelsView.Refresh();
        UpdateModelCount();
    }

    private void UpdateModelCount()
    {
        var visible = _modelsView.Cast<object>().Count();
        TxtModelCount.Text = _models.Count == 0
            ? "暂无模型"
            : $"{visible} / {_models.Count} 个模型";
    }

    private bool FilterModel(object item)
    {
        if (item is not ModelItem model)
        {
            return false;
        }

        var filter = TxtModelFilter.Text?.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            return true;
        }

        return model.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || model.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryReadConnection(out string baseUrl, out string apiKey)
    {
        baseUrl = string.Empty;
        apiKey = CurrentApiKey();

        if (!LlmEndpoints.TryNormalizeBaseUrl(TxtBaseUrl.Text, out var normalized, out var error) ||
            normalized is null)
        {
            SetStatus(error ?? "Base URL 无效", StatusTone.Error);
            TxtBaseUrl.Focus();
            return false;
        }

        if (!string.Equals(TxtBaseUrl.Text.Trim(), normalized, StringComparison.Ordinal))
        {
            TxtBaseUrl.Text = normalized;
        }

        baseUrl = normalized;
        return true;
    }

    private string CurrentApiKey()
    {
        var value = ChkShowKey.IsChecked == true ? TxtApiKeyVisible.Text : PwdApiKey.Password;
        return (value ?? string.Empty).Trim();
    }

    private void FocusKeyInput()
    {
        if (ChkShowKey.IsChecked == true)
        {
            TxtApiKeyVisible.Focus();
        }
        else
        {
            PwdApiKey.Focus();
        }
    }

    private LlmProviderPreset SelectedPreset()
    {
        return CmbProvider.SelectedItem as LlmProviderPreset ?? LlmProviderPresets.Custom;
    }

    /// <summary>根据实际 Base URL 判定服务商，避免"选了 DeepSeek 却填了本地代理"时主界面显示错误。</summary>
    private string ResolveProviderId(string baseUrl)
    {
        return LlmProviderPresets.ResolveDisplay(SelectedPreset().Id, baseUrl).Id;
    }

    private bool TryApplyOverlayDraft()
    {
        if (!TryNormalizeColor(TxtAnswerColor.Text, out var color))
        {
            SetStatus("答案颜色格式应为 #RRGGBB 或颜色名，例如 #00F0FF。", StatusTone.Error);
            TxtAnswerColor.Focus();
            return false;
        }

        _overlayDraft = BuildOverlayDraft(color);
        TxtAnswerColor.Text = color;
        _autoDraft.Enabled = ChkAutoAnswer.IsChecked == true;
        _autoDraft.ExtractAndCorrectQuestion = ChkCorrectQuestion.IsChecked == true;
        _autoDraft.SilenceMs = (int)Math.Round(SldAutoSilence.Value * 1000.0);
        return true;
    }

    private OverlayOptions BuildOverlayDraftFromControls()
    {
        var color = TryNormalizeColor(TxtAnswerColor.Text, out var normalized)
            ? normalized
            : _overlayDraft.AnswerColor;
        return BuildOverlayDraft(color);
    }

    private OverlayOptions BuildOverlayDraft(string answerColor)
    {
        var position = CmbOverlayPosition.SelectedItem as OverlayPositionPreset;
        var custom = string.Equals(position?.Id, OverlayPositionPresets.Custom, StringComparison.OrdinalIgnoreCase);
        var fontName = (CmbOverlayFont.SelectedItem as string)?.Trim();

        var draft = Clone(_overlayDraft);
        draft.Position = position?.Id ?? OverlayPositionPresets.BottomLeft;
        draft.PositionX = custom ? _overlayDraft.PositionX : null;
        draft.PositionY = custom ? _overlayDraft.PositionY : null;
        draft.FontFamily = string.IsNullOrEmpty(fontName) ? "Microsoft YaHei UI" : fontName;
        draft.AnswerFontSize = Math.Round(SldAnswerFontSize.Value);
        draft.QuestionFontSize = Math.Round(SldQuestionFontSize.Value);
        draft.BackgroundOpacity = SldBackgroundOpacity.Value / 100;
        draft.TextOpacity = SldTextOpacity.Value / 100;
        draft.MarginX = Math.Round(SldMarginX.Value);
        draft.MarginY = Math.Round(SldMarginY.Value);
        draft.AnswerColor = answerColor;

        draft.AutoHide = ChkAutoHide.IsChecked == true;
        if (CmbAutoHideSeconds.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var seconds))
        {
            draft.AutoFadeoutSeconds = seconds;
        }

        return draft;
    }

    private static OverlayOptions Clone(OverlayOptions source) => new()
    {
        Opacity = source.Opacity,
        BackgroundOpacity = source.BackgroundOpacity,
        TextOpacity = source.TextOpacity,
        PositionX = source.PositionX,
        PositionY = source.PositionY,
        Position = source.Position,
        MarginX = source.MarginX,
        MarginY = source.MarginY,
        Width = source.Width,
        MinHeight = source.MinHeight,
        FontFamily = source.FontFamily,
        QuestionFontSize = source.QuestionFontSize,
        AnswerFontSize = source.AnswerFontSize,
        AnswerColor = source.AnswerColor,
        ExcludeFromCapture = source.ExcludeFromCapture,
        AutoHide = source.AutoHide,
        AutoFadeoutSeconds = source.AutoFadeoutSeconds
    };

    private static AutoAnswerOptions Clone(AutoAnswerOptions source) => new()
    {
        Enabled = source.Enabled,
        ExtractAndCorrectQuestion = source.ExtractAndCorrectQuestion,
        SilenceMs = source.SilenceMs,
        MinChars = source.MinChars,
        CooldownMs = source.CooldownMs
    };

    private static LlmOptions Clone(LlmOptions source) => new()
    {
        Provider = source.Provider,
        BaseUrl = source.BaseUrl,
        ApiKey = source.ApiKey,
        ProtectedApiKey = source.ProtectedApiKey,
        Model = source.Model,
        MaxChars = source.MaxChars,
        MaxTokens = source.MaxTokens,
        TimeoutMs = source.TimeoutMs,
        FirstTokenTimeoutMs = source.FirstTokenTimeoutMs,
        UserRoleDescription = source.UserRoleDescription,
        UserStance = source.UserStance
    };
    private static AsrOptions Clone(AsrOptions source) => new()
    {
        LoopbackModel = source.LoopbackModel,
        MicModel = source.MicModel,
        ModelRoot = source.ModelRoot,
        Hotwords = new List<string>(source.Hotwords),
        NumThreads = source.NumThreads,
        UseInt8 = source.UseInt8,
        DecodingMethod = source.DecodingMethod
    };
}

/// <summary>ASR 模型选择项。</summary>
public sealed class AsrModelViewItem
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool IsInstalled { get; set; }
}

/// <summary>设置页模型列表中的一行。</summary>
public sealed class ModelItem
{
    public string Id { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public bool IsSuggested { get; init; }
}
