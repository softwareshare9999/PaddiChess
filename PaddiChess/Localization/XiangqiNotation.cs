using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaddiXiangqi.Localization;

/// <summary>
/// Rewrites Chinese move notation (炮二平五, 前車进一) into the catalog's notation, e.g. P2-5, Xt.1.
/// Symbols are data, configured by the "notation" section of the catalog.
/// </summary>
internal sealed class XiangqiNotation
{
    private readonly Dictionary<char, string> _pieces;
    private readonly Dictionary<char, string> _pieceNames;
    private readonly Dictionary<char, string> _colors;
    private readonly Dictionary<char, string> _positions;
    private readonly Dictionary<char, string> _actions;
    private readonly string _coloredPieceFormat;
    private readonly Regex _move;

    private XiangqiNotation(Dictionary<char, string> pieces, Dictionary<char, string> pieceNames, Dictionary<char, string> colors,
        Dictionary<char, string> positions, Dictionary<char, string> actions, string coloredPieceFormat)
    {
        _pieces = pieces; _pieceNames = pieceNames; _colors = colors; _positions = positions; _actions = actions;
        _coloredPieceFormat = coloredPieceFormat;
        var piece = Class(pieces.Keys);
        var number = "[一二三四五六七八九１２３４５６７８９1-9]";
        var position = Class(positions.Keys);
        var action = Class(actions.Keys);
        _move = new Regex(
            $"(?:(?<pos>{position})(?<piece>{piece})|(?<piece>{piece})(?<file>{number}))(?<act>{action})(?<to>{number})",
            RegexOptions.CultureInvariant);
    }

    public static XiangqiNotation? Create(JsonElement json)
    {
        var pieces = Map(json, "pieces");
        var actions = new Dictionary<char, string>();
        foreach (var (name, glyphs) in new[] { ("advance", "进進"), ("retreat", "退"), ("traverse", "平") })
            if (json.TryGetProperty(name, out var symbol) && symbol.GetString() is { } value)
                foreach (var glyph in glyphs) actions[glyph] = value;
        if (pieces.Count == 0 || actions.Count == 0) return null;
        var format = json.TryGetProperty("coloredPieceFormat", out var f) ? f.GetString() ?? "{color}{piece}" : "{color}{piece}";
        return new XiangqiNotation(pieces, Map(json, "pieceNames"), Map(json, "colors"), Map(json, "positions"), actions, format);
    }

    public string Replace(string text) => _move.Replace(text, match =>
    {
        var head = _pieces[match.Groups["piece"].Value[0]];
        var where = match.Groups["pos"].Success ? _positions[match.Groups["pos"].Value[0]] : Digit(match.Groups["file"].Value[0]);
        return head + where + _actions[match.Groups["act"].Value[0]] + Digit(match.Groups["to"].Value[0]);
    });

    /// <summary>Phrases such as 红車 → "Xe đỏ" for prose that names pieces.</summary>
    public IEnumerable<(string Source, string Target)> ColoredPieceNames()
    {
        foreach (var (glyph, name) in _pieceNames)
        {
            yield return (glyph.ToString(), name);
            foreach (var (color, colorName) in _colors)
                yield return ($"{color}{glyph}", _coloredPieceFormat.Replace("{piece}", name, StringComparison.Ordinal).Replace("{color}", colorName, StringComparison.Ordinal));
        }
    }

    private static string Digit(char c) => c switch
    {
        >= '1' and <= '9' => c.ToString(),
        >= '１' and <= '９' => ((char)('1' + (c - '１'))).ToString(),
        _ => ((char)('1' + "一二三四五六七八九".IndexOf(c, StringComparison.Ordinal))).ToString()
    };

    private static string Class(IEnumerable<char> chars)
    {
        var builder = new StringBuilder("[");
        foreach (var c in chars) builder.Append(Regex.Escape(c.ToString()));
        return builder.Length == 1 ? @"[^\s\S]" : builder.Append(']').ToString();
    }

    private static Dictionary<char, string> Map(JsonElement json, string name)
    {
        var result = new Dictionary<char, string>();
        if (json.TryGetProperty(name, out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var property in map.EnumerateObject())
                if (property.Name.Length == 1 && property.Value.GetString() is { } value) result[property.Name[0]] = value;
        return result;
    }
}
