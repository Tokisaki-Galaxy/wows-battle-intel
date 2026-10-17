using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Abstractions;

/// <summary>
/// 对局列表的读取入口，供界面左侧列表使用。
/// </summary>
/// <remarks>
/// 与 <see cref="IBattleRepository"/> 分开，是因为列表只需要若干列，
/// 不必加载完整名单。实现可以用单条 SQL 一次取完，避免逐局往返。
/// </remarks>
public interface IBattleListProvider
{
    /// <summary>
    /// 按开战时间倒序取列表项。
    /// </summary>
    /// <param name="limit">最多返回多少局。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按开战时间倒序的列表项。</returns>
    Task<IReadOnlyList<BattleListEntry>> GetBattleListAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量移除赛前占位记录，连同其参战者与预测。
    /// </summary>
    /// <remarks>
    /// 同一局的占位记录在正式回放记录已存在时应当消失，否则列表会出现
    /// 两条几乎相同的条目。只删标识带 :pre 后缀的记录。
    /// </remarks>
    /// <param name="placeholderIds">要删除的占位记录标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际删除的对局条数。</returns>
    Task<int> DeletePrebattlePlaceholdersAsync(
        IReadOnlyCollection<string> placeholderIds,
        CancellationToken cancellationToken = default);
}
