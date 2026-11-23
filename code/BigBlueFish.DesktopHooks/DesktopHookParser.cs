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
    Cancelled,
    Ended,
    Interrupted
}

public sealed record DesktopHookReminder(
    DesktopHookSource Source,
    DesktopHookOutcome Outcome,
    string Title,
    string Message,
    string? ProjectName,
    string AnimationId,
    string? ModelName = null);

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

        reminder = Build(DesktopHookSource.Gemini, GeminiOutcome(ReadString(payload, "terminationReason")), FirstProjectName(payload, "workspacePaths"), ReadString(payload, "modelName"));
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

        // Official Cursor hook fields still do not distinguish the desktop app from the CLI.
        // Do not infer that from process trees, log files, or unofficial properties.
        var project = FirstProjectName(payload, "workspace_roots");
        if (project is null && env.TryGetValue("CURSOR_PROJECT_DIR", out var projectDir))
            project = ProjectNameFromPath(projectDir);
        reminder = Build(DesktopHookSource.Cursor, CursorOutcome(status), project);
        discardReason = "";
        return true;
    }

    static bool TryParseCodex(JsonObject payload, Dictionary<string, string?> env, out DesktopHookReminder? reminder, out string discardReason)
    {
        reminder = null;
        _ = env;
        // Official Codex payloads have no documented desktop-vs-CLI discriminator.
        // Do not infer that from process trees, log files, or unofficial properties.
        var eventName = ReadString(payload, "hook_event_name");
        var key = (eventName ?? "").Trim().Replace('-', '_').ToLowerInvariant();
        if (key is not ("stop" or "interrupt" or "user_interrupt" or "interrupted"))
        {
            discardReason = "invalid-event";
            return false;
        }

        reminder = Build(DesktopHookSource.Codex, CodexOutcome(eventName), FirstProjectName(payload, "workspace_roots"));
        discardReason = "";
        return true;
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

    public static DesktopHookReminder Build(DesktopHookSource source, DesktopHookOutcome outcome, string? projectName, string? modelName = null)
    {
        var app = AppName(source);
        var result = ResultPhrase(outcome);
        var title = string.IsNullOrWhiteSpace(projectName) ? $"{app} {result}" : $"{app} · {projectName} {result}";
        if (title.Length > 120)
            title = title[..120];
        var model = SanitizeModelName(modelName);
        var message = MessageFor(source, outcome);
        if (model != null)
            message += "使用的是 " + model + "。";
        return new DesktopHookReminder(source, outcome, title, message, projectName, AnimationIdFor(outcome), model);
    }

    public static string AppName(DesktopHookSource source) => source switch
    {
        DesktopHookSource.Cursor => "Cursor",
        DesktopHookSource.Codex => "Codex",
        _ => "Gemini"
    };

    public static string ResultPhrase(DesktopHookOutcome outcome) => outcome switch
    {
        DesktopHookOutcome.Failed => "未能完成",
        DesktopHookOutcome.Cancelled => "已取消",
        DesktopHookOutcome.Ended => "结束了",
        DesktopHookOutcome.Interrupted => "被停下了",
        DesktopHookOutcome.Completed => "已完成",
        _ => "未能完成"
    };

    public static string MessageFor(DesktopHookSource source, DesktopHookOutcome outcome)
    {
        var app = AppName(source);
        if (source is DesktopHookSource.Cursor or DesktopHookSource.Codex)
        {
            return outcome switch
            {
                DesktopHookOutcome.Failed => app + " 这一轮没能做完。",
                DesktopHookOutcome.Cancelled => app + " 这一轮取消了。",
                DesktopHookOutcome.Ended => app + " 这一轮结束了。",
                DesktopHookOutcome.Interrupted => app + " 这一轮被停下了。",
                DesktopHookOutcome.Completed => app + " 这一轮做完了。",
                _ => app + " 这一轮没能做完。"
            };
        }

        return outcome switch
        {
            DesktopHookOutcome.Failed => app + " 在图形界面里没能做完这一轮。",
            DesktopHookOutcome.Cancelled => app + " 在图形界面里取消了这一轮。",
            DesktopHookOutcome.Ended => app + " 在图形界面里这一轮结束了。",
            DesktopHookOutcome.Interrupted => app + " 在图形界面里这一轮被停下了。",
            DesktopHookOutcome.Completed => app + " 在图形界面里做完了这一轮。",
            _ => app + " 在图形界面里没能做完这一轮。"
        };
    }

    public static string AnimationIdFor(DesktopHookOutcome outcome) => outcome switch
    {
        DesktopHookOutcome.Completed => "工作状态-雀跃庆祝",
        DesktopHookOutcome.Failed => "工作状态-垂头叹气冒汗",
        DesktopHookOutcome.Cancelled => "点击回应-害羞惊讶",
        DesktopHookOutcome.Ended => "工作状态-清点归档",
        DesktopHookOutcome.Interrupted => "点击回应-傲娇生气",
        _ => "工作状态-垂头叹气冒汗"
    };

    public static DesktopHookOutcome CursorOutcome(string? status)
    {
        var key = status?.Trim().ToLowerInvariant();
        if (key == "completed")
            return DesktopHookOutcome.Completed;
        if (key == "aborted")
            return DesktopHookOutcome.Cancelled;
        return DesktopHookOutcome.Failed;
    }

    public static DesktopHookOutcome CodexOutcome(string? hookEventName)
    {
        var key = (hookEventName ?? "").Trim().Replace('-', '_').ToLowerInvariant();
        if (key is "interrupt" or "user_interrupt" or "interrupted")
            return DesktopHookOutcome.Interrupted;
        return DesktopHookOutcome.Ended;
    }

    public static string? SanitizeModelName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var text = name.Trim();
        if (text.Contains('@') || text.Contains('/') || text.Contains('\\') ||
            text.Contains("transcript", StringComparison.OrdinalIgnoreCase))
            return null;
        return text.Length > 40 ? text[..40] : text;
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
