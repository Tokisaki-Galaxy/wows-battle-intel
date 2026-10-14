using System.Collections.ObjectModel;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Calibration;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 校准统计视图模型。
/// </summary>
/// <remarks>
/// 这是本项目的核心卖点：把"预测准不准"量化出来，而不是只给一个胜率数字。
/// 数据来自本地库已回填真实结果的预测记录，报告本身是可重复的计算，
/// 每次查看结果一致。
/// </remarks>
public sealed class CalibrationViewModel : ViewModelBase
{
    /// <summary>无信息基线的 Brier 分数。永远预测 0.5 时的期望值。</summary>
    public const double UninformativeBrierScore = 0.25;

    /// <summary>无信息基线的对数损失。</summary>
    public const double UninformativeLogLoss = 0.6931;

    /// <summary>校准曲线柱状条的最大像素宽度。</summary>
    private const double MaxBarWidth = 150;

    private readonly AppServices services;

    private int sampleCount;
    private double brierScore = -1;
    private double logLoss = -1;
    private double accuracy = -1;
    private bool isBusy;
    private string statusMessage = "尚未统计";

    /// <summary>
    /// 构造视图模型。
    /// </summary>
    public CalibrationViewModel(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;

        RefreshCommand = new RelayCommand(
            async _ => await LoadAsync().ConfigureAwait(true),
            _ => !IsBusy);
    }

    /// <summary>校准曲线各分桶。</summary>
    public ObservableCollection<CalibrationBucketViewModel> Buckets { get; } = [];

    /// <summary>重新统计命令。</summary>
    public System.Windows.Input.ICommand RefreshCommand { get; }

    /// <summary>参与统计的样本数。</summary>
    public int SampleCount
    {
        get => sampleCount;
        private set => SetField(ref sampleCount, value);
    }

    /// <summary>Brier 分数，-1 表示无样本。</summary>
    public double BrierScore
    {
        get => brierScore;
        private set
        {
            if (SetField(ref brierScore, value))
            {
                OnPropertyChanged(nameof(BrierScoreText));
                OnPropertyChanged(nameof(BrierComparisonText));
                OnPropertyChanged(nameof(QualityText));
            }
        }
    }

    /// <summary>对数损失，-1 表示无样本。</summary>
    public double LogLoss
    {
        get => logLoss;
        private set
        {
            if (SetField(ref logLoss, value))
            {
                OnPropertyChanged(nameof(LogLossText));
                OnPropertyChanged(nameof(LogLossComparisonText));
            }
        }
    }

    /// <summary>判别准确率，-1 表示无样本。</summary>
    public double Accuracy
    {
        get => accuracy;
        private set
        {
            if (SetField(ref accuracy, value))
            {
                OnPropertyChanged(nameof(AccuracyText));
            }
        }
    }

    /// <summary>Brier 分数显示文本。</summary>
    public string BrierScoreText => HasReport ? brierScore.ToString("F4") : "-";

    /// <summary>对数损失显示文本。</summary>
    public string LogLossText => HasReport ? logLoss.ToString("F4") : "-";

    /// <summary>判别准确率显示文本。</summary>
    public string AccuracyText => HasReport ? accuracy.ToString("P1") : "-";

    /// <summary>Brier 分数与无信息基线的对照说明。</summary>
    public string BrierComparisonText => !HasReport
        ? "无信息基线 0.2500"
        : brierScore < UninformativeBrierScore
            ? $"优于无信息基线 0.2500，低 {UninformativeBrierScore - brierScore:F4}"
            : $"未超过无信息基线 0.2500，高 {brierScore - UninformativeBrierScore:F4}";

    /// <summary>对数损失与无信息基线的对照说明。</summary>
    public string LogLossComparisonText => !HasReport
        ? "无信息基线 0.6931"
        : logLoss < UninformativeLogLoss
            ? $"优于无信息基线 0.6931，低 {UninformativeLogLoss - logLoss:F4}"
            : $"未超过无信息基线 0.6931，高 {logLoss - UninformativeLogLoss:F4}";

    /// <summary>样本规模显示文本。</summary>
    public string SampleCountText => sampleCount == 0
        ? "暂无已回填结果的预测"
        : $"共 {sampleCount} 局已回填真实结果";

    /// <summary>总体质量结论。</summary>
    public string QualityText => !HasReport
        ? "样本不足，无法给出结论"
        : brierScore < 0.20
            ? "预测质量良好"
            : brierScore < UninformativeBrierScore
                ? "预测优于基线，仍有提升空间"
                : "预测未超过基线，需要改进特征或模型";

    /// <summary>是否已有可用报告。</summary>
    public bool HasReport => sampleCount > 0 && brierScore >= 0;

    /// <summary>是否正在统计。</summary>
    public bool IsBusy
    {
        get => isBusy;
        private set => SetField(ref isBusy, value);
    }

    /// <summary>状态栏消息。</summary>
    public string StatusMessage
    {
        get => statusMessage;
        private set => SetField(ref statusMessage, value);
    }

    /// <summary>
    /// 从本地库读取预测样本并重新计算校准报告。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusMessage = "正在统计校准指标...";

        try
        {
            var samples = await services.Repository
                .GetCalibrationSamplesAsync(limit: 100_000, cancellationToken)
                .ConfigureAwait(true);

            var report = CalibrationMetrics.BuildReport(
                samples.Select(s => (s.Prediction, s.Outcome)));

            ApplyReport(report);
            StatusMessage = HasReport
                ? $"统计完成 · {QualityText}"
                : "尚无已回填真实结果的预测，先解析几局回放再来。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "统计已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"统计失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 用一份已算好的报告刷新界面。抽出来便于测试。
    /// </summary>
    public void ApplyReport(CalibrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        SampleCount = report.SampleCount;
        BrierScore = report.BrierScore;
        LogLoss = report.LogLoss;
        Accuracy = report.Accuracy;

        Buckets.Clear();
        foreach (var bucket in report.Curve)
        {
            Buckets.Add(CalibrationBucketViewModel.FromBucket(bucket, MaxBarWidth));
        }

        OnPropertyChanged(nameof(SampleCountText));
    }
}
