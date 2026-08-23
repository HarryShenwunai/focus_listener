using System.Globalization;

namespace FocusListener;

public enum AppLanguage
{
    System,
    ZhHans,
    English
}

public static class ProductText
{
    private static AppLanguage _language = AppLanguage.ZhHans;

    public static AppLanguage Language => _language;

    public static void Use(AppLanguage language) => _language = Resolve(language);

    public static string Choose(string simplifiedChinese, string english) =>
        _language == AppLanguage.English ? english : simplifiedChinese;

    public static AppLanguage Resolve(AppLanguage language)
    {
        if (language != AppLanguage.System)
        {
            return language;
        }

        return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.ZhHans
            : AppLanguage.English;
    }
}

public static class SessionReminderOptions
{
    private static readonly int[] SupportedMinutes = [15, 30, 45, 60];

    public static IReadOnlyList<int> Minutes => SupportedMinutes;

    public static bool IsValid(int? minutes) =>
        minutes is null || SupportedMinutes.Contains(minutes.Value);
}
