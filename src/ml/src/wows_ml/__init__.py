"""wows-battle-intel 模型训练与特征工程管线。

模块划分：
- features：从对局记录提取特征向量
- dataset：从 SQLite 读取并构造训练集
- train：训练与评估
- export：导出 ONNX 供客户端推理
- calibration：校准指标计算
"""

__version__ = "0.1.0"
