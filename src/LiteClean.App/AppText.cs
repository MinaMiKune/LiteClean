using System.Globalization;
using LiteClean.Core.Models;

namespace LiteClean.App;

internal static class AppInfo
{
    public const string Name = "LiteClean";
    public const string Version = "1.0.0";
    public const string Title = "LiteClean - 临时文件清理";
}

internal static class AppText
{
    public static string Reason(SkipReason reason) => reason switch
    {
        SkipReason.Recent => "近期修改，受保护",
        SkipReason.ReparsePoint => "重解析点/链接",
        SkipReason.InUse => "被占用",
        SkipReason.AccessDenied => "权限不足",
        SkipReason.ReadOnly => "只读文件",
        SkipReason.StateChanged => "扫描后状态变化",
        SkipReason.OutsideBoundary => "超出安全边界",
        SkipReason.InvalidPath => "路径无效",
        SkipReason.Unreadable => "无法读取",
        SkipReason.NotFound => "已不存在",
        SkipReason.DirectoryNotEmpty => "目录非空",
        SkipReason.IoError => "I/O 错误",
        SkipReason.Unknown => "无法确认安全性",
        _ => reason.ToString(),
    };

    public static string Kind(ItemKind kind) => kind == ItemKind.File ? "文件" : "目录";

    public static string Status(CleanItemStatus status) => status switch
    {
        CleanItemStatus.Deleted => "已删除",
        CleanItemStatus.Skipped => "跳过",
        CleanItemStatus.Failed => "失败",
        _ => status.ToString(),
    };

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} B"
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.##} {units[unit]}");
    }

    public static string Count(int count) => count.ToString("N0", CultureInfo.CurrentCulture);

    public static string Duration(TimeSpan duration) =>
        duration.TotalSeconds < 1
            ? $"{duration.TotalMilliseconds:0} 毫秒"
            : duration.TotalMinutes < 1
                ? $"{duration.TotalSeconds:0.0} 秒"
                : $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒";

    public static string JoinCounts(IReadOnlyDictionary<SkipReason, int> counts)
    {
        if (counts.Count == 0)
        {
            return "无";
        }

        return string.Join(
            "；",
            counts
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key)
                .Select(pair => $"{Reason(pair.Key)} {Count(pair.Value)}"));
    }
}
