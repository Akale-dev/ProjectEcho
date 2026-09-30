using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace ProjectEcho.App;

/// <summary>Only UI message templates are translated. Meeting text, paths and transcript arguments remain untouched.</summary>
internal static class UiText
{
    private static readonly Dictionary<string, string> Sources = Read("zh-CN");
    private static readonly Dictionary<string, string> English = ReadEnglish();
    private static readonly Dictionary<string, string> Traditional = Read("zh-Hant");
    public static string Language { get; private set; } = "zh-CN";
    internal static IReadOnlyDictionary<string, string> EnglishMessages => English;
    public static string Key(string source) => "Loc" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];

    public static void SetLanguage(string language)
    {
        Language = language is "en" or "zh-Hant" ? language : "zh-CN";
        foreach (string source in Sources.Keys.Concat(English.Keys).Concat(Traditional.Keys).Distinct(StringComparer.Ordinal))
            Application.Current.Resources[Key(source)] = T(source);
    }

    public static string T(string source) => Language switch
    {
        "en" => English.GetValueOrDefault(source, source),
        "zh-Hant" => Traditional.GetValueOrDefault(source, source),
        _ => source
    };

    public static string F(FormattableString source) => string.Format(CultureInfo.CurrentCulture, T(source.Format), source.GetArguments());

    private static Dictionary<string, string> ReadEnglish()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("ProjectEcho.Localization.en", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            foreach (var item in JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!) values[item.Key] = item.Value;
        }
        return values;
    }

    private static Dictionary<string, string> Read(string language)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProjectEcho.Localization." + language + ".json");
        return stream is null ? new(StringComparer.Ordinal) : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new(StringComparer.Ordinal);
    }
}
