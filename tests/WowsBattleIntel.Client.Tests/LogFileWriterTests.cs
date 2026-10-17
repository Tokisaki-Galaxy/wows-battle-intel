using System.Globalization;
using System.IO;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.Services;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 日志落盘测试。
/// </summary>
/// <remarks>
/// 每个用例用独立临时目录，目录随测试销毁。时间用可拨动的替身时钟，
/// 不依赖真实日期变化。
/// </remarks>
public class LogFileWriterTests : IDisposable
{
    private readonly string root = Path.Combine(
        TestServices.TestTempRoot,
        $"wbi-log-test-{Guid.NewGuid():N}");

    /// <summary>可拨动的时钟。本地时间固定为 UTC+8，不依赖宿主机时区。</summary>
    private sealed class FakeClock : TimeProvider
    {
        private static readonly TimeZoneInfo Zone =
            TimeZoneInfo.CreateCustomTimeZone("fake", TimeSpan.FromHours(8), "fake", "fake");

        /// <summary>当前本地时间。</summary>
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 9, 0, 0, TimeSpan.FromHours(8));

        public override TimeZoneInfo LocalTimeZone => Zone;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    /// <summary>
    /// 构造一条本地时区为 UTC+8 的日志时间。写入器按系统时区换算成日志里的时间，
    /// 这里统一用 UTC 表示，避免宿主机时区把日期换到前一天。
    /// </summary>
    private static DateTimeOffset At(int day) =>
        new(new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Utc));

    private static LogEntry Entry(DateTimeOffset at, string message, LogLevel level = LogLevel.Info) =>
        new(at, level, message);

    private LogFileOptions Options(int days = 3, int mb = 10, LogRetentionMode mode = LogRetentionMode.Both) =>
        new(root, days, mb * 1024L * 1024L, mode);

    private string[] LogFiles() =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, "app-*.log").Select(f => Path.GetFileName(f)!).ToArray()
            : [];

    /// <summary>
    /// 读取文件全文。以共享方式打开，模拟用户用编辑器看日志时写入器仍在写。
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Write_CreatesDirectoryAndDatedFile()
    {
        var clock = new FakeClock();
        using var writer = new LogFileWriter(Options(), clock);

        writer.Write(Entry(clock.Now, "第一条"));

        // 缓冲未到阈值，显式刷新后落盘。
        writer.Flush();

        var files = LogFiles();
        string file = Assert.Single(files);
        Assert.Matches(@"^app-\d{8}\.log$", file);

        string text = ReadShared(Path.Combine(root, file));
        Assert.Contains("[INFO] 第一条", text);
    }

    [Fact]
    public void Write_UsesLevelTags()
    {
        var clock = new FakeClock();
        using var writer = new LogFileWriter(Options(), clock);

        writer.Write(Entry(clock.Now, "警告", LogLevel.Warn));
        writer.Write(Entry(clock.Now, "错误", LogLevel.Error));
        writer.Flush();

        string text = ReadShared(Path.Combine(root, LogFiles().Single()));
        Assert.Contains("[WARN] 警告", text);
        Assert.Contains("[ERROR] 错误", text);
    }

    [Fact]
    public void Write_跨天滚动到新文件()
    {
        var clock = new FakeClock();
        var writer = new LogFileWriter(Options(), clock);

        // 显式给出相隔一天的时间，不依赖宿主机时区。
        writer.Write(Entry(At(17), "第一天"));
        writer.Write(Entry(At(18), "第二天"));
        writer.Flush();

        var files = LogFiles();
        Assert.Equal(2, files.Length);
        Assert.Contains(files, f => ReadShared(Path.Combine(root, f)).Contains("第一天"));
        Assert.Contains(files, f => ReadShared(Path.Combine(root, f)).Contains("第二天"));
        writer.Dispose();
    }

    [Fact]
    public void Append_跨实例接续同一文件()
    {
        var clock = new FakeClock();
        using (var first = new LogFileWriter(Options(), clock))
        {
            first.Write(Entry(clock.Now, "上一次运行"));
            first.Flush();
        }

        using (var second = new LogFileWriter(Options(), clock))
        {
            second.Write(Entry(clock.Now, "这一次运行"));
            second.Flush();
        }

        string text = ReadShared(Path.Combine(root, LogFiles().Single()));
        Assert.Contains("上一次运行", text);
        Assert.Contains("这一次运行", text);
    }

    [Fact]
    public void Trim_删除超过保留天数的文件()
    {
        Directory.CreateDirectory(root);
        var old = Path.Combine(root, "app-20261013.log");
        var fresh = Path.Combine(root, "app-20261016.log");
        File.WriteAllText(old, "旧");
        File.WriteAllText(fresh, "新");

        var clock = new FakeClock();
        using var writer = new LogFileWriter(Options(days: 3), clock);

        // 17 号保留 3 天即 15、16、17，14 号及以前删除。
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Trim_按体积从最旧删起()
    {
        Directory.CreateDirectory(root);
        string filler = new('x', 400 * 1024);
        for (int day = 14; day <= 17; day++)
        {
            File.WriteAllText(Path.Combine(root, $"app-202610{day}.log"), filler);
        }

        var clock = new FakeClock();
        // 上限 1MB，四个 400KB 文件合计 1.6MB，最旧的两个要被删掉。
        using var writer = new LogFileWriter(Options(days: 0, mb: 1), clock);

        var files = LogFiles();
        Assert.DoesNotContain("app-20261014.log", files);
        Assert.DoesNotContain("app-20261015.log", files);
        Assert.Contains("app-20261017.log", files);

        long total = files.Sum(f => new FileInfo(Path.Combine(root, f)).Length);
        Assert.True(total <= 1024 * 1024, $"剩余 {total} 字节超过上限");
    }

    [Fact]
    public void Trim_只按体积时不删旧文件()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app-20200101.log"), "很久以前");

        var clock = new FakeClock();
        using var writer = new LogFileWriter(Options(days: 3, mb: 10, mode: LogRetentionMode.Size), clock);

        Assert.True(File.Exists(Path.Combine(root, "app-20200101.log")));
    }

    [Fact]
    public void Trim_只按天数时不看体积()
    {
        Directory.CreateDirectory(root);
        string big = new('x', 2 * 1024 * 1024);
        File.WriteAllText(Path.Combine(root, "app-20261016.log"), big);

        var clock = new FakeClock();
        using var writer = new LogFileWriter(Options(days: 3, mb: 1, mode: LogRetentionMode.Days), clock);

        Assert.True(File.Exists(Path.Combine(root, "app-20261016.log")));
    }

    [Fact]
    public void Trim_当天文件超限时截断保留尾部()
    {
        var clock = new FakeClock();
        // 先写一份很大的当天文件，从完整行开始。
        Directory.CreateDirectory(root);
        string today = Path.Combine(root, $"app-{At(17).DateTime:yyyyMMdd}.log");
        using (var stream = new StreamWriter(today))
        {
            for (int i = 0; i < 20000; i++)
            {
                string time = clock.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                stream.WriteLine($"{time} [INFO] 填充行 {i} ................................................");
            }
        }

        long before = new FileInfo(today).Length;
        Assert.True(before > 1024 * 1024, "测试数据应超过上限");
        var writer = new LogFileWriter(Options(days: 3, mb: 1), clock);
        writer.Write(Entry(clock.Now, "末尾标记"));
        writer.Flush();
        writer.Trim();
        writer.Dispose();

        long after = new FileInfo(today).Length;
        Assert.True(after < before, $"截断后应变小 before={before} after={after}");
        Assert.True(after <= 1024 * 1024, $"截断后仍有 {after} 字节");

        string text = ReadShared(today);
        Assert.Contains("末尾标记", text);

        // 首行必须是完整行，不能从半截开始。
        string firstLine = text.TrimStart('\n').Split('\n')[0];
        Assert.Contains("[INFO] 填充行", firstLine);
    }

    [Fact]
    public void Trim_忽略非本应用命名的文件()
    {
        Directory.CreateDirectory(root);
        string foreign = Path.Combine(root, "notes.log");
        File.WriteAllText(foreign, "手工放的");

        var clock = new FakeClock();
        using var writer = new LogFileWriter(Options(days: 1, mb: 1), clock);

        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void TryParseFileName_识别日期()
    {
        DateTime? parsed = LogFileWriter.TryParseFileName(Path.Combine("x", "app-20261017.log"));

        Assert.Equal(new DateTime(2026, 10, 17), parsed);
        Assert.Null(LogFileWriter.TryParseFileName("notes.log"));
        Assert.Null(LogFileWriter.TryParseFileName("app-abc.log"));
    }

    [Fact]
    public void Dispose_刷出剩余缓冲()
    {
        var clock = new FakeClock();
        var writer = new LogFileWriter(Options(), clock);
        writer.Write(Entry(clock.Now, "未达阈值"));
        writer.Dispose();

        string text = ReadShared(Path.Combine(root, LogFiles().Single()));
        Assert.Contains("未达阈值", text);
    }

    [Fact]
    public void Write_落盘失败时静默降级()
    {
        var clock = new FakeClock();
        using var writer = new LogFileWriter(Options(), clock);

        // 同一天不滚动就不会再打开目录，正常写入不受影响。
        var normal = Record.Exception(() => writer.Write(Entry(clock.Now, "正常一行")));
        Assert.Null(normal);

        // 把目录换成一个同名文件，让跨天滚动时打开失败，写入也不得向外抛。
        writer.Dispose();
        Directory.Delete(root, recursive: true);
        File.WriteAllText(root, "占位文件挡在目录位置");

        var exception = Record.Exception(() => writer.Write(Entry(At(18), "失败后继续")));
        Assert.Null(exception);
        Assert.True(writer.IsDegraded);
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // 文件被占用时留给系统清理。
        }
    }
}
