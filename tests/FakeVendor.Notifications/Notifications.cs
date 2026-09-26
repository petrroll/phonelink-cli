// Original synthetic test double; not Microsoft code.
using System.Text.Json;
using YourPhone.AppCore.WinRT.DataStore;

namespace YourPhone.Notifications.WinRT.DataStore;

public enum PinnedState { Normal, Pinned }
public sealed class Notification
{
    public long Id { get; set; }
    public string NotificationId { get; set; } = "";
    public string PackageName { get; set; } = "";
    public DateTimeOffset PostTime { get; set; }
    public string Json { get; set; } = "";
    public string AppName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public PinnedState PinnedState { get; set; }
}
public sealed class NotificationsTable(ISqliteConnection connection)
{
    public string TableName => "notifications";
    public string PrimaryKeyName => "id";
    public IReadOnlyList<object> GetEntitiesFromIds(IReadOnlyList<long> ids)
    {
        using var row = connection.CreateStatement($"SELECT * FROM notifications WHERE id IN ({string.Join(',', ids)})");
        var result = new List<object>();
        while (row.Step())
        {
            string json = row.ReadText16("json");
            using var document = JsonDocument.Parse(json);
            string Field(string name) => document.RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
            result.Add(new Notification {
                Id = row.ReadInt64("id"), NotificationId = row.ReadText16("notification_id"), PackageName = row.ReadText16("package_name"),
                PostTime = row.ReadDateTime("post_time"), Json = json, AppName = Field("appName"), Title = Field("title"),
                Description = Field("bigText") is { Length: > 0 } expanded ? expanded : Field("text")
            });
        }
        return result;
    }
}
