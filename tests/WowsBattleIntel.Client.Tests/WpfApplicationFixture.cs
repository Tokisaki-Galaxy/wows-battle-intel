using System.Collections.Concurrent;
using System.Windows;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 进程级共享的 WPF Application 夹具。
/// </summary>
/// <remarks>
/// WPF 限制一个 AppDomain 只能有一个 Application 实例，且资源字典只能在
/// 创建线程上读写。这里在专门的 STA 线程上创建常驻 Application，测试用例
/// 通过队列把操作投递到该线程执行。用 <see cref="CollectionAttribute"/>
/// 标记的测试集合会串行执行，避免并发动同一资源链。
/// </remarks>
[CollectionDefinition("WpfApplication", DisableParallelization = true)]
public sealed class WpfApplicationCollection;

/// <summary>
/// 向共享 WPF 线程投递操作的入口。
/// </summary>
internal static class WpfApplicationFixture
{
    private static readonly ConcurrentQueue<(Action<Application> Action, TaskCompletionSource<Exception?> Done)>
        WorkQueue = new();

    private static readonly AutoResetEvent WorkAvailable = new(false);
    private static Application? application;
    private static Thread? uiThread;

    private static readonly object InitLock = new();
    private static bool initialized;

    /// <summary>
    /// 在共享 UI 线程上执行操作，异常会抛回调用方。
    /// </summary>
    public static void Run(Action<Application> action)
    {
        EnsureInitialized();

        var done = new TaskCompletionSource<Exception?>();
        WorkQueue.Enqueue((action, done));
        WorkAvailable.Set();

        Assert.True(done.Task.Wait(TimeSpan.FromSeconds(30)), "WPF 线程执行超时");
        if (done.Task.Result is { } failure)
        {
            throw failure;
        }
    }

    private static void EnsureInitialized()
    {
        if (initialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (initialized)
            {
                return;
            }

            var ready = new ManualResetEventSlim();
            uiThread = new Thread(() =>
            {
                application = new Application();
                ready.Set();

                // 简单消息泵：处理投递过来的操作。
                while (true)
                {
                    while (WorkQueue.TryDequeue(out var work))
                    {
                        Exception? failure = null;
                        try
                        {
                            work.Action(application);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }

                        work.Done.TrySetResult(failure);
                    }

                    WorkAvailable.WaitOne();
                }
            });

            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.IsBackground = true;
            uiThread.Start();

            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "WPF 线程初始化超时");
            initialized = true;
        }
    }
}
