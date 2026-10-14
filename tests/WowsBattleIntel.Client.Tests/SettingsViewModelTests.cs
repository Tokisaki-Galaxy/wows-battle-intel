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
