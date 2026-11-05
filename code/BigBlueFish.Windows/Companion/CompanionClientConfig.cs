#if !COMPANION_SAFE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace BigBlueFish.Windows;

internal static class CompanionClientConfig
{
    internal static string BridgePath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "BigBlueFish.Mcp.exe" : "BigBlueFish.Mcp");
    internal static bool BridgeExists => File.Exists(BridgePath);
    internal static string CursorPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "mcp.json");
    internal static string CodexPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
    internal static string CursorJson => JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["big-blue-fish"] = new { command = BridgePath, args = Array.Empty<string>() } } }, new JsonSerializerOptions { WriteIndented = true });
    internal static string CodexToml => "[mcp_servers.big-blue-fish]\ncommand = " + JsonSerializer.Serialize(BridgePath) + "\nargs = []\nstartup_timeout_sec = 15\n";
    internal sealed record Change(string Path, byte[]? Original, byte[] Updated, string Preview)
    {
        internal bool Changed => Original == null || !Original.SequenceEqual(Updated);
    }
    private static IOException Invalid() => new("配置格式无法安全合并，文件未改动。请复制配置片段手动合并。");

    internal static Change Prepare(string path, bool codex, string? bridge = null)
    {
        path = Path.GetFullPath(path); bridge ??= BridgePath;
        byte[]? original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(original ?? []); }
        catch (DecoderFallbackException) { throw Invalid(); }
        bool bom = text.StartsWith('\uFEFF'); if (bom) text = text[1..];
        string updated;
        var changes = new List<string>();
        try { updated = codex ? MergeToml(text, bridge, changes) : MergeJson(text, bridge, changes); }
        catch (Exception error) when (error is not IOException && error is not UnauthorizedAccessException) { throw Invalid(); }
        byte[] bytes = changes.Count == 0 && original != null ? original : Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + updated);
        string preview = (codex ? "Codex" : "Cursor") + "\n目标：" + path + "\n" +
            (changes.Count == 0 ? "已配置，无需改动。" : string.Join("\n", changes)) +
            "\n只修改 big-blue-fish 的启动字段；其余字段保留，内容已隐藏。\n配置保存不代表 Agent 已连接。";
        return new(path, original, bytes, preview);
    }

    static string MergeJson(string text, string bridge, List<string> changes)
    {
        var root = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? throw Invalid();
        if (root.ContainsKey("mcpServers") && root["mcpServers"] is not JsonObject) throw Invalid();
        var servers = root["mcpServers"] as JsonObject;
        if (servers == null) root["mcpServers"] = servers = new();
        if (servers.ContainsKey("big-blue-fish") && servers["big-blue-fish"] is not JsonObject) throw Invalid();
        var target = servers["big-blue-fish"] as JsonObject;
        if (target == null) servers["big-blue-fish"] = target = new();
        foreach (var field in new Dictionary<string, JsonNode?> { ["command"] = JsonValue.Create(bridge), ["args"] = new JsonArray() })
            if (!JsonNode.DeepEquals(target[field.Key], field.Value)) {
                changes.Add(field.Key + ": " + (target.ContainsKey(field.Key) ? "[原值已隐藏] → " : "新增 → ") + field.Value!.ToJsonString());
                target[field.Key] = field.Value;
            }
        string result = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        _ = JsonNode.Parse(result); return result;
    }

    static TomlTable ParseToml(string text)
    {
        var syntax = Toml.Parse(text);
        if (syntax.HasErrors) throw Invalid();
        return syntax.ToModel();
    }
    static string[] Parts(KeySyntax key)
    {
        var names = new List<string>(); object node = ParseToml(key.ToString() + " = 0");
        while (node is TomlTable table && table.Count == 1) { var pair = table.First(); names.Add(pair.Key); node = pair.Value; }
        return names.ToArray();
    }
    static string MergeToml(string text, string bridge, List<string> changes)
    {
        var syntax = Toml.Parse(text); var model = ParseToml(text);
        if (model.TryGetValue("mcp_servers", out var existing) && existing is not TomlTable) throw Invalid();
        var servers = existing as TomlTable ?? new TomlTable(); model["mcp_servers"] = servers;
        if (servers.TryGetValue("big-blue-fish", out var entry) && entry is not TomlTable) throw Invalid();
        var target = entry as TomlTable ?? new TomlTable(); servers["big-blue-fish"] = target;
        var desired = new Dictionary<string, object> { ["command"] = bridge, ["args"] = new TomlArray(), ["startup_timeout_sec"] = 15L };
        var changed = desired.Where(x => !target.TryGetValue(x.Key, out var value) || !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(value), JsonSerializer.SerializeToNode(x.Value))).ToArray();
        if (changed.Length == 0) return text;
        var table = syntax.Tables.OfType<TableSyntax>().SingleOrDefault(x => x.Name != null && Parts(x.Name).SequenceEqual(new[] { "mcp_servers", "big-blue-fish" }));
        if (entry != null && table == null) throw Invalid();
        var patches = new List<(int Start, int Length, string Text)>();
        string newline = text.Contains("\r\n") ? "\r\n" : "\n", added = "";
        foreach (var field in changed) {
            string value = field.Key == "command" ? JsonSerializer.Serialize(bridge) : field.Key == "args" ? "[]" : "15";
            changes.Add(field.Key + ": " + (target.ContainsKey(field.Key) ? "[原值已隐藏] → " : "新增 → ") + value);
            var item = table?.Items.SingleOrDefault(x => x.Key != null && Parts(x.Key).SequenceEqual(new[] { field.Key }));
            if (item?.Value != null) patches.Add((item.Value.Span.Start.Offset, item.Value.Span.End.Offset - item.Value.Span.Start.Offset + 1, value));
            else { if (target.ContainsKey(field.Key)) throw Invalid(); added += field.Key + " = " + value + newline; }
            target[field.Key] = field.Value;
        }
        if (table == null) patches.Add((text.Length, 0, (text.Length == 0 ? "" : newline) + "[mcp_servers.big-blue-fish]" + newline + added));
        else if (added.Length > 0) {
            int end = text.IndexOf('\n', table.CloseBracket!.Span.End.Offset);
            patches.Add((end < 0 ? text.Length : end + 1, 0, (end < 0 ? newline : "") + added));
        }
        foreach (var patch in patches.OrderByDescending(x => x.Start)) text = text.Remove(patch.Start, patch.Length).Insert(patch.Start, patch.Text);
        var verified = ParseToml(text);
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(model), JsonSerializer.SerializeToNode(verified))) throw Invalid();
        return text;
    }

    internal static string Save(Change change)
    {
        bool Matches() => change.Original == null ? !File.Exists(change.Path) : File.Exists(change.Path) && File.ReadAllBytes(change.Path).SequenceEqual(change.Original);
        if (!Matches()) throw new IOException("配置已被其他程序修改，请重新预览。");
        if (!change.Changed) return "已配置，无需改动。";
        Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
        string temp = change.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string? backup = null;
        void WriteProtected(string path, byte[] contents)
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = change.Original == null ? UnixFileMode.UserRead | UnixFileMode.UserWrite : File.GetUnixFileMode(change.Path);
            using var stream = new FileStream(path, options);
            if (OperatingSystem.IsWindows() && change.Original != null) new FileInfo(path).SetAccessControl(new FileInfo(change.Path).GetAccessControl(AccessControlSections.Access));
            stream.Write(contents); stream.Flush(true);
        }
        try {
            if (change.Original != null) {
                backup = change.Path + "." + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "." + Guid.NewGuid().ToString("N") + ".bak";
                WriteProtected(backup, change.Original);
            }
            WriteProtected(temp, change.Updated);
            if (!Matches()) throw new IOException("配置已被其他程序修改，请重新预览。");
            if (change.Original == null) File.Move(temp, change.Path); else File.Move(temp, change.Path, true);
            return "配置已保存：" + change.Path + (backup == null ? "" : "\n备份：" + backup) + "\n请在客户端重新加载配置；尚未确认连接。";
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
#endif
