namespace BigBlueFish.Memory;

public static class MemoryPaths
{
    public static string DirectoryForUser()
    {
        var overridePath = Environment.GetEnvironmentVariable("BIGBLUEFISH_MEMORY");
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigBlueFish", "memory");
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(data))
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "bigbluefish", "memory");
    }
}
