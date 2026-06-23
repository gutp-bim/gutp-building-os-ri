using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BuildingOS.Shared.Domain.Authorization;

/// <summary>
/// パーミッション文字列のリソースIDハッシュ化ユーティリティ。
/// Entra IDカスタムセキュリティ属性の64文字制限に対応するため、
/// リソースIDをSHA-256の先頭56文字(224ビット)にハッシュ化し、
/// リソースタイプ・アクションを1文字に省略する。
/// 形式: "b:56hexchars:rw"
/// </summary>
public static class PermissionHelper
{
    /// <summary>
    /// ハッシュ化されたリソースIDの16進数文字数（SHA-256先頭224ビット）
    /// </summary>
    public const int HashHexLength = 56;

    // === ResourceType 省略マッピング ===

    private static readonly Dictionary<string, string> ResourceTypeToAbbr = new(StringComparer.OrdinalIgnoreCase)
    {
        ["building"] = "b",
        ["floor"] = "f",
        ["space"] = "s",
        ["device"] = "d",
        ["point"] = "p",
        ["group"] = "g",
    };

    private static readonly Dictionary<string, string> AbbrToResourceType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["b"] = "building",
        ["f"] = "floor",
        ["s"] = "space",
        ["d"] = "device",
        ["p"] = "point",
        ["g"] = "group",
    };

    // === ActionType 省略マッピング ===

    private static readonly Dictionary<string, string> ActionToAbbr = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = "r",
        ["write"] = "w",
        ["admin"] = "a",
    };

    private static readonly Dictionary<string, string> AbbrToAction = new(StringComparer.OrdinalIgnoreCase)
    {
        ["r"] = "read",
        ["w"] = "write",
        ["a"] = "admin",
    };

    /// <summary>
    /// リソースタイプのフル名を省略形に変換する。
    /// 不明なタイプはそのまま返す。
    /// </summary>
    public static string AbbreviateResourceType(string resourceType)
    {
        return ResourceTypeToAbbr.TryGetValue(resourceType, out var abbr) ? abbr : resourceType;
    }

    /// <summary>
    /// リソースタイプの省略形をフル名に変換する。
    /// 不明な省略形はそのまま返す。
    /// </summary>
    public static string ExpandResourceType(string abbreviated)
    {
        return AbbrToResourceType.TryGetValue(abbreviated, out var full) ? full : abbreviated;
    }

    /// <summary>
    /// アクション文字列を省略形に変換する（例: "read,write" → "rw"）。
    /// カンマ区切りのフル名を、1文字ずつ連結した省略形に変換する。
    /// </summary>
    public static string AbbreviateActions(string actions)
    {
        var parts = actions.Split(',');
        var abbreviated = parts.Select(a =>
            ActionToAbbr.TryGetValue(a.Trim(), out var abbr) ? abbr : a.Trim());
        return string.Concat(abbreviated);
    }

    /// <summary>
    /// アクション省略形をフル名のカンマ区切りに変換する（例: "rw" → "read,write"）。
    /// 1文字ずつの連結形式と、旧カンマ区切り形式の両方に対応する。
    /// </summary>
    public static string ExpandActions(string abbreviatedActions)
    {
        // 旧形式: カンマ区切り（"read,write" or "r,w"）
        if (abbreviatedActions.Contains(','))
        {
            var parts = abbreviatedActions.Split(',');
            var expanded = parts.Select(a =>
                AbbrToAction.TryGetValue(a.Trim(), out var full) ? full : a.Trim());
            return string.Join(",", expanded);
        }

        // 新形式: 全文字が既知の省略形（"rw", "rwa" 等）
        if (abbreviatedActions.Length > 0 &&
            abbreviatedActions.All(c => AbbrToAction.ContainsKey(c.ToString())))
        {
            var expanded = abbreviatedActions.Select(c => AbbrToAction[c.ToString()]);
            return string.Join(",", expanded);
        }

        // フル名単体（"read", "admin" 等）
        return abbreviatedActions;
    }

    /// <summary>
    /// リソースIDをSHA-256ハッシュの先頭56文字(hex)に変換する。
    /// </summary>
    /// <param name="resourceId">生のリソースID（ADT dtIdやビジネスID）</param>
    /// <returns>56文字の小文字16進数文字列</returns>
    public static string HashResourceId(string resourceId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(resourceId));
        return Convert.ToHexString(bytes, 0, HashHexLength / 2).ToLowerInvariant();
    }

    /// <summary>
    /// パーミッション文字列を省略形式で構築する。
    /// グループ以外のリソースタイプではresourceIdをハッシュ化する。
    /// </summary>
    /// <param name="resourceType">リソースタイプ (e.g., "device", "building", "group")</param>
    /// <param name="resourceId">生のリソースID</param>
    /// <param name="actions">アクション文字列 (e.g., "read", "read,write")</param>
    /// <returns>省略形式パーミッション文字列 (e.g., "d:hash56:r")</returns>
    public static string BuildPermissionString(string resourceType, string resourceId, string actions)
    {
        var abbrType = AbbreviateResourceType(resourceType);
        var abbrActions = AbbreviateActions(actions);

        if (IsGroupType(resourceType) || IsAlreadyHashed(resourceId))
        {
            return $"{abbrType}:{resourceId}:{abbrActions}";
        }

        return $"{abbrType}:{HashResourceId(resourceId)}:{abbrActions}";
    }

    /// <summary>
    /// パーミッション文字列を解析して、フル名の (resourceType, resourceId, actions) を返す。
    /// 省略形式・旧形式の両方に対応する。
    /// </summary>
    /// <returns>解析結果。不正なフォーマットの場合はnull。</returns>
    public static (string ResourceType, string ResourceId, string Actions)? ParsePermissionString(string permission)
    {
        var parts = permission.Split(':');
        if (parts.Length != 3) return null;

        var resourceType = ExpandResourceType(parts[0]);
        var resourceId = parts[1];
        var actions = ExpandActions(parts[2]);

        return (resourceType, resourceId, actions);
    }

    private static readonly Regex HashedIdPattern = new(@"^[0-9a-f]{56}$", RegexOptions.Compiled);

    /// <summary>
    /// リソースIDが既にハッシュ化済みかどうかを判定する。
    /// （56文字の小文字16進数文字列であればハッシュ化済みとみなす）
    /// </summary>
    public static bool IsAlreadyHashed(string resourceId)
    {
        return resourceId.Length == HashHexLength && HashedIdPattern.IsMatch(resourceId);
    }

    /// <summary>
    /// 指定リソースタイプがグループかどうかを判定する（フル名・省略形の両方に対応）。
    /// グループIDはハッシュ化しない（管理者が作成する短いID＋MySQL検索に使用）。
    /// </summary>
    public static bool IsGroupType(string resourceType)
    {
        return resourceType.Equals("group", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("g", StringComparison.OrdinalIgnoreCase);
    }
}
