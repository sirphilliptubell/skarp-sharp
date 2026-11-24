namespace SkarpSharp.Setup;

/// <summary>
/// One line of typed text with a cursor, for the entry lines of <c>skarp setup</c>: characters go in at the cursor,
/// Left and Right move it, Home and End jump to the ends, Backspace deletes before it and Delete after it. A new text
/// starts with the cursor at its end.
/// </summary>
public sealed class LineEditor(int max)
{
    public string Text { get; private set; } = "";

    /// <summary>The cursor's position in <see cref="Text"/> (0 to its length).</summary>
    public int Cursor { get; private set; }

    public void Start(string text)
    {
        Text = text.Length > max ? text[..max] : text;
        Cursor = Text.Length;
    }

    /// <summary>The entry line: <paramref name="prompt"/>, the text, and the text cursor at the editor's cursor.</summary>
    public Line Line(string prompt, LineStyle style = LineStyle.Normal) => new(prompt + Text, style, prompt.Length + Cursor);

    /// <summary>Applies <paramref name="k"/>; false when it is not an editing key (Enter, Escape and the like).</summary>
    public bool Handle(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.LeftArrow: Cursor = Math.Max(0, Cursor - 1); return true;
            case ConsoleKey.RightArrow: Cursor = Math.Min(Text.Length, Cursor + 1); return true;
            case ConsoleKey.Home: Cursor = 0; return true;
            case ConsoleKey.End: Cursor = Text.Length; return true;
            case ConsoleKey.Backspace:
                if (Cursor > 0) { Text = Text.Remove(Cursor - 1, 1); Cursor--; }
                return true;
            case ConsoleKey.Delete:
                if (Cursor < Text.Length) Text = Text.Remove(Cursor, 1);
                return true;
        }
        if (!SetupMenu.Typed(k)) return false;
        if (Text.Length < max) { Text = Text.Insert(Cursor, k.KeyChar.ToString()); Cursor++; }
        return true;
    }
}
