wows-battle-intel 解压即用绿色包
====================================

一、使用步骤

1. 将本目录解压到任意位置，路径建议不含特殊字符。
2. 双击 WowsBattleIntel.Client.exe 启动。
3. 首次运行请打开设置页完成以下配置：
   - 回放目录：填写 World of Warships 的 replays 文件夹，
     通常位于游戏安装目录下，例如 D:\Games\World_of_Warships\replays。
   - WG API ID：可选。不填时使用内置共享 ID，
     需要独立配额的用户可在 Wargaming 开发者中心申请。
4. 其余路径项留空即可，程序会自动使用包内默认值。

二、运行环境

本包为框架依赖发布，需要已安装 .NET 10 桌面运行时。
未安装时前往 https://dotnet.microsoft.com/download/dotnet/10.0
下载并安装 .NET Desktop Runtime 10.0 (win-x64)。

三、数据说明

- 所有配置与数据存储在 %APPDATA%\wows-battle-intel，
  删除本目录不会影响已采集的数据。
- 包内 models\baseline.onnx 为默认预测模型，随包自动加载。
- 如需更换模型，在设置页选择其他 .onnx 文件即可。

四、附带工具

wows-intel.exe 为命令行工具，供高级用户离线补采与校验，
执行 wows-intel.exe --help 查看用法。

五、目录结构

WowsBattleIntel.Client.exe  主程序
wows-intel.exe              命令行工具
onnxruntime.dll             ONNX 推理原生库
e_sqlite3.dll               SQLite 原生库
models\baseline.onnx        默认预测模型
config.sample.json          配置模板
