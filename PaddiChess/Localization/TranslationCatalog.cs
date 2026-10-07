using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaddiXiangqi.Localization;

/// <summary>
/// Translates finished Chinese UI strings. Upstream code keeps producing Chinese; the catalog maps
/// source text to the target language, so upstream changes merge without touching call sites.
/// Lookup order: exact entry, template ("{0}" holes), per line, per segment, move notation, then a
/// longest-phrase pass over every known entry for concatenated text.
/// </summary>
public sealed partial class TranslationCatalog
{
    private const int MaxDepth = 6;
    private const int CacheLimit = 8192;
    private static readonly string[] SegmentSeparators = ["\n", " · ", "；", "。"];

    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly List<Template> _templates = [];
    private readonly Dictionary<char, (string Source, string Target)[]> _phrases = [];
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly XiangqiNotation? _notation;

    public TranslationCatalog(Stream json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        var phrases = new Dictionary<string, string>(StringComparer.Ordinal);
        // Glossary: fragments that upstream only assembles at runtime, so the extractor never sees them alone.
        if (root.TryGetProperty("glossary", out var glossary))
            foreach (var (source, target) in Pairs(glossary))
            {
                if (Placeholder().IsMatch(source)) AddTemplate(source, target);
                else phrases[source] = target;
            }
        if (root.TryGetProperty("strings", out var strings))
            foreach (var (source, target) in Pairs(strings))
            {
                if (Placeholder().IsMatch(source))
                {
                    if (!AddTemplate(source, target)) _exact[source] = target;
                    var trimmedSource = source.Trim();
                    if (trimmedSource.Length != source.Length) AddTemplate(trimmedSource, target.Trim());
                    continue;
                }
                _exact[source] = target;
                var trimmed = source.Trim();
                if (trimmed.Length >= 2) phrases[trimmed] = target.Trim();
                // Sentences also occur inside longer text, split at their final punctuation.
                var bare = trimmed.TrimEnd('。', '！', '？', '；', '，', '：');
                if (bare.Length >= 2 && bare.Length != trimmed.Length)
                    phrases.TryAdd(bare, target.Trim().TrimEnd('.', '!', '?', ';', ',', ':'));
            }
        if (root.TryGetProperty("notation", out var notation)) _notation = XiangqiNotation.Create(notation);
        if (_notation != null)
            foreach (var (source, target) in _notation.ColoredPieceNames()) phrases.TryAdd(source, target);

        // Most specific templates first: more literal text means fewer false matches.
        _templates.Sort((a, b) => b.LiteralLength.CompareTo(a.LiteralLength));
        foreach (var group in phrases.Where(p => p.Key.Length > 0).GroupBy(p => p.Key[0]))
            _phrases[group.Key] = group.OrderByDescending(p => p.Key.Length).Select(p => (p.Key, p.Value)).ToArray();
    }

    public int Count => _exact.Count + _templates.Count;

    private bool AddTemplate(string source, string target)
    {
        if (Template.TryCreate(source, target) is not { } template) return false;
        _templates.Add(template);
        return true;
    }

    public string Translate(string text)
    {
        if (!ContainsCjk(text)) return text;
        if (_cache.TryGetValue(text, out var cached)) return cached;
        var result = TranslateCore(text, 0);
        if (_cache.Count > CacheLimit) _cache.Clear();
        _cache[text] = result;
        return result;
    }

    /// <summary>Only whole known strings; used where text is user-editable.</summary>
    public string TranslateExact(string text) =>
        ContainsCjk(text) && _exact.TryGetValue(text, out var target) ? target : text;

    public static bool ContainsCjk(string? text)
    {
        if (text is null) return false;
        foreach (var c in text) if (IsCjk(c)) return true;
        return false;
    }

    private static bool IsCjk(char c) => c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';

    private string TranslateCore(string text, int depth)
    {
        if (!ContainsCjk(text)) return text;
        if (_exact.TryGetValue(text, out var exact)) return exact;
        var trimmed = text.Trim();
        if (trimmed.Length != text.Length && _exact.TryGetValue(trimmed, out exact))
            return text[..text.IndexOf(trimmed, StringComparison.Ordinal)] + exact + text[(text.LastIndexOf(trimmed, StringComparison.Ordinal) + trimmed.Length)..];
        if (depth >= MaxDepth) return Phrases(text);

        foreach (var template in _templates)
            if (template.TryApply(text, part => TranslateCore(part, depth + 1)) is { } applied) return applied;

        foreach (var separator in SegmentSeparators)
        {
            if (!text.Contains(separator, StringComparison.Ordinal)) continue;
            var parts = text.Split(separator);
            var joined = string.Join(SeparatorTarget(separator), parts.Select(part => TranslateCore(part, depth + 1)));
            return separator == "。" ? joined.TrimEnd() : joined;
        }

        if (_notation?.Replace(text) is { } notated && notated != text)
            return ContainsCjk(notated) ? TranslateCore(notated, depth + 1) : notated;
        return Phrases(text);
    }

    private static string SeparatorTarget(string separator) => separator switch
    {
        "；" => "; ",
        "。" => ". ",
        _ => separator
    };

    /// <summary>Greedy longest match over all known phrases; untranslatable characters are kept.</summary>
    private string Phrases(string text)
    {
        var output = new StringBuilder(text.Length * 2);
        var i = 0;
        while (i < text.Length)
        {
            var match = Match(text, i);
            if (match is { } m)
            {
                AppendWord(output, m.Target);
                i += m.Source.Length;
                if (i < text.Length && (char.IsLetterOrDigit(text[i]) || IsCjk(text[i])) && output.Length > 0 && char.IsLetterOrDigit(output[^1]))
                    output.Append(' ');
                continue;
            }
            var c = text[i++];
            if (FullWidthPunctuation(c) is { } punctuation) AppendPunctuation(output, punctuation);
            else
            {
                if (IsCjk(c) && output.Length > 0 && char.IsLetterOrDigit(output[^1]) && !IsCjk(output[^1])) output.Append(' ');
                output.Append(c);
            }
        }
        return CollapseSpaces(output.ToString(), text);
    }

    private (string Source, string Target)? Match(string text, int index)
    {
        if (!_phrases.TryGetValue(text[index], out var candidates)) return null;
        foreach (var candidate in candidates)
            if (candidate.Source.Length <= text.Length - index && string.CompareOrdinal(text, index, candidate.Source, 0, candidate.Source.Length) == 0)
                return candidate;
        return null;
    }

    private static void AppendWord(StringBuilder output, string word)
    {
        if (word.Length == 0) return;
        if (output.Length > 0 && (char.IsLetterOrDigit(output[^1]) || output[^1] is ')' or ',' or ':' or ';') && (char.IsLetterOrDigit(word[0]) || word[0] == '('))
            output.Append(' ');
        output.Append(word);
    }

    private static void AppendPunctuation(StringBuilder output, string punctuation)
    {
        if (punctuation.StartsWith(' ') && (output.Length == 0 || output[^1] == ' ')) punctuation = punctuation.TrimStart();
        output.Append(punctuation);
    }

    private static string? FullWidthPunctuation(char c) => c switch
    {
        '，' or '、' => ", ",
        '：' => ": ",
        '；' => "; ",
        '。' => ". ",
        '！' => "! ",
        '？' => "? ",
        '（' => " (",
        '）' => ")",
        '「' or '“' or '”' or '」' => "\"",
        '《' or '》' => "\"",
        _ => null
    };

    private static string CollapseSpaces(string text, string original)
    {
        var collapsed = SpaceBeforePunctuation().Replace(MultipleSpaces().Replace(text, " "), "$1").Trim(' ');
        var trimmed = original.Trim();
        if (trimmed.Length == 0) return original;
        var start = original.IndexOf(trimmed, StringComparison.Ordinal);
        return original[..start] + collapsed + original[(start + trimmed.Length)..];
    }

    private static IEnumerable<(string, string)> Pairs(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } value)
                yield return (property.Name, value);
    }

    [GeneratedRegex(@"\{\d+\}")]
    internal static partial Regex Placeholder();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex(@" +([,.:;!?)])")]
    private static partial Regex SpaceBeforePunctuation();

    private sealed class Template
    {
        private readonly string _source;
        private readonly string _target;
        private readonly string _anchor;
        private readonly int[] _holes;
        private Regex? _regex;

        private Template(string source, string target, string anchor, int literalLength, int[] holes)
        {
            _source = source; _target = target; _anchor = anchor; LiteralLength = literalLength; _holes = holes;
        }

        public int LiteralLength { get; }

        public static Template? TryCreate(string source, string target)
        {
            var literals = Placeholder().Split(source);
            if (!literals.Any(ContainsCjk)) return null;
            var holes = Placeholder().Matches(source).Select(m => int.Parse(m.Value[1..^1], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var anchor = literals.OrderByDescending(l => l.Length).First();
            return new Template(source, target, anchor, literals.Sum(l => l.Length), holes);
        }

        public string? TryApply(string text, Func<string, string> translatePart)
        {
            if (!text.Contains(_anchor, StringComparison.Ordinal)) return null;
            Match match;
            try { match = (_regex ??= Build()).Match(text); }
            catch (RegexMatchTimeoutException) { return null; }
            if (!match.Success) return null;
            var values = new Dictionary<int, string>();
            foreach (var hole in _holes.Distinct()) values[hole] = translatePart(match.Groups["p" + hole].Value);
            return Placeholder().Replace(_target, m =>
                values.TryGetValue(int.Parse(m.Value[1..^1], System.Globalization.CultureInfo.InvariantCulture), out var v) ? v : m.Value);
        }

        private Regex Build()
        {
            var pattern = new StringBuilder("^");
            var seen = new HashSet<int>();
            var last = 0;
            foreach (Match hole in Placeholder().Matches(_source))
            {
                pattern.Append(Regex.Escape(_source[last..hole.Index]));
                var index = int.Parse(hole.Value[1..^1], System.Globalization.CultureInfo.InvariantCulture);
                pattern.Append(seen.Add(index) ? $"(?<p{index}>.*?)" : $@"\k<p{index}>");
                last = hole.Index + hole.Length;
            }
            pattern.Append(Regex.Escape(_source[last..])).Append('$');
            return new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
    }
}
