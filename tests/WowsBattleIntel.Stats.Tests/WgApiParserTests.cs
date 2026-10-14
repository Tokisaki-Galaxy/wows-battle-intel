using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Stats.Tests;

public class WgApiParserTests
{
    [Fact]
    public void ParseAccountIds_ExtractsNicknameToId()
    {
        const string json = """
            {
              "status": "ok",
              "meta": { "count": 2 },
              "data": [
                { "nickname": "Alpha", "account_id": 1001 },
                { "nickname": "Bravo", "account_id": 1002 }
              ]
            }
            """;

        var mapping = WgApiParser.ParseAccountIds(json);

        Assert.Equal(2, mapping.Count);
        Assert.Equal("1001", mapping["Alpha"]);
        Assert.Equal("1002", mapping["Bravo"]);
    }

    [Fact]
    public void ParseAccountIds_ErrorStatus_ReturnsEmpty()
    {
        const string json = """{ "status": "error", "error": { "message": "INVALID_SEARCH" } }""";

        var mapping = WgApiParser.ParseAccountIds(json);

        Assert.Empty(mapping);
    }

    [Fact]
    public void ParseAccountStats_ComputesWinrateFromWinsAndBattles()
    {
        const string json = """
            {
              "status": "ok",
              "data": {
                "1001": {
                  "hidden_profile": false,
                  "statistics": {
                    "pvp":          { "wins": 600, "battles": 1000 },
                    "pvp_solo":     { "wins": 300, "battles": 500 },
                    "pvp_div2":     { "wins": 200, "battles": 300 },
                    "pvp_div3":     { "wins": 100, "battles": 200 }
                  }
                }
              }
            }
            """;

        var stats = WgApiParser.ParseAccountStats(json);

        var s = stats["1001"];
        Assert.Equal(1000, s.Battles);
        Assert.Equal(0.60, s.Winrate, precision: 6);
        Assert.Equal(500, s.BattlesSolo);
        Assert.Equal(0.60, s.WinrateSolo, precision: 6);
        Assert.Equal(300, s.BattlesDiv2);
        Assert.Equal(200.0 / 300.0, s.WinrateDiv2, precision: 6);
        Assert.False(s.IsHidden);
    }

    [Fact]
    public void ParseAccountStats_HiddenProfile_MarksHidden()
    {
        const string json = """
            {
              "status": "ok",
              "data": {
                "1001": { "hidden_profile": true }
              }
            }
            """;

        var stats = WgApiParser.ParseAccountStats(json);

        Assert.True(stats["1001"].IsHidden);
    }

    [Fact]
    public void ParseAccountStats_MissingStatistics_MarksHidden()
    {
        const string json = """
            {
              "status": "ok",
              "data": {
                "1001": { "hidden_profile": false }
              }
            }
            """;

        var stats = WgApiParser.ParseAccountStats(json);

        Assert.True(stats["1001"].IsHidden);
    }

    [Fact]
    public void ParseAccountStats_ZeroBattles_ReturnsZeroWinrate()
    {
        const string json = """
            {
              "status": "ok",
              "data": {
                "1001": {
                  "hidden_profile": false,
                  "statistics": { "pvp": { "wins": 0, "battles": 0 } }
                }
              }
            }
            """;

        var stats = WgApiParser.ParseAccountStats(json);

        var s = stats["1001"];
        Assert.Equal(0, s.Battles);
        Assert.Equal(0, s.Winrate);
    }

    [Fact]
    public void ParseShipStats_ComputesAverages()
    {
        const string json = """
            {
              "status": "ok",
              "meta": { "count": 1 },
              "data": {
                "1001": [
                  { "pvp": { "wins": 30, "battles": 60, "damage_dealt": 300000, "original_exp": 90000 } }
                ]
              }
            }
            """;

        var (battles, winrate, avgDamage, avgExp) = WgApiParser.ParseShipStats(json, "1001");

        Assert.Equal(60, battles);
        Assert.Equal(0.5, winrate, precision: 6);
        Assert.Equal(5000, avgDamage, precision: 6);
        Assert.Equal(1500, avgExp, precision: 6);
    }

    [Fact]
    public void ParseShipStats_NoShipData_ReturnsSentinels()
    {
        const string json = """{ "status": "ok", "data": { "1001": [] } }""";

        var (battles, winrate, avgDamage, avgExp) = WgApiParser.ParseShipStats(json, "1001");

        Assert.Equal(-1, battles);
        Assert.Equal(-1, winrate);
        Assert.Equal(-1, avgDamage);
        Assert.Equal(-1, avgExp);
    }

    [Fact]
    public void ParseShipStats_ZeroBattles_ReturnsZeroes()
    {
        const string json = """
            {
              "status": "ok",
              "data": { "1001": [ { "pvp": { "battles": 0, "wins": 0, "damage_dealt": 0 } } ] }
            }
            """;

        var (battles, winrate, _, _) = WgApiParser.ParseShipStats(json, "1001");

        Assert.Equal(0, battles);
        Assert.Equal(0, winrate);
    }

    [Theory]
    [InlineData(GameServer.Ru, "api.korabli.su")]
    [InlineData(GameServer.Eu, "api.worldofwarships.eu")]
    [InlineData(GameServer.Na, "api.worldofwarships.com")]
    [InlineData(GameServer.Asia, "api.worldofwarships.asia")]
    [InlineData(GameServer.Cn, "api.wowsgame.cn")]
    public void GetApiHost_MapsServers(GameServer server, string expected)
    {
        Assert.Equal(expected, WgApiParser.GetApiHost(server));
    }

    [Fact]
    public void GetApiHost_Auto_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WgApiParser.GetApiHost(GameServer.Auto));
    }
}
