using BuildingOS.Shared.Domain.Authorization;

namespace BuildingOS.Shared.Test.Domain.Authorization;

public class PermissionHelperTest
{
    [Fact]
    public void HashResourceId_ReturnsSameHashForSameInput()
    {
        var hash1 = PermissionHelper.HashResourceId("ahu-301");
        var hash2 = PermissionHelper.HashResourceId("ahu-301");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashResourceId_ReturnsDifferentHashForDifferentInput()
    {
        var hash1 = PermissionHelper.HashResourceId("ahu-301");
        var hash2 = PermissionHelper.HashResourceId("ahu-302");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void HashResourceId_Returns56HexChars()
    {
        var hash = PermissionHelper.HashResourceId("some-long-adt-dtid-that-exceeds-limits");

        Assert.Equal(PermissionHelper.HashHexLength, hash.Length);
        Assert.Matches("^[0-9a-f]{56}$", hash);
    }

    [Fact]
    public void HashResourceId_IsLowercase()
    {
        var hash = PermissionHelper.HashResourceId("test-id");

        Assert.Equal(hash, hash.ToLowerInvariant());
    }

    [Fact]
    public void HashResourceId_LongDtId_StillReturns56Chars()
    {
        var longId = "dtmi:jp:gutp:bim:eng2:hvac:ahu:ahu-301-supply-air-temperature-sensor";
        var hash = PermissionHelper.HashResourceId(longId);

        Assert.Equal(56, hash.Length);
    }

    // === BuildPermissionString (abbreviated format) ===

    [Fact]
    public void BuildPermissionString_NonGroupType_HashesAndAbbreviates()
    {
        var result = PermissionHelper.BuildPermissionString("device", "ahu-301", "read");
        var expectedHash = PermissionHelper.HashResourceId("ahu-301");

        Assert.Equal($"d:{expectedHash}:r", result);
    }

    [Fact]
    public void BuildPermissionString_GroupType_DoesNotHash()
    {
        var result = PermissionHelper.BuildPermissionString("group", "hvac-team", "read");

        Assert.Equal("g:hvac-team:r", result);
    }

    [Fact]
    public void BuildPermissionString_MultipleActions()
    {
        var result = PermissionHelper.BuildPermissionString("device", "ahu-301", "read,write");
        var expectedHash = PermissionHelper.HashResourceId("ahu-301");

        Assert.Equal($"d:{expectedHash}:rw", result);
    }

    [Fact]
    public void BuildPermissionString_AllResourceTypes_UnderLimit()
    {
        var longId = "dtmi:jp:gutp:bim:eng2:hvac:ahu:ahu-301-supply-air-temperature-sensor";
        var longActions = "read,write,admin";
        var resourceTypes = new[] { "building", "floor", "space", "device", "point" };

        foreach (var type in resourceTypes)
        {
            var result = PermissionHelper.BuildPermissionString(type, longId, longActions);
            Assert.True(result.Length <= 64,
                $"Permission string '{result}' for type '{type}' has length {result.Length}, exceeding 64 char limit");
        }
    }

    // === Abbreviation/Expansion ===

    [Theory]
    [InlineData("building", "b")]
    [InlineData("floor", "f")]
    [InlineData("space", "s")]
    [InlineData("device", "d")]
    [InlineData("point", "p")]
    [InlineData("group", "g")]
    public void AbbreviateResourceType_KnownTypes(string full, string expected)
    {
        Assert.Equal(expected, PermissionHelper.AbbreviateResourceType(full));
    }

    [Theory]
    [InlineData("b", "building")]
    [InlineData("f", "floor")]
    [InlineData("s", "space")]
    [InlineData("d", "device")]
    [InlineData("p", "point")]
    [InlineData("g", "group")]
    public void ExpandResourceType_KnownAbbreviations(string abbr, string expected)
    {
        Assert.Equal(expected, PermissionHelper.ExpandResourceType(abbr));
    }

    [Fact]
    public void AbbreviateResourceType_UnknownType_ReturnsAsIs()
    {
        Assert.Equal("custom", PermissionHelper.AbbreviateResourceType("custom"));
    }

    [Fact]
    public void ExpandResourceType_UnknownAbbr_ReturnsAsIs()
    {
        Assert.Equal("x", PermissionHelper.ExpandResourceType("x"));
    }

    [Fact]
    public void AbbreviateActions_SingleAction()
    {
        Assert.Equal("r", PermissionHelper.AbbreviateActions("read"));
        Assert.Equal("w", PermissionHelper.AbbreviateActions("write"));
        Assert.Equal("a", PermissionHelper.AbbreviateActions("admin"));
    }

    [Fact]
    public void AbbreviateActions_MultipleActions()
    {
        Assert.Equal("rw", PermissionHelper.AbbreviateActions("read,write"));
        Assert.Equal("rwa", PermissionHelper.AbbreviateActions("read,write,admin"));
    }

    [Fact]
    public void ExpandActions_SingleAction()
    {
        Assert.Equal("read", PermissionHelper.ExpandActions("r"));
        Assert.Equal("write", PermissionHelper.ExpandActions("w"));
        Assert.Equal("admin", PermissionHelper.ExpandActions("a"));
    }

    [Fact]
    public void ExpandActions_ConcatenatedFormat()
    {
        Assert.Equal("read,write", PermissionHelper.ExpandActions("rw"));
        Assert.Equal("read,write,admin", PermissionHelper.ExpandActions("rwa"));
    }

    [Fact]
    public void ExpandActions_OldCommaFormat_StillWorks()
    {
        Assert.Equal("read,write", PermissionHelper.ExpandActions("r,w"));
        Assert.Equal("read,write,admin", PermissionHelper.ExpandActions("r,w,a"));
    }

    [Fact]
    public void ExpandActions_FullActionName_ReturnsAsIs()
    {
        Assert.Equal("read", PermissionHelper.ExpandActions("read"));
        Assert.Equal("admin", PermissionHelper.ExpandActions("admin"));
    }

    // === ParsePermissionString ===

    [Fact]
    public void ParsePermissionString_AbbreviatedFormat_Expands()
    {
        var hash = PermissionHelper.HashResourceId("ahu-301");
        var result = PermissionHelper.ParsePermissionString($"d:{hash}:rw");

        Assert.NotNull(result);
        Assert.Equal("device", result.Value.ResourceType);
        Assert.Equal(hash, result.Value.ResourceId);
        Assert.Equal("read,write", result.Value.Actions);
    }

    [Fact]
    public void ParsePermissionString_GroupType_Expands()
    {
        var result = PermissionHelper.ParsePermissionString("g:hvac-team:r");

        Assert.NotNull(result);
        Assert.Equal("group", result.Value.ResourceType);
        Assert.Equal("hvac-team", result.Value.ResourceId);
        Assert.Equal("read", result.Value.Actions);
    }

    [Fact]
    public void ParsePermissionString_InvalidFormat_ReturnsNull()
    {
        Assert.Null(PermissionHelper.ParsePermissionString("invalid"));
        Assert.Null(PermissionHelper.ParsePermissionString("only:two"));
        Assert.Null(PermissionHelper.ParsePermissionString("a:b:c:d"));
    }

    // === IsGroupType ===

    [Fact]
    public void IsGroupType_Group_ReturnsTrue()
    {
        Assert.True(PermissionHelper.IsGroupType("group"));
    }

    [Fact]
    public void IsGroupType_GroupUpperCase_ReturnsTrue()
    {
        Assert.True(PermissionHelper.IsGroupType("GROUP"));
    }

    [Fact]
    public void IsGroupType_AbbreviatedG_ReturnsTrue()
    {
        Assert.True(PermissionHelper.IsGroupType("g"));
        Assert.True(PermissionHelper.IsGroupType("G"));
    }

    [Fact]
    public void IsGroupType_NonGroup_ReturnsFalse()
    {
        Assert.False(PermissionHelper.IsGroupType("device"));
        Assert.False(PermissionHelper.IsGroupType("building"));
        Assert.False(PermissionHelper.IsGroupType("floor"));
        Assert.False(PermissionHelper.IsGroupType("space"));
        Assert.False(PermissionHelper.IsGroupType("point"));
    }

    // === IsAlreadyHashed ===

    [Fact]
    public void IsAlreadyHashed_HashedId_ReturnsTrue()
    {
        var hash = PermissionHelper.HashResourceId("ahu-301");
        Assert.True(PermissionHelper.IsAlreadyHashed(hash));
    }

    [Fact]
    public void IsAlreadyHashed_RawId_ReturnsFalse()
    {
        Assert.False(PermissionHelper.IsAlreadyHashed("ahu-301"));
        Assert.False(PermissionHelper.IsAlreadyHashed("eng2"));
        Assert.False(PermissionHelper.IsAlreadyHashed("dtmi:jp:gutp:bim:eng2:hvac:ahu-301"));
    }

    [Fact]
    public void IsAlreadyHashed_UppercaseHex_ReturnsFalse()
    {
        // ハッシュは小文字hexのみ（56文字の大文字hex）
        Assert.False(PermissionHelper.IsAlreadyHashed("A1B2C3D4E5F67890A1B2C3D4E5F67890A1B2C3D4E5F67890A1B2C3D4"));
    }

    // === 二重ハッシュ防止 ===

    [Fact]
    public void BuildPermissionString_AlreadyHashedId_DoesNotDoubleHash()
    {
        var hash = PermissionHelper.HashResourceId("ahu-301");
        var result = PermissionHelper.BuildPermissionString("device", hash, "read");

        // 既にハッシュ済みならそのまま使用（二重ハッシュしない）
        Assert.Equal($"d:{hash}:r", result);
    }

    [Fact]
    public void BuildPermissionString_CalledTwice_SameResult()
    {
        var first = PermissionHelper.BuildPermissionString("device", "ahu-301", "read");
        // firstをパースして再度BuildPermissionStringに渡す
        var parsed = PermissionHelper.ParsePermissionString(first);
        Assert.NotNull(parsed);
        var second = PermissionHelper.BuildPermissionString(
            parsed.Value.ResourceType, parsed.Value.ResourceId, parsed.Value.Actions);

        Assert.Equal(first, second);
    }
}
