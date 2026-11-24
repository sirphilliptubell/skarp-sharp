namespace SkarpSharp.Setup;

/// <summary>What a key on an <see cref="IMenuScreen"/> asks the menu to do.</summary>
public enum ScreenAction { None, Back, Apply, Show }

/// <summary>
/// A screen the menu draws and passes keys to without knowing its content (the "Claude Code hooks" screen,
/// <c>Install.HooksScreen</c>, which lives with the installer so Setup does not depend on it).
/// </summary>
public interface IMenuScreen
{
    string Header { get; }
    string Context { get; }
    /// <summary>The body lines and the list they scroll in (highlighted at its index).</summary>
    (IReadOnlyList<string> Items, SetupMenu.ListState State) Body();
    string Pane();
    /// <summary>The footer line (dim; an entry line has the text cursor).</summary>
    Line Footer { get; }
    /// <summary>Handles a key: the action for the menu and an error for the pane (or null).</summary>
    (ScreenAction Action, string? Error) Handle(ConsoleKeyInfo k, int page);
    /// <summary>The command to print (<c>s</c>).</summary>
    /// <exception cref="SetupException">It cannot be built.</exception>
    CommandBuilder Build();
    /// <summary>The arguments (after <c>skarp</c>) to run after the menu closed (<c>a</c>).</summary>
    /// <exception cref="SetupException">They cannot be built.</exception>
    IReadOnlyList<string> Args();
}

/// <summary>
/// The "Claude Code hooks" row of the level choice and <c>skarp setup claude</c>, and the "Git hooks" row and
/// <c>skarp setup git</c>. The rows, panes and screens come from <see cref="SetupMenuStart"/>.
/// Applying closes the menu and leaves the arguments in <see cref="InstallArgs"/> for <c>skarp install</c> to run on the
/// plain console; <c>s</c> uses the shell choice.
/// </summary>
public sealed partial class SetupMenu
{
    IReadOnlyList<string> _screenRows = [], _screenPanes = [];
    Func<int, IMenuScreen>? _openScreen;
    IMenuScreen? _hooks;

    /// <summary>The arguments of <c>skarp install ...</c> (after <c>skarp</c>) to run after the menu closed, or null.</summary>
    public IReadOnlyList<string>? InstallArgs { get; private set; }

    /// <summary>The level choice's rows: the levels, then the screens offered.</summary>
    int LevelRows => _levels.Count + _screenRows.Count;

    /// <summary>The highlighted screen row (0 is the first after the levels).</summary>
    int ScreenRow => _levelList.Index - _levels.Count;

    bool OnHooksRow => ScreenRow >= 0 && ScreenRow < _screenRows.Count;

    void AddScreens(SetupMenuStart start)
    {
        (_screenRows, _screenPanes, _openScreen) = (start.ScreenRows, start.ScreenPanes, start.OpenScreen);
        if (start.StartScreen >= 0) OpenHooks(start.StartScreen);
    }

    void AddHooksRow(List<string> items) => items.AddRange(_screenRows);

    void OpenHooks(int index)
    {
        _hooks = _openScreen!(index);
        _screen = Screen.Hooks;
    }

    int? HandleHooks(ConsoleKeyInfo k)
    {
        var (action, error) = _hooks!.Handle(k, Page());
        _error = error;
        switch (action)
        {
            case ScreenAction.Show:
                StartShell(Screen.Hooks);
                break;
            case ScreenAction.Apply:
                try { InstallArgs = _hooks.Args(); }
                catch (SetupException ex) { _error = ex.Message; return null; }
                Summary = "";
                return 0;
            case ScreenAction.Back when LevelChosenHere:
                _hooks = null;
                _screen = Screen.Levels;
                break;
            case ScreenAction.Back:
                Summary = "Nothing written.";
                return 0;
        }
        return null;
    }
}
