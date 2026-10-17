namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 让同一批 xUnit 测试类串行执行。
/// </summary>
/// <remarks>
/// 同一条用例会打真实的网络请求（统计数据补拉，测试环境里必定超时），且要
/// 观察超时收尾的时序。与其它用例并发执行时，机器上的连接与线程池被抢满，
/// 收尾时间随之漂移，造成与业务逻辑无关的偶发失败。串行化只牺牲该类的
/// 并发度，换取结果稳定。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SequentialTestCollection
{
    /// <summary>集合名。</summary>
    public const string Name = "sequential";
}
