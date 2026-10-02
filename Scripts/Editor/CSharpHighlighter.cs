using System.Collections.Generic;

namespace KodachiGames.Markdown.Editor
{
    public enum CodeTokenKind
    {
        Plain,
        Whitespace,
        Keyword,
        Type,
        Method,
        String,
        Number,
        Comment,
        Preprocessor
    }

    public readonly struct CodeToken
    {
        public readonly string Text;
        public readonly CodeTokenKind Kind;

        public CodeToken(string text, CodeTokenKind kind)
        {
            Text = text;
            Kind = kind;
        }
    }

    public static class CSharpHighlighter
    {
        static readonly HashSet<string> Keywords = new()
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
            "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
            "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
            "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
            "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
            "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
            "add", "and", "async", "await", "dynamic", "file", "get", "global", "init", "managed", "nameof", "nint",
            "not", "notnull", "nuint", "or", "partial", "record", "remove", "required", "scoped", "set", "unmanaged",
            "var", "when", "where", "with", "yield"
        };

        public static List<List<CodeToken>> Tokenize(string code)
        {
            var lines = new List<List<CodeToken>> { new() };
            var i = 0;

            void Add(int start, int end, CodeTokenKind kind)
            {
                var segmentStart = start;
                for (var k = start; k < end; k++)
                {
                    if (code[k] != '\n') continue;
                    if (k > segmentStart) lines[^1].Add(new CodeToken(code.Substring(segmentStart, k - segmentStart), kind));
                    lines.Add(new List<CodeToken>());
                    segmentStart = k + 1;
                }
                if (end > segmentStart) lines[^1].Add(new CodeToken(code.Substring(segmentStart, end - segmentStart), kind));
            }

            while (i < code.Length)
            {
                var start = i;
                var c = code[i];

                if (c == '\n')
                {
                    Add(i, i + 1, CodeTokenKind.Plain);
                    i++;
                }
                else if (c == ' ')
                {
                    while (i < code.Length && code[i] == ' ') i++;
                    Add(start, i, CodeTokenKind.Whitespace);
                }
                else if (c == '/' && At(code, i + 1) == '/')
                {
                    i = LineEnd(code, i);
                    Add(start, i, CodeTokenKind.Comment);
                }
                else if (c == '/' && At(code, i + 1) == '*')
                {
                    var close = code.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                    i = close < 0 ? code.Length : close + 2;
                    Add(start, i, CodeTokenKind.Comment);
                }
                else if (c == '#' && lines[^1].TrueForAll(t => t.Kind == CodeTokenKind.Whitespace))
                {
                    i = LineEnd(code, i);
                    Add(start, i, CodeTokenKind.Preprocessor);
                }
                else if (StringStart(code, i, out var quote, out var verbatim))
                {
                    i = StringEnd(code, quote, verbatim);
                    Add(start, i, CodeTokenKind.String);
                }
                else if (c == '\'')
                {
                    i++;
                    while (i < code.Length && code[i] != '\'' && code[i] != '\n') i += code[i] == '\\' ? 2 : 1;
                    i = System.Math.Min(i + 1, code.Length);
                    Add(start, i, CodeTokenKind.String);
                }
                else if (char.IsDigit(c))
                {
                    while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_' || code[i] == '.' && char.IsDigit(At(code, i + 1)))) i++;
                    Add(start, i, CodeTokenKind.Number);
                }
                else if (char.IsLetter(c) || c == '_' || c == '@' && IsIdentifierStart(At(code, i + 1)))
                {
                    i++;
                    while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
                    Add(start, i, Classify(code, start, i));
                }
                else
                {
                    i++;
                    Add(start, i, CodeTokenKind.Plain);
                }
            }
            return lines;
        }

        static CodeTokenKind Classify(string code, int start, int end)
        {
            var word = code.Substring(start, end - start);
            if (Keywords.Contains(word)) return CodeTokenKind.Keyword;
            var next = end;
            while (next < code.Length && code[next] == ' ') next++;
            if (At(code, next) == '(') return CodeTokenKind.Method;
            return char.IsUpper(word[word[0] == '@' ? 1 : 0]) ? CodeTokenKind.Type : CodeTokenKind.Plain;
        }

        static bool StringStart(string code, int i, out int quote, out bool verbatim)
        {
            quote = i;
            verbatim = false;
            while (quote < code.Length && (code[quote] == '$' || code[quote] == '@'))
            {
                verbatim |= code[quote] == '@';
                quote++;
            }
            return At(code, quote) == '"';
        }

        static int StringEnd(string code, int quote, bool verbatim)
        {
            var run = 0;
            while (At(code, quote + run) == '"') run++;
            if (run >= 3)
            {
                var close = code.IndexOf(new string('"', run), quote + run, System.StringComparison.Ordinal);
                return close < 0 ? code.Length : close + run;
            }
            if (run == 2) return quote + 2;

            var i = quote + 1;
            while (i < code.Length)
            {
                var c = code[i];
                if (verbatim)
                {
                    if (c == '"' && At(code, i + 1) == '"') { i += 2; continue; }
                    if (c == '"') return i + 1;
                    i++;
                    continue;
                }
                if (c == '\n') return i;
                if (c == '\\') { i += 2; continue; }
                if (c == '"') return i + 1;
                i++;
            }
            return code.Length;
        }

        static int LineEnd(string code, int i)
        {
            var newline = code.IndexOf('\n', i);
            return newline < 0 ? code.Length : newline;
        }

        static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

        static char At(string code, int i) => i < code.Length ? code[i] : '\0';
    }
}
