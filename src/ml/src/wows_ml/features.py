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

# 经验贝叶斯收缩的先验等效场次。分舰种与单船共用。
# 与设计文档 src/ml/docs/features-v2-design.md 的口径一致。
SHRINKAGE_M = 40.0

# 单船白板判定阈值。低于该场次视为不熟悉该船。
FRESH_SHIP_BATTLES = 10.0


def shrunk_winrate(
    battles: float,
    wins: float,
    prior: float,
    m: float = SHRINKAGE_M,
) -> float:
    """经验贝叶斯收缩胜率。

    shrunk = (wins + m * prior) / (battles + m)。
    小样本向先验收缩，未玩过时结果等于先验。
    """
    if battles < 0:
        return UNKNOWN
    return (wins + m * prior) / (battles + m)


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
    # 分舰种统计：本局舰种的场次与胜场。快照缺失时为 UNKNOWN。
    class_battles: float = UNKNOWN
    class_wins: float = UNKNOWN
    # 单船统计的胜场数，供收缩计算。快照缺失时为 UNKNOWN。
    ship_wins: float = UNKNOWN

    @property
    def has_stats(self) -> bool:
        """统计是否可用。隐藏战绩或未查到时为 False。"""
        return not self.is_hidden and self.account_winrate >= 0

    @property
    def has_class_stats(self) -> bool:
        """本局舰种的分舰种统计是否可用。"""
        return self.class_battles >= 0 and self.class_wins >= 0

    @property
    def class_wr_shrunk(self) -> float:
        """本局舰种的收缩胜率。

        先验用账号自身胜率，缺失时取 0.5。口径见设计文档。
        """
        if not self.has_class_stats:
            return UNKNOWN
        prior = self.account_winrate if self.account_winrate >= 0 else 0.5
        return shrunk_winrate(self.class_battles, self.class_wins, prior)

    @property
    def class_specialty(self) -> float:
        """舰种专精度：本局舰种收缩胜率减账号整体胜率。

        正值表示玩家在该舰种比自身平均水平更强。
        """
        wr = self.class_wr_shrunk
        if wr < 0:
            return UNKNOWN
        base = self.account_winrate if self.account_winrate >= 0 else 0.5
        return wr - base

    @property
    def ship_wr_shrunk(self) -> float:
        """单船收缩胜率，先验同为账号自身胜率。"""
        if self.ship_battles < 0 or self.ship_wins < 0:
            return UNKNOWN
        prior = self.account_winrate if self.account_winrate >= 0 else 0.5
        return shrunk_winrate(self.ship_battles, self.ship_wins, prior)

    @property
    def ship_is_fresh(self) -> float:
        """白板船标记。1 表示本局船只场次低于阈值，UNKNOWN 表示未知。"""
        if self.ship_battles < 0:
            return UNKNOWN
        return 1.0 if self.ship_battles < FRESH_SHIP_BATTLES else 0.0


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
    # 分舰种对位特征。舰种键与采集端 ClassStatsJson 一致：DD/CA/BB/CV/SS。
    # class 系列依赖快照分舰种数据，存量数据为 0，采集推进后生效。
    avg_class_wr: float = 0.0
    dd_wr: float = 0.0
    cv_wr: float = 0.0
    # 舰种数量来自对局名单，不依赖快照，存量数据立即可用。
    dd_count: int = 0
    cv_count: int = 0
    fresh_ship_count: int = 0


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
    # 本局全部参战者的账号标识，用于按玩家分组交叉验证。
    # 与特征向量无关，导出与推理不使用。
    participant_account_ids: tuple[str, ...] = ()

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
            # 分舰种熟练度特征，v2 追加。
            "ally_class_wr": a.avg_class_wr,
            "enemy_class_wr": e.avg_class_wr,
            "class_wr_diff": a.avg_class_wr - e.avg_class_wr,
            "ally_dd_wr": a.dd_wr,
            "enemy_dd_wr": e.dd_wr,
            "dd_wr_diff": a.dd_wr - e.dd_wr,
            "ally_cv_wr": a.cv_wr,
            "enemy_cv_wr": e.cv_wr,
            "cv_wr_diff": a.cv_wr - e.cv_wr,
            "dd_count_diff": float(a.dd_count - e.dd_count),
            "cv_count_diff": float(a.cv_count - e.cv_count),
            "fresh_ship_diff": float(a.fresh_ship_count - e.fresh_ship_count),
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
            # 分舰种熟练度特征，v2 追加，旧 20 维顺序保持不变。
            "ally_class_wr",
            "enemy_class_wr",
            "class_wr_diff",
            "ally_dd_wr",
            "enemy_dd_wr",
            "dd_wr_diff",
            "ally_cv_wr",
            "enemy_cv_wr",
            "cv_wr_diff",
            "dd_count_diff",
            "cv_count_diff",
            "fresh_ship_diff",
        ]


def aggregate_team(players: list[PlayerFeatures]) -> TeamFeatures:
    """把单玩家特征聚合成队伍特征。

    与 C# 端 TeamAggregator.Aggregate 逻辑一致。
    """
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

    # 分舰种对位特征。舰种数量从对局名单统计，不依赖快照。
    result.dd_count = sum(1 for p in players if p.ship_class == "Destroyer")
    result.cv_count = sum(1 for p in players if p.ship_class == "Carrier")

    # 收缩胜率类特征只聚合有分舰种统计的玩家，哨兵玩家跳过。
    # 全队都无数据时保持 0，差分两侧同为 0，不产生虚假信号。
    with_class = [p for p in with_stats if p.class_wr_shrunk >= 0]
    if with_class:
        result.avg_class_wr = sum(p.class_wr_shrunk for p in with_class) / len(with_class)

    dd_players = [p for p in with_class if p.ship_class == "Destroyer"]
    if dd_players:
        result.dd_wr = sum(p.class_wr_shrunk for p in dd_players) / len(dd_players)

    cv_players = [p for p in with_class if p.ship_class == "Carrier"]
    if cv_players:
        result.cv_wr = sum(p.class_wr_shrunk for p in cv_players) / len(cv_players)

    fresh = [p for p in players if p.ship_is_fresh >= 0]
    result.fresh_ship_count = sum(1 for p in fresh if p.ship_is_fresh == 1.0)

    return result
