using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Earcue.Core.Configuration;
using Earcue.Overlay.Native;

namespace Earcue.Overlay;

public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _fadeoutTimer;
    private OverlayOptions _options = new();
    private double _targetOpacity = 0.92;
    private int _fadeGeneration;
    private bool _autoHideEnabled;
    private bool _editMode;

    public OverlayWindow()
    {
        InitializeComponent();

        _fadeoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _fadeoutTimer.Tick += (_, _) => FadeOut();

        Loaded += OnLoaded;
    }

    /// <summary>拖动/缩放完成后触发，MainWindow 负责写回 config.json。</summary>
    public event Action<OverlayLayout>? LayoutChanged;

    public bool IsEditMode => _editMode;

    private double EffectiveOpacity => _editMode ? Math.Max(_targetOpacity, 0.45) : _targetOpacity;

    /// <summary>应用透明度、位置、字体、字号、颜色、捕获排除等外观设置；可在运行中重复调用。</summary>
    public void Configure(OverlayOptions options)
    {
        _options = options ?? new OverlayOptions();
        _targetOpacity = Math.Clamp(_options.Opacity, 0.05, 1.0);
        _autoHideEnabled = _options.AutoHide;
        _fadeoutTimer.Interval = TimeSpan.FromSeconds(
            _options.AutoFadeoutSeconds > 0 ? _options.AutoFadeoutSeconds : 20);

        Width = Math.Clamp(_options.Width > 0 ? _options.Width : 460, 300, 1600);
        MinHeight = Math.Clamp(_options.MinHeight, 0, 1200);

        if (!_autoHideEnabled)
        {
            _fadeoutTimer.Stop();
        }
        else if (Visibility == Visibility.Visible && !_editMode)
        {
            RestartAutoHideTimer();
        }

        if (IsLoaded)
        {
            ApplyWindowStyles();
            ApplyAppearance();
            Reposition();

            if (Visibility == Visibility.Visible)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = EffectiveOpacity;
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowStyles();
        ApplyAppearance();
        Reposition();
    }

    public void ApplyWindowStyles()
    {
        var hWnd = new WindowInteropHelper(this).Handle;
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW;

        if (_editMode)
        {
            // 编辑模式：接收鼠标，允许拖动/缩放；暂时取消点击穿透
            exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
            exStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
        }
        else
        {
            exStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;
        }

        NativeMethods.SetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE, exStyle);

        NativeMethods.SetWindowDisplayAffinity(
            hWnd,
            _options.ExcludeFromCapture ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);

        NativeMethods.SetWindowPos(
            hWnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>根据目标坐标动态解析所在显示器的工作区（DIP 逻辑像素），完美支持副屏与多屏边界。</summary>
    private Rect GetTargetWorkArea(double targetX, double targetY)
    {
        var hWnd = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        var dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        var hMonitor = IntPtr.Zero;
        if (hWnd != IntPtr.Zero)
        {
            var pt = new NativeMethods.POINT((int)Math.Round(targetX * dpiX), (int)Math.Round(targetY * dpiY));
            hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);

            if (hMonitor == IntPtr.Zero)
            {
                hMonitor = NativeMethods.MonitorFromWindow(hWnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            }
        }

        if (hMonitor != IntPtr.Zero)
        {
            var mi = new NativeMethods.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO));
            if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            {
                return new Rect(
                    mi.rcWork.Left / dpiX,
                    mi.rcWork.Top / dpiY,
                    (mi.rcWork.Right - mi.rcWork.Left) / dpiX,
                    (mi.rcWork.Bottom - mi.rcWork.Top) / dpiY);
            }
        }

        return SystemParameters.WorkArea;
    }

    /// <summary>按配置的四角位置与边距定位；若配置了绝对坐标（拖动后）则优先使用绝对坐标，并自动适配所在多显示器的工作区。</summary>
    public void Reposition()
    {
        UpdateLayout();

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 120;
        var marginX = Math.Clamp(_options.MarginX, 0, 400);
        var marginY = Math.Clamp(_options.MarginY, 0, 400);

        double left;
        double top;
        Rect workArea;

        if (_options.PositionX is { } absoluteX && _options.PositionY is { } absoluteY)
        {
            left = absoluteX;
            top = absoluteY;
            workArea = GetTargetWorkArea(left, top);
        }
        else
        {
            workArea = GetTargetWorkArea(Left, Top);
            var corner = OverlayPositionPresets.Find(_options.Position).Id;
            left = corner is OverlayPositionPresets.BottomRight or OverlayPositionPresets.TopRight
                ? workArea.Right - width - marginX
                : workArea.Left + marginX;

            top = corner is OverlayPositionPresets.BottomLeft or OverlayPositionPresets.BottomRight
                ? workArea.Bottom - height - marginY
                : workArea.Top + marginY;
        }

        var maxLeft = Math.Max(workArea.Left + 4, workArea.Right - width - 4);
        var maxTop = Math.Max(workArea.Top + 4, workArea.Bottom - height - 4);
        Left = Math.Clamp(left, workArea.Left + 4, maxLeft);
        Top = Math.Clamp(top, workArea.Top + 4, maxTop);
    }

    public void SetEditMode(bool enabled)
    {
        Dispatcher.Invoke(() =>
        {
            _editMode = enabled;
            EditBorder.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            ResizeGrip.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            CardBorder.Cursor = enabled ? Cursors.SizeAll : Cursors.Arrow;

            ApplyWindowStyles();

            if (enabled)
            {
                _fadeoutTimer.Stop();
                Visibility = Visibility.Visible;
                BeginAnimation(OpacityProperty, null);
                Opacity = EffectiveOpacity;

                if (string.IsNullOrWhiteSpace(TxtAnswer.Text))
                {
                    DisplayCard("[预览] 字幕卡调整模式", "拖动卡片移动位置，拖右下角手柄调整大小。", 0);
                }

                ReassertTopmost();
                UpdateLayout();
                Reposition();
            }
            else
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = _targetOpacity;
                if (_autoHideEnabled)
                {
                    RestartAutoHideTimer();
                }
            }
        });
    }

    public void DisplayCard(string question, string answer, double latencySeconds = 0)
    {
        Dispatcher.Invoke(() =>
        {
            _fadeGeneration++;

            TxtQuestion.Text = question;
            TxtAnswer.Text = answer;
            TxtLatency.Text = latencySeconds > 0 ? $"{latencySeconds:0.0}s" : string.Empty;

            // 默认不自动隐藏；只有用户显式开启 autoHide 才开始倒计时
            _fadeoutTimer.Stop();
            if (_autoHideEnabled && !_editMode)
            {
                RestartAutoHideTimer();
            }

            var wasHidden = Visibility != Visibility.Visible;
            Visibility = Visibility.Visible;

            // 取消上一次淡出动画，确保卡片一定能重新出现
            BeginAnimation(OpacityProperty, null);
            var target = EffectiveOpacity;
            var from = wasHidden ? 0.0 : Math.Min(Math.Max(Opacity, 0.01), target);
            Opacity = from;
            var fadeIn = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(150));
            BeginAnimation(OpacityProperty, fadeIn);

            // 其他置顶窗口可能抢到最上层，显示时重新声明 topmost
            ReassertTopmost();

            UpdateLayout();
            Reposition();
        });
    }

    public void UpdateStreamingAnswer(string partialAnswer)
    {
        Dispatcher.Invoke(() =>
        {
            TxtAnswer.Text = partialAnswer;

            // 流式输出期间重置自动隐藏倒计时，避免回答还没写完就淡出
            if (_autoHideEnabled && !_editMode)
            {
                RestartAutoHideTimer();
            }

            // 左下角锚定：答案变长时卡片向上生长，需要重新定位
            UpdateLayout();
            Reposition();
        });
    }

    public void FadeOut()
    {
        Dispatcher.Invoke(() =>
        {
            if (_editMode)
            {
                return;
            }

            _fadeoutTimer.Stop();
            var generation = ++_fadeGeneration;

            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(250));
            fadeOut.Completed += (_, _) =>
            {
                // 淡出期间如果来了新卡片，不要把它误隐藏
                if (generation == _fadeGeneration)
                {
                    Visibility = Visibility.Collapsed;
                }
            };
            BeginAnimation(OpacityProperty, fadeOut);
        });
    }

    /// <summary>启动时立即隐藏，不播放示例卡片。</summary>
    public void HideImmediately()
    {
        Dispatcher.Invoke(() =>
        {
            _fadeoutTimer.Stop();
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Visibility = Visibility.Collapsed;
        });
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标状态异常时忽略
        }

        e.Handled = true;
        _options.Position = OverlayPositionPresets.Custom;
        _options.PositionX = Left;
        _options.PositionY = Top;
        RaiseLayoutChanged();
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_editMode)
        {
            return;
        }

        Width = Math.Clamp(Width + e.HorizontalChange, 300, 1600);
        MinHeight = Math.Clamp(Math.Max(ActualHeight, MinHeight) + e.VerticalChange, 0, 1200);
        UpdateLayout();
        Reposition();
    }

    private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_editMode)
        {
            return;
        }

        _options.Width = Width;
        _options.MinHeight = MinHeight;
        _options.Position = OverlayPositionPresets.Custom;
        _options.PositionX = Left;
        _options.PositionY = Top;
        RaiseLayoutChanged();
    }

    private void RaiseLayoutChanged()
    {
        _options.Position = OverlayPositionPresets.Custom;
        _options.PositionX = Left;
        _options.PositionY = Top;
        _options.Width = Width;
        _options.MinHeight = MinHeight;
        LayoutChanged?.Invoke(new OverlayLayout(Left, Top, Width, MinHeight));
    }

    private void RestartAutoHideTimer()
    {
        if (!_autoHideEnabled || _editMode)
        {
            return;
        }

        _fadeoutTimer.Stop();
        _fadeoutTimer.Start();
    }

    private void ReassertTopmost()
    {
        var hWnd = new WindowInteropHelper(this).Handle;
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            hWnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void ApplyAppearance()
    {
        var family = ResolveFontFamily(_options.FontFamily);
        FontFamily = family;

        var questionSize = Math.Clamp(_options.QuestionFontSize, 10, 40);
        var answerSize = Math.Clamp(_options.AnswerFontSize, 12, 56);

        TxtQuestion.FontFamily = family;
        TxtQuestion.FontSize = questionSize;
        TxtQuestion.LineHeight = Math.Round(questionSize * 1.5);
        TxtQuestion.MaxHeight = Math.Round(questionSize * 3.6);

        TxtAnswer.FontFamily = family;
        TxtAnswer.FontSize = answerSize;
        TxtAnswer.LineHeight = Math.Round(answerSize * 1.5);
        TxtAnswer.Foreground = new SolidColorBrush(ResolveColor(_options.AnswerColor, Color.FromRgb(0x00, 0xF0, 0xFF)));

        // 不再限制回答字数：卡片允许长到约 2/3 屏，超出才截断
        TxtAnswer.MaxHeight = Math.Max(140, SystemParameters.WorkArea.Height * 0.65);
    }

    private static FontFamily ResolveFontFamily(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return SystemFonts.MessageFontFamily;
        }

        try
        {
            return new FontFamily(name.Trim());
        }
        catch
        {
            return SystemFonts.MessageFontFamily;
        }
    }

    private static Color ResolveColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return ColorConverter.ConvertFromString(value.Trim()) is Color color ? color : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
