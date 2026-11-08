namespace Amoxtli.Editing;

public enum CodeLanguage { None, Css, Html, JavaScript, Json, Python, Shell, Sql, Swift, TypeScript }

public enum TokenKind { Keyword, Type, String, Number, Comment, Property, Tag, Attribute }

/// <summary>A colored range of code, in UTF-16 units from the start of the block.</summary>
public readonly record struct CodeToken(int Start, int Length, TokenKind Kind);

/// <summary>
/// Hand-written lexers for coloring fenced code, one per family. They color, they do not parse:
/// odd code is colored approximately and never fails. Tokens are sorted and never overlap.
/// </summary>
public static class CodeLexer
{
    private static readonly Dictionary<string, CodeLanguage> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["swift"] = CodeLanguage.Swift,
        ["python"] = CodeLanguage.Python, ["py"] = CodeLanguage.Python, ["python3"] = CodeLanguage.Python,
        ["javascript"] = CodeLanguage.JavaScript, ["js"] = CodeLanguage.JavaScript, ["jsx"] = CodeLanguage.JavaScript,
        ["mjs"] = CodeLanguage.JavaScript, ["cjs"] = CodeLanguage.JavaScript, ["node"] = CodeLanguage.JavaScript,
        ["typescript"] = CodeLanguage.TypeScript, ["ts"] = CodeLanguage.TypeScript, ["tsx"] = CodeLanguage.TypeScript,
        ["json"] = CodeLanguage.Json, ["jsonc"] = CodeLanguage.Json, ["json5"] = CodeLanguage.Json,
        ["html"] = CodeLanguage.Html, ["htm"] = CodeLanguage.Html, ["xml"] = CodeLanguage.Html, ["xhtml"] = CodeLanguage.Html,
        ["svg"] = CodeLanguage.Html, ["plist"] = CodeLanguage.Html,
        ["css"] = CodeLanguage.Css, ["scss"] = CodeLanguage.Css, ["less"] = CodeLanguage.Css,
        ["sql"] = CodeLanguage.Sql, ["mysql"] = CodeLanguage.Sql, ["postgresql"] = CodeLanguage.Sql, ["postgres"] = CodeLanguage.Sql, ["sqlite"] = CodeLanguage.Sql,
        ["shell"] = CodeLanguage.Shell, ["sh"] = CodeLanguage.Shell, ["bash"] = CodeLanguage.Shell, ["zsh"] = CodeLanguage.Shell,
        ["console"] = CodeLanguage.Shell, ["shellscript"] = CodeLanguage.Shell
    };

    /// <summary>Known languages in menu order, by display name.</summary>
    public static readonly CodeLanguage[] Known = Enum.GetValues<CodeLanguage>().Where(l => l != CodeLanguage.None).OrderBy(Name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The first word of a fence info string: "py title=\"a\"" and "json{1,3}" give "py" and "json".</summary>
    public static string FirstWord(string info)
    {
        var text = info.AsSpan().TrimStart();
        int end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '-' or '+' or '#' or '.')) end++;
        return text[..end].ToString();
    }

    public static CodeLanguage Detect(string info) => Aliases.GetValueOrDefault(FirstWord(info));

    public static string Name(CodeLanguage language) => language switch
    {
        CodeLanguage.Css => "CSS", CodeLanguage.Html => "HTML", CodeLanguage.JavaScript => "JavaScript", CodeLanguage.Json => "JSON",
        CodeLanguage.Python => "Python", CodeLanguage.Shell => "Shell", CodeLanguage.Sql => "SQL", CodeLanguage.Swift => "Swift",
        CodeLanguage.TypeScript => "TypeScript", _ => ""
    };

    /// <summary>The word written in the fence for a language.</summary>
    public static string Fence(CodeLanguage language) => language == CodeLanguage.None ? "" : language.ToString().ToLowerInvariant();

    private static HashSet<string> Words(string words, bool ignoreCase = false) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly HashSet<string> SwiftKeywords = Words("associatedtype class deinit enum extension fileprivate func import init inout internal let open operator private precedencegroup protocol public rethrows static struct subscript typealias var break case catch continue default defer do else fallthrough for guard if in repeat return throw switch where while as await async false is nil self Self super throws true try some any actor nonisolated mutating override final lazy weak unowned convenience required optional get set willSet didSet");
    private static readonly HashSet<string> PythonKeywords = Words("False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield match case self");
    private static readonly HashSet<string> PythonTypes = Words("int float str bool list dict set tuple bytes object");
    private static readonly HashSet<string> JavaScriptKeywords = Words("break case catch class const continue debugger default delete do else export extends finally for function if import in instanceof let new return super switch this throw try typeof var void while with yield async await of static get set null undefined true false NaN Infinity from");
    private static readonly HashSet<string> TypeScriptKeywords = Words("interface type enum readonly keyof implements namespace declare abstract private protected public as is infer satisfies module");
    private static readonly HashSet<string> TypeScriptTypes = Words("any boolean never number object string symbol unknown bigint void");
    private static readonly HashSet<string> JsonKeywords = Words("true false null");
    private static readonly HashSet<string> SqlKeywords = Words("select from where insert into values update set delete create table drop alter add column join inner left right outer full cross natural on using as and or not null is in exists between like ilike order by group having limit offset union all distinct case when then else end primary key foreign references default index unique view begin commit rollback transaction if returning with asc desc count sum avg min max true false constraint check", true);
    private static readonly HashSet<string> SqlTypes = Words("int integer bigint smallint varchar char text boolean bool date time timestamp real numeric decimal float double serial blob json uuid", true);
    private static readonly HashSet<string> ShellKeywords = Words("if then else elif fi for while until do done case esac in function select return exit export local readonly declare unset source alias echo cd set");
    private static readonly HashSet<string> CssKeywords = Words("important");

    public static List<CodeToken> Tokenize(string code, CodeLanguage language) => language switch
    {
        CodeLanguage.None => [],
        CodeLanguage.Html => Markup(code),
        CodeLanguage.Shell => Shell(code),
        _ => new Scanner(code, language).Run()
    };

    /// <summary>The C-like family: Swift, JavaScript, TypeScript, JSON, CSS, SQL and Python.</summary>
    private sealed class Scanner(string code, CodeLanguage language)
    {
        private readonly List<CodeToken> _tokens = [];
        private int _braces;

        private bool Python => language == CodeLanguage.Python;
        private bool Css => language == CodeLanguage.Css;
        private bool Sql => language == CodeLanguage.Sql;
        private bool Script => language is CodeLanguage.JavaScript or CodeLanguage.TypeScript;

        public List<CodeToken> Run()
        {
            int i = 0;
            while (i < code.Length)
            {
                char c = code[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if ((Python && c == '#') || (Sql && At(i, "--")) || (!Python && !Sql && !Css && At(i, "//")) || (language == CodeLanguage.Json && At(i, "//")))
                {
                    i = Add(i, LineEnd(i), TokenKind.Comment);
                    continue;
                }
                if (!Python && At(i, "/*"))
                {
                    int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = Add(i, end < 0 ? code.Length : end + 2, TokenKind.Comment);
                    continue;
                }
                if (String(i) is int stringEnd) { i = AddString(i, stringEnd); continue; }
                if (Css && c == '#' && _braces > 0 && i + 1 < code.Length && char.IsAsciiHexDigit(code[i + 1]))
                {
                    int end = i + 1;
                    while (end < code.Length && char.IsAsciiHexDigit(code[end])) end++;
                    i = Add(i, end, TokenKind.Number);
                    continue;
                }
                if (char.IsAsciiDigit(c) || (c == '.' && Next(i) is char d && char.IsAsciiDigit(d) && !IdentifierBefore(i))
                    || (Css && c == '-' && Next(i) is char n && (char.IsAsciiDigit(n) || n == '.') && !IdentifierBefore(i)))
                {
                    if (IdentifierBefore(i)) { i++; continue; }
                    i = Add(i, Number(i), TokenKind.Number);
                    continue;
                }
                if (c == '@' && Next(i) is char a && char.IsLetter(a))
                {
                    int end = Identifier(i + 1, Python || language == CodeLanguage.TypeScript);
                    // Swift attributes and CSS at-rules read as keywords; Python and TypeScript decorators as attributes.
                    i = Add(i, end, Python || language == CodeLanguage.TypeScript ? TokenKind.Attribute : TokenKind.Keyword);
                    continue;
                }
                if (c == '#' && language == CodeLanguage.Swift && Next(i) is char h && char.IsLetter(h))
                {
                    i = Add(i, Identifier(i + 1), TokenKind.Keyword);
                    continue;
                }
                if (IsIdentifierStart(c))
                {
                    int end = Identifier(i);
                    if (Classify(code[i..end], i, end) is TokenKind kind) Add(i, end, kind);
                    i = end;
                    continue;
                }
                if (Css && c == '{') _braces++;
                if (Css && c == '}') _braces = Math.Max(0, _braces - 1);
                i++;
            }
            return _tokens;
        }

        private TokenKind? Classify(string word, int start, int end)
        {
            switch (language)
            {
                case CodeLanguage.Swift:
                    if (SwiftKeywords.Contains(word)) return TokenKind.Keyword;
                    return char.IsUpper(word[0]) ? TokenKind.Type : null;
                case CodeLanguage.Python:
                    if (PythonKeywords.Contains(word)) return TokenKind.Keyword;
                    return PythonTypes.Contains(word) ? TokenKind.Type : null;
                case CodeLanguage.JavaScript:
                    return JavaScriptKeywords.Contains(word) ? TokenKind.Keyword : null;
                case CodeLanguage.TypeScript:
                    if (JavaScriptKeywords.Contains(word) || TypeScriptKeywords.Contains(word)) return TokenKind.Keyword;
                    return TypeScriptTypes.Contains(word) ? TokenKind.Type : null;
                case CodeLanguage.Json:
                    return JsonKeywords.Contains(word) ? TokenKind.Keyword : null;
                case CodeLanguage.Sql:
                    if (SqlKeywords.Contains(word)) return TokenKind.Keyword;
                    return SqlTypes.Contains(word) ? TokenKind.Type : null;
                case CodeLanguage.Css:
                    if (CssKeywords.Contains(word) && start > 0 && code[start - 1] == '!') return TokenKind.Keyword;
                    // Inside a rule, a word followed by ":" is a property.
                    if (_braces > 0 && NextNonSpace(end) == ':') return TokenKind.Property;
                    return null;
            }
            return null;
        }

        /// <summary>The end of a string starting at <paramref name="i"/>, or null when none starts there.</summary>
        private int? String(int i)
        {
            int quote = i;
            if (Python)
            {
                // Prefixes r b f u and their pairs belong to the string.
                int p = i;
                while (p < code.Length && p - i < 2 && code[p] is 'r' or 'R' or 'b' or 'B' or 'f' or 'F' or 'u' or 'U') p++;
                if (p > i && (p >= code.Length || code[p] is not ('"' or '\'') || IdentifierBefore(i))) return null;
                quote = p;
            }
            if (quote >= code.Length) return null;
            char q = code[quote];
            bool allowed = q == '"' || (q == '\'' && language != CodeLanguage.Swift) || (q == '`' && Script);
            if (!allowed) return null;
            bool raw = Sql || (Python && code[i..quote].Contains('r', StringComparison.OrdinalIgnoreCase));
            if ((language is CodeLanguage.Swift or CodeLanguage.Python) && At(quote, new string(q, 3)))
            {
                string fence = new(q, 3);
                int end = quote + 3;
                while (end < code.Length && !At(end, fence)) end += code[end] == '\\' && !raw ? 2 : 1;
                return Math.Min(code.Length, end + 3);
            }
            bool multiline = q == '`';
            for (int end = quote + 1; end < code.Length; end++)
            {
                char c = code[end];
                if (c == '\\' && !raw) { end++; continue; }
                if (c == q)
                {
                    // SQL doubles the quote to escape it.
                    if (Sql && end + 1 < code.Length && code[end + 1] == q) { end++; continue; }
                    return end + 1;
                }
                // A string left open ends with its line.
                if (c == '\n' && !multiline) return end;
            }
            return code.Length;
        }

        private int AddString(int start, int end)
        {
            // In JSON, a string followed by ":" is a key.
            var kind = language == CodeLanguage.Json && NextNonSpace(end) == ':' ? TokenKind.Property : TokenKind.String;
            return Add(start, end, kind);
        }

        private int Number(int i)
        {
            int end = i;
            if (code[end] == '-') end++;
            if (At(end, "0x") || At(end, "0X"))
            {
                end += 2;
                while (end < code.Length && (char.IsAsciiHexDigit(code[end]) || code[end] == '_')) end++;
                return end;
            }
            while (end < code.Length && (char.IsAsciiDigit(code[end]) || code[end] is '_' or '.')) end++;
            if (end < code.Length && code[end] is 'e' or 'E' && Next(end) is char e && (char.IsAsciiDigit(e) || e is '+' or '-'))
            {
                end += 2;
                while (end < code.Length && char.IsAsciiDigit(code[end])) end++;
            }
            // CSS units and percentages belong to the number.
            if (Css) while (end < code.Length && (char.IsAsciiLetter(code[end]) || code[end] == '%')) end++;
            return end;
        }

        private int Identifier(int i, bool dotted = false)
        {
            int end = i;
            while (end < code.Length && (IsIdentifierPart(code[end]) || (dotted && code[end] == '.' && Next(end) is char n && IsIdentifierStart(n)))) end++;
            return end;
        }

        private bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || (Script && c == '$') || (Css && c == '-');
        private bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || (Script && c == '$') || (Css && c == '-');
        private bool IdentifierBefore(int i) => i > 0 && IsIdentifierPart(code[i - 1]);
        private char? Next(int i) => i + 1 < code.Length ? code[i + 1] : null;
        private bool At(int i, string value) => string.CompareOrdinal(code, i, value, 0, value.Length) == 0;
        private int LineEnd(int i) { int end = code.IndexOf('\n', i); return end < 0 ? code.Length : end; }

        private char? NextNonSpace(int i)
        {
            while (i < code.Length && code[i] is ' ' or '\t') i++;
            return i < code.Length ? code[i] : null;
        }

        private int Add(int start, int end, TokenKind kind)
        {
            if (end > start) _tokens.Add(new CodeToken(start, end - start, kind));
            return Math.Max(end, start + 1);
        }
    }

    /// <summary>Shell: "#" starts a comment only at the start of a word; $VAR, ${var}, $1 and $@ are variables.</summary>
    private static List<CodeToken> Shell(string code)
    {
        var tokens = new List<CodeToken>();
        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];
            bool wordStart = i == 0 || char.IsWhiteSpace(code[i - 1]) || code[i - 1] is ';' or '(' or '|' or '&';
            if (c == '#' && wordStart)
            {
                int end = code.IndexOf('\n', i);
                end = end < 0 ? code.Length : end;
                tokens.Add(new CodeToken(i, end - i, TokenKind.Comment));
                i = end;
                continue;
            }
            if (c is '"' or '\'')
            {
                int end = i + 1;
                while (end < code.Length && code[end] != c) end += c == '"' && code[end] == '\\' ? 2 : 1;
                end = Math.Min(code.Length, end + 1);
                tokens.Add(new CodeToken(i, end - i, TokenKind.String));
                i = end;
                continue;
            }
            if (c == '$' && i + 1 < code.Length)
            {
                int end = i + 1;
                if (code[end] == '{') { int close = code.IndexOf('}', end); end = close < 0 ? code.Length : close + 1; }
                else if (code[end] is '@' or '*' or '#' or '?' or '$' or '!' || char.IsAsciiDigit(code[end])) end++;
                else while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '_')) end++;
                if (end > i + 1) { tokens.Add(new CodeToken(i, end - i, TokenKind.Property)); i = end; continue; }
            }
            if (wordStart && !char.IsWhiteSpace(c))
            {
                int end = i;
                while (end < code.Length && !char.IsWhiteSpace(code[end]) && code[end] is not (';' or '|' or '&' or '"' or '\'' or '$' or ')' or '(')) end++;
                string word = code[i..end];
                // "-n" is a flag, not a number: only whole numeric words are numbers.
                if (word.Length > 0 && word.All(char.IsAsciiDigit)) tokens.Add(new CodeToken(i, end - i, TokenKind.Number));
                else if (ShellKeywords.Contains(word)) tokens.Add(new CodeToken(i, end - i, TokenKind.Keyword));
                i = Math.Max(end, i + 1);
                continue;
            }
            i++;
        }
        return tokens;
    }

    /// <summary>HTML and XML: comments, tags with their attributes and quoted values. "a &lt; b" is not a tag.</summary>
    private static List<CodeToken> Markup(string code)
    {
        var tokens = new List<CodeToken>();
        int i = 0;
        while (i < code.Length)
        {
            if (string.CompareOrdinal(code, i, "<!--", 0, 4) == 0)
            {
                int end = code.IndexOf("-->", i + 4, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 3;
                tokens.Add(new CodeToken(i, end - i, TokenKind.Comment));
                i = end;
                continue;
            }
            if (code[i] == '<' && i + 1 < code.Length && (char.IsLetter(code[i + 1]) || code[i + 1] is '/' or '?' or '!'))
            {
                int end = i + 1;
                if (code[end] is '/' or '?' or '!') end++;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] is '-' or ':' or '_' or '.')) end++;
                tokens.Add(new CodeToken(i, end - i, TokenKind.Tag));
                i = end;
                while (i < code.Length && code[i] != '>' && string.CompareOrdinal(code, i, "/>", 0, 2) != 0 && code[i] != '<')
                {
                    char c = code[i];
                    if (c is '"' or '\'')
                    {
                        int close = code.IndexOf(c, i + 1);
                        close = close < 0 ? code.Length : close + 1;
                        tokens.Add(new CodeToken(i, close - i, TokenKind.String));
                        i = close;
                    }
                    else if (char.IsLetter(c) || c is '_' or ':' or '@')
                    {
                        int name = i;
                        while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] is '-' or ':' or '_' or '.' or '@')) i++;
                        tokens.Add(new CodeToken(name, i - name, TokenKind.Attribute));
                    }
                    else i++;
                }
                if (i < code.Length && code[i] != '<')
                {
                    int close = code[i] == '/' ? 2 : 1;
                    tokens.Add(new CodeToken(i, close, TokenKind.Tag));
                    i += close;
                }
                continue;
            }
            i++;
        }
        return tokens;
    }
}
