using System.Threading;
using System.Windows;
using Earcue.Core.Logging;

namespace Earcue.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\Earcue_SingleInstance_Mutex";
    private const string RestoreEventName = @"Local\Earcue_Restore_Event";

    private Mutex? _mutex;
    private EventWaitHandle? _restoreEvent;
    private CancellationTokenSource? _listenCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool createdNew;
        try
        {
            _mutex = new Mutex(true, MutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例异常中止，当前实例接管锁所有权
            createdNew = true;
        }

        if (!createdNew)
        {
            // 已有实例在运行，通知已有实例唤起并退出当前重复进程
            try
            {
                using var restoreEvent = EventWaitHandle.OpenExisting(RestoreEventName);
                restoreEvent.Set();
            }
            catch
            {
                // 若打开信号量失败，静默退出
            }

            Shutdown();
            return;
        }

        // 第一个实例：创建唤醒信号量，并在后台监听外部重复启动时的唤起信号
        try
        {
            _restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RestoreEventName);
            _listenCts = new CancellationTokenSource();
            StartListeningForRestoreSignal(_restoreEvent, _listenCts.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warning(ex, "创建单实例唤醒信号量失败");
        }

        base.OnStartup(e);
    }

    private void StartListeningForRestoreSignal(EventWaitHandle handle, CancellationToken ct)
    {
        Task.Run(() =>
        {
            var waitHandles = new WaitHandle[] { handle, ct.WaitHandle };
            while (!ct.IsCancellationRequested)
            {
                var index = WaitHandle.WaitAny(waitHandles);
                if (index == 0) // handle signaled
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is MainWindow mw)
                        {
                            mw.RestoreMainWindow();
                        }
                    });
                }
                else
                {
                    break;
                }
            }
        }, ct);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _listenCts?.Cancel();
        _listenCts?.Dispose();
        _restoreEvent?.Dispose();
        if (_mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch { }
            _mutex.Dispose();
        }

        base.OnExit(e);
    }
}

