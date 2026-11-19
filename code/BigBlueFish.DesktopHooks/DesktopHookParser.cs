using System.Text.Json;
using System.Text.Json.Nodes;

namespace BigBlueFish.DesktopHooks;

public enum DesktopHookSource
{
    Cursor,
    Codex,
    Gemini
}

public enum DesktopHookOutcome
{
    Completed,
    Failed,
    Cancelled
}

public sealed record DesktopHookReminder(
    DesktopHookSource Source,
    DesktopHookOutcome Outcome,
    string Title,
    string Message,
    string? ProjectName);

public static class DesktopHookParser
{
    public const int MaxBodyBytes = 256 * 1024;

    static readonly HashSet<string> AllowedEnvNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CURSOR_PROJECT_DIR",
        "CURSOR_VERSION",
        "CURSOR_TRANSCRIPT_PATH",
        "CURSOR_CODE_REMOTE",
        "CURSOR_PLUGIN_ROOT"
    };

    public static bool TryParse(string? sourceKind, string? json, IReadOnlyDictionary<string, string?>? env, out DesktopHookReminder? reminder, out string discardReason)
    {
        reminder = null;
        discardReason = "invalid-json";
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBodyBytes)
            return false;

        JsonObject root;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
                return false;
            root = obj;
        }
        catch (JsonException)
        {
            return false;
        }

        var payload = root;
        var mergedEnv = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        CopyAllowedEnv(env, mergedEnv);

        if (root.TryGetPropertyValue("source", out var sourceNode) && sourceNode is JsonValue sourceValue
            && root.TryGetPropertyValue("payload", out var payloadNode))
        {
            if (payloadNode is not JsonObject payloadObj)
            {
                discardReason = "invalid-json";
                return false;
            }
            if (!sourceValue.TryGetValue<string>(out var wrappedSource) || string.IsNullOrWhiteSpace(wrappedSource))
            {
                discardReason = "unknown-source";
                return false;
            }
            sourceKind = wrappedSource;
            payload = payloadObj;
            if (root.TryGetPropertyValue("env", out var envNode) && envNode is JsonObject envObj)
                CopyAllowedEnvObject(envObj, mergedEnv);
        }

        if (!TryParseSource(sourceKind, out var source))
        {
            discardReason = "unknown-source";
            return false;
        }

        return source switch
        {
            DesktopHookSource.Gemini => TryParseGemini(payload, out reminder, out discardReason),
            DesktopHookSource.Cursor => TryParseCursor(payload, mergedEnv, out reminder, out discardReason),
            DesktopHookSource.Codex => TryParseCodex(payload, mergedEnv, out reminder, out discardReason),
            _ => Fail("unknown-source", out reminder, out discardReason)
        };
    }

    static bool TryParseGemini(JsonObject payload, out DesktopHookReminder? reminder, out string discardReason)
    {
        reminder = null;
        if (!payload.TryGetPropertyValue("fullyIdle", out var idleNode) || idleNode is not JsonValue idleValue
            || !idleValue.TryGetValue<bool>(out var idle) || !idle)
        {
            discardReason = "not-idle";
            return false;
        }

        var paths = new List<string>();
        AddString(payload, "transcriptPath", paths);
        AddString(payload, "artifactDirectoryPath", paths);
        if (paths.Any(IsCliSurfacePath))
        {
            discardReason = "cli";
            return false;
        }
        if (!paths.Any(IsDesktopSurfacePath))
        {
            discardReason = "unconfirmed-desktop";
            return false;
        }

        reminder = Build(DesktopHookSource.Gemini, GeminiOutcome(ReadString(payload, "terminationReason")), FirstProjectName(payload, "workspacePaths"));
        discardReason = "";
        return true;
    }

    static bool TryParseCursor(JsonObject payload, Dictionary<string, string?> env, out DesktopHookReminder? reminder, out string discardReason)
    {
        reminder = null;
        var status = ReadString(payload, "status");
        if (status is null ||
            status is not ("completed" or "error" or "aborted"))
        {
            discardReason = "invalid-status";
            return false;
        }

        // Official Cursor hook fields do not distinguish the desktop app from the CLI.
        // Do not infer this from process trees, log files, or unofficial properties.
        _ = env;
        _ = payload;
        discardReason = "unconfirmed-desktop";
        return false;
    }

    static bool TryParseCodex(JsonObject payload, Dictionary<string, string?> env, out DesktopHookReminder? reminder, out string discardReason)
    {
        reminder = null;
        // Official Codex Stop payloads have no documented desktop-vs-CLI discriminator.
        _ = env;
        _ = payload;
        discardReason = "unconfirmed-desktop";
        return false;
    }

    public static DesktopHookOutcome GeminiOutcome(string? reason)
    {
        var key = NormalizeTerminationReason(reason);
        if (key is "user_canceled" or "user_cancelled" or "aborted" or "canceled" or "cancelled")
            return DesktopHookOutcome.Cancelled;
        if (key is "no_tool_call" or "model_stop" or "terminal_step_type")
            return DesktopHookOutcome.Completed;
        // Official Antigravity reasons also include ERROR, MAX_INVOCATIONS,
        // MAX_FORCED_INVOCATIONS, MAX_TOKEN_BUDGET_EXCEEDED, TERMINAL_CUSTOM_HOOK,
        // INJECTED_RESPONSE, plus public aliases such as error and max_steps_exceeded.
        // Unknown reasons are not treated as a successful finish.
        return DesktopHookOutcome.Failed;
    }

    static string NormalizeTerminationReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "";
        var key = reason.Trim().Replace('-', '_');
        const string prefix = "EXECUTOR_TERMINATION_REASON_";
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            key = key[prefix.Length..];
        return key.ToLowerInvariant();
    }

    public static bool IsCliSurfacePath(string? path) => HasPathSegment(path, "antigravity-cli");

    public static bool IsDesktopSurfacePath(string? path) =>
        HasPathSegment(path, "antigravity-ide") || HasPathSegment(path, "antigravity");

    public static bool HasPathSegment(string? path, string segment)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(segment))
            return false;
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals(segment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static bool TryParseSource(string? sourceKind, out DesktopHookSource source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(sourceKind))
            return false;
        switch (sourceKind.Trim().ToLowerInvariant())
        {
            case "cursor":
                source = DesktopHookSource.Cursor;
                return true;
            case "codex":
                source = DesktopHookSource.Codex;
                return true;
            case "gemini":
            case "antigravity":
                source = DesktopHookSource.Gemini;
                return true;
            default:
                return false;
        }
    }

    public static DesktopHookReminder Build(DesktopHookSource source, DesktopHookOutcome outcome, string? projectName)
    {
        var app = source switch
        {
            DesktopHookSource.Cursor => "Cursor",
            DesktopHookSource.Codex => "Codex",
            _ => "Gemini"
        };
        var result = outcome switch
        {
            DesktopHookOutcome.Failed => "未能完成",
            DesktopHookOutcome.Cancelled => "已取消",
            _ => "已完成"
        };
        var title = string.IsNullOrWhiteSpace(projectName) ? $"{app} {result}" : $"{app} · {projectName} {result}";
        if (title.Length > 120)
            title = title[..120];
        var message = outcome switch
        {
            DesktopHookOutcome.Failed => "图形界面里的这一轮任务没能完成。",
            DesktopHookOutcome.Cancelled => "图形界面里的这一轮任务已取消。",
            _ => "图形界面里的这一轮任务已经结束。"
        };
        return new DesktopHookReminder(source, outcome, title, message, projectName);
    }

    static bool Fail(string reason, out DesktopHookReminder? reminder, out string discardReason)
    {
        reminder = null;
        discardReason = reason;
        return false;
    }

    static void CopyAllowedEnv(IReadOnlyDictionary<string, string?>? env, Dictionary<string, string?> dest)
    {
        if (env is null)
            return;
        foreach (var pair in env)
        {
            if (IsAllowedEnvName(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                dest[pair.Key] = pair.Value;
        }
    }

    static void CopyAllowedEnvObject(JsonObject envObj, Dictionary<string, string?> dest)
    {
        foreach (var pair in envObj)
        {
            if (!IsAllowedEnvName(pair.Key) || pair.Value is not JsonValue value)
                continue;
            if (value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
                dest[pair.Key] = text;
        }
    }

    static bool IsAllowedEnvName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && AllowedEnvNames.Contains(name);

    static string? ReadString(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is not JsonValue value)
            return null;
        return value.TryGetValue<string>(out var text) ? text : null;
    }

    static void AddString(JsonObject obj, string name, List<string> list)
    {
        var text = ReadString(obj, name);
        if (!string.IsNullOrWhiteSpace(text))
            list.Add(text);
    }

    static string? FirstProjectName(JsonObject payload, string arrayName)
    {
        if (payload.TryGetPropertyValue(arrayName, out var node) && node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    var name = ProjectNameFromPath(text);
                    if (name != null)
                        return name;
                }
            }
        }
        return ProjectNameFromPath(ReadString(payload, "cwd"));
    }

    public static string? ProjectNameFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var trimmed = path.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0)
            return null;
        var name = Path.GetFileName(trimmed.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith(':'))
            return null;
        return name.Length > 60 ? name[..60] : name;
    }
}
