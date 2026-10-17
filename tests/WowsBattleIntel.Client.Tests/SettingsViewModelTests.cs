using System.IO;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Client.ViewModels;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Tests;

public class SettingsViewModelTests
{
    /// <summary>返回预设路径的路径选择器替身。</summary>
    private sealed class FakePathPicker : IPathPicker
    {
        public string? FolderResult { get; set; }

        public string? FileResult { get; set; }

        public string? LastFolderTitle { get; private set; }

        public string? LastFileFilter { get; private set; }

        public string? PickFolder(string title, string currentPath)
        {
            LastFolderTitle = title;
            return FolderResult;
        }

        public string? PickFile(string title, string filter, string currentPath)
        {
            LastFileFilter = filter;
            return FileResult;
        }
    }

    private static AppConfig MakeConfig(string databasePath) => new()
    {
        DatabasePath = databasePath,
        ModelVersion = "baseline-logistic-v1",
    };

    [Fact]
    public async Task Constructor_LoadsFieldsFromConfig()
    {
        string dbPath = TestServices.NewDatabasePath();
        using var holder = await TestServices.CreateAsync(MakeConfig(dbPath));

        var vm = new SettingsViewModel(holder.Services, new FakePathPicker());

        Assert.Equal(dbPath, vm.DatabasePath);
        Assert.Equal("baseline-logistic-v1", vm.ModelVersion);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task EditingField_MarksUnsaved()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker());

        vm.ModelVersion = "changed";

        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task BrowseFolder_AppliesPickedPath()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var picker = new FakePathPicker { FolderResult = @"D:\Games\World_of_Warships" };
        var vm = new SettingsViewModel(holder.Services, picker);

        // 游戏目录不存在时校验会拦，这里只验证选择器结果被写入字段。
        vm.BrowseGamePathCommand.Execute(null);

        Assert.Equal(@"D:\Games\World_of_Warships", vm.GamePath);
        Assert.NotNull(picker.LastFolderTitle);
    }

    [Fact]
    public async Task BrowseFolder_Cancelled_KeepsCurrentValue()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var picker = new FakePathPicker { FolderResult = null };
        var vm = new SettingsViewModel(holder.Services, picker);
        string original = vm.GamePath;

        vm.BrowseGamePathCommand.Execute(null);

        Assert.Equal(original, vm.GamePath);
    }

    [Fact]
    public async Task Validate_EmptyDatabasePath_Fails()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
            DatabasePath = string.Empty,
        };

        Assert.Contains("数据库路径不能为空", vm.Validate());
    }

    [Fact]
    public async Task Validate_MissingModelFile_Fails()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
            ModelPath = @"Z:\does\not\exist.onnx",
        };

        Assert.Contains("模型文件不存在", vm.Validate());
    }

    [Fact]
    public async Task Validate_TooShortPollInterval_Fails()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
            PollIntervalSeconds = 1,
        };

        Assert.Contains("轮询间隔不能小于 3 秒", vm.Validate());
    }

    [Fact]
    public async Task Validate_ValidSettings_Passes()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker());

        Assert.Null(vm.Validate());
    }

    [Fact]
    public async Task Validate_NegativeBattleCacheDays_Fails()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
            BattleCacheDays = -1,
        };

        Assert.Contains("战绩缓存天数不能为负", vm.Validate());
    }

    [Fact]
    public async Task BattleCache_RoundTripsThroughConfig()
    {
        // 缓存开关与时长应能写盘再读回，0 天在配置里换算成"不过期"。
        string dbPath = TestServices.NewDatabasePath();
        using var holder = await TestServices.CreateAsync(MakeConfig(dbPath));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
            EnableBattleCache = true,
            BattleCacheDays = 3,
        };

        await vm.SaveAsync();

        Assert.True(holder.Services.Config.EnableBattleCache);
        Assert.Equal(3, holder.Services.Config.BattleCacheDays);
        Assert.Equal(TimeSpan.FromDays(3), holder.Services.Config.BattleCacheTtl);

        holder.Services.Config.BattleCacheDays = 0;
        Assert.Equal(TimeSpan.MaxValue, holder.Services.Config.BattleCacheTtl);
    }

    [Fact]
    public async Task ColorRules_DefaultTwoRulesWithExpectedThresholds()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker());

        // 固定两条：紫在前红在后，阈值按界面百分数展示。
        Assert.Equal(2, vm.ColorRules.Count);
        Assert.Equal("紫色", vm.ColorRules[0].ColorLabel);
        Assert.Equal("红色", vm.ColorRules[1].ColorLabel);
        Assert.Equal("RowUnicumBrush", vm.ColorRules[0].RowBrushKey);
        Assert.Equal("RowApeBrush", vm.ColorRules[1].RowBrushKey);
        Assert.Equal(58, vm.ColorRules[0].Threshold, 3);
        Assert.Equal(47, vm.ColorRules[1].Threshold, 3);
    }

    [Fact]
    public async Task ColorRules_EditRoundTripsAsFraction()
    {
        string dbPath = TestServices.NewDatabasePath();
        using var holder = await TestServices.CreateAsync(MakeConfig(dbPath));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker());

        // 界面按百分数改成 60，落盘应是 0.6 的小数。
        vm.ColorRules[0].Threshold = 60;
        Assert.True(vm.HasUnsavedChanges);

        await vm.SaveAsync();

        Assert.Equal(0.6, holder.Services.Config.ColorRules[0].Threshold, 3);
    }

    [Fact]
    public async Task Validate_ColorRuleOver100Percent_Fails()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
        };
        vm.ColorRules[0].Threshold = 120;

        Assert.Contains("胜率阈值应在 0 到 100 之间", vm.Validate());
    }

    [Fact]
    public async Task SaveAsync_InvalidSettings_DoesNotApply()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
            ModelPath = @"Z:\does\not\exist.onnx",
        };

        bool applied = false;
        vm.SettingsApplied += () => applied = true;

        await vm.SaveAsync();

        Assert.False(applied);
        Assert.Contains("模型文件不存在", vm.StatusMessage);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveAsync_ValidSettings_AppliesAndClearsDirty()
    {
        string dbPath = TestServices.NewDatabasePath();
        using var holder = await TestServices.CreateAsync(MakeConfig(dbPath));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
            ModelVersion = "baseline-logistic-v2",
        };

        bool applied = false;
        vm.SettingsApplied += () => applied = true;

        await vm.SaveAsync();

        Assert.True(applied);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal("baseline-logistic-v2", holder.Services.Config.ModelVersion);
        Assert.Contains("已保存", vm.StatusMessage);
    }

    [Fact]
    public async Task SaveAsync_SwitchingDatabase_PointsRepositoryAtNewFile()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        string newDb = TestServices.NewDatabasePath();

        var vm = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
            DatabasePath = newDb,
        };

        await vm.SaveAsync();

        // 新库应已建表并可用。
        Assert.Equal(0, await holder.Services.Repository.CountBattlesAsync());
        await holder.Services.Repository.SaveBattleAsync(new BattleRecord
        {
            BattleId = "battle-001",
            StartedAt = DateTimeOffset.UtcNow,
            Outcome = BattleOutcome.Win,
        });
        Assert.Equal(1, await holder.Services.Repository.CountBattlesAsync());

        // 清理新库文件。
        TryDelete(newDb);
    }

    // 日志设置的测试：默认口径、校验、往返、打开目录与立即清理。

    /// <summary>记录被打开目录的替身启动器。</summary>
    private sealed class FakeFolderLauncher : IFolderLauncher
    {
        public string? OpenedPath { get; private set; }

        public bool Result { get; set; } = true;

        public bool Open(string path)
        {
            OpenedPath = path;
            return Result;
        }
    }

    [Fact]
    public async Task LogSettings_DefaultToThreeDaysAndTenMb()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker());

        Assert.Equal(3, vm.LogRetentionDays);
        Assert.Equal(10, vm.LogMaxSizeMb);
        Assert.Equal(0, vm.LogRetentionModeIndex);
        Assert.False(string.IsNullOrWhiteSpace(vm.LogDirectory));
        Assert.Contains("3 天", vm.LogRetentionSummary);
        Assert.Contains("10 MB", vm.LogRetentionSummary);
    }

    [Fact]
    public async Task LogSettings_OpenFolder_UsesLauncher()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var launcher = new FakeFolderLauncher();
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker(), launcher)
        {
            LogDirectory = @"Z:\some\logs",
        };

        vm.OpenLogFolderCommand.Execute(null);

        Assert.Equal(@"Z:\some\logs", launcher.OpenedPath);
        Assert.Contains("已打开", vm.StatusMessage);
    }

    [Fact]
    public async Task LogSettings_OpenFolder_FailureReportsStatus()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));
        var launcher = new FakeFolderLauncher { Result = false };
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker(), launcher);

        vm.OpenLogFolderCommand.Execute(null);

        Assert.Contains("无法打开", vm.StatusMessage);
    }

    [Fact]
    public async Task LogSettings_RoundTripsThroughConfig()
    {
        string dbPath = TestServices.NewDatabasePath();
        using var holder = await TestServices.CreateAsync(MakeConfig(dbPath));
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
            LogRetentionDays = 5,
            LogMaxSizeMb = 20,
            LogRetentionModeIndex = 2,
        };

        await vm.SaveAsync();

        Assert.Equal(5, holder.Services.Config.LogRetentionDays);
        Assert.Equal(20, holder.Services.Config.LogMaxSizeMb);
        Assert.Equal("size", holder.Services.Config.LogRetentionMode);
        Assert.Equal(LogRetentionMode.Size, holder.Services.Config.LogOptions.Mode);
        Assert.Equal(20 * 1024L * 1024L, holder.Services.Config.LogOptions.MaxBytes);
    }

    [Fact]
    public async Task LogSettings_Validation()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));

        var empty = new SettingsViewModel(holder.Services, new FakePathPicker()) { LogDirectory = " " };
        Assert.Contains("日志目录不能为空", empty.Validate());

        var negativeDays = new SettingsViewModel(holder.Services, new FakePathPicker()) { LogRetentionDays = -1 };
        Assert.Contains("日志保留天数不能为负", negativeDays.Validate());

        var negativeMb = new SettingsViewModel(holder.Services, new FakePathPicker()) { LogMaxSizeMb = -1 };
        Assert.Contains("日志体积上限不能为负", negativeMb.Validate());

        var unlimited = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
            LogRetentionDays = 0,
            LogMaxSizeMb = 0,
        };
        Assert.Contains("不能同时不限", unlimited.Validate());

        // 只按天数时不限体积是合法组合。
        var daysOnly = new SettingsViewModel(holder.Services, new FakePathPicker())
        {
            LogRetentionDays = 3,
            LogMaxSizeMb = 0,
        };
        Assert.Null(daysOnly.Validate());
    }

    [Fact]
    public async Task LogSettings_Trim_UsesPersistentWriter()
    {
        using var holder = await TestServices.CreateAsync(MakeConfig(TestServices.NewDatabasePath()));

        // 测试装配默认带落盘，立即清理应给出成功提示。
        var vm = new SettingsViewModel(holder.Services, new FakePathPicker());

        vm.TrimLogsCommand.Execute(null);

        Assert.Contains("已按当前设置清理", vm.StatusMessage);
    }

    /// <summary>
    /// 测试装配下的日志目录必须落到临时目录，不能污染仓库 Logs。
    /// </summary>
    [Fact]
    public async Task AppServices_WritesLogsIntoConfiguredDirectory()
    {
        string logDir = Path.Combine(TestServices.TestTempRoot, $"wbi-logs-{Guid.NewGuid():N}");
        var config = MakeConfig(TestServices.NewDatabasePath());
        config.LogDirectory = logDir;

        using (var holder = await TestServices.CreateAsync(config))
        {
            holder.Services.Logs.Info("装配测试");
            holder.Services.Logs.Dispose();

            var files = Directory.GetFiles(logDir, "app-*.log");
            Assert.Single(files);
            Assert.Contains("装配测试", File.ReadAllText(files[0]));
        }

        try
        {
            Directory.Delete(logDir, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论。
        }
    }

    private static void TryDelete(string path)
    {
        foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch
            {
                // 清理失败不影响测试结论。
            }
        }
    }
}
