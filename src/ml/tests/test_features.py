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
) -> PlayerFeatures:
    """构造测试用玩家特征。"""
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
    )


class TestTeamAggregation:
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
