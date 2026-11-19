using System.Text.Json;
using System.Text.Json.Nodes;

namespace BigBlueFish.DesktopHooks;

public static class DesktopHookConfigInstaller
{
    public const string ScriptFileName = "Send-DesktopHook.ps1";
    public const string GeminiHookName = "big-blue-fish-desktop-reminder";
    public const int HookTimeoutSeconds = 3;

    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public static string BuildCommand(string scriptPath, string source)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            throw new ArgumentException("scriptPath");
        var kind = source.Trim().ToLowerInvariant();
        if (kind is not ("cursor" or "codex" or "gemini"))
            throw new ArgumentException("source");
        return "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -Source " + kind;
    }

    public static bool IsOurCommand(string? command) =>
        !string.IsNullOrWhiteSpace(command) && command.Contains(ScriptFileName, StringComparison.OrdinalIgnoreCase);

    public static bool TryMergeCursor(string? existingJson, string scriptPath, out string updatedJson, out string status)
        => TryMerge(existingJson, scriptPath, "cursor", MergeCursor, out updatedJson, out status);

    public static bool TryMergeCodex(string? existingJson, string scriptPath, out string updatedJson, out string status)
        => TryMerge(existingJson, scriptPath, "codex", MergeCodex, out updatedJson, out status);

    public static bool TryMergeGemini(string? existingJson, string scriptPath, out string updatedJson, out string status)
        => TryMerge(existingJson, scriptPath, "gemini", MergeGemini, out updatedJson, out status);

    static bool TryMerge(
        string? existingJson,
        string scriptPath,
        string source,
        Func<JsonObject, string, bool> merge,
        out string updatedJson,
        out string status)
    {
        updatedJson = existingJson ?? "";
        try
        {
            JsonObject root;
            if (string.IsNullOrWhiteSpace(existingJson))
            {
                root = new JsonObject();
            }
            else if (JsonNode.Parse(existingJson) is JsonObject obj)
            {
                root = obj;
            }
            else
            {
                status = "invalid-existing";
                return false;
            }

            var command = BuildCommand(scriptPath, source);
            if (!merge(root, command))
            {
                status = "unchanged";
                updatedJson = existingJson ?? "";
                return true;
            }

            updatedJson = root.ToJsonString(WriteOptions);
            if (!updatedJson.EndsWith('\n'))
                updatedJson += "\n";
            status = string.IsNullOrWhiteSpace(existingJson) ? "created" : "updated";
            return true;
        }
        catch (JsonException)
        {
            status = "invalid-existing";
            return false;
        }
    }

    static bool MergeCursor(JsonObject root, string command)
    {
        if (root["version"] is null)
            root["version"] = 1;
        var hooks = GetOrCreateObject(root, "hooks");
        var stop = GetOrCreateArray(hooks, "stop");
        if (CommandAlreadyExact(stop, command))
            return false;
        if (UpdateCommandObject(stop, command))
            return true;
        stop.Add(NewCommandObject(command));
        return true;
    }

    static bool MergeCodex(JsonObject root, string command)
    {
        var hooks = GetOrCreateObject(root, "hooks");
        var stop = GetOrCreateArray(hooks, "Stop");
        foreach (var item in stop)
        {
            if (item is not JsonObject group || group["hooks"] is not JsonArray inner)
                continue;
            if (CommandAlreadyExact(inner, command))
                return false;
            if (UpdateCommandObject(inner, command))
                return true;
        }

        stop.Add(new JsonObject
        {
            ["hooks"] = new JsonArray { NewCommandObject(command, includeType: true) }
        });
        return true;
    }

    static bool MergeGemini(JsonObject root, string command)
    {
        foreach (var property in root.ToList())
        {
            if (property.Value is not JsonObject definition || definition["Stop"] is not JsonArray stop)
                continue;
            if (CommandAlreadyExact(stop, command) || NestedCommandAlreadyExact(stop, command))
                return false;
            if (UpdateCommandObject(stop, command) || UpdateNestedCommand(stop, command))
                return true;
        }

        var hook = root[GeminiHookName] as JsonObject ?? new JsonObject();
        root[GeminiHookName] = hook;
        var handlers = GetOrCreateArray(hook, "Stop");
        if (CommandAlreadyExact(handlers, command))
            return false;
        if (UpdateCommandObject(handlers, command))
            return true;
        handlers.Add(NewCommandObject(command, includeType: true));
        return true;
    }

    static bool UpdateNestedCommand(JsonArray stop, string command)
    {
        foreach (var item in stop)
        {
            if (item is JsonObject group && group["hooks"] is JsonArray inner && UpdateCommandObject(inner, command))
                return true;
        }
        return false;
    }

    static bool NestedCommandAlreadyExact(JsonArray stop, string command)
    {
        foreach (var item in stop)
        {
            if (item is JsonObject group && group["hooks"] is JsonArray inner && CommandAlreadyExact(inner, command))
                return true;
        }
        return false;
    }

    static bool UpdateCommandObject(JsonArray list, string command)
    {
        foreach (var item in list)
        {
            if (item is not JsonObject obj || !IsOurCommand(ReadCommand(obj)))
                continue;
            obj["command"] = command;
            obj["timeout"] = HookTimeoutSeconds;
            return true;
        }
        return false;
    }

    static bool CommandAlreadyExact(JsonArray list, string command)
    {
        foreach (var item in list)
        {
            if (item is not JsonObject obj || !IsOurCommand(ReadCommand(obj)))
                continue;
            var timeout = obj["timeout"] is JsonValue value && value.TryGetValue<int>(out var seconds)
                ? seconds
                : (int?)null;
            return ReadCommand(obj) == command && timeout == HookTimeoutSeconds;
        }
        return false;
    }

    static JsonObject NewCommandObject(string command, bool includeType = false)
    {
        var obj = new JsonObject
        {
            ["command"] = command,
            ["timeout"] = HookTimeoutSeconds
        };
        if (includeType)
            obj["type"] = "command";
        return obj;
    }

    static string? ReadCommand(JsonObject obj) =>
        obj["command"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static JsonObject GetOrCreateObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    static JsonArray GetOrCreateArray(JsonObject parent, string name)
    {
        if (parent[name] is JsonArray existing)
            return existing;
        var created = new JsonArray();
        parent[name] = created;
        return created;
    }
}
