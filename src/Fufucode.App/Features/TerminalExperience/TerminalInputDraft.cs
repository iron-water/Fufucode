using System.Text;

namespace Fufucode.App.Features.TerminalExperience;

/// <summary>Rules for moving clipboard text into the per-tab command draft (never straight into the shell).</summary>
public static class TerminalInputDraft
{
    /// <summary>Normalize clipboard line endings to '\n' and drop stray NULs. Content is otherwise preserved verbatim.</summary>
    public static string Normalize(string? clipboardText)
    {
        if (string.IsNullOrEmpty(clipboardText)) return string.Empty;
        var builder = new StringBuilder(clipboardText.Length);
        for (var i = 0; i < clipboardText.Length; i++)
        {
            var character = clipboardText[i];
            if (character == '\0') continue;
            if (character == '\r')
            {
                // Collapse CRLF into a single line break; a lone CR also becomes one.
                if (i + 1 < clipboardText.Length && clipboardText[i + 1] == '\n') i++;
                builder.Append('\n');
                continue;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Conservative paste merge: the existing draft is never overwritten, replaced or truncated.
    /// Pasted text is appended, separated by a single newline when both sides are non-empty and
    /// neither boundary already carries a line break.
    /// </summary>
    public static string Merge(string? existing, string? pasted)
    {
        existing ??= string.Empty;
        pasted ??= string.Empty;
        if (pasted.Length == 0) return existing;
        if (existing.Length == 0) return pasted;
        var separated = existing.EndsWith('\n') || pasted.StartsWith('\n');
        return separated ? existing + pasted : existing + "\n" + pasted;
    }
}
