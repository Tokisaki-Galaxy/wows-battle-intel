wows-intel 命令行工具
====================

wows-battle-intel 的离线分析与批量处理工具，面向高级用户。
日常使用请下载客户端 WowsBattleIntel.Client.exe，本工具不是必需的。

一、运行环境

本工具为框架依赖发布，需要已安装 .NET 10 运行时。
未安装时前往 https://dotnet.microsoft.com/download/dotnet/10.0
下载并安装 .NET 10 Runtime (win-x64)。

首次启动时会把内置的原生库解压到 %TEMP%\.net 下，
这是 .NET 单文件发布的正常行为，不影响使用。

二、常用命令

执行下面的命令查看完整用法：

    wows-intel.exe --help

主要能力：

  import              把解析结果批量导入本地数据库
  backtest            批量预测并对照真实结果，输出校准曲线
  survey              统计解析结果的分布
  predict             对历史特征做预测并与真实结果比对
  history-backtest    用近期状态特征回测，避免时间泄漏
  dedupe-battles      清理同局重复与非 PvP 的预测记录

三、说明

多数命令需要先准备解析结果目录。回放解析依赖 replayshark，
与客户端共用同一套游戏目录，详见主包说明。
