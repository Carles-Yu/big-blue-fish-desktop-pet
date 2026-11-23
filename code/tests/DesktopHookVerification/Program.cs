using System.Text.Json.Nodes;
using BigBlueFish.DesktopHooks;

int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}

bool Parse(string source, string json, out DesktopHookReminder? reminder, out string reason, IReadOnlyDictionary<string, string?>? env = null)
    => DesktopHookParser.TryParse(source, json, env, out reminder, out reason);

Check(!Parse("cursor", "{", out var badJson, out var badJsonReason) && badJson is null && badJsonReason == "invalid-json", "Malformed JSON is discarded");
Check(!Parse("unknown", """{"status":"completed"}""", out _, out var unknownReason) && unknownReason == "unknown-source", "Unknown source is discarded");

var geminiDesktop = """
{
  "executionNum": 1,
  "terminationReason": "model_stop",
  "error": "",
  "fullyIdle": true,
  "conversationId": "ec33ebf9-0cba-4100-8142-c61503f6c587",
  "workspacePaths": ["F:/github/big-blue-fish-desktop-pet"],
  "transcriptPath": "C:/Users/Administrator/.gemini/antigravity/brain/ec33ebf9-0cba-4100-8142-c61503f6c587/.system_generated/logs/transcript.jsonl",
  "artifactDirectoryPath": "C:/Users/Administrator/.gemini/antigravity/brain/ec33ebf9-0cba-4100-8142-c61503f6c587",
  "modelName": "gemini-3.6-flash-medium",
  "lastUserInput": "secret prompt"
}
""";
Check(Parse("gemini", geminiDesktop, out var geminiOk, out var geminiReason) && geminiReason == "", "Gemini desktop idle payload is accepted");
Check(geminiOk is { Source: DesktopHookSource.Gemini, Outcome: DesktopHookOutcome.Completed, ProjectName: "big-blue-fish-desktop-pet" }, "Gemini desktop reminder uses the workspace name");
Check(geminiOk!.Title == "Gemini · big-blue-fish-desktop-pet 已完成", "Gemini completed title includes the project");
Check(geminiOk.Message == "Gemini 在图形界面里做完了这一轮。使用的是 gemini-3.6-flash-medium。", "Gemini completed message names the tool and model, not the transcript");
Check(geminiOk.AnimationId == "工作状态-雀跃庆祝" && geminiOk.ModelName == "gemini-3.6-flash-medium", "Gemini completed reminder plays the celebration clip and keeps a sanitized model name");
Check(!geminiOk.Title.Contains("secret", StringComparison.OrdinalIgnoreCase) && !geminiOk.Message.Contains("secret", StringComparison.OrdinalIgnoreCase), "Gemini reminder does not include user input");
Check(!geminiOk.Title.Contains("Administrator") && !geminiOk.Message.Contains("transcript", StringComparison.OrdinalIgnoreCase), "Gemini reminder does not include transcript paths");

var geminiIde = geminiDesktop.Replace("/.gemini/antigravity/", "/.gemini/antigravity-ide/");
Check(Parse("gemini", geminiIde, out var geminiIdeOk, out _) && geminiIdeOk is { Outcome: DesktopHookOutcome.Completed }, "Gemini IDE path is treated as desktop");

var geminiCli = geminiDesktop.Replace("/.gemini/antigravity/", "/.gemini/antigravity-cli/");
Check(!Parse("gemini", geminiCli, out var geminiCliReminder, out var geminiCliReason) && geminiCliReminder is null && geminiCliReason == "cli", "Gemini CLI path is discarded");

var geminiBusy = geminiDesktop.Replace("\"fullyIdle\": true", "\"fullyIdle\": false");
Check(!Parse("gemini", geminiBusy, out _, out var busyReason) && busyReason == "not-idle", "Gemini events that are not fully idle are discarded");
Check(!Parse("gemini", """{"terminationReason":"model_stop"}""", out _, out var missingIdle) && missingIdle == "not-idle", "Gemini payload without fullyIdle is discarded");

var geminiError = geminiDesktop.Replace("\"terminationReason\": \"model_stop\"", "\"terminationReason\": \"error\"");
Check(Parse("gemini", geminiError, out var geminiFailed, out _) && geminiFailed is { Outcome: DesktopHookOutcome.Failed, Title: "Gemini · big-blue-fish-desktop-pet 未能完成" }, "Gemini desktop error is a failure reminder");
Check(geminiFailed!.AnimationId == "工作状态-垂头叹气冒汗" && geminiFailed.Message == "Gemini 在图形界面里没能做完这一轮。使用的是 gemini-3.6-flash-medium。", "Gemini failure reminder plays the sigh clip by Id and is not worded as 已完成");

var lookalike = geminiDesktop.Replace(
    "C:/Users/Administrator/.gemini/antigravity/brain/ec33ebf9-0cba-4100-8142-c61503f6c587/.system_generated/logs/transcript.jsonl",
    "C:/notes/my-antigravity-cli-notes/transcript.jsonl").Replace(
    "C:/Users/Administrator/.gemini/antigravity/brain/ec33ebf9-0cba-4100-8142-c61503f6c587",
    "C:/notes/my-antigravity-cli-notes");
Check(!Parse("gemini", lookalike, out _, out var lookalikeReason) && lookalikeReason == "unconfirmed-desktop", "A path segment that only contains antigravity-cli as a substring is not treated as CLI or desktop");
Check(!DesktopHookParser.HasPathSegment(@"C:\notes\my-antigravity-cli-notes\transcript.jsonl", "antigravity-cli"), "Path segment matching does not use substrings");
Check(!DesktopHookParser.HasPathSegment(@"C:\notes\antigravity-clipboard\file.jsonl", "antigravity"), "antigravity-clipboard is not the antigravity segment");

var wrappedGemini = """
{"source":"gemini","payload":{"executionNum":1,"terminationReason":"max_steps_exceeded","fullyIdle":true,"workspacePaths":["/workspace/project"],"transcriptPath":"~/.gemini/antigravity/brain/x/transcript.jsonl","artifactDirectoryPath":"~/.gemini/antigravity/brain/x"},"env":{"CURSOR_USER_EMAIL":"hidden@example.com"}}
""";
Check(Parse("ignored", wrappedGemini, out var wrapped, out _) && wrapped is { Source: DesktopHookSource.Gemini, Outcome: DesktopHookOutcome.Failed, ProjectName: "project" }, "Wrapper JSON uses the inner source and payload, and max_steps_exceeded is not treated as completed");
Check(!wrapped!.Title.Contains('@') && !wrapped.Message.Contains('@'), "Forbidden email env is not copied into the reminder");

var cursorCompleted = """
{"status":"completed","conversation_id":"c1","workspace_roots":["F:/github/big-blue-fish-desktop-pet"],"user_email":"hidden@example.com","transcript_path":"C:/Users/Administrator/.cursor/projects/pet/transcript.jsonl","hook_event_name":"stop"}
""";
Check(!Parse("cursor", cursorCompleted, out var cursorReminder, out var cursorReason) && cursorReminder is null && cursorReason == "unconfirmed-desktop", "Cursor payloads without an official desktop field are discarded");
Check(!Parse("cursor", cursorCompleted.Replace("completed", "error"), out _, out var cursorErrorReason) && cursorErrorReason == "unconfirmed-desktop", "Cursor error events are still discarded when the desktop source is unconfirmed");
Check(!Parse("cursor", cursorCompleted.Replace("completed", "aborted"), out _, out var cursorAbortReason) && cursorAbortReason == "unconfirmed-desktop", "Cursor aborted events are still discarded when the desktop source is unconfirmed");
Check(!Parse("cursor", """{"status":"running","user_email":"hidden@example.com"}""", out _, out var cursorStatus) && cursorStatus == "invalid-status", "Cursor payload with an unknown status is discarded first");

var cursorEnv = new Dictionary<string, string?>
{
    ["CURSOR_PROJECT_DIR"] = @"F:\github\big-blue-fish-desktop-pet",
    ["CURSOR_USER_EMAIL"] = "hidden@example.com",
    ["CURSOR_VERSION"] = "2.0.0"
};
Check(!Parse("cursor", cursorCompleted, out _, out var cursorEnvReason, cursorEnv) && cursorEnvReason == "unconfirmed-desktop", "Cursor official environment variables are not used as a desktop discriminator");

var built = DesktopHookParser.Build(DesktopHookSource.Cursor, DesktopHookOutcome.Cancelled, "big-blue-fish-desktop-pet");
Check(built.Title == "Cursor · big-blue-fish-desktop-pet 已取消" && built.Message == "Cursor 在图形界面里取消了这一轮。", "Cancelled wording is not treated as a failure");
Check(built.AnimationId == "点击回应-害羞惊讶", "Cancelled reminder plays the shy-surprise clip");
Check(!built.Title.Contains('@') && !built.Message.Contains("hidden"), "Built reminders never include an email");

var codexStop = """
{"session_id":"s1","transcript_path":"C:/Users/Administrator/.codex/sessions/s1.jsonl","cwd":"F:/github/big-blue-fish-desktop-pet","hook_event_name":"Stop","turn_id":"t1","stop_hook_active":true,"last_assistant_message":"secret answer"}
""";
Check(!Parse("codex", codexStop, out var codexReminder, out var codexReason) && codexReminder is null && codexReason == "unconfirmed-desktop", "Codex Stop payloads without an official desktop field are discarded");

var existingCursor = """
{
  "version": 1,
  "hooks": {
    "sessionStart": [ { "command": "node F:/desktopPet/hook-bridge/cursor-hook.js sessionStart", "timeout": 3 } ],
    "stop": [ { "command": "node F:/desktopPet/hook-bridge/cursor-hook.js stop", "timeout": 3 } ]
  }
}
""";
var script = @"C:\Users\Administrator\AppData\Local\BigBlueFish\desktop-hooks\Send-DesktopHook.ps1";
Check(DesktopHookConfigInstaller.TryMergeCursor(existingCursor, script, out var cursorMerged, out var cursorMergeStatus) && cursorMergeStatus == "updated", "Cursor hooks.json gains a stop command");
var cursorNode = JsonNode.Parse(cursorMerged)!.AsObject();
var stop = cursorNode["hooks"]!["stop"]!.AsArray();
Check(stop.Count == 2, "Existing Cursor stop command is kept");
Check(stop[0]!["command"]!.GetValue<string>() == "node F:/desktopPet/hook-bridge/cursor-hook.js stop", "The desktopPet Cursor stop entry is not rewritten");
Check(DesktopHookConfigInstaller.IsOurCommand(stop[1]!["command"]!.GetValue<string>()) && stop[1]!["command"]!.GetValue<string>()!.Contains("-Source cursor"), "The new Cursor stop command points at the fish script");
Check(DesktopHookConfigInstaller.TryMergeCursor(cursorMerged, script, out var cursorAgain, out var cursorAgainStatus) && cursorAgainStatus == "unchanged" && cursorAgain == cursorMerged, "A second Cursor merge does not duplicate or rewrite the file");
Check(!cursorMerged.Contains("27182", StringComparison.Ordinal), "Cursor merge does not mention the old hook-bridge port");

var broken = "{ not json";
Check(!DesktopHookConfigInstaller.TryMergeCursor(broken, script, out var brokenOut, out var brokenStatus) && brokenStatus == "invalid-existing" && brokenOut == broken, "Invalid Cursor hooks.json is left untouched");

Check(DesktopHookConfigInstaller.TryMergeCodex(null, script, out var codexCreated, out var codexCreatedStatus) && codexCreatedStatus == "created", "Missing Codex hooks.json is created");
Check(codexCreated.Contains("-Source codex") && !codexCreated.Contains("notify", StringComparison.OrdinalIgnoreCase), "Codex hooks.json only installs Stop and does not mention notify");
var existingCodex = """
{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"other.exe","timeout":10}]}],"UserPromptSubmit":[{"hooks":[{"type":"command","command":"keep.exe"}]}]}}
""";
Check(DesktopHookConfigInstaller.TryMergeCodex(existingCodex, script, out var codexMerged, out _) , "Existing Codex hooks.json can be merged");
var codexNode = JsonNode.Parse(codexMerged)!.AsObject();
Check(codexNode["hooks"]!["Stop"]!.AsArray().Count == 2, "Existing Codex Stop handlers are kept");
Check(codexNode["hooks"]!["UserPromptSubmit"]!.AsArray()[0]!["hooks"]!.AsArray()[0]!["command"]!.GetValue<string>() == "keep.exe", "Unrelated Codex hooks are preserved");
Check(DesktopHookConfigInstaller.TryMergeCodex(codexMerged, script, out var codexAgain, out var codexAgainStatus) && codexAgainStatus == "unchanged", "A second Codex merge does not duplicate Stop");

Check(DesktopHookConfigInstaller.TryMergeGemini(null, script, out var geminiCreated, out var geminiCreatedStatus) && geminiCreatedStatus == "created", "Missing Gemini hooks.json is created");
Check(geminiCreated.Contains("big-blue-fish-desktop-reminder") && geminiCreated.Contains("-Source gemini"), "Gemini hook uses a stable definition name");
var existingGemini = """
{"safety-gate":{"enabled":false,"PreToolUse":[{"matcher":"run_command","hooks":[{"command":"./scripts/safety-check.sh"}]}]}}
""";
Check(DesktopHookConfigInstaller.TryMergeGemini(existingGemini, script, out var geminiMerged, out _), "Existing Gemini hooks.json can be merged");
var geminiNode = JsonNode.Parse(geminiMerged)!.AsObject();
Check(geminiNode["safety-gate"]!["enabled"]!.GetValue<bool>() == false, "Unrelated Gemini hook definitions are preserved");
Check(DesktopHookConfigInstaller.TryMergeGemini(geminiMerged, script, out _, out var geminiAgainStatus) && geminiAgainStatus == "unchanged", "A second Gemini merge does not duplicate the Stop handler");

var command = DesktopHookConfigInstaller.BuildCommand(script, "cursor");
Check(command.Contains(script, StringComparison.OrdinalIgnoreCase) && command.Contains("-Source cursor"), "Hook command invokes the installed script");

var geminiNoTool = geminiDesktop.Replace("\"terminationReason\": \"model_stop\"", "\"terminationReason\": \"NO_TOOL_CALL\"");
Check(Parse("gemini", geminiNoTool, out var noTool, out _) && noTool is { Outcome: DesktopHookOutcome.Completed }, "Gemini NO_TOOL_CALL is a normal desktop completion");
var geminiCanceled = geminiDesktop.Replace("\"terminationReason\": \"model_stop\"", "\"terminationReason\": \"USER_CANCELED\"");
Check(Parse("gemini", geminiCanceled, out var canceled, out _) && canceled is { Outcome: DesktopHookOutcome.Cancelled } && canceled.Title.EndsWith("已取消"), "Gemini USER_CANCELED is a cancellation, not a completion");
var geminiMax = geminiDesktop.Replace("\"terminationReason\": \"model_stop\"", "\"terminationReason\": \"MAX_INVOCATIONS\"");
Check(Parse("gemini", geminiMax, out var maxInv, out _) && maxInv is { Outcome: DesktopHookOutcome.Failed }, "Gemini MAX_INVOCATIONS is not treated as completed");
Check(DesktopHookParser.GeminiOutcome("max_steps_exceeded") == DesktopHookOutcome.Failed, "Public max_steps_exceeded alias is a failure");
Check(DesktopHookParser.GeminiOutcome("EXECUTOR_TERMINATION_REASON_NO_TOOL_CALL") == DesktopHookOutcome.Completed, "Prefixed official reason names are normalized");
Check(canceled!.AnimationId == "点击回应-害羞惊讶" && !canceled.Title.Contains("已完成"), "Gemini cancellation plays the shy-surprise clip and is not worded as 已完成");
Check(DesktopHookParser.CursorOutcome("completed") == DesktopHookOutcome.Completed, "Cursor completed maps to completed wording");
Check(DesktopHookParser.CursorOutcome("aborted") == DesktopHookOutcome.Cancelled, "Cursor aborted maps to cancelled wording");
Check(DesktopHookParser.CursorOutcome("error") == DesktopHookOutcome.Failed, "Cursor error maps to failed wording");
Check(DesktopHookParser.CodexOutcome("Stop") == DesktopHookOutcome.Ended && DesktopHookParser.ResultPhrase(DesktopHookOutcome.Ended) == "结束了", "Codex Stop says 结束了, not 已完成");
Check(DesktopHookParser.CodexOutcome("Interrupt") == DesktopHookOutcome.Interrupted && DesktopHookParser.ResultPhrase(DesktopHookOutcome.Interrupted) == "被停下了", "Codex Interrupt says 被停下了");
Check(DesktopHookParser.CodexOutcome("unknown") == DesktopHookOutcome.Ended, "Unknown Codex hook names do not fall through to 已完成");
Check(DesktopHookParser.AnimationIdFor(DesktopHookOutcome.Failed) == "工作状态-垂头叹气冒汗", "Failure animation is the disabled sigh clip, selected by Id");
Check(DesktopHookParser.AnimationIdFor(DesktopHookOutcome.Ended) == "工作状态-清点归档", "Ended animation is the archive clip");
Check(DesktopHookParser.AnimationIdFor(DesktopHookOutcome.Interrupted) == "点击回应-傲娇生气", "Interrupted animation is the tsundere clip");
Check(DesktopHookParser.SanitizeModelName("ok-model") == "ok-model", "Plain model names are kept");
Check(DesktopHookParser.SanitizeModelName("a@b") is null && DesktopHookParser.SanitizeModelName("a/b") is null && DesktopHookParser.SanitizeModelName(@"C:\x") is null && DesktopHookParser.SanitizeModelName("transcript.json") is null, "Model names that look like emails, paths, or transcripts are dropped");
Check(DesktopHookParser.SanitizeModelName(new string('m', 41))!.Length == 40, "Model names longer than 40 characters are truncated");
var ended = DesktopHookParser.Build(DesktopHookSource.Codex, DesktopHookOutcome.Ended, "proj");
Check(ended.Title == "Codex · proj 结束了" && ended.Message == "Codex 在图形界面里这一轮结束了。" && !ended.Title.Contains("已完成") && !ended.Message.Contains("已完成"), "Codex ended wording is not 已完成");
var interrupted = DesktopHookParser.Build(DesktopHookSource.Codex, DesktopHookOutcome.Interrupted, null);
Check(interrupted.Title == "Codex 被停下了" && interrupted.Message == "Codex 在图形界面里这一轮被停下了。" && interrupted.AnimationId == "点击回应-傲娇生气", "Interrupted title works without a project name");

var delivered = new System.Collections.Concurrent.ConcurrentBag<DesktopHookReminder>();
using (var server = DesktopHookServer.Start(delivered.Add))
{
    Check(Equals(server.BoundAddress, System.Net.IPAddress.Loopback) && server.Url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal), "Receive socket is bound only to 127.0.0.1");
    using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };

    System.Net.Http.HttpResponseMessage Post(string url, string? token, string json)
    {
        var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url);
        if (token != null)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return client.Send(request);
    }

    string Wrap(string source, string payloadJson) =>
        string.Concat("{\"source\":\"", source, "\",\"payload\":", payloadJson, "}");

    var ok = Post(server.Url, server.Token, Wrap("gemini", geminiDesktop));
    Check((int)ok.StatusCode == 204 && delivered.Count == 1 && delivered.Single().Source == DesktopHookSource.Gemini, "Loopback server accepts a confirmed Gemini desktop event");
    var denied = Post(server.Url, "wrong-token", Wrap("gemini", geminiDesktop));
    Check((int)denied.StatusCode == 401 && delivered.Count == 1, "Loopback server rejects a bad bearer token");
    var skipped = Post(server.Url, server.Token, Wrap("cursor", cursorCompleted));
    Check((int)skipped.StatusCode == 204 && delivered.Count == 1, "Unconfirmed Cursor events are discarded without failing the hook");
    var cli = Post(server.Url, server.Token, Wrap("gemini", geminiCli));
    Check((int)cli.StatusCode == 204 && delivered.Count == 1, "Gemini CLI events are discarded by the receive socket");
}

Console.WriteLine($"Desktop hook verification passed, {checks} checks.");
