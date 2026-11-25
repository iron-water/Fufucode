namespace Fufucode.Fui;

// A small, dependency-free lexer for the fence languages FUI advertises. It highlights one source
// line at a time; multiline strings/comments deliberately degrade to per-line color without changing
// any source character.
internal static class SyntaxHighlighter
{
    private enum Kind
    {
        Plain, Keyword, Type, String, Number, Comment, Function, Property, Operator, Added, Removed, Meta
    }

    private static readonly HashSet<string> CSharp = Words(
        "abstract as async await base break case catch checked class const continue default delegate do else enum event explicit extern " +
        "false finally fixed for foreach goto if implicit in interface internal is lock namespace new null operator out override params " +
        "private protected public readonly record ref return sealed sizeof stackalloc static struct switch this throw true try typeof " +
        "unchecked unsafe using virtual void volatile while with yield get init set required file global");
    private static readonly HashSet<string> JavaScript = Words(
        "async await break case catch class const continue debugger default delete do else export extends false finally for from function " +
        "get if implements import in instanceof interface let new null of package private protected public return set static super switch " +
        "this throw true try typeof undefined var void while with yield");
    private static readonly HashSet<string> TypeScript = Words(
        "any as asserts bigint boolean break case catch class const constructor declare default delete do else enum export extends false " +
        "finally for from function get if implements import in infer instanceof interface is keyof let module namespace never new null " +
        "number object of package private protected public readonly return satisfies set static string super switch symbol this throw true " +
        "try type typeof undefined unique unknown var void while with yield");
    private static readonly HashSet<string> Python = Words(
        "and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None " +
        "nonlocal not or pass raise return True try while with yield match case");
    private static readonly HashSet<string> Bash = Words(
        "case do done elif else esac fi for function if in select then time until while");
    private static readonly HashSet<string> PowerShell = Words(
        "begin break catch class continue data define do dynamicparam else elseif end enum exit filter finally for foreach from function " +
        "hidden if in parallel param process return sequence switch throw trap try until using var while workflow");
    private static readonly HashSet<string> Rust = Words(
        "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return " +
        "self Self static struct super trait true type unsafe use where while");
    private static readonly HashSet<string> Go = Words(
        "break case chan const continue default defer else fallthrough false for func go goto if import interface map nil package range " +
        "return select struct switch true type var");
    private static readonly HashSet<string> Sql = Words(
        "add all alter and any as asc backup begin between by case check column commit constraint create database default delete desc distinct " +
        "drop else end exec exists foreign from full group having if in index inner insert into is join key left like limit not null on or " +
        "order outer primary procedure references right rollback rownum select set table then top transaction truncate union unique update " +
        "values view when where with");
    private static readonly HashSet<string> TypeWords = Words(
        "bool byte char decimal double float int long object sbyte short string uint ulong ushort void nint nuint " +
        "any bigint boolean never number unknown string symbol i8 i16 i32 i64 i128 isize u8 u16 u32 u64 u128 usize f32 f64 str bool " +
        "byte rune error int int8 int16 int32 int64 uint uint8 uint16 uint32 uint64 uintptr float32 float64 complex64 complex128");

    internal static IReadOnlyList<StyledSpan> Highlight(string line, string language)
    {
        if (string.IsNullOrEmpty(line)) return [];
        var languageId = NormalizeLanguage(language);
        if (languageId == "diff") return Diff(line);
        if (languageId is not ("csharp" or "javascript" or "typescript" or "json" or "python" or "bash" or
            "powershell" or "rust" or "go" or "sql")) return [new(line, Ink.Code)];

        var spans = new List<StyledSpan>();
        var index = 0;
        while (index < line.Length)
        {
            if (TryComment(line, languageId, index, out var commentEnd))
            {
                Add(index, commentEnd, Kind.Comment);
                index = commentEnd;
                continue;
            }
            if (TryString(line, languageId, index, out var stringEnd))
            {
                Add(index, stringEnd, languageId == "json" && JsonKey(line, stringEnd) ? Kind.Property : Kind.String);
                index = stringEnd;
                continue;
            }
            if (TryVariableOrParameter(line, languageId, index, out var variableEnd))
            {
                Add(index, variableEnd, Kind.Property);
                index = variableEnd;
                continue;
            }
            var ch = line[index];
            if (char.IsDigit(ch) || ch == '.' && index + 1 < line.Length && char.IsDigit(line[index + 1]))
            {
                var end = index + 1;
                while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '.' or '_' or '+')) end++;
                Add(index, end, Kind.Number);
                index = end;
                continue;
            }
            if (IsIdentifierStart(ch))
            {
                var end = index + 1;
                while (end < line.Length && IsIdentifierPart(line[end], languageId)) end++;
                var word = line[index..end];
                var next = end;
                while (next < line.Length && char.IsWhiteSpace(line[next])) next++;
                var kind = WordKind(word, languageId, next < line.Length && line[next] == ':',
                    next < line.Length && line[next] == '(');
                Add(index, end, kind);
                index = end;
                continue;
            }
            if (IsOperator(ch))
            {
                var end = index + 1;
                while (end < line.Length && IsOperator(line[end])) end++;
                Add(index, end, Kind.Operator);
                index = end;
                continue;
            }
            Add(index, index + 1, Kind.Plain);
            index++;
        }
        return spans;

        void Add(int start, int end, Kind kind)
        {
            if (end <= start) return;
            var ink = InkFor(kind);
            var text = line[start..end];
            if (spans.Count != 0 && spans[^1].Ink == ink)
                spans[^1] = spans[^1] with { Text = spans[^1].Text + text };
            else spans.Add(new(text, ink));
        }
    }

    internal static string NormalizeLanguage(string? language)
    {
        var value = language?.Trim().TrimStart('{', '.').ToLowerInvariant() ?? "";
        var split = value.IndexOfAny([' ', '\t', ',', ';', '}']);
        if (split >= 0) value = value[..split];
        return value switch
        {
            "cs" or "c#" or "csharp" => "csharp",
            "ps1" or "pwsh" or "powershell" => "powershell",
            "js" or "jsx" or "mjs" or "cjs" or "javascript" => "javascript",
            "ts" or "tsx" or "typescript" => "typescript",
            "py" or "python" => "python",
            "sh" or "shell" or "bash" or "zsh" or "ksh" => "bash",
            "rs" or "rust" => "rust",
            "golang" or "go" => "go",
            "sql" => "sql",
            "patch" or "diff" => "diff",
            "json" or "jsonc" => "json",
            _ => value
        };
    }

    private static IReadOnlyList<StyledSpan> Diff(string line)
    {
        var kind = line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) ||
            line.StartsWith("@@", StringComparison.Ordinal) || line.StartsWith("diff ", StringComparison.Ordinal) ||
            line.StartsWith("index ", StringComparison.Ordinal)
            ? Kind.Meta
            : line.StartsWith('+') ? Kind.Added : line.StartsWith('-') ? Kind.Removed : Kind.Plain;
        return [new(line, InkFor(kind))];
    }

    private static bool TryComment(string line, string language, int start, out int end)
    {
        end = start;
        if (start + 1 < line.Length && line[start] == '/' && line[start + 1] == '/')
        {
            end = line.Length;
            return true;
        }
        if (start + 1 < line.Length && line[start] == '/' && line[start + 1] == '*')
        {
            var close = line.IndexOf("*/", start + 2, StringComparison.Ordinal);
            end = close < 0 ? line.Length : close + 2;
            return true;
        }
        if (language == "powershell" && start + 1 < line.Length && line[start] == '<' && line[start + 1] == '#')
        {
            var close = line.IndexOf("#>", start + 2, StringComparison.Ordinal);
            end = close < 0 ? line.Length : close + 2;
            return true;
        }
        if (language == "sql" && start + 1 < line.Length && line[start] == '-' && line[start + 1] == '-')
        {
            end = line.Length;
            return true;
        }
        if (language is "python" or "bash" or "powershell" && line[start] == '#' &&
            (start == 0 || char.IsWhiteSpace(line[start - 1]) || line[start - 1] is ';' or '('))
        {
            end = line.Length;
            return true;
        }
        return false;
    }

    private static bool TryString(string line, string language, int start, out int end)
    {
        end = start;
        var ch = line[start];
        if (language == "csharp" && ch == '@' && start + 1 < line.Length && line[start + 1] == '"')
            return QuotedWithDoubled(line, start + 1, '"', out end);
        if (ch == '$' && start + 1 < line.Length && line[start + 1] == '"' &&
            (language is "csharp" or "powershell"))
            return Quoted(line, start + 1, '"', escaped: true, out end);
        if (ch is not ('"' or '\'' or '`')) return false;
        if (language == "json" && ch != '"') return false;
        if (language == "python" && (ch is '"' or '\'') && start + 2 < line.Length &&
            line[start + 1] == ch && line[start + 2] == ch)
            return Quoted(line, start, new string(ch, 3), out end);
        if ((language is "javascript" or "typescript" or "go") && ch == '`')
            return Quoted(line, start, '`', escaped: language != "go", out end);
        if (language == "powershell" && ch == '`') return false;
        return Quoted(line, start, ch, escaped: language is not ("json" or "python"), out end);
    }

    private static bool Quoted(string line, int start, char quote, bool escaped, out int end) =>
        Quoted(line, start, quote, null, escaped, false, out end);

    private static bool QuotedWithDoubled(string line, int start, char quote, out int end) =>
        Quoted(line, start, quote, null, true, true, out end);

    private static bool Quoted(string line, int start, string quote, out int end) =>
        Quoted(line, start, quote[0], quote, false, false, out end);

    private static bool Quoted(string line, int start, char quote, string? multi, bool escaped, bool doubledEscape,
        out int end)
    {
        var token = multi ?? quote.ToString();
        var index = start + token.Length;
        while (index < line.Length)
        {
            if (multi is not null && line.AsSpan(index).StartsWith(multi, StringComparison.Ordinal))
            {
                end = index + multi.Length;
                return true;
            }
            if (multi is null && line[index] == quote)
            {
                if (doubledEscape && index + 1 < line.Length && line[index + 1] == quote)
                {
                    index += 2;
                    continue;
                }
                end = index + 1;
                return true;
            }
            if (escaped && line[index] == '\\' && index + 1 < line.Length)
            {
                index += 2;
                continue;
            }
            index++;
        }
        end = line.Length;
        return true;
    }

    private static bool TryVariableOrParameter(string line, string language, int start, out int end)
    {
        end = start;
        if (line[start] == '$' && start + 1 < line.Length && IsIdentifierStart(line[start + 1]))
        {
            end = start + 2;
            while (end < line.Length && IsIdentifierPart(line[end], language)) end++;
            return true;
        }
        if (language == "powershell" && line[start] == '-' && start + 1 < line.Length && char.IsLetter(line[start + 1]) &&
            (start == 0 || char.IsWhiteSpace(line[start - 1]) || line[start - 1] is '(' or ','))
        {
            end = start + 2;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '_' or '-')) end++;
            return true;
        }
        return false;
    }

    private static bool JsonKey(string line, int end)
    {
        while (end < line.Length && char.IsWhiteSpace(line[end])) end++;
        return end < line.Length && line[end] == ':';
    }

    private static Kind WordKind(string word, string language, bool keyBeforeColon, bool call)
    {
        var words = language switch
        {
            "csharp" => CSharp,
            "javascript" => JavaScript,
            "typescript" => TypeScript,
            "python" => Python,
            "bash" => Bash,
            "powershell" => PowerShell,
            "rust" => Rust,
            "go" => Go,
            "sql" => Sql,
            _ => null
        };
        if (language == "json")
            return keyBeforeColon ? Kind.Property : word is "true" or "false" or "null" ? Kind.Keyword : Kind.Plain;
        if (TypeWords.Contains(word)) return Kind.Type;
        if (keyBeforeColon && language == "typescript") return Kind.Property;
        if ((language is "csharp" or "typescript" or "rust" or "go") && word.Length > 0 && char.IsUpper(word[0]))
            return Kind.Type;
        if (words?.Contains(word) == true) return Kind.Keyword;
        if (call) return Kind.Function;
        if (language == "powershell" && word.Contains('-') && !word.StartsWith('-')) return Kind.Function;
        return Kind.Plain;
    }

    private static bool IsIdentifierStart(char ch) => char.IsLetter(ch) || ch is '_' or '@';

    private static bool IsIdentifierPart(char ch, string language) =>
        char.IsLetterOrDigit(ch) || ch is '_' or '@' || (language == "powershell" && ch == '-');

    private static bool IsOperator(char ch) => ch is '=' or '+' or '*' or '/' or '%' or '<' or '>' or '!' or '&' or '|' or
        '^' or '~' or '?' or ':' or '-' or '.';

    private static Ink InkFor(Kind kind) => kind switch
    {
        Kind.Keyword => Ink.SyntaxKeyword,
        Kind.Type => Ink.SyntaxType,
        Kind.String => Ink.SyntaxString,
        Kind.Number => Ink.SyntaxNumber,
        Kind.Comment => Ink.SyntaxComment,
        Kind.Function => Ink.SyntaxFunction,
        Kind.Property => Ink.SyntaxProperty,
        Kind.Operator => Ink.SyntaxOperator,
        Kind.Added => Ink.DiffAdded,
        Kind.Removed => Ink.DiffRemoved,
        Kind.Meta => Ink.DiffMeta,
        _ => Ink.Code
    };

    private static HashSet<string> Words(string words) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
}
