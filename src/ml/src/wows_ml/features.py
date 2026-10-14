"""特征提取。

与 C# 端 WowsBattleIntel.Core 的特征定义保持一致，
确保训练与推理使用同一套特征口径。见 docs/feature-engineering.md。

设计原则：
- 赛前可得：只用开局前能拿到的信息
- 相对优于绝对：胜率转服务器内相对位置
- 短期与长期分开：账号总胜率与近期状态是独立信号
- 可复现：特征快照落盘，历史预测可回放
"""

from __future__ import annotations

from dataclasses import dataclass, field

# 阈值为 ApeRadar 默认口径，作为兼容契约保持稳定。
APE_WINRATE_THRESHOLD = 0.47
UNICUM_WINRATE_THRESHOLD = 0.58
UNICUM_BATTLES_THRESHOLD = 500.0

# 哨兵值：负数表示未知，与 C# 端一致。
UNKNOWN = -1.0


@dataclass
class PlayerFeatures:
    """单个玩家在预测时点的特征。"""

    account_id: str
    server: str
    account_battles: float = UNKNOWN
    account_winrate: float = UNKNOWN
    account_winrate_solo: float = UNKNOWN
    account_battles_solo: float = UNKNOWN
    account_winrate_div2: float = UNKNOWN
    account_battles_div2: float = UNKNOWN
    account_winrate_div3: float = UNKNOWN
    account_battles_div3: float = UNKNOWN
    weighted_winrate: float = UNKNOWN
    ship_battles: float = UNKNOWN
    ship_winrate: float = UNKNOWN
    ship_avg_damage: float = UNKNOWN
    ship_tier: int = 0
    ship_class: str = "Unknown"
    division_size: int = 1
    is_ally: bool = True
    is_hidden: bool = False
    recent20_winrate: float = UNKNOWN
    recent20_battles: int = 0
    winrate_percentile: float = UNKNOWN

    @property
    def has_stats(self) -> bool:
        """统计是否可用。隐藏战绩或未查到时为 False。"""
        return not self.is_hidden and self.account_winrate >= 0


@dataclass
class TeamFeatures:
    """队伍层面的聚合特征。"""

    player_count: int = 0
    bot_count: int = 0
    hidden_count: int = 0
    avg_account_winrate: float = 0.0
    avg_weighted_winrate: float = 0.0
    avg_recent_winrate: float = 0.0
    avg_battles: float = 0.0
    top_winrate: float = 0.0
    bottom_winrate: float = 0.0
    unicum_count: int = 0
    ape_count: int = 0
    avg_tier: float = 0.0
    div2_groups: int = 0
    div3_groups: int = 0


@dataclass
class MatchFeatures:
    """一场对局的完整特征，用于训练与推理。"""

    battle_id: str
    battle_type: str = "Unknown"
    server: str = "Auto"
    map_id: str = ""
    ally: TeamFeatures = field(default_factory=TeamFeatures)
    enemy: TeamFeatures = field(default_factory=TeamFeatures)
    # 标签：1 表示友方获胜，0 表示失败，None 表示未知。
    label: float | None = None

    def to_vector(self) -> dict[str, float]:
        """展平成可用于训练的数值向量。

        使用双方差分而非绝对值，差分通常更有判别力且更稳定。
        """
        a, e = self.ally, self.enemy
        return {
            "winrate_diff": a.avg_account_winrate - e.avg_account_winrate,
            "weighted_winrate_diff": a.avg_weighted_winrate - e.avg_weighted_winrate,
            "recent_winrate_diff": a.avg_recent_winrate - e.avg_recent_winrate,
            "battles_diff": a.avg_battles - e.avg_battles,
            "unicum_diff": a.unicum_count - e.unicum_count,
            "ape_diff": a.ape_count - e.ape_count,
            "tier_diff": a.avg_tier - e.avg_tier,
            "player_count_diff": a.player_count - e.player_count,
            "ally_winrate": a.avg_account_winrate,
            "enemy_winrate": e.avg_account_winrate,
            "ally_weighted": a.avg_weighted_winrate,
            "enemy_weighted": e.avg_weighted_winrate,
            "ally_battles": a.avg_battles,
            "enemy_battles": e.avg_battles,
            "ally_top_winrate": a.top_winrate,
            "enemy_top_winrate": e.top_winrate,
            "ally_ape_count": float(a.ape_count),
            "enemy_ape_count": float(e.ape_count),
            "ally_unicum_count": float(a.unicum_count),
            "enemy_unicum_count": float(e.unicum_count),
        }

    @staticmethod
    def feature_names() -> list[str]:
        """特征列名的固定顺序，训练与推理必须一致。"""
        return [
            "winrate_diff",
            "weighted_winrate_diff",
            "recent_winrate_diff",
            "battles_diff",
            "unicum_diff",
            "ape_diff",
            "tier_diff",
            "player_count_diff",
            "ally_winrate",
            "enemy_winrate",
            "ally_weighted",
            "enemy_weighted",
            "ally_battles",
            "enemy_battles",
            "ally_top_winrate",
            "enemy_top_winrate",
            "ally_ape_count",
            "enemy_ape_count",
            "ally_unicum_count",
            "enemy_unicum_count",
        ]


def aggregate_team(players: list[PlayerFeatures]) -> TeamFeatures:
    """把单玩家特征聚合成队伍特征。

    与 C# 端 TeamAggregator.Aggregate 逻辑一致。
    """
    human = [p for p in players if not p.is_hidden or True]  # 隐藏玩家计入人数
    with_stats = [p for p in players if p.has_stats]

    result = TeamFeatures(
        player_count=len(players),
        hidden_count=sum(1 for p in players if p.is_hidden),
    )

    if not with_stats:
        return result

    result.avg_account_winrate = sum(p.account_winrate for p in with_stats) / len(with_stats)
    result.avg_battles = sum(p.account_battles for p in with_stats) / len(with_stats)
    result.top_winrate = max(p.account_winrate for p in with_stats)
    result.bottom_winrate = min(p.account_winrate for p in with_stats)

    weighted = [p for p in with_stats if p.weighted_winrate >= 0]
    if weighted:
        result.avg_weighted_winrate = sum(p.weighted_winrate for p in weighted) / len(weighted)

    recent = [p for p in with_stats if p.recent20_winrate >= 0]
    if recent:
        result.avg_recent_winrate = sum(p.recent20_winrate for p in recent) / len(recent)

    result.unicum_count = sum(
        1
        for p in with_stats
        if p.account_winrate > UNICUM_WINRATE_THRESHOLD
        and p.account_battles >= UNICUM_BATTLES_THRESHOLD
    )
    result.ape_count = sum(
        1 for p in with_stats if 0 <= p.account_winrate <= APE_WINRATE_THRESHOLD
    )
    result.avg_tier = sum(p.ship_tier for p in players) / len(players) if players else 0.0
    result.div2_groups = sum(1 for p in players if p.division_size == 2) // 2
    result.div3_groups = sum(1 for p in players if p.division_size == 3) // 3

    _ = human
    return result
