# UnBramble

<p align="center">
  <img src="docs/assets/unbramble-emblem.png" alt="UnBramble mouse walking through a pastoral bramble hedge" width="560">
</p>

*Give your coding agent the map you wish you had.*

In a complex Unity project, the real architecture is scattered across C#, scenes, prefabs, materials, Shader Graphs, Addressables, UnityEvents, packages, and GUIDs. No one keeps every connection in their head — and an agent starting fresh sees even less.

UnBramble gives coding agents a current, queryable map of how the project fits together. They can trace what a change will touch, discover connections that grep and ordinary code search miss, and make better decisions without you walking them through the project first.

## Get started

Install UnBramble on Windows with WinGet:

```powershell
winget install --exact --id i-snyder.unbramble
```

Open a terminal at the root of a Unity project and run:

```powershell
unbramble
```

UnBramble will walk you through setup and grow its index. From there, `unbramble --help` shows every path through the hedge.

Setup also adds a small managed instruction block to the project so compatible coding agents know UnBramble is available and when to query it. After that, ask your agent to work normally — you don't need to reintroduce the tool or manually map dependencies for every new session.

UnBramble is self-contained; you don't need .NET or a running Unity Editor. See [installing](docs/installing.md) for upgrades, uninstalling, and the manual ZIP option. Prefer to inspect and compile it yourself? See [building from source](docs/building.md).

## Why UnBramble?

The hard part of agentic work in Unity is rarely generating code. It's understanding what the change is connected to before touching it.

| Without project-wide dependency awareness | With UnBramble |
| --- | --- |
| Project context must be reconstructed from code search and grep | The agent can query one graph across C# and Unity assets |
| Hidden serialized connections are easy to miss | GUIDs, Shader Graphs, UI Toolkit, Addressables, UnityEvents, asmdefs, managed plugins, and package assets are modeled explicitly |
| The developer has to explain where systems connect | The agent can trace direct and transitive impact on demand |
| “No text match” can look like “unused” | Conservative reachability results include confidence and blind spots |
| Index freshness is another assumption | Every query verifies freshness before answering |

UnBramble doesn't replace the Unity Editor or your IDE. It adds cross-project dependency awareness through a standalone, machine-readable interface while you keep using those tools for what they do best.

See the [technical comparison](docs/comparison.md) for the exact reference forms, sourced tool-by-tool notes, and the limits behind these claims.

## What agents can ask

| When you want to know… | Start here | What you get |
| --- | --- | --- |
| What touches this file, asset, type, or method? | `unbramble who-uses <target>` | Reverse references across assets and C#, direct or transitive |
| What does this asset depend on? | `unbramble uses <target>` | Forward references, direct or transitive |
| Which references are broken? | `unbramble uses <target> --missing-only` | Missing GUID and path references with useful Unity context |
| What may be safe to prune? | `unbramble dead-candidates` | Conservatively screened, unreachable asset and C# candidates with blind spots stated plainly |
| Is the graph still fresh? | Just run a query | Every query checks freshness; a background watcher keeps the common path quick |
| Can an agent consume this reliably? | Use `--json` (`--jsonl` for batch queries) | Stable machine-readable results, non-interactive setup, and explicit confidence |

When full semantic C# analysis isn't available, UnBramble says so and degrades conservatively instead of pretending the answer is complete.

`dead-candidates` is conservative static analysis, not permission to delete blindly. Read its blind-spots footer and use the documented delete-batch → smoke-test workflow.

Design, guarantees, and known gaps live in [`docs/architecture.md`](docs/architecture.md).

Bug reports and focused contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a proposal or pull request.
