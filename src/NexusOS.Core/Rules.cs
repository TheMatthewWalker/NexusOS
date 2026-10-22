using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// Automations in plain words (feature 9). A rule is data in a fixed schema:
// one trigger from a fixed list, optional conditions, and one to five actions
// from a fixed list. Nothing here runs anything: Core matches rules against
// events and hands the actions to the shell, which performs them. Actions that
// would change the system do not exist in this schema; they stay Guardian
// proposals.
public class RuleRejectedException(string message) : Exception(message);

public sealed class SystemChangeRejectedException() : RuleRejectedException(RuleSchema.SystemChangeMessage);

public static class RuleTriggers
{
    public const string DeviceConnected = "device.connected";
    public const string DeviceDisconnected = "device.disconnected";
    public const string TimeDaily = "time.daily";
    public const string AppOpened = "app.opened";
    public const string NetworkJoined = "network.joined";
    public const string PowerPlugged = "power.plugged";
    public const string PowerUnplugged = "power.unplugged";
    public static readonly string[] All =
        [DeviceConnected, DeviceDisconnected, TimeDaily, AppOpened, NetworkJoined, PowerPlugged, PowerUnplugged];
    // Everything except time, which Core's own timer observes.
    public static readonly string[] Events = All.Where(x => x != TimeDaily).ToArray();
    public static readonly string[] DeviceKinds = ["audio", "display", "usb"];
}

public static class RuleConditions
{
    public const string TimeBetween = "time.between";
    public const string Days = "time.days";
    public const string PowerIs = "power.is";
    public static readonly string[] All = [TimeBetween, Days, PowerIs];
    public static readonly string[] PowerStates = ["plugged", "unplugged"];
}

public static class RuleActions
{
    public const string AppOpen = "app.open";
    public const string SettingSet = "setting.set";
    public const string Notify = "notify";
    public const string BackupRun = "backup.run";
    public static readonly string[] All = [AppOpen, SettingSet, Notify, BackupRun];
}

public enum RuleSettingKind { Percent, Toggle, Wallpaper }

// Reversible personal settings only. Each has a value the shell can read
// back and restore; nothing here reaches system configuration.
public static class RuleSettings
{
    public const string Volume = "volume";
    public const string NotificationsVolume = "notifications.volume";
    public const string NightLight = "night-light";
    public const string DoNotDisturb = "do-not-disturb";
    public const string Wallpaper = "wallpaper";
    public static readonly IReadOnlyDictionary<string, (RuleSettingKind Kind, string Label)> Keys =
        new Dictionary<string, (RuleSettingKind, string)>(StringComparer.Ordinal)
        {
            [Volume] = (RuleSettingKind.Percent, "volume"),
            [NotificationsVolume] = (RuleSettingKind.Percent, "notifications volume"),
            [NightLight] = (RuleSettingKind.Toggle, "night light"),
            [DoNotDisturb] = (RuleSettingKind.Toggle, "do-not-disturb"),
            [Wallpaper] = (RuleSettingKind.Wallpaper, "wallpaper"),
        };
}

public sealed record RuleTrigger(string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Time = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Days = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AppId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ssid = null);

public sealed record RuleCondition(string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? From = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? To = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Days = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State = null);

public sealed record RuleAction(string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AppId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Profile = null);

public sealed record RuleDefinition(RuleTrigger Trigger, IReadOnlyList<RuleCondition> Conditions, IReadOnlyList<RuleAction> Actions);

// An event the shell observed (device, app, network, power). Same field rules as triggers.
public sealed record RuleEvent(string Type, string? Kind = null, string? Name = null, string? AppId = null, string? Ssid = null);

public static partial class RuleSchema
{
    public const string SystemChangeMessage =
        "System changes go through approval: ask Nexus to propose the change and Guardian will show it to you first. " +
        "Automations can only open apps, change personal settings, notify you or run a backup.";
    public const int MaxConditions = 4;
    public const int MaxActions = 5;
    public const int MaxNameLength = 80;
    public const int MaxCreatedFromLength = 500;
    public const int MaxNotifyLength = 200;
    public const int MaxDeviceMatchLength = 64;
    public const int MaxEventNameLength = 128;
    public static readonly string[] DayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    [GeneratedRegex(@"^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    private static partial Regex TimePattern();
    // App ids are desktop-entry style names the shell resolves against its
    // installed apps; no spaces, slashes or shell syntax can appear.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex AppIdPattern();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex WallpaperPattern();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex ProfilePattern();

    // Interpreters and privileged tools are not apps: naming one is a request
    // to run commands, which only Guardian may turn into a change.
    private static readonly HashSet<string> CommandLikeApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "su", "pkexec", "doas", "sh", "bash", "zsh", "fish", "dash", "ksh", "csh", "env", "xargs",
        "python", "python3", "perl", "ruby", "node", "nix", "nix-env", "nix-shell", "nixos-rebuild",
        "systemctl", "rm", "dd", "mkfs", "chmod", "chown", "kill", "pkill", "killall", "reboot",
        "shutdown", "poweroff", "halt", "curl", "wget", "crontab", "at", "xdg-open"
    };

    // An action or trigger type the model invents that names a system change
    // gets the approval message rather than a generic "unknown type".
    [GeneratedRegex(@"shell|command|exec|script|system|sudo|root|package|install|service|guardian|nix|file|delete|kill|reboot|shutdown",
        RegexOptions.IgnoreCase)]
    private static partial Regex SystemLikeType();

    public static bool IsSystemLikeType(string type) => SystemLikeType().IsMatch(type);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Strict reading: exactly the allowed keys for each type, each once,
    // with the right JSON types. Used for model output, API input and stored
    // rows alike, so there is one definition of a valid rule.
    public static RuleDefinition ReadDefinition(JsonElement root, params string[] extraKeys)
    {
        var keys = Keys(root, "Rule", ["trigger", "actions"], ["conditions", .. extraKeys]);
        var trigger = ReadTrigger(root.GetProperty("trigger"));
        var conditions = new List<RuleCondition>();
        if (keys.Contains("conditions"))
        {
            var list = root.GetProperty("conditions");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxConditions)
                throw new RuleRejectedException($"Conditions must be a list of at most {MaxConditions}.");
            conditions.AddRange(list.EnumerateArray().Select(ReadCondition));
        }
        var actionList = root.GetProperty("actions");
        if (actionList.ValueKind != JsonValueKind.Array || actionList.GetArrayLength() is 0 or > MaxActions)
            throw new RuleRejectedException($"A rule needs 1 to {MaxActions} actions.");
        var actions = actionList.EnumerateArray().Select(ReadAction).ToList();
        return Validate(new RuleDefinition(trigger, conditions, actions));
    }

    public static RuleTrigger ReadTrigger(JsonElement element)
    {
        var type = TypeOf(element, "Trigger", RuleTriggers.All);
        switch (type)
        {
            case RuleTriggers.DeviceConnected or RuleTriggers.DeviceDisconnected:
            {
                var keys = Keys(element, "Trigger", ["type", "kind"], ["name"]);
                return new RuleTrigger(type, OneOf(element, "kind", RuleTriggers.DeviceKinds),
                    keys.Contains("name") ? Text(element, "name", MaxDeviceMatchLength) : null);
            }
            case RuleTriggers.TimeDaily:
            {
                var keys = Keys(element, "Trigger", ["type", "time"], ["days"]);
                return new RuleTrigger(type, Time: Clock(element, "time"),
                    Days: keys.Contains("days") ? Days(element.GetProperty("days")) : null);
            }
            case RuleTriggers.AppOpened:
                Keys(element, "Trigger", ["type", "appId"], []);
                return new RuleTrigger(type, AppId: AppId(element));
            case RuleTriggers.NetworkJoined:
                Keys(element, "Trigger", ["type", "ssid"], []);
                return new RuleTrigger(type, Ssid: Ssid(element));
            default:
                Keys(element, "Trigger", ["type"], []);
                return new RuleTrigger(type);
        }
    }

    public static RuleCondition ReadCondition(JsonElement element)
    {
        var type = TypeOf(element, "Condition", RuleConditions.All);
        switch (type)
        {
            case RuleConditions.TimeBetween:
                Keys(element, "Condition", ["type", "from", "to"], []);
                var from = Clock(element, "from");
                var to = Clock(element, "to");
                if (from == to) throw new RuleRejectedException("A time range needs different start and end times.");
                return new RuleCondition(type, From: from, To: to);
            case RuleConditions.Days:
                Keys(element, "Condition", ["type", "days"], []);
                return new RuleCondition(type, Days: Days(element.GetProperty("days")));
            default:
                Keys(element, "Condition", ["type", "state"], []);
                return new RuleCondition(type, State: OneOf(element, "state", RuleConditions.PowerStates));
        }
    }

    public static RuleAction ReadAction(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("type", out var raw) &&
            raw.ValueKind == JsonValueKind.String && !RuleActions.All.Contains(raw.GetString()) &&
            IsSystemLikeType(raw.GetString()!))
            throw new SystemChangeRejectedException();
        var type = TypeOf(element, "Action", RuleActions.All);
        switch (type)
        {
            case RuleActions.AppOpen:
                Keys(element, "Action", ["type", "appId"], []);
                return new RuleAction(type, AppId: AppId(element));
            case RuleActions.SettingSet:
            {
                Keys(element, "Action", ["type", "key", "value"], []);
                var key = element.GetProperty("key");
                if (key.ValueKind != JsonValueKind.String || !RuleSettings.Keys.TryGetValue(key.GetString()!, out var setting))
                    throw new RuleRejectedException(
                        "Settings must be one of: " + string.Join(", ", RuleSettings.Keys.Keys) + ".");
                return new RuleAction(type, Key: key.GetString(), Value: SettingValue(setting.Kind, element.GetProperty("value")));
            }
            case RuleActions.Notify:
                Keys(element, "Action", ["type", "text"], []);
                return new RuleAction(type, Text: Text(element, "text", MaxNotifyLength));
            default:
                var keys = Keys(element, "Action", ["type"], ["profile"]);
                string profile = "home";
                if (keys.Contains("profile"))
                {
                    var p = element.GetProperty("profile");
                    if (p.ValueKind != JsonValueKind.String || !ProfilePattern().IsMatch(p.GetString()!))
                        throw new RuleRejectedException("A backup profile is a short lowercase name, for example 'home'.");
                    profile = p.GetString()!;
                }
                return new RuleAction(type, Profile: profile);
        }
    }

    public static RuleEvent ReadEvent(JsonElement element)
    {
        var type = TypeOf(element, "Event", RuleTriggers.Events);
        switch (type)
        {
            case RuleTriggers.DeviceConnected or RuleTriggers.DeviceDisconnected:
            {
                var keys = Keys(element, "Event", ["type", "kind"], ["name"]);
                return new RuleEvent(type, OneOf(element, "kind", RuleTriggers.DeviceKinds),
                    keys.Contains("name") ? Text(element, "name", MaxEventNameLength) : null);
            }
            case RuleTriggers.AppOpened:
                Keys(element, "Event", ["type", "appId"], []);
                return new RuleEvent(type, AppId: AppId(element));
            case RuleTriggers.NetworkJoined:
                Keys(element, "Event", ["type", "ssid"], []);
                return new RuleEvent(type, Ssid: Ssid(element));
            default:
                Keys(element, "Event", ["type"], []);
                return new RuleEvent(type);
        }
    }

    // Whole-rule checks that span the trigger and actions.
    public static RuleDefinition Validate(RuleDefinition rule)
    {
        if (rule.Actions.Count is 0 or > MaxActions)
            throw new RuleRejectedException($"A rule needs 1 to {MaxActions} actions.");
        if (rule.Conditions.Count > MaxConditions)
            throw new RuleRejectedException($"A rule can have at most {MaxConditions} conditions.");
        foreach (var action in rule.Actions.Where(a => a.Type == RuleActions.AppOpen))
            if (CommandLikeApps.Contains(action.AppId!)) throw new SystemChangeRejectedException();
        if (rule.Trigger.Type == RuleTriggers.AppOpened && rule.Trigger.AppId is { } opened && CommandLikeApps.Contains(opened))
            throw new SystemChangeRejectedException();
        if (rule.Trigger.Type == RuleTriggers.AppOpened && rule.Actions.Any(a =>
                a.Type == RuleActions.AppOpen && string.Equals(a.AppId, rule.Trigger.AppId, StringComparison.OrdinalIgnoreCase)))
            throw new RuleRejectedException("This rule would trigger itself: it opens the app that starts it.");
        if (rule.Conditions.GroupBy(c => c.Type).Any(g => g.Count() > 1))
            throw new RuleRejectedException("Each kind of condition can appear once.");
        return rule;
    }

    public static string Serialize(RuleDefinition rule) =>
        JsonSerializer.Serialize(new { trigger = rule.Trigger, conditions = rule.Conditions, actions = rule.Actions }, Json);

    public static RuleDefinition Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        return ReadDefinition(document.RootElement);
    }

    // The sentence card. Core renders it from the typed rule, never from model
    // text, so what the person reads is exactly what the rule does.
    public static string Describe(RuleDefinition rule)
    {
        var text = new StringBuilder(DescribeTrigger(rule.Trigger));
        if (rule.Conditions.Count > 0)
            text.Append(", ").Append(string.Join(" and ", rule.Conditions.Select(DescribeCondition)));
        text.Append(", ").Append(JoinAnd(rule.Actions.Select(DescribeAction).ToList())).Append('.');
        return text.ToString();
    }

    public static string DescribeTrigger(RuleTrigger trigger) => trigger.Type switch
    {
        RuleTriggers.DeviceConnected => $"When {DeviceNoun(trigger)} connects",
        RuleTriggers.DeviceDisconnected => $"When {DeviceNoun(trigger)} disconnects",
        RuleTriggers.TimeDaily => $"{DaysPhrase(trigger.Days, true)} at {trigger.Time}",
        RuleTriggers.AppOpened => $"When {trigger.AppId} opens",
        RuleTriggers.NetworkJoined => $"When you join the “{trigger.Ssid}” network",
        RuleTriggers.PowerPlugged => "When the charger is plugged in",
        _ => "When the charger is unplugged",
    };

    public static string DescribeEvent(RuleEvent e) => e.Type switch
    {
        RuleTriggers.DeviceConnected or RuleTriggers.DeviceDisconnected =>
            $"{(e.Kind == "usb" ? "USB" : e.Kind)} device{(e.Name is null ? "" : $" “{e.Name}”")} " +
            (e.Type == RuleTriggers.DeviceConnected ? "connected" : "disconnected"),
        RuleTriggers.AppOpened => $"{e.AppId} opened",
        RuleTriggers.NetworkJoined => $"joined “{e.Ssid}”",
        RuleTriggers.PowerPlugged => "charger plugged in",
        _ => "charger unplugged",
    };

    private static string DescribeCondition(RuleCondition condition) => condition.Type switch
    {
        RuleConditions.TimeBetween => $"if it is between {condition.From} and {condition.To}",
        RuleConditions.Days => $"if it is {DaysPhrase(condition.Days, false)}",
        _ => condition.State == "plugged" ? "if the charger is plugged in" : "if running on battery",
    };

    private static string DescribeAction(RuleAction action)
    {
        switch (action.Type)
        {
            case RuleActions.AppOpen: return $"open {action.AppId}";
            case RuleActions.Notify: return $"notify you: “{action.Text}”";
            case RuleActions.BackupRun: return $"run the “{action.Profile}” backup";
        }
        var (kind, label) = RuleSettings.Keys[action.Key!];
        var value = action.Value!.Value;
        return kind switch
        {
            RuleSettingKind.Percent => $"set {label} to {value.GetInt32()}%",
            RuleSettingKind.Toggle => $"turn {label} {(value.GetBoolean() ? "on" : "off")}",
            _ => $"set the {label} to {value.GetString()}",
        };
    }

    private static string DeviceNoun(RuleTrigger trigger)
    {
        var noun = trigger.Kind switch { "audio" => "an audio device", "display" => "a display", _ => "a USB device" };
        return trigger.Name is null ? noun : $"{noun} matching “{trigger.Name}”";
    }

    private static string DaysPhrase(string[]? days, bool leading)
    {
        string phrase;
        if (days is null || days.Length == 7) phrase = "every day";
        else if (days.SequenceEqual(["mon", "tue", "wed", "thu", "fri"])) phrase = "every weekday";
        else if (days.SequenceEqual(["sat", "sun"])) phrase = "every weekend day";
        else phrase = "on " + JoinAnd(days.Select(d => char.ToUpperInvariant(d[0]) + d[1..]).ToList());
        if (!leading) return phrase == "every day" ? "any day" : phrase.Replace("every ", "a ", StringComparison.Ordinal);
        return char.ToUpperInvariant(phrase[0]) + phrase[1..];
    }

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    // Drops control characters, bidi overrides and other invisible format
    // characters, and line separators, so stored text is one plain line.
    public static string Clean(string value) =>
        new string(value.Where(ch => !char.IsControl(ch) && char.GetUnicodeCategory(ch) is not
            (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or
             System.Globalization.UnicodeCategory.ParagraphSeparator)).ToArray()).Trim();

    private static HashSet<string> Keys(JsonElement element, string what, string[] required, string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new RuleRejectedException($"{what} must be a JSON object.");
        var names = element.EnumerateObject().Select(x => x.Name).ToList();
        var set = names.ToHashSet(StringComparer.Ordinal);
        if (set.Count != names.Count) throw new RuleRejectedException($"{what} has a repeated field.");
        if (!required.All(set.Contains) || !set.All(n => required.Contains(n) || optional.Contains(n)))
            throw new RuleRejectedException($"{what} has unexpected or missing fields.");
        return set;
    }

    private static string TypeOf(JsonElement element, string what, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || !allowed.Contains(type.GetString(), StringComparer.Ordinal))
            throw new RuleRejectedException($"{what} type must be one of: {string.Join(", ", allowed)}.");
        return type.GetString()!;
    }

    private static string OneOf(JsonElement element, string key, string[] allowed)
    {
        var value = element.GetProperty(key);
        if (value.ValueKind != JsonValueKind.String || !allowed.Contains(value.GetString(), StringComparer.Ordinal))
            throw new RuleRejectedException($"'{key}' must be one of: {string.Join(", ", allowed)}.");
        return value.GetString()!;
    }

    private static string Text(JsonElement element, string key, int max)
    {
        var value = element.GetProperty(key);
        if (value.ValueKind != JsonValueKind.String) throw new RuleRejectedException($"'{key}' must be text.");
        var text = Clean(value.GetString()!);
        if (text.Length is 0 || text.Length > max) throw new RuleRejectedException($"'{key}' must be 1-{max} characters.");
        return text;
    }

    private static string Clock(JsonElement element, string key)
    {
        var value = element.GetProperty(key);
        if (value.ValueKind != JsonValueKind.String || !TimePattern().IsMatch(value.GetString()!))
            throw new RuleRejectedException($"'{key}' must be a 24-hour time such as 08:30.");
        return value.GetString()!;
    }

    private static string[] Days(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > 7)
            throw new RuleRejectedException("'days' must list 1 to 7 days.");
        var days = new HashSet<string>(StringComparer.Ordinal);
        foreach (var day in value.EnumerateArray())
            if (day.ValueKind != JsonValueKind.String || !DayNames.Contains(day.GetString()) || !days.Add(day.GetString()!))
                throw new RuleRejectedException("'days' must be distinct values from: " + string.Join(", ", DayNames) + ".");
        return DayNames.Where(days.Contains).ToArray();
    }

    private static string AppId(JsonElement element)
    {
        var value = element.GetProperty("appId");
        if (value.ValueKind != JsonValueKind.String || !AppIdPattern().IsMatch(value.GetString()!))
            throw new RuleRejectedException("'appId' must be an app name such as 'spotify' or 'org.gnome.Calendar'.");
        return value.GetString()!;
    }

    private static string Ssid(JsonElement element)
    {
        var ssid = Text(element, "ssid", 32);
        if (Encoding.UTF8.GetByteCount(ssid) > 32) throw new RuleRejectedException("A Wi-Fi name is at most 32 bytes.");
        return ssid;
    }

    private static JsonElement SettingValue(RuleSettingKind kind, JsonElement value)
    {
        switch (kind)
        {
            case RuleSettingKind.Percent:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var percent) || percent is < 0 or > 100)
                    throw new RuleRejectedException("Volume levels are whole numbers from 0 to 100.");
                return JsonSerializer.SerializeToElement(percent);
            case RuleSettingKind.Toggle:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new RuleRejectedException("Night light and do-not-disturb take true or false.");
                return JsonSerializer.SerializeToElement(value.GetBoolean());
            default:
                if (value.ValueKind != JsonValueKind.String || !WallpaperPattern().IsMatch(value.GetString()!) ||
                    value.GetString()!.Contains("..", StringComparison.Ordinal))
                    throw new RuleRejectedException("A wallpaper is named from your wallpaper library, not by a path.");
                return JsonSerializer.SerializeToElement(value.GetString());
        }
    }
}
