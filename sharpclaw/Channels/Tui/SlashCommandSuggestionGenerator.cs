using System.Text;
using Terminal.Gui.Views;

namespace sharpclaw.UI;

/// <summary>
/// 斜杠指令补全生成器：输入 / 开头时弹出指令提示。
/// </summary>
public class SlashCommandSuggestionGenerator : ISuggestionGenerator
{
    private readonly List<string> _commands;

    public SlashCommandSuggestionGenerator(IEnumerable<string> commands)
    {
        _commands = commands.ToList();
    }

    public IEnumerable<Suggestion> GenerateSuggestions(AutocompleteContext context)
    {
        var line = context.CurrentLine.Select(c => c.Grapheme).ToList();
        var word = GetCurrentWord(line, context.CursorPosition, out var startIdx);
        context.CursorPosition = startIdx;

        if (string.IsNullOrEmpty(word) || word[0] != '/')
            return [];

        return _commands
            .Where(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                        && !c.Equals(word, StringComparison.OrdinalIgnoreCase))
            .Select(c => new Suggestion(word.Length, c))
            .ToList();
    }

    public bool IsWordChar(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var r = text.EnumerateRunes().First();
        return Rune.IsLetterOrDigit(r) || text[0] == '/' || text[0] == '-' || text[0] == '_';
    }

    private string GetCurrentWord(List<string> line, int idx, out int startIdx)
    {
        startIdx = idx;

        // walk back from cursor to find word start
        var sb = new StringBuilder();
        int i = idx - 1;
        while (i >= 0 && IsWordChar(line[i]))
        {
            sb.Insert(0, line[i]);
            i--;
        }

        startIdx = i + 1;
        return sb.ToString();
    }
}
