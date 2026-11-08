#if DEBUG
using Amoxtli.Editing;

namespace Amoxtli;

// Code blocks colored by language (port document, section 6).
public sealed partial class MainWindow
{
    private static void RunLexerTests(Action<string, bool, string?> check)
    {
        void Check(string name, bool condition, string? detail = null) => check(name, condition, detail);
        static string Show(string code, List<CodeToken> tokens) => string.Join(" ", tokens.Select(t => $"{t.Kind}:{code.Substring(t.Start, t.Length).Replace("\n", "⏎")}"));
        static bool Has(string code, List<CodeToken> tokens, string text, TokenKind kind) => tokens.Any(t => t.Kind == kind && code.Substring(t.Start, t.Length) == text);
        void Expect(string name, string code, CodeLanguage language, params (string Text, TokenKind Kind)[] expected)
        {
            var tokens = CodeLexer.Tokenize(code, language);
            Check($"Lexer: {name}", expected.All(e => Has(code, tokens, e.Text, e.Kind)), Show(code, tokens));
        }
        bool Lacks(string code, CodeLanguage language, string text) =>
            !CodeLexer.Tokenize(code, language).Any(t => code.Substring(t.Start, t.Length) == text);

        Check("Alias del fence", CodeLexer.Detect("swift") == CodeLanguage.Swift && CodeLexer.Detect("py title=\"a\"") == CodeLanguage.Python
            && CodeLexer.Detect("json{1,3}") == CodeLanguage.Json && CodeLexer.Detect("JS") == CodeLanguage.JavaScript && CodeLexer.Detect("tsx") == CodeLanguage.TypeScript
            && CodeLexer.Detect("bash") == CodeLanguage.Shell && CodeLexer.Detect("plist") == CodeLanguage.Html && CodeLexer.Detect("postgres") == CodeLanguage.Sql
            && CodeLexer.Detect("scss") == CodeLanguage.Css && CodeLexer.Detect("rust") == CodeLanguage.None && CodeLexer.Detect("") == CodeLanguage.None);
        Check("Lenguajes conocidos en orden alfabético", string.Join(",", CodeLexer.Known.Select(CodeLexer.Name)) == "CSS,HTML,JavaScript,JSON,Python,Shell,SQL,Swift,TypeScript");

        Expect("Swift", "@MainActor let x: String = \"a\\\"b\" // c\n#if DEBUG", CodeLanguage.Swift,
            ("@MainActor", TokenKind.Keyword), ("let", TokenKind.Keyword), ("String", TokenKind.Type), ("\"a\\\"b\"", TokenKind.String), ("// c", TokenKind.Comment), ("#if", TokenKind.Keyword));
        Expect("Swift multilínea", "let s = \"\"\"\nuno\ndos\n\"\"\"\n/* a\nb */ 42", CodeLanguage.Swift,
            ("\"\"\"\nuno\ndos\n\"\"\"", TokenKind.String), ("/* a\nb */", TokenKind.Comment), ("42", TokenKind.Number));
        Expect("Python", "@app.route('/')\ndef f(x: int) -> str:\n    return rb\"x\" # c", CodeLanguage.Python,
            ("@app.route", TokenKind.Attribute), ("def", TokenKind.Keyword), ("int", TokenKind.Type), ("rb\"x\"", TokenKind.String), ("# c", TokenKind.Comment), ("'/'", TokenKind.String));
        Expect("Python triple comilla", "x = '''a\nb'''\nf'{y}'", CodeLanguage.Python, ("'''a\nb'''", TokenKind.String), ("f'{y}'", TokenKind.String));
        Expect("JavaScript", "const $el = `a\n${b}`; /* c\nd */ let x = 0x1F", CodeLanguage.JavaScript,
            ("const", TokenKind.Keyword), ("`a\n${b}`", TokenKind.String), ("/* c\nd */", TokenKind.Comment), ("0x1F", TokenKind.Number));
        Check("JavaScript: $ es parte del nombre", Lacks("const $el = 1", CodeLanguage.JavaScript, "el"));
        Expect("TypeScript", "@Component()\ninterface A { b: string; c: unknown }", CodeLanguage.TypeScript,
            ("@Component", TokenKind.Attribute), ("interface", TokenKind.Keyword), ("string", TokenKind.Type), ("unknown", TokenKind.Type));
        Expect("JSON", "{\"a\": 1.5, \"b\": \"c\", \"d\": true // nota\n}", CodeLanguage.Json,
            ("\"a\"", TokenKind.Property), ("1.5", TokenKind.Number), ("\"c\"", TokenKind.String), ("true", TokenKind.Keyword), ("// nota", TokenKind.Comment));
        Expect("CSS", "a { color: #fff; margin: -4px; width: 50% !important } /* c */ @media print { }", CodeLanguage.Css,
            ("color", TokenKind.Property), ("#fff", TokenKind.Number), ("-4px", TokenKind.Number), ("50%", TokenKind.Number), ("important", TokenKind.Keyword),
            ("/* c */", TokenKind.Comment), ("@media", TokenKind.Keyword));
        Expect("SQL", "SELECT name FROM t WHERE x = 'it''s' -- c\n/* d */ int", CodeLanguage.Sql,
            ("SELECT", TokenKind.Keyword), ("FROM", TokenKind.Keyword), ("'it''s'", TokenKind.String), ("-- c", TokenKind.Comment), ("/* d */", TokenKind.Comment), ("int", TokenKind.Type));
        Check("SQL: cadena sin escapes con barra", CodeLexer.Tokenize("'a\\' b", CodeLanguage.Sql) is [{ Kind: TokenKind.String, Length: 4 }, ..]);
        Expect("Shell", "echo $HOME ${x} $1 $@ \"a\" 'b' url#frag -n 10 # c", CodeLanguage.Shell,
            ("echo", TokenKind.Keyword), ("$HOME", TokenKind.Property), ("${x}", TokenKind.Property), ("$1", TokenKind.Property), ("$@", TokenKind.Property),
            ("\"a\"", TokenKind.String), ("'b'", TokenKind.String), ("10", TokenKind.Number), ("# c", TokenKind.Comment));
        Check("Shell: # dentro de una palabra no es comentario", Lacks("echo url#frag", CodeLanguage.Shell, "#frag"));
        Check("Shell: -n es palabra, no número", Lacks("test -n x", CodeLanguage.Shell, "-n") && Lacks("test -n x", CodeLanguage.Shell, "n"));
        Expect("HTML", "<a href=\"x\" disabled>t</a><!-- c -->\n<br/> a < b", CodeLanguage.Html,
            ("<a", TokenKind.Tag), ("href", TokenKind.Attribute), ("\"x\"", TokenKind.String), ("disabled", TokenKind.Attribute), (">", TokenKind.Tag),
            ("</a", TokenKind.Tag), ("<!-- c -->", TokenKind.Comment), ("<br", TokenKind.Tag), ("/>", TokenKind.Tag));
        Check("HTML: a < b no es etiqueta", Lacks("a < b", CodeLanguage.Html, "<"));

        var unclosed = CodeLexer.Tokenize("let a = \"abc\nlet b", CodeLanguage.Swift);
        Check("Cadena sin cerrar termina en la línea", Has("let a = \"abc\nlet b", unclosed, "\"abc", TokenKind.String) && unclosed.Count(t => t.Kind == TokenKind.Keyword) == 2,
            Show("let a = \"abc\nlet b", unclosed));
        Check("Palabras que contienen palabras clave no lo son", Lacks("letter = define", CodeLanguage.Swift, "let") && Lacks("define = 1", CodeLanguage.Python, "def")
            && Lacks("selected", CodeLanguage.Sql, "select"));
        string emoji = "let ñ😀 = \"😀\" // ñ";
        var utf16 = CodeLexer.Tokenize(emoji, CodeLanguage.Swift);
        Check("Offsets UTF-16", utf16.Any(t => t.Kind == TokenKind.String && t.Start == emoji.IndexOf('"') && t.Length == 4)
            && utf16.Any(t => t.Kind == TokenKind.Comment && t.Start == emoji.IndexOf("//")), Show(emoji, utf16));

        var samples = new (string, CodeLanguage)[]
        {
            ("@x let y = \"a\" /* b */ 1.5e3 // c", CodeLanguage.Swift), ("def f(): return f'x' # y", CodeLanguage.Python),
            ("let a = `b${c}` + 'd' // e", CodeLanguage.JavaScript), ("{\"a\": [1, 2, \"b\"]}", CodeLanguage.Json),
            ("a{b:c;d:-1.5em}", CodeLanguage.Css), ("select 'a' -- b", CodeLanguage.Sql), ("x=$(ls) # y", CodeLanguage.Shell),
            ("<p class='a'>b</p>", CodeLanguage.Html), ("\"unterminated", CodeLanguage.TypeScript), ("/* open", CodeLanguage.Css), ("<a", CodeLanguage.Html)
        };
        bool ordered = samples.All(sample =>
        {
            var tokens = CodeLexer.Tokenize(sample.Item1, sample.Item2);
            return tokens.All(t => t.Length > 0 && t.Start >= 0 && t.Start + t.Length <= sample.Item1.Length)
                && tokens.Zip(tokens.Skip(1)).All(pair => pair.First.Start + pair.First.Length <= pair.Second.Start);
        });
        Check("Tokens ordenados y sin solaparse", ordered);
    }
}
#endif
