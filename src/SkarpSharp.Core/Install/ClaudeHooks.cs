using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkarpSharp.Install;

/// <summary>What <c>skarp install claude</c> writes: the program and the options appended to each hook's <c>args</c>.</summary>
public sealed record HookSpec
{
    /// <summary>The program (<c>--command</c>); default plain <c>skarp</c>, found on PATH.</summary>
    public string Command { get; init; } = "skarp";
    /// <summary>True: <c>--command</c> was given (it also recognizes Skarp's entries).</summary>
    public bool CommandGiven { get; init; }
    /// <summary>True: the handler runs the program through <c>cmd.exe /c</c> (Windows, where the program is a <c>.cmd</c>
    /// shim that exec form cannot start). <see cref="ClaudeHooks.CmdProgram"/>.</summary>
    public bool ViaCmd { get; init; }
    /// <summary>False with <c>--no-stop</c>: no Stop entry (an existing one is removed).</summary>
    public bool Stop { get; init; } = true;
    /// <summary>False with <c>--no-compact</c>: no PostCompact entry (an existing one is removed).</summary>
    public bool Compact { get; init; } = true;
    public bool Gate { get; init; }
    /// <summary>Stop only.</summary>
    public bool Semantic { get; init; }
    public int? MaxLines { get; init; }
    /// <summary><c>--include-notices</c> (true) or <c>--no-include-notices</c> (false); null keeps what an
    /// existing entry shows.</summary>
    public bool? Notice { get; init; }

    /// <summary>The <c>args</c> of an event's handler: <c>check --hook claude</c>, the options, then <paramref name="kept"/>.</summary>
    public List<string> Args(string hookEvent, IReadOnlyList<string>? kept = null)
    {
        List<string> args = ["check", "--hook", "claude"];
        if (Gate) args.Add("--gate");
        if (Semantic && hookEvent == ClaudeHooks.Stop) args.Add("--semantic");
        if (MaxLines is { } n) args.AddRange(["--max-lines", n.ToString(CultureInfo.InvariantCulture)]);
        if (Notice == true) args.Add("--include-notices");
        if (kept is not null) args.AddRange(kept);
        return args;
    }

    /// <summary>A new handler object for <paramref name="hookEvent"/> (exec form; timeout 60 for Stop, else 30).</summary>
    public JsonObject Handler(string hookEvent, IReadOnlyList<string>? kept = null)
    {
        var args = new JsonArray();
        if (ViaCmd) { args.Add((JsonNode?)JsonValue.Create("/c")); args.Add((JsonNode?)JsonValue.Create(Command)); }
        foreach (var a in Args(hookEvent, kept)) args.Add((JsonNode?)JsonValue.Create(a));
        return new JsonObject
        {
            ["type"] = "command",
            ["command"] = ViaCmd ? ClaudeHooks.CmdProgram : Command,
            ["args"] = args,
            ["timeout"] = hookEvent == ClaudeHooks.Stop ? 60 : 30,
        };
    }

    /// <summary>The events written: PostToolUse, Stop unless <c>--no-stop</c>, PostCompact unless <c>--no-compact</c>.</summary>
    public IEnumerable<string> Events => ClaudeHooks.Events.Where(Writes);

    /// <summary>Whether this spec writes an entry for <paramref name="hookEvent"/>.</summary>
    public bool Writes(string hookEvent) => hookEvent switch
    {
        ClaudeHooks.Stop => Stop,
        ClaudeHooks.PostCompact => Compact,
        _ => true,
    };
}

/// <summary>The options of one Skarp handler after <c>check --hook claude</c>, split into the ones the installer
/// writes and the others (<see cref="Kept"/>, in their order).</summary>
public sealed class HookOptions
{
    public bool Gate { get; private set; }
    public bool Semantic { get; private set; }
    public int? MaxLines { get; private set; }
    /// <summary><c>--include-notices</c>, or the removed <c>--min-severity notice</c>.</summary>
    public bool Notice { get; private set; }
    public List<string> Kept { get; } = [];

    /// <summary>The options of <paramref name="handler"/> (none when it has no readable invocation).</summary>
    public static HookOptions Of(JsonObject handler)
    {
        var o = new HookOptions();
        if (ClaudeHooks.Invocation(handler) is not { } inv) return o;
        for (int i = ClaudeHooks.HookArgs.Length; i < inv.Args.Count; i++) i = o.Take(inv.Args, i);
        return o;
    }

    /// <summary>Reads the option at <paramref name="i"/>; returns the index of its last token.</summary>
    int Take(List<string> args, int i)
    {
        string? next = i + 1 < args.Count ? args[i + 1] : null;
        switch (args[i])
        {
            case "--gate": Gate = true; return i;
            case "--semantic": Semantic = true; return i;
            case "--include-notices": Notice = true; return i;
            // --min-severity was removed from check: notice becomes --include-notices, any other value is dropped.
            case "--min-severity": Notice |= next == "notice"; return next is null ? i : i + 1;
            case "--max-lines" when int.TryParse(next, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1:
                MaxLines = n;
                return i + 1;
            default: Kept.Add(args[i]); return i;
        }
    }
}

/// <summary>Skarp's entry in one settings file read back: the spec that writes it and the options kept.</summary>
public sealed class HookEntry
{
    public required HookSpec Spec { get; init; }
    /// <summary>The options the installer does not write: PostToolUse's, then Stop's others.</summary>
    public required List<string> Kept { get; init; }

    /// <summary>
    /// Skarp's entry in <paramref name="root"/> read back into the spec that writes it, or null when there is none. The
    /// program and the options come from the first PostToolUse handler (else the Stop one), the semantic check from the
    /// Stop handler; <see cref="HookSpec.Stop"/> and <see cref="HookSpec.Compact"/> say whether a Stop and a PostCompact
    /// handler exist.
    /// </summary>
    public static HookEntry? Read(JsonObject root)
    {
        var found = ClaudeHooks.Find(root);
        if (found.Count == 0) return null;
        var post = found.Find(f => f.Event == ClaudeHooks.PostToolUse)?.Handler;
        var stop = found.Find(f => f.Event == ClaudeHooks.Stop)?.Handler;
        bool compact = found.Exists(f => f.Event == ClaudeHooks.PostCompact);
        var first = post ?? stop!;
        var main = HookOptions.Of(first);
        var stopOptions = stop is null ? new HookOptions() : HookOptions.Of(stop);
        var kept = new List<string>(main.Kept);
        kept.AddRange(stopOptions.Kept.Where(k => !kept.Contains(k)));
        var program = ClaudeHooks.Invocation(first)!.Value.Program;
        var spec = new HookSpec
        {
            Command = program, CommandGiven = program != "skarp", ViaCmd = ClaudeHooks.IsCmdWrapped(first), Stop = stop is not null, Compact = compact, Gate = main.Gate,
            Semantic = stopOptions.Semantic, MaxLines = main.MaxLines, Notice = main.Notice,
        };
        return new HookEntry { Spec = spec, Kept = kept };
    }
}

/// <summary>What install or remove does to one event's Skarp handler.</summary>
public enum HookChangeKind { None, Add, Update, Unchanged, Remove }

/// <summary>One event's change: the old and new Skarp handler (with their groups, for the matcher).</summary>
public sealed class HookChange
{
    public required string Event { get; init; }
    public HookChangeKind Kind { get; set; }
    public JsonObject? Old { get; init; }
    public JsonObject? OldGroup { get; init; }
    public JsonObject? New { get; set; }
    public JsonObject? NewGroup { get; set; }
}

/// <summary>A Skarp handler found in a settings tree.</summary>
public sealed class FoundHook
{
    public required string Event { get; init; }
    public required JsonObject Group { get; init; }
    public required JsonArray Handlers { get; init; }
    public required JsonObject Handler { get; init; }
}

/// <summary>A settings tree whose <c>hooks</c> has a shape Skarp cannot edit (the message names the key).</summary>
public sealed class HookShapeException(string message) : Exception(message);

/// <summary>
/// Skarp's hook entries in a Claude Code settings tree, as pure functions over a
/// <see cref="JsonObject"/> that keep key order. Recognizing Skarp's handlers, installing them (an existing handler is
/// replaced in place, else a group is appended), and removing them with the groups, events and <c>hooks</c> they leave
/// empty. Every other key and handler is left as it is.
/// </summary>
public static class ClaudeHooks
{
    public const string PostToolUse = "PostToolUse";
    public const string Stop = "Stop";
    /// <summary>After Claude Code compacts the context; the handler only counts it.</summary>
    public const string PostCompact = "PostCompact";
    public const string Matcher = "Edit|Write";
    public const string ProjectDirVariable = "${CLAUDE_PROJECT_DIR}";
    public static readonly string[] Events = [PostToolUse, Stop, PostCompact];

    // ---- recognizing ------------------------------------------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="node"/> is a Skarp handler: <c>type</c> <c>command</c>, a program whose file name is
    /// <c>skarp</c> (after <c>${CLAUDE_PROJECT_DIR}</c>, the folder and the extension are stripped) or equals
    /// <paramref name="given"/>, and arguments starting with <c>check --hook claude</c>. Shell form (no <c>args</c>) is
    /// recognized by its command text.
    /// </summary>
    public static bool IsSkarp(JsonNode? node, string? given = null)
    {
        if (node is not JsonObject h || Str(h["type"]) != "command" || Invocation(h) is not { } inv) return false;
        if (!inv.Args.Take(HookArgs.Length).SequenceEqual(HookArgs)) return false;
        return IsSkarpProgram(inv.Program) || inv.Program == given;
    }

    /// <summary>The arguments every Skarp hook starts with.</summary>
    internal static readonly string[] HookArgs = ["check", "--hook", "claude"];

    /// <summary>The program's file name without folder and extension is <c>skarp</c> (any case).</summary>
    public static bool IsSkarpProgram(string program)
    {
        var p = program.Replace(ProjectDirVariable, "", StringComparison.Ordinal).Replace('\\', '/');
        var name = p[(p.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        return name.Equals("skarp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True: a <c>command</c> handler without <c>args</c> (Claude Code runs it through a shell).</summary>
    public static bool IsShellForm(JsonObject h) => h["args"] is null;

    /// <summary>The program <c>cmd.exe</c> that runs a <c>.cmd</c> shim for exec form: <c>cmd.exe /c skarp ...</c>.</summary>
    public const string CmdProgram = "cmd.exe";

    /// <summary>True when the handler runs its program through <c>cmd.exe /c</c> (exec form).</summary>
    public static bool IsCmdWrapped(JsonObject h) => RawInvocation(h) is { } inv && Unwrap(inv) is not null;

    /// <summary>The program and arguments of the handler's own program, seeing through <c>cmd.exe /c &lt;program&gt; ...</c>:
    /// exec form's <c>command</c> and <c>args</c>, or shell form's command text split into words; null when
    /// <c>command</c> is not a string or an argument is not one.</summary>
    public static (string Program, List<string> Args)? Invocation(JsonObject h) =>
        RawInvocation(h) is { } inv ? Unwrap(inv) ?? inv : null;

    /// <summary>The program and arguments inside <c>cmd.exe /c</c>, or null when <paramref name="inv"/> is not that form.</summary>
    static (string Program, List<string> Args)? Unwrap((string Program, List<string> Args) inv)
    {
        if (!IsCmdProgram(inv.Program)) return null;
        if (inv.Args.Count < 2 || !inv.Args[0].Equals("/c", StringComparison.OrdinalIgnoreCase)) return null;
        return (inv.Args[1], inv.Args[2..]);
    }

    /// <summary>The program's file name is <c>cmd</c> or <c>cmd.exe</c> (any case, any folder).</summary>
    internal static bool IsCmdProgram(string program)
    {
        var p = program.Replace('\\', '/');
        var name = p[(p.LastIndexOf('/') + 1)..];
        return name.Equals("cmd", StringComparison.OrdinalIgnoreCase) || name.Equals(CmdProgram, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The handler's <c>command</c> and <c>args</c> as written (shell form split into words).</summary>
    static (string Program, List<string> Args)? RawInvocation(JsonObject h)
    {
        if (Str(h["command"]) is not { Length: > 0 } command) return null;
        if (h["args"] is JsonArray array)
        {
            var args = new List<string>();
            foreach (var a in array) { if (Str(a) is not { } s) return null; args.Add(s); }
            return (command, args);
        }
        if (h["args"] is not null) return null;
        var words = Words(command);
        return words.Count == 0 ? null : (words[0], words[1..]);
    }

    /// <summary>Shell-like words: whitespace separates, quotes group (no escapes).</summary>
    public static List<string> Words(string text)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        bool inWord = false;
        char quote = '\0';
        foreach (var c in text)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else sb.Append(c); continue; }
            if (c is '"' or '\'') { quote = c; inWord = true; continue; }
            if (char.IsWhiteSpace(c)) { if (inWord) { words.Add(sb.ToString()); sb.Clear(); inWord = false; } continue; }
            sb.Append(c);
            inWord = true;
        }
        if (inWord) words.Add(sb.ToString());
        return words;
    }

    /// <summary>The Skarp handlers of <paramref name="root"/>, event by event (PostToolUse, Stop, PostCompact), in file order.
    /// Groups or events of an unexpected shape are skipped.</summary>
    public static List<FoundHook> Find(JsonObject root, string? given = null)
    {
        var found = new List<FoundHook>();
        if (root["hooks"] is not JsonObject hooks) return found;
        foreach (var ev in Events)
            if (hooks[ev] is JsonArray groups)
                found.AddRange(Handlers(ev, groups).Where(f => IsSkarp(f.Handler, given)));
        return found;
    }

    /// <summary>Every command handler object of an event's groups (other shapes skipped).</summary>
    static IEnumerable<FoundHook> Handlers(string ev, JsonArray groups)
    {
        foreach (var g in groups)
        {
            if (g is not JsonObject group || group["hooks"] is not JsonArray handlers) continue;
            foreach (var h in handlers)
                if (h is JsonObject handler) yield return new FoundHook { Event = ev, Group = group, Handlers = handlers, Handler = handler };
        }
    }

    /// <summary>
    /// The command and arguments Claude Code runs, with <c>${CLAUDE_PROJECT_DIR}</c> replaced by
    /// <paramref name="projectDir"/> and separators made <c>/</c>, for comparing entries across scopes.
    /// </summary>
    public static string Key(JsonObject h, string projectDir)
    {
        if (RawInvocation(h) is not { } inv) return "";
        string Expand(string s) => s.Replace(ProjectDirVariable, projectDir, StringComparison.Ordinal).Replace('\\', '/');
        return string.Join("\u0001", [Expand(inv.Program), .. inv.Args.Select(Expand)]);
    }

    // ---- install and remove -----------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="original"/> with Skarp's entries installed (a new tree; the original is not changed). Per event: an
    /// existing Skarp handler is replaced in place (further Skarp handlers of the event are removed), a Skarp-only
    /// PostToolUse group gets the matcher <c>Edit|Write</c>; without one a group is appended. With <c>--no-stop</c> an
    /// existing Stop handler is removed (with <c>--no-compact</c> the PostCompact one). An event whose result equals what
    /// was there is <see cref="HookChangeKind.Unchanged"/>.
    /// </summary>
    /// <exception cref="HookShapeException"><c>hooks</c> is not an object, or an event is not an array.</exception>
    public static (JsonObject Result, List<HookChange> Changes) Install(JsonObject original, HookSpec spec)
    {
        CheckShape(original);
        var root = (JsonObject)original.DeepClone();
        var changes = new List<HookChange>();
        var given = spec.CommandGiven ? spec.Command : null;
        foreach (var ev in Events)
        {
            var found = Find(root, given).Where(f => f.Event == ev).ToList();
            if (!spec.Writes(ev))
            {
                if (found.Count > 0) changes.Add(RemoveAll(root, ev, found));
                continue;
            }
            changes.Add(found.Count == 0 ? Add(root, ev, spec.Handler(ev)) : Replace(root, found, Updated(spec, ev, found[0].Handler), given));
        }
        return (root, changes);
    }

    /// <summary>
    /// The handler that replaces <paramref name="old"/>: <paramref name="spec"/>'s options, then the old handler's options
    /// that the installer does not write, in their order. Without an explicit choice
    /// (<see cref="HookSpec.Notice"/> null) an entry that shows notices keeps showing them, and the removed
    /// <c>--min-severity notice</c> becomes <c>--include-notices</c>; <c>--no-include-notices</c> drops it.
    /// </summary>
    static JsonObject Updated(HookSpec spec, string ev, JsonObject old)
    {
        var options = HookOptions.Of(old);
        return (spec.Notice is null ? spec with { Notice = options.Notice } : spec).Handler(ev, options.Kept);
    }

    /// <summary>Appends a new group with <paramref name="handler"/> (PostToolUse: with the matcher), creating
    /// <c>hooks</c> and the event as needed.</summary>
    static HookChange Add(JsonObject root, string ev, JsonObject handler)
    {
        if (root["hooks"] is not JsonObject hooks) { hooks = []; root["hooks"] = hooks; }
        if (hooks[ev] is not JsonArray groups) { groups = []; hooks[ev] = groups; }
        var group = ev == PostToolUse ? new JsonObject { ["matcher"] = Matcher } : [];
        group["hooks"] = new JsonArray(handler);
        groups.Add((JsonNode)group);
        return new HookChange { Event = ev, Kind = HookChangeKind.Add, New = handler, NewGroup = group };
    }

    /// <summary>Replaces the first Skarp handler in place and removes the others; a Skarp-only PostToolUse group gets the
    /// matcher. <see cref="HookChangeKind.Unchanged"/> when the event ends up as it was.</summary>
    static HookChange Replace(JsonObject root, List<FoundHook> found, JsonObject handler, string? given)
    {
        var (first, ev) = (found[0], found[0].Event);
        var before = root["hooks"]![ev]!.ToJsonString();
        var change = new HookChange { Event = ev, Kind = HookChangeKind.Update, Old = (JsonObject)first.Handler.DeepClone(), OldGroup = (JsonObject)first.Group.DeepClone() };
        first.Handlers[IndexOf(first.Handlers, first.Handler)] = handler;
        foreach (var extra in found.Skip(1)) RemoveHandler(root, ev, extra);
        if (ev == PostToolUse && first.Handlers.All(h => IsSkarp(h, given)))
        {
            if (first.Group.ContainsKey("matcher")) first.Group["matcher"] = Matcher;
            else first.Group.Insert(0, "matcher", Matcher);
        }
        change.New = handler;
        change.NewGroup = first.Group;
        if (before == root["hooks"]![ev]!.ToJsonString()) change.Kind = HookChangeKind.Unchanged;
        return change;
    }

    /// <summary><paramref name="original"/> without Skarp's handlers, then without the groups, events and <c>hooks</c>
    /// they leave empty (a new tree). The changes list one <see cref="HookChangeKind.Remove"/> per event that had one.</summary>
    /// <exception cref="HookShapeException"><c>hooks</c> is not an object, or an event is not an array.</exception>
    public static (JsonObject Result, List<HookChange> Changes) Remove(JsonObject original, string? given = null)
    {
        CheckShape(original);
        var root = (JsonObject)original.DeepClone();
        var changes = new List<HookChange>();
        foreach (var ev in Events)
        {
            var found = Find(root, given).Where(f => f.Event == ev).ToList();
            if (found.Count > 0) changes.Add(RemoveAll(root, ev, found));
        }
        return (root, changes);
    }

    static HookChange RemoveAll(JsonObject root, string ev, List<FoundHook> found)
    {
        var change = new HookChange { Event = ev, Kind = HookChangeKind.Remove, Old = (JsonObject)found[0].Handler.DeepClone(), OldGroup = (JsonObject)found[0].Group.DeepClone() };
        foreach (var f in found) RemoveHandler(root, ev, f);
        return change;
    }

    /// <summary>Removes one handler, then its group when left empty, the event when left empty, and <c>hooks</c> when left empty.</summary>
    static void RemoveHandler(JsonObject root, string ev, FoundHook f)
    {
        f.Handlers.RemoveAt(IndexOf(f.Handlers, f.Handler));
        if (f.Handlers.Count > 0) return;
        if (root["hooks"] is not JsonObject hooks || hooks[ev] is not JsonArray groups) return;
        groups.RemoveAt(IndexOf(groups, f.Group));
        if (groups.Count > 0) return;
        hooks.Remove(ev);
        if (hooks.Count == 0) root.Remove("hooks");
    }

    static int IndexOf(JsonArray array, JsonNode node)
    {
        for (int i = 0; i < array.Count; i++) if (ReferenceEquals(array[i], node)) return i;
        throw new InvalidOperationException("handler not found");
    }

    static void CheckShape(JsonObject root)
    {
        if (root["hooks"] is null) { if (root.ContainsKey("hooks")) throw new HookShapeException("\"hooks\" is null; expected an object"); return; }
        if (root["hooks"] is not JsonObject hooks) throw new HookShapeException("\"hooks\" is not an object");
        foreach (var ev in Events)
            if (hooks.ContainsKey(ev) && hooks[ev] is not JsonArray) throw new HookShapeException($"\"hooks.{ev}\" is not an array");
    }

    // ---- words ------------------------------------------------------------------------------------------------------

    /// <summary>A handler in words: <c>skarp check --hook claude (matcher Edit|Write, timeout 30)</c>; shell form says so.</summary>
    public static string Describe(string ev, JsonObject handler, JsonObject? group)
    {
        var text = handler["args"] is JsonArray args
            ? string.Join(" ", [Quote(Str(handler["command"]) ?? ""), .. args.Select(a => Quote(Str(a) ?? a?.ToJsonString() ?? "null"))])
            : (Str(handler["command"]) ?? "") + " (shell form)";
        var details = new List<string>();
        if (ev == PostToolUse) details.Add(group?["matcher"] is { } m && Str(m) is { } ms ? "matcher " + ms : "every tool");
        if (handler["timeout"] is JsonValue t && t.GetValueKind() == JsonValueKind.Number) details.Add("timeout " + t.ToJsonString());
        return details.Count == 0 ? text : $"{text} ({string.Join(", ", details)})";
    }

    static string Quote(string s) => s.Length == 0 || s.Any(char.IsWhiteSpace) ? "\"" + s + "\"" : s;

    internal static string? Str(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>The entries for a file that has none: <c>{ "hooks": { ... } }</c> with Skarp's handlers only (the
    /// snippet printed for a file Skarp refuses, and the content of a new file).</summary>
    public static JsonObject Snippet(HookSpec spec) => Install([], spec).Result;
}
