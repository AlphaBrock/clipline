using System.Text.RegularExpressions;

namespace Clipline;

public enum CodeToken { Plain, Keyword, String, Number, Comment, Function, Type, Tag, Attribute, Punctuation }

/// Colours code without knowing its language. Copied code comes from
/// anywhere, so it reads what most languages share: comments, strings,
/// numbers, keywords, calls and type names, or tags and attributes in
/// markup. It is often a little wrong, never far off, and always quick.
public static partial class CodeHighlighter
{
    /// The code cut into runs of one kind each, in order, covering all of it.
    public static List<(string Text, CodeToken Kind)> Tokens(string code)
    {
        var tokens = new List<(string, CodeToken)>();
        bool markup = code.TrimStart().StartsWith('<');
        var regex = markup ? Markup() : Code();
        int at = 0;
        foreach (Match m in regex.Matches(code))
        {
            if (m.Index > at) tokens.Add((code[at..m.Index], CodeToken.Plain));
            tokens.Add((m.Value, Kind(m, code, markup)));
            at = m.Index + m.Length;
        }
        if (at < code.Length) tokens.Add((code[at..], CodeToken.Plain));
        return tokens;
    }

    private static CodeToken Kind(Match m, string code, bool markup)
    {
        if (m.Groups["comment"].Success) return CodeToken.Comment;
        if (m.Groups["string"].Success) return CodeToken.String;
        if (m.Groups["number"].Success) return CodeToken.Number;
        if (m.Groups["tag"].Success) return CodeToken.Tag;
        if (m.Groups["attr"].Success) return CodeToken.Attribute;
        if (m.Groups["punct"].Success) return CodeToken.Punctuation;
        if (markup) return CodeToken.Plain;

        string word = m.Value;
        if (Keywords.Contains(word)) return CodeToken.Keyword;
        // SQL is mostly written in capitals.
        if (word.Length > 1 && word.All(c => !char.IsLower(c)) && SqlKeywords.Contains(word.ToLowerInvariant())) return CodeToken.Keyword;
        bool typeName = char.IsUpper(word[0]) && word.Any(char.IsLower);
        // `new Thing(` makes a Thing; it does not call one.
        if (typeName && code.AsSpan(0, m.Index).TrimEnd().EndsWith("new")) return CodeToken.Type;
        if (CalledNext(code, m.Index + m.Length)) return CodeToken.Function;
        if (typeName) return CodeToken.Type;
        return CodeToken.Plain;
    }

    /// A name followed by an opening bracket, perhaps after a generic's.
    private static bool CalledNext(string code, int i)
    {
        while (i < code.Length && code[i] == ' ') i++;
        return i < code.Length && code[i] == '(';
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "elif", "for", "foreach", "while", "do", "switch", "case", "default", "break",
        "continue", "return", "goto", "function", "func", "fn", "def", "lambda", "class", "struct",
        "interface", "enum", "record", "trait", "impl", "type", "namespace", "package", "module",
        "public", "private", "protected", "internal", "static", "readonly", "const", "final",
        "let", "var", "val", "mut", "pub", "new", "delete", "this", "self", "super", "base",
        "null", "nil", "None", "undefined", "true", "false", "True", "False",
        "import", "from", "export", "as", "using", "use", "require", "include", "extern",
        "try", "catch", "except", "finally", "throw", "throws", "raise", "with", "pass",
        "async", "await", "yield", "in", "is", "not", "and", "or", "of", "typeof", "instanceof",
        "void", "int", "long", "short", "float", "double", "decimal", "bool", "boolean", "string",
        "char", "byte", "object", "dynamic", "auto", "unsigned", "signed",
        "match", "where", "extends", "implements", "override", "virtual", "abstract", "sealed",
        "partial", "get", "set", "init", "out", "ref", "params", "operator", "implicit", "explicit",
        "global", "nonlocal", "defer", "go", "chan", "map", "range", "select",
        "echo", "then", "fi", "done", "esac", "local", "export", "declare", "end", "begin",
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.Ordinal)
    {
        "select", "from", "where", "and", "or", "not", "insert", "into", "values", "update", "set",
        "delete", "create", "table", "drop", "alter", "add", "join", "left", "right", "inner", "outer",
        "on", "group", "by", "order", "having", "limit", "offset", "as", "distinct", "union", "all",
        "null", "is", "in", "like", "between", "case", "when", "then", "else", "end", "primary", "key",
        "foreign", "references", "index", "view", "exists", "count", "sum", "avg", "min", "max", "asc", "desc",
    };

    [GeneratedRegex("""
        (?<comment>/\*[\s\S]*?(?:\*/|$)|//[^\n]*|(?<=^|[ \t])\#(?=[ \t!\#]|$)[^\n]*|(?<=^|\s)--\s[^\n]*)
        |(?<string>@?"(?:[^"\\\n]|\\.)*"?|'(?:[^'\\\n]|\\.)*'?|`(?:[^`\\]|\\.)*`?)
        |(?<number>\b(?:0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)[a-zA-Z]*\b|\#[0-9a-fA-F]{3,8}\b)
        |(?<word>[A-Za-z_$][\w$]*)
        |(?<punct>[{}()\[\];,.:?=<>+\-*/%&|!^~@])
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.Multiline)]
    private static partial Regex Code();

    [GeneratedRegex("""
        (?<comment><!--[\s\S]*?(?:-->|$))
        |(?<tag></?[A-Za-z!?][\w:.\-]*|/?>|\?>)
        |(?<attr>[A-Za-z_:@][\w:.\-]*(?=\s*=))
        |(?<string>"[^"\n]*"?|'[^'\n]*'?)
        |(?<number>&\#?\w+;)
        |(?<punct>=)
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Markup();
}
