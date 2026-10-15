using System.Collections.ObjectModel;
using System.Windows.Threading;
using WowsBattleIntel.Client.Services;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 运行日志页签的视图模型。
/// </summary>
/// <remarks>
/// 订阅 <see cref="LogService.EntryLogged"/>，把条目追加进集合。服务端环形
/// 缓冲容量 500，集合同步截断到同一容量，避免长会话下集合无限增长。
/// 构造时先取一次快照，页面打开能看到此前累积的日志。
/// </remarks>
public sealed class LogViewModel : ViewModelBase
{
    private readonly int constructorThreadId;
    private readonly Dispatcher? dispatcher;

    /// <summary>
    /// 构造视图模型并订阅日志服务。
    /// </summary>
    /// <remarks>
    /// 构造发生在界面线程，记录该线程并尝试取得其调度器。后台线程写入日志
    /// 时经调度器封送；无调度器的环境（测试宿主）退化为直接追加。
    /// </remarks>
    public LogViewModel(LogService logs)
    {
        ArgumentNullException.ThrowIfNull(logs);

        var currentThread = Thread.CurrentThread;
        constructorThreadId = currentThread.ManagedThreadId;
        dispatcher = Dispatcher.FromThread(currentThread);

        foreach (var entry in logs.Snapshot())
        {
            Entries.Add(entry);
        }

        logs.EntryLogged += OnEntryLogged;
    }

    /// <summary>界面侧日志条目。顺序与写入顺序一致。</summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>集合追加新条目后触发，供视图做自动滚动。</summary>
    public event Action? EntriesAppended;

    private void OnEntryLogged(LogEntry entry)
    {
        if (Thread.CurrentThread.ManagedThreadId == constructorThreadId)
        {
            Append(entry);
            return;
        }

        // 跨线程写入时封送回构造线程，保证集合只在单线程上变更。
        if (dispatcher is not null)
        {
            dispatcher.BeginInvoke(() => Append(entry));
        }
        else
        {
            Append(entry);
        }
    }

    private void Append(LogEntry entry)
    {
        Entries.Add(entry);
        if (Entries.Count > LogService.Capacity)
        {
            Entries.RemoveAt(0);
        }

        EntriesAppended?.Invoke();
    }
}
