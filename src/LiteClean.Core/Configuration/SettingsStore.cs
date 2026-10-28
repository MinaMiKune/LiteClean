using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteClean.Core.Configuration;

public sealed class AppSettings
{
    public const int MinProtectionHours = 1;
    public const int MaxProtectionHours = 720;

    [JsonPropertyName("recentFileProtectionHours")]
    public int RecentFileProtectionHours { get; init; } = 24;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    public static AppSettings Default => new();

    public AppSettings Normalize() => new()
    {
        RecentFileProtectionHours = Math.Clamp(
            RecentFileProtectionHours,
            MinProtectionHours,
            MaxProtectionHours),
        SchemaVersion = 1,
    };

    public bool IsNormalized() =>
        RecentFileProtectionHours is >= MinProtectionHours and <= MaxProtectionHours &&
        SchemaVersion == 1;
}

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);

public sealed record SettingsSaveResult(bool Success, string? Error);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public SettingsStore(string settingsFilePath)
    {
        if (string.IsNullOrWhiteSpace(settingsFilePath))
        {
            throw new ArgumentException("Settings path must not be empty.", nameof(settingsFilePath));
        }

        SettingsFilePath = settingsFilePath;
    }

    public string SettingsFilePath { get; }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return new SettingsLoadResult(AppSettings.Default, null);
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null)
            {
                return new SettingsLoadResult(AppSettings.Default, "设置文件为空，已使用默认设置。");
            }

            var normalized = loaded.Normalize();
            var warning = loaded.IsNormalized()
                ? null
                : "设置值超出允许范围，已自动修正。";
            return new SettingsLoadResult(normalized, warning);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException or ArgumentException)
        {
            return new SettingsLoadResult(AppSettings.Default, $"读取设置失败，已使用默认设置：{ex.Message}");
        }
    }

    public SettingsSaveResult Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        string? tempPath = null;
        try
        {
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            tempPath = SettingsFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsFilePath, overwrite: true);
            tempPath = null;
            return new SettingsSaveResult(true, null);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException)
        {
            return new SettingsSaveResult(false, $"保存设置失败：{ex.Message}");
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup of the temp file; the save error is already reported.
                }
            }
        }
    }
}

public sealed class AppPaths
{
    private AppPaths(string baseDirectory)
    {
        BaseDirectory = baseDirectory;
        LogDirectory = Path.Combine(baseDirectory, "logs");
        SettingsFilePath = Path.Combine(baseDirectory, "settings.json");
    }

    public string BaseDirectory { get; }
    public string LogDirectory { get; }
    public string SettingsFilePath { get; }

    public static bool TryCreateDefault(out AppPaths? paths, out string? error)
    {
        paths = null;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            error = "无法确定本地应用数据目录，设置和日志功能将不可用。";
            return false;
        }

        try
        {
            var baseDirectory = Path.Combine(localAppData, "LiteClean");
            baseDirectory = Path.GetFullPath(baseDirectory);
            paths = new AppPaths(baseDirectory);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"应用数据路径无效：{ex.Message}";
            return false;
        }
    }

    public static bool TryCreateCustom(string baseDirectory, out AppPaths? paths, out string? error)
    {
        paths = null;
        error = null;
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Path.IsPathFullyQualified(baseDirectory))
        {
            error = "应用数据目录必须是绝对路径。";
            return false;
        }

        try
        {
            var full = Path.GetFullPath(baseDirectory);
            paths = new AppPaths(full);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"应用数据路径无效：{ex.Message}";
            return false;
        }
    }
}
