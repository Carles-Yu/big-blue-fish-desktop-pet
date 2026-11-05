using System.Linq;
using System.Reflection;

namespace BigBlueFish.Deployment;

internal static class ReleaseIdentity
{
    private static string Get(string key) => typeof(ReleaseIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(x => x.Key == key)?.Value ?? "未知";
    internal static string Repository => Get("RepositoryUrl");
    internal static string Upstream => Get("UpstreamUrl");
    internal static string Maintainer => Get("MaintenanceAccount");
    internal static string Label => Get("MaintenanceLabel");
    internal static string LatestUrl => "https://api.github.com/repos/" + new System.Uri(Repository).AbsolutePath.Trim('/') + "/releases/latest";
    internal static string Description => $"蓝色大肥鱼 {typeof(ReleaseIdentity).Assembly.GetName().Version!.ToString(3)} · {Label}\n{Get("CompanionEdition")} · {Get("UpdateChannel")} · 提交 {Get("BuildCommit")}\n更新与反馈：{Repository}\n上游作者 Bai-Cute：{Upstream}";
}
