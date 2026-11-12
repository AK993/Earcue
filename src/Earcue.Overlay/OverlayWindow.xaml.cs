using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Earcue.Overlay.Native;

namespace Earcue.Overlay;

public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _fadeoutTimer;
    private bool _isExcludedFromCapture = true;
    private double _targetOpacity = 0.95;

    public OverlayWindow()
    {
        InitializeComponent();

        _fadeoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _fadeoutTimer.Tick += (s, e) => FadeOut();

        Loaded += OnLoaded;
    }

    public void Configure(double opacity, bool excludeFromCapture, int autoFadeoutSeconds)
    {
        _targetOpacity = opacity;
        _isExcludedFromCapture = excludeFromCapture;
        _fadeoutTimer.Interval = TimeSpan.FromSeconds(autoFadeoutSeconds > 0 ? autoFadeoutSeconds : 20);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowStyles();
        RepositionToBottomRight();
    }

    public void ApplyWindowStyles()
    {
        var helper = new WindowInteropHelper(this);
        var hWnd = helper.Handle;

        if (hWnd != IntPtr.Zero)
        {
            // 设置不抢焦点、点击穿透、不在任务栏显示
            int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_LAYERED |
                       NativeMethods.WS_EX_TRANSPARENT |
                       NativeMethods.WS_EX_TOOLWINDOW |
                       NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE, exStyle);

            // 设置屏幕捕获排除 (会议共享屏幕时不可见)
            if (_isExcludedFromCapture)
            {
                NativeMethods.SetWindowDisplayAffinity(hWnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
            }
            else
            {
                NativeMethods.SetWindowDisplayAffinity(hWnd, NativeMethods.WDA_NONE);
            }

            // 保持置顶且不激活
            NativeMethods.SetWindowPos(hWnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    public void RepositionToBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Bottom - Height - 24;
    }

    public void DisplayCard(string question, string answer, double latencyMs = 0)
    {
        Dispatcher.Invoke(() =>
        {
            TxtQuestion.Text = question;
            TxtAnswer.Text = answer;
            TxtLatency.Text = latencyMs > 0 ? $"{latencyMs:0.0}s" : "";

            // 重启淡出倒计时
            _fadeoutTimer.Stop();
            _fadeoutTimer.Start();

            // 展现动画
            Visibility = Visibility.Visible;
            BeginAnimation(OpacityProperty, null);
            var fadeIn = new DoubleAnimation(_targetOpacity, TimeSpan.FromMilliseconds(150));
            BeginAnimation(OpacityProperty, fadeIn);

            RepositionToBottomRight();
        });
    }

    public void UpdateStreamingAnswer(string partialAnswer)
    {
        Dispatcher.Invoke(() =>
        {
            TxtAnswer.Text = partialAnswer;
        });
    }

    public void FadeOut()
    {
        Dispatcher.Invoke(() =>
        {
            _fadeoutTimer.Stop();
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(250));
            fadeOut.Completed += (s, e) => Visibility = Visibility.Collapsed;
            BeginAnimation(OpacityProperty, fadeOut);
        });
    }
}
