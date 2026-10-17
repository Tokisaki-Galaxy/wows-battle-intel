using System.IO;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 赛前名单观测器测试。文件比上次新即触发，文件消失即判定对局结束；
/// 修改时间超过新鲜度窗口的残留文件不触发。
/// </summary>
public class ArenaInfoWatcherTests : IDisposable
{
    private readonly string gamePath;
    private readonly string replayDir;

    public ArenaInfoWatcherTests()
    {
        gamePath = Path.Combine(TestServices.TestTempRoot, $"wbi-game-{Guid.NewGuid():N}");
        replayDir = Path.Combine(gamePath, "replays");
        Directory.CreateDirectory(replayDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(gamePath, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论。
        }
    }

    private static string BuildArenaJson(string dateTime, int playerCount = 2)
    {
        var vehicles = new List<string>();
        for (int i = 0; i < playerCount; i++)
        {
            int relation = i < playerCount / 2 ? 0 : 2;
            vehicles.Add(
                $$"""{"id": {{500000000 + i}}, "name": "player_{{i}}", "relation": {{relation}}, "shipId": "{{4286581900 + i}}"}""");
        }

        return $$"""
            {
              "matchGroup": "pvp",
              "gameType": "RandomBattle",
              "dateTime": "{{dateTime}}",
              "vehicles": [{{string.Join(",\n    ", vehicles)}}]
            }
            """;
    }

    private string WriteArena(string dateTime, int playerCount = 2, string subDir = "")
    {
        string dir = subDir.Length == 0 ? replayDir : Path.Combine(replayDir, subDir);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "tempArenaInfo.json");
        File.WriteAllText(path, BuildArenaJson(dateTime, playerCount));
        return path;
    }

    private AppConfig CreateConfig() => new()
    {
        GamePath = gamePath,
        DatabasePath = TestServices.NewDatabasePath(),
    };

    [Fact]
    public void ScanOnce_NewFile_FiresDiscovered()
    {
        WriteArena("16.10.2026 13:00:00");
        using var watcher = new ArenaInfoWatcher(CreateConfig());

        BattleRecord? captured = null;
        watcher.PreBattleDiscovered += b => captured = b;

        watcher.ScanOnce();

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Players.Count);
        Assert.Equal(BattleType.Random, captured.BattleType);
    }

    [Fact]
    public void ScanOnce_StaleFile_DoesNotFire()
    {
        // 崩溃或强退残留的 tempArenaInfo 修改时间很久远，不是进行中的对局，
        // 必须挡掉，否则界面会把两个月前的旧对局当成当前对局。
        WriteArena("16.10.2026 08:00:00");
        File.SetLastWriteTimeUtc(
            Path.Combine(replayDir, "tempArenaInfo.json"),
            DateTime.UtcNow.AddHours(-5));

        using var watcher = new ArenaInfoWatcher(CreateConfig());
        int fired = 0;
        watcher.PreBattleDiscovered += _ => fired++;

        watcher.ScanOnce();

        Assert.Equal(0, fired);
    }

    [Fact]
    public void ScanOnce_MidBattleStartup_RecentFileFires()
    {
        // 对局进行中启动客户端：文件修改时间是最近几分钟，仍应触发。
        WriteArena("16.10.2026 13:00:00");
        File.SetLastWriteTimeUtc(
            Path.Combine(replayDir, "tempArenaInfo.json"),
            DateTime.UtcNow.AddMinutes(-2));

        using var watcher = new ArenaInfoWatcher(CreateConfig());
        int fired = 0;
        watcher.PreBattleDiscovered += _ => fired++;

        watcher.ScanOnce();

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ScanOnce_WithinOneHourWindow_Fires()
    {
        // 窗口是一小时，接近上界但仍在窗口内的文件应触发。
        WriteArena("16.10.2026 13:00:00");
        File.SetLastWriteTimeUtc(
            Path.Combine(replayDir, "tempArenaInfo.json"),
            DateTime.UtcNow.AddMinutes(-50));

        using var watcher = new ArenaInfoWatcher(CreateConfig());
        int fired = 0;
        watcher.PreBattleDiscovered += _ => fired++;

        watcher.ScanOnce();

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ScanOnce_StaleFileAfterActive_FiresEnded()
    {
        // 已有一局在展示，随后文件变陈旧（例如游戏异常退出后残留），应收起。
        string path = WriteArena("16.10.2026 13:00:00");
        using var watcher = new ArenaInfoWatcher(CreateConfig());
        bool ended = false;
        watcher.PreBattleEnded += () => ended = true;

        watcher.ScanOnce();
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
        watcher.ScanOnce();

        Assert.True(ended);
    }

    [Fact]
    public void ScanOnce_SameWriteTime_FiresOnce()
    {
        WriteArena("16.10.2026 13:00:00");
        using var watcher = new ArenaInfoWatcher(CreateConfig());
        int fired = 0;
        watcher.PreBattleDiscovered += _ => fired++;

        watcher.ScanOnce();
        watcher.ScanOnce();
        watcher.ScanOnce();

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ScanOnce_FileRewritten_FiresAgain()
    {
        string path = WriteArena("16.10.2026 13:00:00", playerCount: 2);
        using var watcher = new ArenaInfoWatcher(CreateConfig());
        int fired = 0;
        watcher.PreBattleDiscovered += _ => fired++;

        watcher.ScanOnce();

        // 玩家陆续加入：文件被重写且修改时间前进。
        File.WriteAllText(path, BuildArenaJson("16.10.2026 13:00:00", playerCount: 4));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(30));
        watcher.ScanOnce();

        Assert.Equal(2, fired);
    }

    [Fact]
    public void ScanOnce_FindsFileInVersionSubdirectory()
    {
        // 游戏把 tempArenaInfo 写进版本子目录时也要能找到。
        WriteArena("16.10.2026 13:00:00", subDir: "15.9.0.0");
        using var watcher = new ArenaInfoWatcher(CreateConfig());
        int fired = 0;
        watcher.PreBattleDiscovered += _ => fired++;

        watcher.ScanOnce();

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ScanOnce_FileDeleted_FiresEnded()
    {
        string path = WriteArena("16.10.2026 13:00:00");
        using var watcher = new ArenaInfoWatcher(CreateConfig());
        bool ended = false;
        watcher.PreBattleEnded += () => ended = true;

        watcher.ScanOnce();
        File.Delete(path);
        watcher.ScanOnce();

        Assert.True(ended);
    }

    [Fact]
    public void ScanOnce_NoFile_DoesNotFireEndedWithoutActiveBattle()
    {
        using var watcher = new ArenaInfoWatcher(CreateConfig());
        bool ended = false;
        watcher.PreBattleEnded += () => ended = true;

        watcher.ScanOnce();

        Assert.False(ended);
    }
}
