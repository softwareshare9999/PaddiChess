#:property RestorePackagesWithLockFile=false
#:property TreatWarningsAsErrors=false
// Localization catalog maintenance for the Vietnamese UI layer (PaddiChess/Localization).
//
//   dotnet run scripts/i18n.cs -- check            List Chinese strings in the sources that have no translation.
//   dotnet run scripts/i18n.cs -- sync             Append untranslated strings to the catalog with an empty value.
//   dotnet run scripts/i18n.cs -- sync --prune     Also remove catalog entries that no longer occur in the sources.
//   dotnet run scripts/i18n.cs -- list             Print every extracted string with its first source location.
//   dotnet run scripts/i18n.cs -- import <file>    Merge translations from a JSON file: either a flat {"中文": "dịch"} object
//                                                  or an object with "strings" / "glossary" / "notation" / "ignore" sections.
//
// Options: --catalog <path> (default PaddiChess/Localization/vi.json).
// Exit code of `check` is 1 when anything is missing, so it can gate CI after an upstream merge.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

Console.OutputEncoding = new UTF8Encoding(false);
var command = args.FirstOrDefault() ?? "check";
var root = FindRepositoryRoot();
var catalogPath = Path.Combine(root, OptionValue("--catalog") ?? Path.Combine("PaddiChess", "Localization", "vi.json"));
var prune = args.Contains("--prune");

var found = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var file in SourceFiles(root))
{
    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
    var text = File.ReadAllText(file);
    IEnumerable<(string Value, int Line)> literals = Path.GetExtension(file) switch
    {
        ".cs" => CSharpLexer.Extract(text),
        ".swift" => SwiftLexer.Extract(text),
        ".axaml" => AxamlExtractor.Extract(text),
        _ => []
    };
    foreach (var (value, line) in literals)
        if (Cjk.Contains(value) && !Cjk.IsSinglePieceOrNumeral(value))
            found.TryAdd(value, $"{relative}:{line}");
}

var catalog = File.Exists(catalogPath)
    ? JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject()
    : new JsonObject { ["strings"] = new JsonObject(), ["glossary"] = new JsonObject(), ["ignore"] = new JsonArray() };
var strings = catalog["strings"]!.AsObject();
var ignore = (catalog["ignore"]?.AsArray() ?? []).Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

bool Translated(string key) => strings[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0;
var missing = found.Where(p => !ignore.Contains(p.Key) && !Translated(p.Key)).ToList();
var stale = strings.Select(p => p.Key).Where(k => !found.ContainsKey(k)).ToList();

switch (command)
{
    case "list":
        foreach (var (value, where) in found) Console.WriteLine($"{where}\t{Escape(value)}");
        break;
    case "check":
        foreach (var (value, where) in missing) Console.WriteLine($"MISSING {where}\t{Escape(value)}");
        Console.WriteLine($"{found.Count} strings in sources, {missing.Count} untranslated, {stale.Count} catalog entries unused.");
        return missing.Count == 0 ? 0 : 1;
    case "sync":
        foreach (var (value, _) in missing) if (!strings.ContainsKey(value)) strings[value] = "";
        if (prune) foreach (var key in stale) strings.Remove(key);
        SaveCatalog();
        foreach (var (value, where) in missing) Console.WriteLine($"TODO {where}\t{Escape(value)}");
        Console.WriteLine($"{missing.Count} strings need translation{(prune ? $", {stale.Count} unused entries removed" : "")}. Catalog: {Path.GetRelativePath(root, catalogPath)}");
        break;
    case "import":
    {
        var source = args.ElementAtOrDefault(1) ?? throw new ArgumentException("import requires a JSON file path");
        var input = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        var sections = input.ContainsKey("strings") || input.ContainsKey("glossary") || input.ContainsKey("notation") || input.ContainsKey("ignore");
        var imported = 0; var unknown = 0;
        foreach (var (key, value) in sections ? input["strings"]?.AsObject() ?? [] : input)
        {
            if (!found.ContainsKey(key)) { Console.WriteLine($"NOT IN SOURCES\t{Escape(key)}"); unknown++; }
            strings[key] = value?.GetValue<string>() ?? "";
            imported++;
        }
        if (input["glossary"] is JsonObject newGlossary)
        {
            var glossaryTarget = catalog["glossary"] as JsonObject ?? (JsonObject)(catalog["glossary"] = new JsonObject());
            foreach (var (key, value) in newGlossary) glossaryTarget[key] = value?.DeepClone();
        }
        if (input["notation"] is JsonObject notation) catalog["notation"] = notation.DeepClone();
        if (input["ignore"] is JsonArray newIgnore)
        {
            var ignoreTarget = catalog["ignore"] as JsonArray ?? (JsonArray)(catalog["ignore"] = new JsonArray());
            foreach (var item in newIgnore)
                if (item?.GetValue<string>() is { } text && !ignore.Contains(text))
                {
                    if (!found.ContainsKey(text)) Console.WriteLine($"IGNORED BUT NOT IN SOURCES\t{Escape(text)}");
                    ignoreTarget.Add((JsonNode)JsonValue.Create(text)); ignore.Add(text);
                    strings.Remove(text);
                }
        }
        SaveCatalog();
        Console.WriteLine($"Imported {imported} strings ({unknown} not found in sources).");
        break;
    }
    default:
        Console.Error.WriteLine("Usage: dotnet run scripts/i18n.cs -- [check|sync [--prune]|list] [--catalog <path>]");
        return 2;
}
return 0;

void SaveCatalog()
{
    Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
    File.WriteAllText(catalogPath, catalog.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    }) + "\n", new UTF8Encoding(false));
}

string? OptionValue(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string Escape(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

static string FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "PaddiChess.slnx"))) return dir.FullName;
    throw new InvalidOperationException("Run this script from inside the PaddiChess repository.");
}

static IEnumerable<string> SourceFiles(string root)
{
    var app = Path.Combine(root, "PaddiChess");
    var excluded = new[] { "bin", "obj", "Packaging", "PikafishRules", "Localization" };
    return Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".swift", StringComparison.Ordinal))
        .Where(f => !Path.GetRelativePath(app, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(excluded.Contains))
        .Order(StringComparer.Ordinal);
}

static class Cjk
{
    public static bool Contains(string value)
    {
        foreach (var c in value) if (c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff') return true;
        return false;
    }

    // Single glyphs are board lettering, OCR alphabets or notation numerals; they are never translated as phrases.
    public static bool IsSinglePieceOrNumeral(string value) => value.Trim().Length == 1;
}

/// <summary>Extracts string literals from C#, turning interpolation holes into {0}, {1}… and merging "a" + "b".</summary>
static class CSharpLexer
{
    public static List<(string, int)> Extract(string s)
    {
        var result = new List<(string, int)>();
        var i = 0;
        Scan(s, ref i, s.Length, result, stopAtBrace: false);
        return result;
    }

    // Scans code until `end` (or an unmatched '}' when stopAtBrace), collecting literals.
    static void Scan(string s, ref int i, int end, List<(string, int)> result, bool stopAtBrace)
    {
        var depth = 0;
        while (i < end)
        {
            var c = s[i];
            if (c == '/' && i + 1 < end && s[i + 1] == '/') { while (i < end && s[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < end && s[i + 1] == '*') { var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = close < 0 ? end : close + 2; continue; }
            if (c == '\'') { SkipChar(s, ref i); continue; }
            if (IsStringStart(s, i))
            {
                var line = LineOf(s, i);
                var builder = new StringBuilder();
                var holes = 0;
                ReadLiteral(s, ref i, builder, ref holes, result);
                // Merge compile-time concatenations: "a" + "b" (+ ...).
                while (true)
                {
                    var j = SkipTrivia(s, i);
                    if (j >= s.Length || s[j] != '+') break;
                    var k = SkipTrivia(s, j + 1);
                    if (k >= s.Length || !IsStringStart(s, k)) break;
                    i = k;
                    ReadLiteral(s, ref i, builder, ref holes, result);
                }
                result.Add((builder.ToString(), line));
                continue;
            }
            if (stopAtBrace)
            {
                if (c is '{' or '(' or '[') depth++;
                else if (c is ')' or ']') depth--;
                else if (c == '}') { if (depth == 0) return; depth--; }
            }
            i++;
        }
    }

    static bool IsStringStart(string s, int i)
    {
        var j = i;
        while (j < s.Length && (s[j] == '$' || s[j] == '@')) j++;
        if (j >= s.Length || s[j] != '"') return false;
        // Avoid identifiers such as `abc"` (not valid C#, but be defensive) and verbatim identifiers @name.
        return j == i || i == 0 || !char.IsLetterOrDigit(s[i - 1]);
    }

    static void ReadLiteral(string s, ref int i, StringBuilder text, ref int holes, List<(string, int)> nested)
    {
        var dollars = 0; var verbatim = false;
        while (s[i] is '$' or '@') { if (s[i] == '$') dollars++; else verbatim = true; i++; }
        var quotes = 0;
        while (i + quotes < s.Length && s[i + quotes] == '"') quotes++;
        if (quotes >= 3)
        {
            // Raw string literal: content runs until the same number of quotes.
            i += quotes;
            var close = s.IndexOf(new string('"', quotes), i, StringComparison.Ordinal);
            var content = close < 0 ? s[i..] : s[i..close];
            i = close < 0 ? s.Length : close + quotes;
            AppendInterpolated(content, dollars, text, ref holes, nested, braces: Math.Max(1, dollars));
            return;
        }
        if (quotes == 2 && !(i + 2 < s.Length && s[i + 2] == '"')) { i += 2; return; } // empty ""
        i++; // opening quote
        var interpolated = dollars > 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (verbatim && c == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { text.Append('"'); i += 2; continue; } i++; return; }
            if (!verbatim && c == '"') { i++; return; }
            if (!verbatim && c == '\\') { text.Append(Unescape(s, ref i)); continue; }
            if (!verbatim && c == '\n') { i++; return; } // unterminated; recover
            if (interpolated && c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { text.Append('{'); i += 2; continue; }
                i++;
                Scan(s, ref i, s.Length, nested, stopAtBrace: true); // hole expression (with format clause)
                i++; // closing brace
                text.Append('{').Append(holes++).Append('}');
                continue;
            }
            if (interpolated && c == '}' && i + 1 < s.Length && s[i + 1] == '}') { text.Append('}'); i += 2; continue; }
            text.Append(c); i++;
        }
    }

    static void AppendInterpolated(string content, int dollars, StringBuilder text, ref int holes, List<(string, int)> nested, int braces)
    {
        if (dollars == 0) { text.Append(content); return; }
        var open = new string('{', braces);
        var i = 0;
        while (i < content.Length)
        {
            if (string.CompareOrdinal(content, i, open, 0, braces) == 0 && (i + braces >= content.Length || content[i + braces] != '{'))
            {
                i += braces;
                Scan(content, ref i, content.Length, nested, stopAtBrace: true);
                i += braces;
                text.Append('{').Append(holes++).Append('}');
                continue;
            }
            text.Append(content[i++]);
        }
    }

    static string Unescape(string s, ref int i)
    {
        var e = i + 1 < s.Length ? s[i + 1] : '\\';
        i += 2;
        switch (e)
        {
            case 'n': return "\n";
            case 'r': return "\r";
            case 't': return "\t";
            case '0': return "\0";
            case 'u': { var hex = s.Substring(i, 4); i += 4; return ((char)Convert.ToInt32(hex, 16)).ToString(); }
            case 'U': { var hex = s.Substring(i, 8); i += 8; return char.ConvertFromUtf32(Convert.ToInt32(hex, 16)); }
            case 'x':
            {
                var start = i;
                while (i < s.Length && i - start < 4 && Uri.IsHexDigit(s[i])) i++;
                return ((char)Convert.ToInt32(s[start..i], 16)).ToString();
            }
            default: return e.ToString();
        }
    }

    static void SkipChar(string s, ref int i)
    {
        i++;
        while (i < s.Length && s[i] != '\'' && s[i] != '\n') i += s[i] == '\\' ? 2 : 1;
        i++;
    }

    static int SkipTrivia(string s, int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*') { var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = close < 0 ? s.Length : close + 2; continue; }
            break;
        }
        return i;
    }

    static int LineOf(string s, int index)
    {
        var line = 1;
        for (var k = 0; k < index; k++) if (s[k] == '\n') line++;
        return line;
    }
}

/// <summary>Swift string literals; \( … ) interpolations become {0}, {1}….</summary>
static class SwiftLexer
{
    public static List<(string, int)> Extract(string s)
    {
        var result = new List<(string, int)>();
        var line = 1;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\n') { line++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i + 1 < s.Length && s[i + 1] != '\n') i++; continue; }
            if (c != '"') continue;
            var text = new StringBuilder();
            var holes = 0;
            var startLine = line;
            i++;
            while (i < s.Length && s[i] != '"' && s[i] != '\n')
            {
                if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '(')
                {
                    var depth = 1; i += 2;
                    while (i < s.Length && depth > 0) { if (s[i] == '(') depth++; else if (s[i] == ')') depth--; i++; }
                    text.Append('{').Append(holes++).Append('}');
                    continue;
                }
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    text.Append(s[i + 1] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var other => other });
                    i += 2; continue;
                }
                text.Append(s[i++]);
            }
            result.Add((text.ToString(), startLine));
        }
        return result;
    }
}

/// <summary>Attribute values and text content of Avalonia XAML, excluding markup extensions.</summary>
static class AxamlExtractor
{
    public static List<(string, int)> Extract(string text)
    {
        var result = new List<(string, int)>();
        var document = XDocument.Parse(text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        foreach (var element in document.Descendants())
        {
            var line = ((System.Xml.IXmlLineInfo)element).LineNumber;
            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value;
                if (value.StartsWith("{}", StringComparison.Ordinal)) value = value[2..];
                else if (value.StartsWith('{')) continue;
                result.Add((value, ((System.Xml.IXmlLineInfo)attribute).LineNumber is var l && l > 0 ? l : line));
            }
            foreach (var node in element.Nodes().OfType<XText>())
            {
                var value = string.Join(' ', node.Value.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
                if (value.Length > 0) result.Add((value, line));
            }
        }
        return result;
    }
}
