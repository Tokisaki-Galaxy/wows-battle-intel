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
    public void ParseAccountStats_ComputesAvgDamageFromDamageAndBattles()
    {
        const string json = """
            {
              "status": "ok",
              "data": {
                "1001": {
                  "hidden_profile": false,
                  "statistics": {
                    "pvp": { "wins": 600, "battles": 1000, "damage_dealt": 42600000 }
                  }
                }
              }
            }
            """;

        var stats = WgApiParser.ParseAccountStats(json);

        Assert.Equal(42600, stats["1001"].AvgDamage, precision: 6);
    }

    [Fact]
    public void ParseAccountStats_MissingDamage_KeepsSentinel()
    {
        const string json = """
            {
              "status": "ok",
              "data": {
                "1001": {
                  "hidden_profile": false,
                  "statistics": {
                    "pvp": { "wins": 600, "battles": 1000 }
                  }
                }
              }
            }
            """;

        var stats = WgApiParser.ParseAccountStats(json);

        Assert.Equal(-1, stats["1001"].AvgDamage);
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

    [Fact]
    public void ParseAllShipStats_ExtractsPerShipEntries()
    {
        const string json = """
            {
              "status": "ok",
              "data": {
                "1001": [
                  { "ship_id": 4291, "pvp": { "battles": 100, "wins": 60 } },
                  { "ship_id": 4292, "pvp": { "battles": 30, "wins": 12 } },
                  { "ship_id": 4293, "pvp": null }
                ]
              }
            }
            """;

        var result = WgApiParser.ParseAllShipStats(json);

        Assert.True(result.ContainsKey("1001"));
        var byShip = result["1001"];
        Assert.Equal(2, byShip.Count);
        Assert.Equal(100, byShip["4291"].Battles);
        Assert.Equal(60, byShip["4291"].Wins);
    }

    [Fact]
    public void ParseAllShipStats_ErrorStatus_ReturnsEmpty()
    {
        const string json = """{ "status": "error" }""";

        Assert.Empty(WgApiParser.ParseAllShipStats(json));
    }

    [Fact]
    public void ParseShipsPageTotal_ReadsMeta()
    {
        const string json = """{ "status": "ok", "meta": { "page_total": 6 } }""";

        Assert.Equal(6, WgApiParser.ParseShipsPageTotal(json));
    }

    [Fact]
    public void ParseShipClasses_CollectsTypesIntoMap()
    {
        const string json = """
            {
              "status": "ok",
              "data": {
                "4291": { "type": "Destroyer" },
                "4292": { "type": "AirCarrier" }
              }
            }
            """;

        var into = new Dictionary<string, string>();
        WgApiParser.ParseShipClasses(json, into);

        Assert.Equal("Destroyer", into["4291"]);
        Assert.Equal("AirCarrier", into["4292"]);
    }

    [Theory]
    [InlineData("Destroyer", "DD")]
    [InlineData("Cruiser", "CA")]
    [InlineData("Battleship", "BB")]
    [InlineData("AirCarrier", "CV")]
    [InlineData("Submarine", "SS")]
    [InlineData("UnknownType", null)]
    public void ToClassKey_NormalizesWgTypes(string wgType, string? expected)
    {
        Assert.Equal(expected, WowsBattleIntel.Stats.WgPublicStatsProvider.ToClassKey(wgType));
    }

    // 以下 JSON 片段来自 api.worldofwarships.asia 实测响应（2026-10）。
    [Fact]
    public void ParseClanList_ExtractsClanEntries()
    {
        const string json = """
            {
              "status": "ok",
              "meta": { "count": 1, "page": 1, "total": 1 },
              "data": [
                {
                  "clan_id": 2000035484,
                  "name": "くろねこ海賊団 ～BLACK CAT～",
                  "tag": "BCAT",
                  "created_at": 1686446094,
                  "members_count": 33
                }
              ]
            }
            """;

        var clans = WgApiParser.ParseClanList(json);

        var clan = Assert.Single(clans);
        Assert.Equal("2000035484", clan.ClanId);
        Assert.Equal("BCAT", clan.Tag);
        Assert.Equal("くろねこ海賊団 ～BLACK CAT～", clan.Name);
        Assert.Equal(33, clan.MembersCount);
    }

    [Fact]
    public void ParseClanList_ErrorStatus_ReturnsEmpty()
    {
        const string json = """{ "status": "error", "error": { "message": "INVALID_SEARCH" } }""";

        Assert.Empty(WgApiParser.ParseClanList(json));
    }

    [Fact]
    public void ParseClanInfo_ExtractsMembers()
    {
        const string json = """
            {
              "status": "ok",
              "meta": { "count": 1 },
              "data": {
                "2000035484": {
                  "name": "くろねこ海賊団 ～BLACK CAT～",
                  "tag": "BCAT",
                  "members_count": 33,
                  "members_ids": [2002288289, 2002300930, 3009054842]
                },
                "2000002405": null
              }
            }
            """;

        var clans = WgApiParser.ParseClanInfo(json);

        Assert.True(clans.ContainsKey("2000035484"));
        Assert.False(clans.ContainsKey("2000002405"));
        var clan = clans["2000035484"];
        Assert.Equal("BCAT", clan.Tag);
        Assert.Equal(3, clan.MemberIds.Count);
        Assert.Equal("2002288289", clan.MemberIds[0]);
        Assert.Equal("3009054842", clan.MemberIds[2]);
    }

    [Fact]
    public void ParseClanInfo_MissingMembersIds_ReturnsEmptyMemberList()
    {
        const string json = """
            {
              "status": "ok",
              "meta": { "count": 1 },
              "data": {
                "2000031944": { "name": "Kemomimi", "members_count": 6, "tag": "KEMO" }
              }
            }
            """;

        var clans = WgApiParser.ParseClanInfo(json);

        var clan = clans["2000031944"];
        Assert.Empty(clan.MemberIds);
        Assert.Equal(6, clan.MembersCount);
    }

    [Theory]
    [InlineData(0.552, 46, 225112, ClanGradeTier.Unicum)]
    [InlineData(0.55, 5, 5000, ClanGradeTier.Unicum)]
    [InlineData(0.5499, 46, 225112, ClanGradeTier.Good)]
    [InlineData(0.52, 5, 5000, ClanGradeTier.Good)]
    [InlineData(0.5199, 46, 225112, ClanGradeTier.Normal)]
    [InlineData(0.496, 42, 124316, ClanGradeTier.Normal)]
    [InlineData(0.60, 4, 5000, ClanGradeTier.Normal)]
    [InlineData(0.60, 5, 4999, ClanGradeTier.Normal)]
    [InlineData(-1, 46, 225112, ClanGradeTier.Normal)]
    public void Grade_ThresholdsMatchAggregatedWinrate(
        double winrate, int visibleMembers, double totalBattles, ClanGradeTier expected)
    {
        Assert.Equal(expected, ClanGrading.Grade(winrate, visibleMembers, totalBattles));
    }

    [Fact]
    public void NormalizeTag_StripsBrackets()
    {
        Assert.Equal("BCAT", ClanCatalogService.NormalizeTag("[BCAT]"));
        Assert.Equal("BCAT", ClanCatalogService.NormalizeTag(" BCAT "));
        Assert.Equal("BCAT", ClanCatalogService.NormalizeTag("[BCAT]"));
        Assert.Equal(string.Empty, ClanCatalogService.NormalizeTag("[]"));
        Assert.Equal(string.Empty, ClanCatalogService.NormalizeTag(""));
    }

    [Fact]
    public void ClanGradeInfo_ColorKey_MapsTier()
    {
        Assert.Equal("UnicumBrush", new ClanGradeInfo { Grade = ClanGradeTier.Unicum }.ColorKey);
        Assert.Equal("PrimaryFgBrush", new ClanGradeInfo { Grade = ClanGradeTier.Good }.ColorKey);
        Assert.Equal(string.Empty, new ClanGradeInfo { Grade = ClanGradeTier.Normal }.ColorKey);
    }
}
