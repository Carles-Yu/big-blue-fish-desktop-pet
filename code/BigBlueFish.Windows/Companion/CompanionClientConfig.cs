using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BigBlueFish.Windows;

// Cursor and Codex both launch this stdio bridge. There is no HTTP address.
internal static class CompanionClientConfig
{
    internal static string BridgePath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "BigBlueFish.Mcp.exe" : "BigBlueFish.Mcp");
    internal static bool BridgeExists => File.Exists(BridgePath);
    internal static string CursorPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "mcp.json");
    internal static string CodexPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

    internal static string CursorJson => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            ["big-blue-fish"] = new { command = BridgePath, args = Array.Empty<string>() }
        }
    }, new JsonSerializerOptions { WriteIndented = true });

    internal static string CodexToml => "[mcp_servers.big-blue-fish]\nargs = []\ncommand = '" + BridgePath.Replace("'", "") + "'\nstartup_timeout_sec = 15\n";

    internal static void WriteCursor()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CursorPath)!);
        JsonNode root;
        if (File.Exists(CursorPath))
        {
            try { root = JsonNode.Parse(File.ReadAllText(CursorPath)) ?? new JsonObject(); }
            catch (JsonException) { throw new IOException("Cursor 的 mcp.json 无法读取，没有改动。"); }
        }
        else root = new JsonObject();
        if (root is not JsonObject document)
            throw new IOException("Cursor 的 mcp.json 格式无法合并，没有改动。");
        if (document["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            document["mcpServers"] = servers;
        }
        servers["big-blue-fish"] = new JsonObject
        {
            ["command"] = BridgePath,
            ["args"] = new JsonArray()
        };
        File.WriteAllText(CursorPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void WriteCodex()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CodexPath)!);
        string text = File.Exists(CodexPath) ? File.ReadAllText(CodexPath) : "";
        string block = CodexToml.TrimEnd() + "\n";
        if (Regex.IsMatch(text, @"(?m)^\[mcp_servers\.big-blue-fish\][ \t]*$"))
            text = Regex.Replace(text, @"(?ms)^\[mcp_servers\.big-blue-fish\][ \t]*\r?\n.*?(?=^\[|\z)", block);
        else
            text = text.TrimEnd() + "\n\n" + block;
        File.WriteAllText(CodexPath, text.TrimEnd() + "\n");
    }
}
