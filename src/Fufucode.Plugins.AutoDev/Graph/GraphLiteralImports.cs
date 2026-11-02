using System.Text;

namespace Fufucode.Plugins.AutoDev.Graph;

/// <summary>A small lexical reader for Dart directives. It ignores comments and string bodies, never evaluates code.</summary>
internal static class GraphLiteralImports
{
    internal sealed record Import(string Kind, string Target, int Line);
    internal static IEnumerable<Import> Read(string text)
    {
        var i = 0; var line = 1;
        void Advance() { if (text[i++] == '\n') line++; }
        void SkipSpace()
        {
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { Advance(); continue; }
                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
                { while (i < text.Length && text[i] != '\n') Advance(); continue; }
                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
                {
                    i += 2; var nesting = 1;
                    while (i < text.Length && nesting > 0)
                    {
                        if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*') { nesting++; i += 2; }
                        else if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/') { nesting--; i += 2; }
                        else Advance();
                    }
                    continue;
                }
                break;
            }
        }
        string ReadString()
        {
            var quote = text[i++];
            var triple = i + 1 < text.Length && text[i] == quote && text[i + 1] == quote;
            if (triple) i += 2;
            var value = new StringBuilder();
            while (i < text.Length)
            {
                if (text[i] == quote && (!triple || i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote))
                { i += triple ? 3 : 1; return value.ToString(); }
                if (text[i] == '\\')
                {
                    // Escaped/interpolated URI syntax is kept unresolved rather than guessed.
                    value.Append('\\'); Advance();
                    if (i < text.Length) { value.Append(text[i]); Advance(); }
                    continue;
                }
                value.Append(text[i]); Advance();
            }
            return "";
        }
        while (i < text.Length)
        {
            SkipSpace(); if (i >= text.Length) break;
            if (text[i] is '\'' or '"') { ReadString(); continue; }
            if (!char.IsLetter(text[i]) && text[i] != '_') { Advance(); continue; }
            var start = i; var directiveLine = line;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            var word = text[start..i];
            if (word is not ("import" or "export" or "part")) continue;
            SkipSpace();
            if (i < text.Length && text[i] == 'r' && i + 1 < text.Length && text[i + 1] is '\'' or '"') i++;
            if (i >= text.Length || text[i] is not ('\'' or '"')) continue;
            var target = ReadString();
            if (target.Length > 0) yield return new Import("dart:" + word, target, directiveLine);
            // Conditional alternatives are facts too. Leave each condition in its provenance;
            // the scanner never chooses a platform branch or claims it was evaluated.
            while (i < text.Length && text[i] != ';')
            {
                SkipSpace(); if (i >= text.Length || text[i] == ';') break;
                if (text[i] is '\'' or '"')
                {
                    var alternative = ReadString();
                    if (alternative.Length > 0) yield return new Import("dart:conditional-" + word, alternative, directiveLine);
                }
                else Advance();
            }
        }
    }
}
