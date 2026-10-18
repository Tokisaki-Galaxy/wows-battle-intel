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
    private const string MutexName = "Local\\WowsBattleIntel.Client.SingleInstance";
    private const uint WmShowSelf = 0x0400 + 0x5117;

    private Mutex? mutex;

    /// <summary>
    /// 尝试成为第一实例。
    /// </summary>
    /// <remarks>
    /// 只申请互斥体，不做前台广播，供第二实例判断前的第一步使用。申请失败
    /// 说明已有实例在跑。
    /// </remarks>
    /// <returns>成为第一实例返回 true。</returns>
    public bool TryAcquire()
    {
        mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        return createdNew;
    }

    /// <summary>
    /// 通知已运行的实例把主窗口带到前台。
    /// </summary>
    /// <remarks>
    /// 按窗口标题找 MainWindow。标题固定含应用名，不会撞上其它进程的窗口。
    /// 找到后发一条自定义消息，窗口过程里还原并激活自己。
    /// </remarks>
    public static void SignalExistingInstance()
    {
        nint handle = NativeMethods.FindWindowByCaption(
            string.Empty, "wows-battle-intel · 战斗情报与胜负预测");
        if (handle != 0)
        {
            _ = NativeMethods.PostMessage(handle, WmShowSelf, 0, 0);
        }
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

    /// <summary>Win32 导入。互斥体之外的窗口操作只此两个，不值得引库。</summary>
    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern nint FindWindowByCaption(string zeroOnly, string lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    }
}
