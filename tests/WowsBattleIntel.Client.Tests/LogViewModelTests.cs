using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Client.ViewModels;

namespace WowsBattleIntel.Client.Tests;

public class LogViewModelTests
{
    [Fact]
    public void Constructor_LoadsExistingSnapshot()
    {
        var logs = new LogService();
        logs.Info("历史条目");

        var viewModel = new LogViewModel(logs);

        Assert.Single(viewModel.Entries);
        Assert.Equal("历史条目", viewModel.Entries[0].Message);
    }

    [Fact]
    public void EntryLogged_AppendsEntry()
    {
        var logs = new LogService();
        logs.Info("历史条目");
        var viewModel = new LogViewModel(logs);

        logs.Warn("新条目");

        Assert.Equal(2, viewModel.Entries.Count);
        Assert.Equal("新条目", viewModel.Entries[^1].Message);
    }

    [Fact]
    public void Entries_CapacityBoundToServiceCapacity()
    {
        var logs = new LogService();
        var viewModel = new LogViewModel(logs);

        for (int i = 0; i < LogService.Capacity + 10; i++)
        {
            logs.Info($"条目 {i}");
        }

        Assert.Equal(LogService.Capacity, viewModel.Entries.Count);
        Assert.Equal("条目 10", viewModel.Entries[0].Message);
    }

    [Fact]
    public void EntryLogged_RaisesEntriesAppended()
    {
        var logs = new LogService();
        var viewModel = new LogViewModel(logs);
        int appended = 0;
        viewModel.EntriesAppended += () => appended++;

        logs.Info("触发追加");

        Assert.Equal(1, appended);
    }
}
