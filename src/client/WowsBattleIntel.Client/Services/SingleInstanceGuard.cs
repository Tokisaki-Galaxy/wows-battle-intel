namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 进程级单实例守卫。
/// </summary>
/// <remarks>
/// 用命名互斥体保证客户端同时只跑一份。已有实例时第二份拿到的是被占有的
/// 互斥体，此时不退出就完事：通过向既有实例广播自定义消息把它的主窗口拉到
/// 前台，第二份自己安静退出。用户感觉就是"重复打开时跳出之前的窗口"。
///
/// 只在 GUI 客户端使用；CLI 与测试宿主不经这里，互不影响。
///
/// 消息广播选 WM_USER 区间的自定义消息而不是命名管道：只需要"让窗口到前台"
/// 这一个动作，不需要传数据，消息够用且不引入管线线程。找不到窗口句柄时
/// 退化为只退出，行为与旧版一致。
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>单实例广播用的自定义消息。取 WM_APP 段，避开窗口类私有的 WM_USER 段。</summary>
    internal const uint WmShowSelf = 0x8000 + 0x5117;

    private const string MutexName = "Local\\WowsBattleIntel.Client.SingleInstance";

    private Mutex? mutex;

    /// <summary>
    /// 尝试成为第一实例。
    /// </summary>
    /// <remarks>
    /// 先创建互斥体再尝试立即接管。上个实例崩溃时 OS 会把互斥体标记为
    /// abandoned，WaitOne 抛 <see cref="AbandonedMutexException"/>，此时持有权
    /// 已落到本线程，视为成功——否则崩溃后第一次启动会被误判成第二实例。
    /// </remarks>
    /// <returns>成为第一实例返回 true。</returns>
    public bool TryAcquire()
    {
        mutex = new Mutex(initiallyOwned: false, MutexName, out _);

        // 不管互斥体是不是本次新建，所有权都必须真正拿到手。createdNew 只
        // 说明命名对象刚被创建，不等于持有：initiallyOwned 传 false 时第一
        // 实例不拥有它，第二实例的 WaitOne 就能成功接管，单实例形同虚设。
        try
        {
            return mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // 前一个实例异常退出，持有权已由本次 WaitOne 接管，照常成为第一实例。
            return true;
        }
    }

    /// <summary>
    /// 通知已运行的实例把主窗口带到前台。
    /// </summary>
    /// <remarks>
    /// 按窗口标题找 MainWindow。标题固定含应用名，不会撞上其它进程的窗口。
    ///
    /// 前台切换由第二实例直接完成：刚被用户启动的进程天然持有前台权限
    /// （参见 Raymond Chen 的经典文章，置前权限"只能给不能抢"），它能置前
    /// 就能直接把第一实例的窗口设为前台。让第一实例收到消息后自己 Activate
    /// 的路径在双击场景下不可靠——第二实例尚未创建窗口，权限让渡实际不生效。
    ///
    /// 消息仍要发：WPF 内部状态同步（Activate、最小化还原的属性侧）交给
    /// 第一实例自己做，两边各管一半。
    ///
    /// 局限：第二实例权限低于第一实例时（如前者以管理员运行），UIPI 会
    /// 静默丢弃消息与置前，此时退化为第二实例只退出，与旧版行为一致。
    /// </remarks>
    public static void SignalExistingInstance()
    {
        nint handle = NativeMethods.FindWindowByCaption(null, "wows-battle-intel · 战斗情报与胜负预测");
        if (handle == 0)
        {
            return;
        }

        // 最小化的窗口先还原，置前才有意义。
        if (NativeMethods.IsIconic(handle))
        {
            _ = NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
        }

        // 第二实例刚被用户启动，持有前台权限，直接置前。
        _ = NativeMethods.SetForegroundWindow(handle);

        // 广播消息让第一实例同步 WPF 内部状态。
        _ = NativeMethods.PostMessage(handle, WmShowSelf, 0, 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 只有拥有者释放；第二实例的互斥体对象没被拥有，直接丢弃。
        if (mutex is { } owned)
        {
            try
            {
                owned.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 当前线程不持有（TryAcquire 失败路径），无需释放。
            }

            owned.Dispose();
            mutex = null;
        }
    }

    /// <summary>Win32 导入。互斥体之外的窗口操作只此几个，不值得引库。</summary>
    internal static class NativeMethods
    {
        /// <summary>窗口还原命令值。</summary>
        internal const int SwRestore = 9;

        /// <summary>按标题找顶层窗口。第一个参数传 null 表示忽略类名；传空串会被当成类名匹配，永远找不到。</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "FindWindowW")]
        internal static extern nint FindWindowByCaption(string? zeroOnly, string lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool IsIconic(nint hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(nint hWnd);
    }
}
