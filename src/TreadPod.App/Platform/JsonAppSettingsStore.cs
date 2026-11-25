using System.Globalization;
using System.Text.Json;
using TreadPod.App.Localization;

namespace TreadPod.App.Platform;

public interface IAppSettingsStore
{
    AppLanguage? LoadLanguage();
    void SaveLanguage(AppLanguage language);
}

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private sealed class AppSettingsModel
    {
        public string? Language { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public JsonAppSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TreadPod",
            "settings.json");
    }

    public static AppLanguage DetectDefaultLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("uk", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Uk
            : AppLanguage.En;

    public AppLanguage? LoadLanguage()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var json = File.ReadAllText(_filePath);
            var model = JsonSerializer.Deserialize<AppSettingsModel>(json, JsonOptions);
            return model?.Language?.ToLowerInvariant() switch
            {
                "uk" => AppLanguage.Uk,
                "en" => AppLanguage.En,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public void SaveLanguage(AppLanguage language)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var model = new AppSettingsModel
            {
                Language = language == AppLanguage.Uk ? "uk" : "en"
            };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(model, JsonOptions));
        }
        catch
        {
            // Ignore transient settings write failures.
        }
    }
}
