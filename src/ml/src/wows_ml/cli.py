"""模型训练命令行入口。

用法：
    python -m wows_ml.cli baselines <db>    # 对比基线
    python -m wows_ml.cli train <db>        # 训练并评估模型
    python -m wows_ml.cli dataset <db>      # 数据集统计

默认只使用标准 PvP 对局（Random 与 Ranked）。Coop 等 PvE 模式
胜负与玩家水平关系弱，混入会污染训练集。需要全部类型时加 --all-types。
"""

from __future__ import annotations

import argparse
import sys

from wows_ml.dataset import DEFAULT_BATTLE_TYPES, load_battles
from wows_ml.train import compare_baselines, train_lightgbm, train_logistic


def _resolve_types(all_types: bool) -> tuple[str, ...]:
    """把 CLI 的全类型开关转成类型白名单。

    --all-types 时返回空元组，表示不做类型过滤。
    """
    return () if all_types else DEFAULT_BATTLE_TYPES


def cmd_dataset(
    db_path: str,
    allow_future: bool = False,
    all_types: bool = False,
) -> int:
    """数据集统计。"""
    matches, stats = load_battles(
        db_path,
        allow_future_snapshots=allow_future,
        allowed_battle_types=_resolve_types(all_types),
    )
    print("数据集统计")
    print(f"  对局总数:     {stats.total_battles}")
    print(f"  跳过（非 PvP 类型）: {stats.skipped_battle_type}")
    print(f"  可用于训练:   {stats.usable_battles}")
    print(f"  跳过（无结果）: {stats.skipped_no_result}")
    print(f"  跳过（无特征）: {stats.skipped_no_features}")
    print(f"  玩家记录总数:  {stats.total_players}")
    print(f"  有效特征对局:  {len(matches)}")

    if stats.future_snapshots_used > 0:
        print()
        print(f"  警告: {stats.future_snapshots_used} 个玩家使用了晚于对局时间的快照。")
        print("        存在时间泄漏，评估结果偏乐观，不可用于判断真实预测能力。")

    if matches:
        with_features = sum(
            1 for m in matches if m.ally.avg_account_winrate > 0 or m.enemy.avg_account_winrate > 0
        )
        print(f"  含账号胜率特征: {with_features}")
    return 0


def cmd_baselines(db_path: str, allow_future: bool = False, all_types: bool = False) -> int:
    """对比基线。"""
    matches, stats = load_battles(
        db_path,
        allow_future_snapshots=allow_future,
        allowed_battle_types=_resolve_types(all_types),
    )
    if not matches:
        print(f"没有可用样本。统计: {stats}")
        if stats.skipped_no_features > 0 and stats.future_snapshots_used == 0:
            print()
            print("提示: 无特征通常是因为快照时间晚于对局时间。")
            print("      事后补采的数据默认被拒绝以避免时间泄漏。")
            print("      若要验证管线，可加 --allow-future 参数，但结果偏乐观。")
        return 1

    print(compare_baselines(matches))
    return 0


def cmd_train(
    db_path: str,
    model_type: str,
    allow_future: bool = False,
    all_types: bool = False,
) -> int:
    """训练并评估。"""
    matches, stats = load_battles(
        db_path,
        allow_future_snapshots=allow_future,
        allowed_battle_types=_resolve_types(all_types),
    )
    if stats.future_snapshots_used > 0:
        print(f"警告: 使用 {stats.future_snapshots_used} 个未来快照，结果偏乐观。")
        print()
    if len(matches) < 10:
        print(f"样本不足（{len(matches)} 条），无法训练。先积累数据。")
        print(f"统计: {stats}")
        return 1

    print(f"训练样本: {len(matches)} 条")
    if stats.skipped_battle_type > 0:
        print(f"已跳过非 PvP 对局: {stats.skipped_battle_type} 局")
    print()
    print(compare_baselines(matches))
    print()

    if model_type in ("logistic", "all"):
        print("=== 逻辑回归 ===")
        _, result = train_logistic(matches)
        print(result.summary())
        print()

    if model_type in ("lightgbm", "all"):
        print("=== LightGBM ===")
        try:
            _, result = train_lightgbm(matches)
            print(result.summary())
        except ImportError:
            print("LightGBM 未安装，跳过。")

    return 0


def cmd_baselines_compute(db_path: str) -> int:
    """计算并保存服务器基线。"""
    from wows_ml.baselines import compute_baselines, describe, save_baselines

    baselines = compute_baselines(db_path)
    if not baselines:
        print("样本不足，无法计算基线。需要至少 30 个有效玩家每服务器。")
        return 1

    save_baselines(db_path, baselines)
    print(describe(baselines))
    print()
    print(f"已保存 {len(baselines)} 个服务器的基线到库。")
    return 0


def cmd_pipeline(
    db_path: str,
    model_path: str,
    manifest_path: str,
    model_type: str = "logistic",
    allow_future: bool = False,
    all_types: bool = False,
) -> int:
    """训练到导出的完整管线。"""
    from wows_ml.pipeline import describe, run

    try:
        result = run(
            db_path,
            model_path,
            manifest_path,
            model_type=model_type,
            allow_future_snapshots=allow_future,
            allowed_battle_types=_resolve_types(all_types),
        )
    except (ValueError, RuntimeError) as ex:
        print(f"管线失败: {ex}")
        return 1

    print("=== 训练导出管线 ===")
    print(describe(result))
    return 0


def main(argv: list[str] | None = None) -> int:
    """入口。"""
    parser = argparse.ArgumentParser(
        prog="wows-ml",
        description="wows-battle-intel 模型训练与评估",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_dataset = sub.add_parser("dataset", help="数据集统计")
    p_dataset.add_argument("db", help="SQLite 数据库路径")
    p_dataset.add_argument(
        "--allow-future",
        action="store_true",
        help="允许使用晚于对局的快照（会时间泄漏，仅用于管线验证）",
    )
    p_dataset.add_argument(
        "--all-types",
        action="store_true",
        help="不过滤对局类型，把 Coop 等 PvE 模式也纳入（默认只留 PvP）",
    )

    p_baselines = sub.add_parser("baselines", help="对比基线")
    p_baselines.add_argument("db", help="SQLite 数据库路径")
    p_baselines.add_argument(
        "--allow-future",
        action="store_true",
        help="允许使用晚于对局的快照（会时间泄漏，仅用于管线验证）",
    )
    p_baselines.add_argument(
        "--all-types",
        action="store_true",
        help="不过滤对局类型，把 Coop 等 PvE 模式也纳入（默认只留 PvP）",
    )

    p_train = sub.add_parser("train", help="训练模型")
    p_train.add_argument("db", help="SQLite 数据库路径")
    p_train.add_argument(
        "--model",
        choices=["logistic", "lightgbm", "all"],
        default="all",
        help="模型类型",
    )
    p_train.add_argument(
        "--allow-future",
        action="store_true",
        help="允许使用晚于对局的快照（会时间泄漏，仅用于管线验证）",
    )
    p_train.add_argument(
        "--all-types",
        action="store_true",
        help="不过滤对局类型，把 Coop 等 PvE 模式也纳入（默认只留 PvP）",
    )

    p_server = sub.add_parser("server-baselines", help="计算服务器基线")
    p_server.add_argument("db", help="SQLite 数据库路径")

    p_pipeline = sub.add_parser("pipeline", help="训练到导出的完整管线")
    p_pipeline.add_argument("db", help="SQLite 数据库路径")
    p_pipeline.add_argument("model", help="输出 ONNX 模型路径")
    p_pipeline.add_argument("manifest", help="输出模型清单路径")
    p_pipeline.add_argument(
        "--model-type",
        choices=["logistic", "lightgbm"],
        default="logistic",
        help="模型类型",
    )
    p_pipeline.add_argument(
        "--allow-future",
        action="store_true",
        help="允许使用晚于对局的快照（会时间泄漏，仅用于管线验证）",
    )
    p_pipeline.add_argument(
        "--all-types",
        action="store_true",
        help="不过滤对局类型，把 Coop 等 PvE 模式也纳入（默认只留 PvP）",
    )

    args = parser.parse_args(argv)

    if args.command == "dataset":
        return cmd_dataset(args.db, args.allow_future, args.all_types)
    if args.command == "baselines":
        return cmd_baselines(args.db, args.allow_future, args.all_types)
    if args.command == "train":
        return cmd_train(args.db, args.model, args.allow_future, args.all_types)
    if args.command == "pipeline":
        return cmd_pipeline(
            args.db, args.model, args.manifest, args.model_type, args.allow_future,
            args.all_types,
        )
    if args.command == "server-baselines":
        return cmd_baselines_compute(args.db)

    parser.print_help()
    return 1


if __name__ == "__main__":
    sys.exit(main())
