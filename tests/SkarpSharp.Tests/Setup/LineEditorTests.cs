using SkarpSharp.Setup;

namespace SkarpSharp.Tests.Setup;

/// <summary>The entry line of <c>skarp setup</c>: typing at the cursor, Left, Right, Home, End, Backspace and Delete.</summary>
public sealed class LineEditorTests
{
    static ConsoleKeyInfo K(ConsoleKey key, char c = '\0') => new(c, key, false, false, false);

    [Test]
    public async Task Keys_edit_at_the_cursor_and_stop_at_the_ends()
    {
        var e = new LineEditor(10);
        e.Start("skarp");
        await Assert.That(e.Cursor).IsEqualTo(5);
        e.Handle(K(ConsoleKey.Home));
        e.Handle(K(ConsoleKey.LeftArrow));
        await Assert.That(e.Cursor).IsEqualTo(0);
        e.Handle(K(ConsoleKey.Delete));
        e.Handle(K(ConsoleKey.X, 'X'));
        e.Handle(K(ConsoleKey.RightArrow));
        e.Handle(K(ConsoleKey.Backspace));
        await Assert.That(e.Text).IsEqualTo("Xarp");
        await Assert.That(e.Cursor).IsEqualTo(1);
        e.Handle(K(ConsoleKey.End));
        e.Handle(K(ConsoleKey.RightArrow));
        e.Handle(K(ConsoleKey.Delete));
        await Assert.That(e.Cursor).IsEqualTo(4);
        await Assert.That(e.Line("p: ").Cursor).IsEqualTo(7);
        await Assert.That(e.Handle(K(ConsoleKey.Enter, '\r'))).IsFalse();
    }

    [Test]
    public async Task The_writer_shows_the_text_cursor_on_an_entry_line_and_hides_it_after()
    {
        var t = new FakeTerminal(40, 10);
        var w = new FrameWriter(t, color: false);
        w.Draw(new Frame([new Line("a"), new Line("p: skarp", cursor: 4)]));
        await Assert.That(t.Output.ToString()).EndsWith("p: skarp\r\u001b[4C\u001b[?25h");
        t.Output.Clear();
        w.Draw(new Frame([new Line("a"), new Line("done")]));
        await Assert.That(t.Output.ToString()).EndsWith("done\u001b[?25l");
        await Assert.That(new Frame([new Line("p: skarp", cursor: 4)]).ToSnapshot()).IsEqualTo(" |p: s_karp\n");
    }

    [Test]
    public async Task Typing_stops_at_the_limit()
    {
        var e = new LineEditor(3);
        e.Start("abcdef");
        await Assert.That(e.Text).IsEqualTo("abc");
        e.Handle(K(ConsoleKey.Home));
        e.Handle(K(ConsoleKey.Z, 'z'));
        await Assert.That(e.Text).IsEqualTo("abc");
    }
}
