"""特征提取测试。

覆盖队伍聚合、特征向量、哨兵值处理。用合成数据，不含真实玩家信息。
"""

from __future__ import annotations

import pytest

from wows_ml.features import (
    APE_WINRATE_THRESHOLD,
    UNKNOWN,
    MatchFeatures,
    PlayerFeatures,
    aggregate_team,
)


def make_player(
    account_id: str,
    winrate: float,
    battles: float,
    *,
    is_ally: bool = True,
    hidden: bool = False,
    division_size: int = 1,
    tier: int = 10,
    weighted: float | None = None,
    recent20_winrate: float | None = None,
    recent20_battles: int = 0,
) -> PlayerFeatures:
    """构造测试用玩家特征。"""
    recent = UNKNOWN if recent20_winrate is None else recent20_winrate
    return PlayerFeatures(
        account_id=account_id,
        server="Asia",
        account_winrate=UNKNOWN if hidden else winrate,
        account_battles=UNKNOWN if hidden else battles,
        weighted_winrate=weighted if weighted is not None else winrate,
        is_ally=is_ally,
        is_hidden=hidden,
        division_size=division_size,
        ship_tier=tier,
        recent20_winrate=recent,
        recent20_battles=recent20_battles,
    )


class TestTeamAggregation:
    def test_recent_winrate_aggregates_known_players(self) -> None:
        """有近期数据的玩家进平均，哨兵玩家被排除。"""
        players = [
            make_player("1", 0.55, 500, recent20_winrate=0.70),
            make_player("2", 0.55, 500, recent20_winrate=0.50),
            make_player("3", 0.55, 500),  # 无近期数据，哨兵值。
        ]

        result = aggregate_team(players)

        assert result.avg_recent_winrate == pytest.approx(0.60)

    def test_recent_winrate_all_unknown_is_zero(self) -> None:
        """全员无近期数据时聚合保持 0，recent_winrate_diff 随之为 0。"""
        players = [
            make_player("1", 0.55, 500),
            make_player("2", 0.45, 500),
        ]

        result = aggregate_team(players)

        assert result.avg_recent_winrate == 0.0

    def test_recent_winrate_diff_enters_vector(self) -> None:
        """近期胜率差进入特征向量，死特征回归的前提条件。"""
        ally = aggregate_team([make_player("1", 0.55, 500, recent20_winrate=0.70)])
        enemy = aggregate_team([make_player("2", 0.55, 500, recent20_winrate=0.40)])
        match = MatchFeatures(battle_id="t", ally=ally, enemy=enemy, label=1.0)

        vector = match.to_vector()

        assert vector["recent_winrate_diff"] == pytest.approx(0.30)

    def test_computes_averages(self) -> None:
        players = [
            make_player("1", 0.65, 1000),
            make_player("2", 0.45, 800),
            make_player("3", 0.55, 600),
        ]

        result = aggregate_team(players)

        assert result.player_count == 3
        assert result.avg_account_winrate == pytest.approx((0.65 + 0.45 + 0.55) / 3)
        assert result.top_winrate == pytest.approx(0.65)
        assert result.bottom_winrate == pytest.approx(0.45)

    def test_counts_unicum_and_ape(self) -> None:
        players = [
            make_player("1", 0.65, 1000),  # 海王
            make_player("2", 0.45, 800),  # 猴子
            make_player("3", 0.55, 600),
        ]

        result = aggregate_team(players)

        assert result.unicum_count == 1
        assert result.ape_count == 1

    def test_hidden_players_excluded_from_stats(self) -> None:
        players = [
            make_player("1", 0.65, 1000),
            make_player("2", 0.0, 0, hidden=True),
        ]

        result = aggregate_team(players)

        assert result.player_count == 2
        assert result.hidden_count == 1
        assert result.avg_account_winrate == pytest.approx(0.65)

    def test_all_hidden_returns_zero_stats(self) -> None:
        players = [
            make_player("1", 0.0, 0, hidden=True),
            make_player("2", 0.0, 0, hidden=True),
        ]

        result = aggregate_team(players)

        assert result.player_count == 2
        assert result.avg_account_winrate == 0.0
        assert result.unicum_count == 0

    def test_counts_division_groups(self) -> None:
        players = [
            make_player("1", 0.55, 500, division_size=3),
            make_player("2", 0.55, 500, division_size=3),
            make_player("3", 0.55, 500, division_size=3),
            make_player("4", 0.55, 500, division_size=2),
            make_player("5", 0.55, 500, division_size=2),
            make_player("6", 0.55, 500, division_size=1),
        ]

        result = aggregate_team(players)

        assert result.div3_groups == 1
        assert result.div2_groups == 1

    def test_empty_team(self) -> None:
        result = aggregate_team([])

        assert result.player_count == 0
        assert result.avg_account_winrate == 0.0

    def test_unicum_requires_battles_threshold(self) -> None:
        # 胜率够高但场次不足，不算海王。
        players = [make_player("1", 0.70, 100)]

        result = aggregate_team(players)

        assert result.unicum_count == 0

    def test_ape_threshold_boundary(self) -> None:
        players = [make_player("1", APE_WINRATE_THRESHOLD, 500)]

        result = aggregate_team(players)

        assert result.ape_count == 1


class TestFeatureVector:
    def test_produces_all_features(self) -> None:
        match = MatchFeatures(
            battle_id="test",
            ally=aggregate_team([make_player("1", 0.60, 1000)]),
            enemy=aggregate_team([make_player("2", 0.45, 1000)]),
            label=1.0,
        )

        vector = match.to_vector()

        assert vector["winrate_diff"] == pytest.approx(0.15)
        assert vector["weighted_winrate_diff"] == pytest.approx(0.15)
        assert vector["ally_winrate"] == pytest.approx(0.60)
        assert vector["enemy_winrate"] == pytest.approx(0.45)

    def test_feature_names_match_vector_keys(self) -> None:
        match = MatchFeatures(battle_id="test")
        vector = match.to_vector()

        assert set(MatchFeatures.feature_names()) == set(vector.keys())


class TestPlayerFeatures:
    def test_has_stats_true_when_valid(self) -> None:
        player = make_player("1", 0.55, 1000)
        assert player.has_stats

    def test_has_stats_false_when_hidden(self) -> None:
        player = make_player("1", 0.0, 0, hidden=True)
        assert not player.has_stats

    def test_has_stats_false_when_unknown(self) -> None:
        player = PlayerFeatures(account_id="1", server="Asia")
        assert not player.has_stats


class TestShrinkage:
    """经验贝叶斯收缩测试。口径见 src/ml/docs/features-v2-design.md。"""

    def test_shrunk_winrate_pulls_small_samples_to_prior(self) -> None:
        """小样本向先验收缩：10 场全胜不等于 1.0。"""
        from wows_ml.features import shrunk_winrate

        wr = shrunk_winrate(10, 10, 0.5)
        assert 0.5 < wr < 0.8

    def test_shrunk_winrate_converges_for_large_samples(self) -> None:
        """大样本趋近真实胜率。"""
        from wows_ml.features import shrunk_winrate

        # (3000 + 40*0.5) / (5000 + 40)。
        wr = shrunk_winrate(5000, 3000, 0.5)
        assert wr == pytest.approx(3020 / 5040, abs=1e-6)

    def test_class_wr_unknown_without_class_stats(self) -> None:
        player = make_player("1", 0.55, 1000)
        assert player.class_wr_shrunk == UNKNOWN
        assert player.class_specialty == UNKNOWN

    def test_class_wr_shrinks_toward_account_winrate(self) -> None:
        """舰种收缩胜率以账号胜率为先验。"""
        player = make_player("1", 0.50, 1000)
        player.class_battles = 0
        player.class_wins = 0
        # 未玩过该舰种时等于先验，即账号胜率。
        assert player.class_wr_shrunk == pytest.approx(0.50)

        player.class_battles = 100
        player.class_wins = 70
        # 大样本趋近 (70 + 40*0.5) / (100 + 40) = 0.6429。
        assert player.class_wr_shrunk == pytest.approx(90 / 140, abs=1e-6)

    def test_class_specialty_positive_for_specialist(self) -> None:
        player = make_player("1", 0.50, 1000)
        player.class_battles = 1000
        player.class_wins = 600
        assert player.class_specialty > 0

    def test_ship_is_fresh_flags_low_battles(self) -> None:
        player = make_player("1", 0.55, 1000)
        player.ship_battles = 5
        assert player.ship_is_fresh == 1.0

        player.ship_battles = 50
        assert player.ship_is_fresh == 0.0

        player.ship_battles = UNKNOWN
        assert player.ship_is_fresh == UNKNOWN


class TestClassAwareAggregation:
    """分舰种对位聚合测试。"""

    def _make_class_player(
        self,
        account_id: str,
        is_ally: bool,
        ship_class: str,
        class_battles: float,
        class_wins: float,
        winrate: float = 0.50,
    ) -> PlayerFeatures:
        player = make_player(account_id, winrate, 1000, is_ally=is_ally)
        player.ship_class = ship_class
        player.class_battles = class_battles
        player.class_wins = class_wins
        return player

    def test_counts_from_roster_work_without_snapshots(self) -> None:
        """舰种数量来自对局名单，存量无快照数据也可用。"""
        players = [
            self._make_class_player("1", True, "Destroyer", UNKNOWN, UNKNOWN),
            self._make_class_player("2", True, "Destroyer", UNKNOWN, UNKNOWN),
            self._make_class_player("3", False, "Carrier", UNKNOWN, UNKNOWN),
        ]

        ally = aggregate_team(players[:2])
        enemy = aggregate_team(players[2:])

        assert ally.dd_count == 2
        assert enemy.cv_count == 1

    def test_class_wr_averages_known_players_only(self) -> None:
        """收缩胜率均值只含有分舰种数据的玩家。"""
        known = self._make_class_player("1", True, "Battleship", 100, 60)
        unknown = self._make_class_player("2", True, "Battleship", UNKNOWN, UNKNOWN)

        team = aggregate_team([known, unknown])

        assert team.avg_class_wr == pytest.approx(known.class_wr_shrunk)

    def test_all_unknown_class_wr_is_zero(self) -> None:
        """全员无分舰种数据时聚合为 0，差分两侧同为 0。"""
        ally = aggregate_team(
            [self._make_class_player("1", True, "Battleship", UNKNOWN, UNKNOWN)]
        )
        enemy = aggregate_team(
            [self._make_class_player("2", False, "Battleship", UNKNOWN, UNKNOWN)]
        )

        match = MatchFeatures(battle_id="t", ally=ally, enemy=enemy, label=1.0)
        vector = match.to_vector()

        assert vector["ally_class_wr"] == 0.0
        assert vector["class_wr_diff"] == 0.0

    def test_dd_wr_diff_enters_vector(self) -> None:
        """DD 对位差分正确进入向量。"""
        ally_dd = self._make_class_player("1", True, "Destroyer", 100, 65)
        enemy_dd = self._make_class_player("2", False, "Destroyer", 100, 40)

        ally = aggregate_team([ally_dd])
        enemy = aggregate_team([enemy_dd])
        match = MatchFeatures(battle_id="t", ally=ally, enemy=enemy, label=1.0)
        vector = match.to_vector()

        assert vector["dd_wr_diff"] == pytest.approx(
            ally_dd.class_wr_shrunk - enemy_dd.class_wr_shrunk
        )

    def test_new_features_appended_after_legacy(self) -> None:
        """新特征追加在旧 20 维之后，旧顺序不变。"""
        names = MatchFeatures.feature_names()
        assert len(names) == 32
        assert names[:20] == [
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
