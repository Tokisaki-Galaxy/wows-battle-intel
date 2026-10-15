using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Abstractions;

/// <summary>
/// 军团分档的持久化缓存。
/// </summary>
/// <remarks>
/// 军团分档查询一次要走三个 API 调用，成本高。按标签落库缓存，
/// TTL 一周，过期行在写入时顺带清理。接口放 Core 供 Stats 层
/// 面向抽象消费，Storage 层提供 SQLite 实现。
/// </remarks>
public interface IClanGradeCache
{
    /// <summary>
    /// 取一批标签的未过期分档信息。键为标签，缺失的标签不在返回值里。
    /// </summary>
    /// <param name="tags">军团标签，统一为不带方括号的大写形式。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyDictionary<string, ClanGradeInfo>> GetClanGradesAsync(
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量写入分档信息，同标签覆盖旧值，并顺带删除全表过期行。
    /// </summary>
    /// <param name="grades">分档信息集合。</param>
    /// <param name="queriedAt">查询时间，作为 TTL 起点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveClanGradesAsync(
        IReadOnlyList<ClanGradeInfo> grades,
        DateTimeOffset queriedAt,
        CancellationToken cancellationToken = default);
}
