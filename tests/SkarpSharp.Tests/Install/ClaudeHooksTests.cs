using System.Text.Json.Nodes;
using SkarpSharp.Install;
using SkarpSharp.Setup;

namespace SkarpSharp.Tests.Install;

/// <summary>
/// Skarp's hook entries in a Claude Code settings tree, as
/// pure functions over made-up fixtures. The gate tests: install then remove restores each fixture byte for byte, a
/// second install changes nothing, and foreign keys and handlers are left as they are.
/// </summary>
public sealed class ClaudeHooksTests
{
    const string Empty = "{}\n";

    /// <summary>Line feeds whatever the checkout did to this file.</summary>
    static string N(string s) => s.Replace("\r\n", "\n");

    static readonly string OtherEvents = N("""
        {
          "hooks": {
            "PreToolUse": [
              {
                "matcher": "Bash",
                "hooks": [
                  {
                    "type": "command",
                    "command": "node",
                    "args": [
                      "guard.js"
                    ]
                  }
                ]
              }
            ],
            "PostToolUse": [
              {
                "matcher": "Write",
                "hooks": [
                  {
                    "type": "command",
                    "command": "npx prettier --write \"$CLAUDE_FILE\""
                  }
                ]
              }
            ],
            "Notification": [
              {
                "hooks": [
                  {
                    "type": "command",
                    "command": "notify-send Claude"
                  }
                ]
              }
            ]
          }
        }

        """);

    static readonly string PermissionsAndSchema = N("""
        {
          "$schema": "https://json.schemastore.org/claude-code-settings.json",
          "permissions": {
            "allow": [
              "Bash(dotnet test:*)",
              "Read(~/.zshrc)"
            ],
            "deny": []
          },
          "env": {
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1"
          },
          "model": "opus"
        }

        """);

    static readonly string ShellFormSkarp = N("""
        {
          "hooks": {
            "PostToolUse": [
              {
                "matcher": "Edit|Write|MultiEdit",
                "hooks": [
                  {
                    "type": "command",
                    "command": "skarp check --hook claude --min-severity notice"
                  }
                ]
              }
            ]
          }
        }

        """);

    static readonly string SharedGroup = N("""
        {
          "hooks": {
            "PostToolUse": [
              {
                "matcher": "Edit|Write",
                "hooks": [
                  {
                    "type": "command",
                    "command": "dotnet",
                    "args": [
                      "format",
                      "whitespace"
                    ]
                  },
                  {
                    "type": "command",
                    "command": "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe",
                    "args": [
                      "check",
                      "--hook",
                      "claude"
                    ],
                    "timeout": 30
                  }
                ]
              }
            ],
            "Stop": [
              {
                "hooks": [
                  {
                    "type": "command",
                    "command": "say done"
                  }
                ]
              }
            ]
          }
        }

        """);

    static readonly string RepositoryToday = N("""
        {
          "hooks": {
            "PostToolUse": [
              {
                "matcher": "Edit|Write|MultiEdit",
                "hooks": [
                  {
                    "type": "command",
                    "command": "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe",
                    "args": ["check", "--hook", "claude"],
                    "timeout": 30
                  }
                ]
              }
            ]
          }
        }

        """);

    static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");
    static string Tabs(string s) => string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => { int n = l.Length - l.TrimStart(' ').Length; return new string('\t', n / 2) + l.TrimStart(' '); }));

    /// <summary>Every fixture, canonical (as Claude Code and Skarp render JSON: 2 spaces, LF), plus CRLF and tab copies.</summary>
    internal static IEnumerable<(string Name, string Text)> Fixtures()
    {
        (string, string)[] plain = [("empty", Empty), ("other-events", OtherEvents), ("permissions-schema", PermissionsAndSchema), ("shell-form-skarp", ShellFormSkarp), ("shared-group", SharedGroup)];
        foreach (var f in plain) yield return f;
        yield return ("other-events-crlf", Crlf(OtherEvents));
        yield return ("permissions-tabs", Tabs(PermissionsAndSchema));
        yield return ("shared-group-crlf-tabs", Crlf(Tabs(SharedGroup)));
    }

    static JsonObject Tree(string text) => (JsonObject)JsonFile.Parse(text, strict: true)!;

    static string Render(string original, JsonObject root)
    {
        var (c, size) = JsonFile.DetectIndent(original);
        return JsonFile.Render(root, c, size, JsonFile.DetectNewLine(original));
    }

    static string Install(string text, HookSpec? spec = null) => Render(text, ClaudeHooks.Install(Tree(text), spec ?? new HookSpec()).Result);
    static string Remove(string text) => Render(text, ClaudeHooks.Remove(Tree(text)).Result);

    // ---- gate tests ----------------------------------------------------------------------------------------

    [Test]
    public async Task Fixtures_are_canonical_so_a_byte_comparison_means_something()
    {
        foreach (var (name, text) in Fixtures())
            await Assert.That(Render(text, Tree(text))).IsEqualTo(text).Because(name);
    }

    [Test]
    public async Task Install_then_remove_restores_each_fixture_byte_for_byte()
    {
        foreach (var (name, text) in Fixtures())
        {
            // A fixture that already holds a Skarp entry comes back without it: the same as removing from the original.
            var expected = ClaudeHooks.Find(Tree(text)).Count == 0 ? text : Remove(text);
            await Assert.That(Remove(Install(text))).IsEqualTo(expected).Because(name);
            await Assert.That(Remove(Install(text, new HookSpec { Gate = true, Semantic = true, MaxLines = 8, Notice = true }))).IsEqualTo(expected).Because(name + " with options");
        }
    }

    [Test]
    public async Task A_second_install_changes_nothing()
    {
        foreach (var (name, text) in Fixtures())
            foreach (var spec in new[] { new HookSpec(), new HookSpec { Stop = false }, new HookSpec { Command = "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe", CommandGiven = true } })
            {
                var once = Install(text, spec);
                var (_, changes) = ClaudeHooks.Install(Tree(once), spec);
                await Assert.That(changes.All(c => c.Kind == HookChangeKind.Unchanged)).IsTrue().Because(name);
                await Assert.That(Install(once, spec)).IsEqualTo(once).Because(name);
            }
    }

    [Test]
    public async Task Foreign_keys_and_handlers_are_unchanged()
    {
        foreach (var (name, text) in Fixtures())
        {
            var installed = Tree(Install(text));
            var original = Tree(text);
            // Every top-level key other than hooks is the same, in the same order.
            await Assert.That(installed.Where(p => p.Key != "hooks").Select(p => p.Key).ToList()).IsEquivalentTo(original.Where(p => p.Key != "hooks").Select(p => p.Key).ToList());
            foreach (var (key, value) in original.Where(p => p.Key != "hooks"))
                await Assert.That(JsonNode.DeepEquals(value, installed[key])).IsTrue().Because($"{name}: {key}");
            // The non-Skarp handlers, event by event, in order.
            await Assert.That(Foreign(installed)).IsEqualTo(Foreign(original)).Because(name);
            // Events Skarp does not write keep their place and content.
            if (original["hooks"] is JsonObject hooks)
                foreach (var (ev, groups) in hooks.Where(p => p.Key is not (ClaudeHooks.PostToolUse or ClaudeHooks.Stop)))
                    await Assert.That(JsonNode.DeepEquals(groups, installed["hooks"]![ev])).IsTrue().Because($"{name}: {ev}");
        }
    }

    static string Foreign(JsonObject root)
    {
        var lines = new List<string>();
        if (root["hooks"] is JsonObject hooks)
            foreach (var (ev, groups) in hooks)
                foreach (var g in groups!.AsArray())
                    foreach (var h in g!["hooks"]!.AsArray())
                        if (!ClaudeHooks.IsSkarp(h)) lines.Add($"{ev}: {h!.ToJsonString()}");
        return string.Join("\n", lines);
    }

    // ---- install -------------------------------------------------------------------------------------------

    [Test]
    public async Task Empty_file_gets_the_three_entries_with_the_matcher()
    {
        var (_, changes) = ClaudeHooks.Install([], new HookSpec());
        await Assert.That(changes.Select(c => $"{c.Event} {c.Kind}")).IsEquivalentTo(["PostToolUse Add", "Stop Add", "PostCompact Add"]);
        await Assert.That(JsonFile.Render(ClaudeHooks.Snippet(new HookSpec()))).IsEqualTo(Install(Empty));
        var root = Tree(Install(Empty));
        await Assert.That(root["hooks"]!["PostToolUse"]![0]!["matcher"]!.GetValue<string>()).IsEqualTo("Edit|Write");
        await Assert.That(root["hooks"]!["Stop"]![0]!.AsObject().ContainsKey("matcher")).IsFalse();
        await Assert.That(root["hooks"]!["Stop"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>()).IsEqualTo(60);
        // PostCompact, no matcher, the same command and arguments.
        await Assert.That(root["hooks"]!["PostCompact"]![0]!.ToJsonString()).IsEqualTo("""{"hooks":[{"type":"command","command":"skarp","args":["check","--hook","claude"],"timeout":30}]}""");
    }

    [Test]
    public async Task No_compact_leaves_out_or_removes_the_PostCompact_entry_and_a_reinstall_adds_it()
    {
        var (_, changes) = ClaudeHooks.Install([], new HookSpec { Compact = false });
        await Assert.That(changes.Select(c => $"{c.Event} {c.Kind}")).IsEquivalentTo(["PostToolUse Add", "Stop Add"]);
        var full = Tree(Install(Empty));
        var (without, removed) = ClaudeHooks.Install(full, new HookSpec { Compact = false });
        await Assert.That(removed.Select(c => $"{c.Event} {c.Kind}")).IsEquivalentTo(["PostToolUse Unchanged", "Stop Unchanged", "PostCompact Remove"]);
        await Assert.That(without["hooks"]!.AsObject().ContainsKey("PostCompact")).IsFalse();
        await Assert.That(HookEntry.Read(without)!.Spec.Compact).IsFalse();
        // An earlier install (no PostCompact entry) gets it on the next install.
        var (again, added) = ClaudeHooks.Install(without, new HookSpec());
        await Assert.That(added.Select(c => $"{c.Event} {c.Kind}")).IsEquivalentTo(["PostToolUse Unchanged", "Stop Unchanged", "PostCompact Add"]);
        await Assert.That(JsonFile.Render(again)).IsEqualTo(Install(Empty));
        var (gone, removeChanges) = ClaudeHooks.Remove(again);
        await Assert.That(removeChanges.Select(c => $"{c.Event} {c.Kind}")).IsEquivalentTo(["PostToolUse Remove", "Stop Remove", "PostCompact Remove"]);
        await Assert.That(gone.ContainsKey("hooks")).IsFalse();
    }

    [Test]
    public async Task Other_events_keep_their_place_and_a_new_group_is_appended()
    {
        var root = Tree(Install(OtherEvents));
        var hooks = root["hooks"]!.AsObject();
        await Assert.That(hooks.Select(p => p.Key).ToList()).IsEquivalentTo(["PreToolUse", "PostToolUse", "Notification", "Stop", "PostCompact"]);
        var post = hooks["PostToolUse"]!.AsArray();
        await Assert.That(post.Count).IsEqualTo(2);
        await Assert.That(post[0]!["matcher"]!.GetValue<string>()).IsEqualTo("Write");
        await Assert.That(post[1]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("skarp");
    }

    [Test]
    public async Task Shell_form_skarp_is_replaced_in_place_by_exec_form_with_the_new_matcher_and_include_notices()
    {
        var (result, changes) = ClaudeHooks.Install(Tree(ShellFormSkarp), new HookSpec());
        await Assert.That(changes[0].Kind).IsEqualTo(HookChangeKind.Update);
        await Assert.That(ClaudeHooks.Describe("PostToolUse", changes[0].Old!, changes[0].OldGroup)).IsEqualTo("skarp check --hook claude --min-severity notice (shell form) (matcher Edit|Write|MultiEdit)");
        var group = result["hooks"]!["PostToolUse"]!.AsArray();
        await Assert.That(group.Count).IsEqualTo(1);
        // The old --min-severity notice becomes --include-notices.
        await Assert.That(group[0]!.ToJsonString()).IsEqualTo("""{"matcher":"Edit|Write","hooks":[{"type":"command","command":"skarp","args":["check","--hook","claude","--include-notices"],"timeout":30}]}""");
    }

    [Test]
    public async Task A_shared_group_keeps_its_other_handler_and_matcher()
    {
        var spec = new HookSpec { Command = "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe", CommandGiven = true, Gate = true };
        var (result, changes) = ClaudeHooks.Install(Tree(SharedGroup), spec);
        await Assert.That(changes.Select(c => $"{c.Event} {c.Kind}")).IsEquivalentTo(["PostToolUse Update", "Stop Add", "PostCompact Add"]);
        var handlers = result["hooks"]!["PostToolUse"]![0]!["hooks"]!.AsArray();
        await Assert.That(handlers.Count).IsEqualTo(2);
        await Assert.That(handlers[0]!["command"]!.GetValue<string>()).IsEqualTo("dotnet");
        await Assert.That(handlers[1]!["args"]!.ToJsonString()).IsEqualTo("""["check","--hook","claude","--gate"]""");
        // The foreign Stop handler stays first; Skarp's group is appended.
        await Assert.That(result["hooks"]!["Stop"]!.AsArray().Count).IsEqualTo(2);
    }

    [Test]
    public async Task This_repositorys_entry_becomes_the_installed_form_in_place()
    {
        var spec = new HookSpec { Command = "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe", CommandGiven = true };
        var text = Install(RepositoryToday, spec);
        await Assert.That(text).IsEqualTo(N("""
            {
              "hooks": {
                "PostToolUse": [
                  {
                    "matcher": "Edit|Write",
                    "hooks": [
                      {
                        "type": "command",
                        "command": "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe",
                        "args": [
                          "check",
                          "--hook",
                          "claude"
                        ],
                        "timeout": 30
                      }
                    ]
                  }
                ],
                "Stop": [
                  {
                    "hooks": [
                      {
                        "type": "command",
                        "command": "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe",
                        "args": [
                          "check",
                          "--hook",
                          "claude"
                        ],
                        "timeout": 60
                      }
                    ]
                  }
                ],
                "PostCompact": [
                  {
                    "hooks": [
                      {
                        "type": "command",
                        "command": "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe",
                        "args": [
                          "check",
                          "--hook",
                          "claude"
                        ],
                        "timeout": 30
                      }
                    ]
                  }
                ]
              }
            }

            """));
    }

    [Test]
    public async Task Options_are_appended_to_args_and_semantic_goes_to_stop_only()
    {
        var spec = new HookSpec { Gate = true, Semantic = true, MaxLines = 8, Notice = true };
        await Assert.That(string.Join(" ", spec.Args("PostToolUse"))).IsEqualTo("check --hook claude --gate --max-lines 8 --include-notices");
        await Assert.That(string.Join(" ", spec.Args("Stop"))).IsEqualTo("check --hook claude --gate --semantic --max-lines 8 --include-notices");
    }

    [Test]
    public async Task No_stop_removes_an_existing_skarp_stop_entry_only()
    {
        var installed = Install(SharedGroup);
        var (result, changes) = ClaudeHooks.Install(Tree(installed), new HookSpec { Stop = false });
        await Assert.That(changes.Select(c => $"{c.Event} {c.Kind}")).IsEquivalentTo(["PostToolUse Unchanged", "Stop Remove", "PostCompact Unchanged"]);
        var stop = result["hooks"]!["Stop"]!.AsArray();
        await Assert.That(stop.Count).IsEqualTo(1);
        await Assert.That(stop[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("say done");
    }

    [Test]
    public async Task Extra_skarp_handlers_of_an_event_are_folded_into_the_first()
    {
        var two = """{"hooks":{"PostToolUse":[{"matcher":"Edit","hooks":[{"type":"command","command":"skarp","args":["check","--hook","claude"]}]},{"matcher":"Write","hooks":[{"type":"command","command":"skarp.exe","args":["check","--hook","claude","--gate"]}]}]}}""";
        var (result, _) = ClaudeHooks.Install(Tree(two), new HookSpec { Stop = false });
        await Assert.That(result["hooks"]!["PostToolUse"]!.ToJsonString()).IsEqualTo("""[{"matcher":"Edit|Write","hooks":[{"type":"command","command":"skarp","args":["check","--hook","claude"],"timeout":30}]}]""");
    }

    [Test]
    public async Task A_hooks_value_of_the_wrong_shape_is_refused()
    {
        await Assert.That(() => ClaudeHooks.Install(Tree("""{"hooks":[]}"""), new HookSpec())).Throws<HookShapeException>();
        await Assert.That(() => ClaudeHooks.Install(Tree("""{"hooks":{"Stop":{}}}"""), new HookSpec())).Throws<HookShapeException>();
        await Assert.That(() => ClaudeHooks.Remove(Tree("""{"hooks":null}"""))).Throws<HookShapeException>();
    }

    // ---- remove -----------------------------------------------------------------------------------------------------

    [Test]
    public async Task Remove_drops_empty_groups_events_and_hooks_but_never_other_keys()
    {
        var installed = Install(PermissionsAndSchema);
        var removed = Remove(installed);
        await Assert.That(removed).IsEqualTo(PermissionsAndSchema);
        // A shared group keeps its other handler.
        var shared = Tree(Remove(SharedGroup));
        await Assert.That(shared["hooks"]!["PostToolUse"]![0]!["hooks"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(Remove(Empty)).IsEqualTo(Empty);
    }

    // ---- recognizing ------------------------------------------------------------------------------------------------

    [Test]
    [Arguments("""{"type":"command","command":"skarp","args":["check","--hook","claude"]}""", null, true)]
    [Arguments("""{"type":"command","command":"skarp.exe","args":["check","--hook","claude","--gate"]}""", null, true)]
    [Arguments("""{"type":"command","command":"${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe","args":["check","--hook","claude"]}""", null, true)]
    [Arguments("""{"type":"command","command":"C:\\tools\\Skarp.EXE","args":["check","--hook","claude"]}""", null, true)]
    [Arguments("""{"type":"command","command":"/usr/local/bin/skarp","args":["check","--hook","claude"]}""", null, true)]
    [Arguments("""{"type":"command","command":"skarp check --hook claude"}""", null, true)]
    [Arguments("""{"type":"command","command":"\"C:/Program Files/skarp/skarp.exe\" check --hook claude --gate"}""", null, true)]
    [Arguments("""{"type":"command","command":"tools/sk","args":["check","--hook","claude"]}""", "tools/sk", true)]
    [Arguments("""{"type":"command","command":"tools/sk","args":["check","--hook","claude"]}""", null, false)]
    [Arguments("""{"type":"command","command":"skarp","args":["analyze","."]}""", null, false)]
    [Arguments("""{"type":"command","command":"skarp","args":["check","--hook"]}""", null, false)]
    [Arguments("""{"type":"command","command":"skarpish","args":["check","--hook","claude"]}""", null, false)]
    [Arguments("""{"type":"prompt","command":"skarp","args":["check","--hook","claude"]}""", null, false)]
    [Arguments("""{"type":"command","command":"skarp","args":"check --hook claude"}""", null, false)]
    [Arguments("""{"type":"command","command":"echo skarp check --hook claude"}""", null, false)]
    public async Task Recognizes_skarp_handlers_only(string json, string? given, bool expected)
    {
        await Assert.That(ClaudeHooks.IsSkarp(JsonNode.Parse(json), given)).IsEqualTo(expected);
    }

    [Test]
    public async Task Identical_means_equal_command_and_args_after_project_dir_expansion()
    {
        var a = Tree("""{"type":"command","command":"${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe","args":["check","--hook","claude"],"timeout":30}""");
        var b = Tree("""{"type":"command","command":"D:\\repo/.skarp/bin/skarp.exe","args":["check","--hook","claude"],"timeout":99}""");
        var c = Tree("""{"type":"command","command":"D:\\repo/.skarp/bin/skarp.exe","args":["check","--hook","claude","--gate"]}""");
        await Assert.That(ClaudeHooks.Key(a, @"D:\repo")).IsEqualTo(ClaudeHooks.Key(b, @"D:\repo"));
        await Assert.That(ClaudeHooks.Key(a, @"D:\repo")).IsNotEqualTo(ClaudeHooks.Key(c, @"D:\repo"));
    }

    // ---- read back, kept options, notices off -------------------------------------------------------------

    [Test]
    public async Task An_entry_reads_back_into_the_spec_that_writes_it()
    {
        foreach (var spec in new[]
        {
            new HookSpec { Notice = false },
            new HookSpec { Command = "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe", CommandGiven = true, Gate = true, Semantic = true, MaxLines = 8, Notice = true },
            new HookSpec { Stop = false, Gate = true, Notice = false },
        })
        {
            var entry = HookEntry.Read(Tree(Install(Empty, spec)))!;
            await Assert.That(entry.Spec).IsEqualTo(spec);
            await Assert.That(entry.Kept).IsEmpty();
        }
        await Assert.That(HookEntry.Read(Tree(OtherEvents))).IsNull();
        // The old shell form: --min-severity notice reads as notices on.
        var shell = HookEntry.Read(Tree(ShellFormSkarp))!;
        await Assert.That(shell.Spec).IsEqualTo(new HookSpec { Stop = false, Compact = false, Notice = true });
    }

    [Test]
    public async Task Options_added_by_hand_are_kept_in_their_order_after_the_ones_skarp_writes()
    {
        var hand = """{"hooks":{"PostToolUse":[{"matcher":"Edit|Write","hooks":[{"type":"command","command":"skarp","args":["check","--hook","claude","--no-save","--gate","--min-severity","warning","--gate-user-config","--max-lines","x"],"timeout":30}]}]}}""";
        var entry = HookEntry.Read(Tree(hand))!;
        await Assert.That(entry.Spec).IsEqualTo(new HookSpec { Stop = false, Compact = false, Gate = true, Notice = false });
        // --min-severity warning is gone (check no longer takes it); a --max-lines without a number is kept as it is.
        await Assert.That(entry.Kept).IsEquivalentTo(new[] { "--no-save", "--gate-user-config", "--max-lines", "x" }, CollectionOrdering.Matching);
        var (result, _) = ClaudeHooks.Install(Tree(hand), new HookSpec { Semantic = true, Notice = true });
        await Assert.That(result["hooks"]!["PostToolUse"]![0]!["hooks"]![0]!["args"]!.ToJsonString())
            .IsEqualTo("""["check","--hook","claude","--include-notices","--no-save","--gate-user-config","--max-lines","x"]""");
        // The new Stop entry has no old handler, so nothing to keep there.
        await Assert.That(result["hooks"]!["Stop"]![0]!["hooks"]![0]!["args"]!.ToJsonString()).IsEqualTo("""["check","--hook","claude","--semantic","--include-notices"]""");
        // A second install with the same spec changes nothing.
        var (_, again) = ClaudeHooks.Install(result, new HookSpec { Semantic = true, Notice = true });
        await Assert.That(again.All(c => c.Kind == HookChangeKind.Unchanged)).IsTrue();
    }

    [Test]
    public async Task Notices_stay_without_a_choice_and_go_with_notices_off()
    {
        var on = Install(Empty, new HookSpec { Notice = true });
        await Assert.That(Install(on, new HookSpec())).IsEqualTo(on);
        await Assert.That(Install(on, new HookSpec { Notice = false })).IsEqualTo(Install(Empty));
        await Assert.That(Install(ShellFormSkarp, new HookSpec { Notice = false })).DoesNotContain("notice");
    }

    // ---- cmd.exe form (Windows, a .cmd shim) ----------------------------------------------------------------

    [Test]
    public async Task Via_cmd_writes_cmd_exe_slash_c_then_the_program_and_its_arguments()
    {
        var root = Tree(Install(Empty, new HookSpec { ViaCmd = true, Gate = true }));
        var handler = root["hooks"]!["PostToolUse"]![0]!["hooks"]![0]!.AsObject();
        await Assert.That(handler["command"]!.GetValue<string>()).IsEqualTo("cmd.exe");
        await Assert.That(handler["args"]!.AsArray().Select(a => a!.GetValue<string>()))
            .IsEquivalentTo(["/c", "skarp", "check", "--hook", "claude", "--gate"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_cmd_wrapped_entry_is_recognized_read_back_and_unchanged_by_a_second_install()
    {
        var spec = new HookSpec { ViaCmd = true, Gate = true, MaxLines = 7 };
        var installed = Install(Empty, spec);
        var root = Tree(installed);
        await Assert.That(ClaudeHooks.Find(root).Count).IsEqualTo(3);
        var entry = HookEntry.Read(root)!;
        await Assert.That((entry.Spec.ViaCmd, entry.Spec.Command, entry.Spec.CommandGiven, entry.Spec.Gate, entry.Spec.MaxLines)).IsEqualTo((true, "skarp", false, true, 7));
        await Assert.That(Install(installed, spec)).IsEqualTo(installed);
        var (_, again) = ClaudeHooks.Install(root, spec);
        await Assert.That(again.All(c => c.Kind == HookChangeKind.Unchanged)).IsTrue();
    }

    [Test]
    public async Task Switching_between_the_plain_and_the_cmd_form_is_an_update_that_keeps_the_options()
    {
        var plain = Install(Empty, new HookSpec { Gate = true });
        var (wrapped, toCmd) = ClaudeHooks.Install(Tree(plain), new HookSpec { Gate = true, ViaCmd = true });
        await Assert.That(toCmd.Select(c => c.Kind)).IsEquivalentTo([HookChangeKind.Update, HookChangeKind.Update, HookChangeKind.Update]);
        var back = Render(plain, ClaudeHooks.Install(wrapped, new HookSpec { Gate = true }).Result);
        await Assert.That(back).IsEqualTo(plain);
    }

    [Test]
    public async Task The_plain_and_the_wrapped_entry_are_different_commands_and_cmd_with_another_program_is_not_skarp()
    {
        var plain = Tree(Install(Empty))["hooks"]!["PostToolUse"]![0]!["hooks"]![0]!.AsObject();
        var wrapped = Tree(Install(Empty, new HookSpec { ViaCmd = true }))["hooks"]!["PostToolUse"]![0]!["hooks"]![0]!.AsObject();
        await Assert.That(ClaudeHooks.Key(plain, "/p")).IsNotEqualTo(ClaudeHooks.Key(wrapped, "/p"));
        await Assert.That(ClaudeHooks.IsCmdWrapped(plain)).IsFalse();
        await Assert.That(ClaudeHooks.IsCmdWrapped(wrapped)).IsTrue();
        var other = Tree("""{ "type": "command", "command": "cmd.exe", "args": ["/c", "tool", "check", "--hook", "claude"] }""");
        await Assert.That(ClaudeHooks.IsSkarp(other)).IsFalse();
        await Assert.That(ClaudeHooks.IsSkarp(other, "tool")).IsTrue();
    }
}
