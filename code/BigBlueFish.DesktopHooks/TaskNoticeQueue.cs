using System;
using System.Collections.Generic;
using System.Linq;

namespace BigBlueFish.DesktopHooks;

public sealed record TaskNoticeCard(
    string Id,
    string Tool,
    string? ProjectName,
    string Status,
    DateTimeOffset CreatedAt);

public sealed class TaskNoticeQueue
{
    public const int VisibleLimit = 8;

    private readonly List<TaskNoticeCard> items = new();

    public IReadOnlyList<TaskNoticeCard> Items => items;

    public IReadOnlyList<TaskNoticeCard> Visible =>
        items.Count <= VisibleLimit ? items : items.Take(VisibleLimit).ToList();

    public TaskNoticeCard Enqueue(string tool, string? projectName, string status, DateTimeOffset? now = null)
    {
        var project = string.IsNullOrWhiteSpace(projectName) ? null : projectName.Trim();
        var card = new TaskNoticeCard(
            Guid.NewGuid().ToString("N"),
            tool,
            project,
            status,
            now ?? DateTimeOffset.UtcNow);
        items.Insert(0, card);
        return card;
    }

    public bool Dismiss(string id) => items.RemoveAll(card => card.Id == id) > 0;

    public static bool ShouldShowOverlay(bool otherAppFullscreen, bool showOnFullscreen) =>
        !otherAppFullscreen || showOnFullscreen;
}
