using WowsBattleIntel.Client.ViewModels;
using WowsBattleIntel.Core.Calibration;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Tests;

public class CalibrationViewModelTests
{
    [Fact]
    public async Task EmptyReport_ShowsNoConclusion()
    {
        using var holder = await TestServices.CreateAsync();
        var vm = new CalibrationViewModel(holder.Services);

        vm.ApplyReport(new CalibrationReport
        {
            SampleCount = 0,
            BrierScore = -1,
            LogLoss = -1,
            Accuracy = -1,
            Curve = [],
        });

        Assert.False(vm.HasReport);
        Assert.Equal("-", vm.BrierScoreText);
        Assert.Equal("-", vm.LogLossText);
        Assert.Equal("-", vm.AccuracyText);
        Assert.Empty(vm.Buckets);
        Assert.Equal("样本不足，无法给出结论", vm.QualityText);
    }

    [Fact]
    public async Task GoodReport_ShowsMetricsAndConclusion()
    {
        using var holder = await TestServices.CreateAsync();
        var vm = new CalibrationViewModel(holder.Services);

        vm.ApplyReport(new CalibrationReport
        {
            SampleCount = 120,
            BrierScore = 0.18,
            LogLoss = 0.52,
            Accuracy = 0.71,
            Curve = [],
        });

        Assert.True(vm.HasReport);
        Assert.Equal("0.1800", vm.BrierScoreText);
        Assert.Equal("0.5200", vm.LogLossText);
        Assert.Equal("71.0%", vm.AccuracyText);
        Assert.Equal("预测质量良好", vm.QualityText);
        Assert.Contains("优于无信息基线", vm.BrierComparisonText);
        Assert.Contains("共 120 局", vm.SampleCountText);
    }

    [Fact]
    public async Task BrierAboveBaseline_ReportsNotBeatingBaseline()
    {
        using var holder = await TestServices.CreateAsync();
        var vm = new CalibrationViewModel(holder.Services);

        vm.ApplyReport(new CalibrationReport
        {
            SampleCount = 50,
            BrierScore = 0.30,
            LogLoss = 0.75,
            Accuracy = 0.5,
            Curve = [],
        });

        Assert.Equal("预测未超过基线，需要改进特征或模型", vm.QualityText);
        Assert.Contains("未超过无信息基线", vm.BrierComparisonText);
        Assert.Contains("未超过无信息基线", vm.LogLossComparisonText);
    }

    [Fact]
    public async Task ApplyReport_BuildsOneBucketPerCurveEntry()
    {
        using var holder = await TestServices.CreateAsync();
        var vm = new CalibrationViewModel(holder.Services);

        vm.ApplyReport(new CalibrationReport
        {
            SampleCount = 10,
            BrierScore = 0.2,
            LogLoss = 0.5,
            Accuracy = 0.8,
            Curve =
            [
                new CalibrationBucket
                {
                    LowerBound = 0.0,
                    UpperBound = 0.1,
                    SampleCount = 4,
                    PredictedMean = 0.05,
                    ActualWinrate = 0.0,
                },
                new CalibrationBucket
                {
                    LowerBound = 0.9,
                    UpperBound = 1.0,
                    SampleCount = 6,
                    PredictedMean = 0.95,
                    ActualWinrate = 1.0,
                },
            ],
        });

        Assert.Equal(2, vm.Buckets.Count);
        Assert.Equal("0.0-0.1", vm.Buckets[0].RangeText);
        Assert.Equal("0.9-1.0", vm.Buckets[1].RangeText);
        Assert.Equal("0.0%", vm.Buckets[0].ActualWinrateText);
        Assert.Equal("100.0%", vm.Buckets[1].ActualWinrateText);
    }

    [Fact]
    public async Task LoadAsync_NoSamples_ReportsNoData()
    {
        using var holder = await TestServices.CreateAsync();
        var vm = new CalibrationViewModel(holder.Services);

        await vm.LoadAsync();

        Assert.False(vm.HasReport);
        Assert.Equal(0, vm.SampleCount);
        Assert.Contains("尚无已回填真实结果的预测", vm.StatusMessage);
    }

    [Fact]
    public async Task LoadAsync_WithBackfilledPredictions_ComputesReport()
    {
        using var holder = await TestServices.CreateAsync();
        var repository = holder.Services.Repository;

        // 四局，预测方向全对，验证报告从库里正确算出。
        for (int i = 0; i < 4; i++)
        {
            bool win = i < 2;
            string battleId = $"battle-{i:D3}";
            await repository.SaveBattleAsync(new BattleRecord
            {
                BattleId = battleId,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                Outcome = win ? BattleOutcome.Win : BattleOutcome.Loss,
            });
            await repository.SavePredictionAsync(new PredictionResult
            {
                BattleId = battleId,
                PredictedAt = DateTimeOffset.UtcNow,
                AllyWinProbability = win ? 0.8 : 0.2,
            });
            await repository.BackfillOutcomeAsync(
                battleId,
                win ? BattleOutcome.Win : BattleOutcome.Loss);
        }

        var vm = new CalibrationViewModel(holder.Services);
        await vm.LoadAsync();

        Assert.True(vm.HasReport);
        Assert.Equal(4, vm.SampleCount);
        Assert.Equal(1.0, vm.Accuracy, precision: 6);
        // 预测 0.8/0.2 且全对，Brier 应为 0.04。
        Assert.Equal(0.04, vm.BrierScore, precision: 6);
    }
}
