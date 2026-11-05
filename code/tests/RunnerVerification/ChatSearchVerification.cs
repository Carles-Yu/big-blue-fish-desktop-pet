using BigBlueFish.Windows;
using System.Net;
using System.Net.Http;
using System.Text.Json;

internal static class ChatSearchVerification
{
    sealed class Handler : HttpMessageHandler
    {
        internal int Chat, Search;
        internal bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            if (request.RequestUri!.AbsolutePath == "/v1/tools/search")
            {
                Search++;
                if (Fail) return new(HttpStatusCode.PaymentRequired);
                return Json("{\"results\":[{\"title\":\"来源\",\"url\":\"https://example.com/\",\"summary\":\"资料\"}]}");
            }
            Chat++;
            if (Chat == 1) return Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"call1\",\"type\":\"function\",\"function\":{\"name\":\"web_search\",\"arguments\":\"{\\\"query\\\":\\\"DSH\\\"}\"}}]}}]}");
            using var parsed = JsonDocument.Parse(body);
            if (!body.Contains("call1") || !parsed.RootElement.GetProperty("messages").EnumerateArray().Any(m => m.TryGetProperty("role", out var role) && role.GetString() == "tool" && m.GetProperty("content").GetString()!.Contains(Fail ? "余额不足" : "example.com"))) throw new Exception("tool result missing");
            return Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"完成\"}}]}");
        }
        static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    }
    internal static async Task<int> Run()
    {
        foreach (var fail in new[] { false, true })
        {
            var handler = new Handler { Fail = fail };
            using var http = new HttpClient(handler);
            var sources = new List<SearchSource>();
            var notices = new List<string>();
            var reply = await CompanionChatSearch.Run(http, new("https://api.deepseek.com/v1", "secret", "deepseek-flash"), "persona", "搜索 DSH", Array.Empty<CompanionBrain.ChatMessage>(), sources, notices.Add, CancellationToken.None);
            if (reply != "完成" || handler.Search != 1 || handler.Chat != 2 || sources.Count != (fail ? 0 : 1) || notices.Any(s => s.Contains("secret"))) throw new Exception("search verification failed");
            if (fail && !notices.Any(s => s.Contains("余额不足"))) throw new Exception("failure notice missing");
        }
        Console.WriteLine("PASS DeepSeek search and failure handling");
        return 0;
    }
}
