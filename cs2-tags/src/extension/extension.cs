using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Players;
using System.Text.RegularExpressions;
using static SwiftlyS2.Shared.Helper;
using static Tags.Tags;
using static TagsApi.Tags;

namespace Tags;

public static partial class TagExtensions
{
    [GeneratedRegex(@"\{.*?\}|\p{C}")]
    private static partial Regex MyRegex();

    // One scoreboard refresh per world update (prevents spam)
    private static int _scoreRefreshScheduled = 0;

    public static string Name(this Team team)
        => Tags.Config.Settings.TeamChatNames.TryGetValue(team, out var name) ? name : string.Empty;

    public static string PrefixName(this Team team)
        => Tags.Config.Settings.TeamPrefixNames.TryGetValue(team, out var name) ? name : string.Empty;

    public static string RemoveCurlyBraceContent(this string message)
        => string.IsNullOrEmpty(message) ? string.Empty : MyRegex().Replace(message, string.Empty);

    public static string FormatMessage(Team team, params string[] args)
        => ReplaceTags(string.Concat(args), team);

    public static string ReplaceTags(this string message, Team team)
        => message.Replace("[teamcolor]", ForTeam(team), StringComparison.OrdinalIgnoreCase).Colored();

    public static string ForTeam(Team team) => team switch
    {
        Team.None => "\u0001",
        Team.Spectator => "\u0003",
        Team.CT => "\v",
        Team.T => "\u0010",
        _ => "\u0001"
    };

    private static bool IsDefaultTag(Tag tag)
    {
        var d = Tags.Config.Default;
        return string.Equals(tag.ScoreTag ?? "", d.ScoreTag ?? "", StringComparison.Ordinal)
            && string.Equals(tag.ChatTag ?? "", d.ChatTag ?? "", StringComparison.Ordinal)
            && string.Equals(tag.NameColor ?? "", d.NameColor ?? "", StringComparison.Ordinal)
            && string.Equals(tag.ChatColor ?? "", d.ChatColor ?? "", StringComparison.Ordinal)
            && tag.ChatSound == d.ChatSound
            && tag.Visibility == d.Visibility;
    }

    private static bool TagContentEquals(Tag a, Tag b)
    {
        return string.Equals(a.ScoreTag ?? "", b.ScoreTag ?? "", StringComparison.Ordinal)
            && string.Equals(a.ChatTag ?? "", b.ChatTag ?? "", StringComparison.Ordinal)
            && string.Equals(a.NameColor ?? "", b.NameColor ?? "", StringComparison.Ordinal)
            && string.Equals(a.ChatColor ?? "", b.ChatColor ?? "", StringComparison.Ordinal);
    }

    private static Tag MergeUserPrefs(Tag baseTag, Tag? oldTag)
    {
        if (oldTag == null) return baseTag;
        baseTag.ChatSound = oldTag.ChatSound;
        baseTag.Visibility = oldTag.Visibility;
        return baseTag;
    }

    // Optional: keep (used by manual/occasional refresh), but NO periodic loop in ultra-lite
    public static void RevalidateTagFromPermissions(this IPlayer player)
    {
        if (player == null || !player.IsValid || player.IsFakeClient || player.SteamID == 0)
            return;

        PlayerTagsList.TryGetValue(player.SteamID, out var cached);

        var computed = player.GetTag();
        computed = MergeUserPrefs(computed, cached);

        if (cached != null && TagContentEquals(cached, computed))
            return;

        if (IsDefaultTag(computed))
            PlayerTagsList.Remove(player.SteamID);
        else
            PlayerTagsList[player.SteamID] = computed;

        player.SetScoreTag(player.GetVisibility() ? computed.ScoreTag : Tags.Config.Default.ScoreTag);
    }

    public static Tag GetOrCreatePlayerTag(IPlayer player, bool force)
    {
        if (player == null) return Tags.Config.Default.Clone();

        if (!force && PlayerTagsList.TryGetValue(player.SteamID, out var cached) && cached != null)
            return cached;

        var newTag = player.GetTag();

        // Never cache default (so async perms can flip later)
        if (IsDefaultTag(newTag))
        {
            PlayerTagsList.Remove(player.SteamID);
            return newTag;
        }

        PlayerTagsList[player.SteamID] = newTag;
        return newTag;
    }

    // ULTRA-LITE: uses prebuilt indexes from Tags class (no LINQ, no scanning full list every time)
    public static Tag GetTag(this IPlayer player)
    {
        if (player == null) return Tags.Config.Default.Clone();

        // 1) direct steamid mapping
        if (Tags.TryGetSteamIdTag(player.SteamID, out var direct))
            return direct.Clone();

        // 2) permission roles mapping
        var perm = Tags.Instance?.Permission;
        if (perm == null)
            return Tags.Config.Default.Clone();

        foreach (var entry in Tags.GetRoleTagIndex())
        {
            if (perm.PlayerHasPermission(player.SteamID, entry.Role))
                return entry.Tag.Clone();
        }

        return Tags.Config.Default.Clone();
    }

    public static string GetPrePostValue(TagPrePost prePost, string? oldValue, string newValue)
    {
        string old = oldValue ?? string.Empty;
        return prePost switch
        {
            TagPrePost.Pre => newValue + old,
            TagPrePost.Post => old + newValue,
            _ => newValue
        };
    }

    public static void AddAttribute(this IPlayer player, TagType types, TagPrePost prePost, string newValue)
    {
        var tag = GetOrCreatePlayerTag(player, false);
        Tags.Api.TagsUpdatedPre(player, tag);

        if ((types & TagType.ScoreTag) != 0)
        {
            var value = GetPrePostValue(prePost, tag.ScoreTag, newValue);
            tag.ScoreTag = value;
            player.SetScoreTag(value);
        }
        if ((types & TagType.ChatTag) != 0) tag.ChatTag = GetPrePostValue(prePost, tag.ChatTag, newValue);
        if ((types & TagType.NameColor) != 0) tag.NameColor = GetPrePostValue(prePost, tag.NameColor, newValue);
        if ((types & TagType.ChatColor) != 0) tag.ChatColor = GetPrePostValue(prePost, tag.ChatColor, newValue);

        Tags.Api.TagsUpdatedPost(player, tag);
    }

    public static void SetAttribute(this IPlayer player, TagType types, string newValue)
    {
        var tag = GetOrCreatePlayerTag(player, false);
        Tags.Api.TagsUpdatedPre(player, tag);

        if ((types & TagType.ScoreTag) != 0)
        {
            tag.ScoreTag = newValue;
            player.SetScoreTag(newValue);
        }
        if ((types & TagType.ChatTag) != 0) tag.ChatTag = newValue;
        if ((types & TagType.NameColor) != 0) tag.NameColor = newValue;
        if ((types & TagType.ChatColor) != 0) tag.ChatColor = newValue;

        Tags.Api.TagsUpdatedPost(player, tag);
    }

    public static string? GetAttribute(this IPlayer player, TagType type)
    {
        var tag = GetOrCreatePlayerTag(player, false);
        return type switch
        {
            TagType.ScoreTag => tag.ScoreTag,
            TagType.ChatTag => tag.ChatTag,
            TagType.NameColor => tag.NameColor,
            TagType.ChatColor => tag.ChatColor,
            _ => null
        };
    }

    public static void ResetAttribute(this IPlayer player, TagType types)
    {
        var tag = GetOrCreatePlayerTag(player, false);
        var defaultTag = player.GetTag();

        Tags.Api.TagsUpdatedPre(player, tag);

        if ((types & TagType.ScoreTag) != 0)
        {
            tag.ScoreTag = defaultTag.ScoreTag;
            player.SetScoreTag(defaultTag.ScoreTag);
        }
        if ((types & TagType.ChatTag) != 0) tag.ChatTag = defaultTag.ChatTag;
        if ((types & TagType.NameColor) != 0) tag.NameColor = defaultTag.NameColor;
        if ((types & TagType.ChatColor) != 0) tag.ChatColor = defaultTag.ChatColor;

        Tags.Api.TagsUpdatedPost(player, tag);
    }

    public static bool GetChatSound(this IPlayer player)
        => PlayerTagsList.TryGetValue(player.SteamID, out var tag) ? tag.ChatSound : GetOrCreatePlayerTag(player, true).ChatSound;

    public static void SetChatSound(this IPlayer player, bool value)
    {
        var tag = GetOrCreatePlayerTag(player, false);
        Tags.Api.TagsUpdatedPre(player, tag);
        tag.ChatSound = value;
        Tags.Api.TagsUpdatedPost(player, tag);
    }

    public static bool GetVisibility(this IPlayer player)
        => PlayerTagsList.TryGetValue(player.SteamID, out var tag) ? tag.Visibility : GetOrCreatePlayerTag(player, true).Visibility;

    public static void SetVisibility(this IPlayer player, bool value)
    {
        var tag = GetOrCreatePlayerTag(player, false);
        Tags.Api.TagsUpdatedPre(player, tag);

        tag.Visibility = value;
        player.SetScoreTag(value ? player.GetAttribute(TagType.ScoreTag) : Tags.Config.Default.ScoreTag);

        Tags.Api.TagsUpdatedPost(player, tag);
    }

    public static void SetScoreTag(this IPlayer player, string? tag)
    {
        if (player == null || !player.IsValid)
            return;

        var normalized = tag ?? string.Empty;

        if (normalized.Length == 0)
        {
            if (player.Controller.Clan != string.Empty)
                player.Controller.Clan = string.Empty;

            player.Controller.ClanUpdated();
            FireScoreTagRefreshEvent();
            return;
        }

        if (player.Controller.Clan != normalized)
            player.Controller.Clan = normalized;

        player.Controller.ClanUpdated();
        FireScoreTagRefreshEvent();
    }

    private static void FireScoreTagRefreshEvent()
    {
        if (Instance == null)
            return;

        if (Interlocked.Exchange(ref _scoreRefreshScheduled, 1) == 1)
            return;

        Instance.Scheduler.NextWorldUpdate(() =>
        {
            Interlocked.Exchange(ref _scoreRefreshScheduled, 0);
            if (Instance == null) return;
            Instance.GameEvent.Fire<EventNextlevelChanged>();
        });
    }

    public static void ReloadConfig()
    {
        Tags.Config = ConfigMonitor.CurrentValue;
        Tags.Config.Settings.Init();
        RebuildTagIndexes();
    }

    public static void ReloadTags()
    {
        RebuildTagIndexes();

        var players = Instance.PlayerManager.GetAllPlayers();
        foreach (var player in players)
        {
            if (player == null || !player.IsValid || player.IsFakeClient || player.SteamID == 0)
                continue;

            var tag = GetOrCreatePlayerTag(player, true);
            player.SetScoreTag(player.GetVisibility() ? tag.ScoreTag : Tags.Config.Default.ScoreTag);
        }
    }
}
