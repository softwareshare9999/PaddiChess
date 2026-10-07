using System.Diagnostics.CodeAnalysis;

namespace PaddiXiangqi.Localization;

/// <summary>
/// Entry point of the display-language layer. Chinese stays the source language of the code;
/// when a catalog is active, text is translated at the UI boundary (see <see cref="UiTextHooks"/>).
/// </summary>
public static class L10n
{
    public const string SourceLanguage = "zh";
    public const string DefaultLanguage = "vi";
    public const string EnvironmentVariable = "PADDICHESS_LANG";

    private static TranslationCatalog? _catalog;

    public static string Language { get; private set; } = SourceLanguage;
    public static bool IsActive => _catalog != null;

    /// <summary>Loads the embedded catalog for <paramref name="language"/>; the source language deactivates translation.</summary>
    public static bool Activate(string language)
    {
        language = language.Trim().ToLowerInvariant();
        var dash = language.IndexOfAny(['-', '_']);
        if (dash > 0) language = language[..dash];
        if (language is SourceLanguage or "" or "off" or "cn")
        {
            _catalog = null; Language = SourceLanguage;
            return false;
        }
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream($"PaddiXiangqi.Localization.{language}.json");
        if (stream is null) return false;
        _catalog = new TranslationCatalog(stream);
        Language = language;
        return true;
    }

    public static void Use(TranslationCatalog? catalog, string language)
    {
        _catalog = catalog;
        Language = catalog is null ? SourceLanguage : language;
    }

    /// <summary>Translates display text; returns the input unchanged when no catalog is active.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? T(string? text) => text is null || _catalog is null ? text : _catalog.Translate(text);

    /// <summary>
    /// Text painted on the board: piece glyphs stay Chinese, file numerals 一…九 become 1…9,
    /// other lettering (river, brand) is translated.
    /// </summary>
    public static string BoardText(string text)
    {
        if (_catalog is null) return text;
        if (text.Length != 1) return _catalog.Translate(text);
        var numeral = "一二三四五六七八九".IndexOf(text[0], StringComparison.Ordinal);
        return numeral >= 0 ? ((char)('1' + numeral)).ToString() : text;
    }

    /// <summary>Translates only text that is a complete catalog entry (safe for editable fields).</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? TExact(string? text) => text is null || _catalog is null ? text : _catalog.TranslateExact(text);

    /// <summary>Resolves the requested language: --lang argument, PADDICHESS_LANG, then the default.</summary>
    public static string RequestedLanguage(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].StartsWith("--lang=", StringComparison.OrdinalIgnoreCase)) return arguments[i][7..];
            if (arguments[i].Equals("--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Count) return arguments[i + 1];
        }
        return Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } value ? value : DefaultLanguage;
    }
}
