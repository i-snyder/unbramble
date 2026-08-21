# UnBramble

<p align="center">
  <img src="docs/assets/unbramble-emblem.png" alt="UnBramble mouse peeking over an UnBramble sign" width="560">
</p>

*UnBramble makes fully agentic development practical when a large Unity project's asset and code relationships have grown into a thicket.*

It gives coding agents the project-wide dependency context they need to trace the impact of a change before editing, including Unity asset GUID relationships and Roslyn-resolved C# relationships that ordinary code search misses. The graph is built locally and deterministically, with no AI calls or agent-driven indexing, and developers can query the same data directly through a simple CLI.

## Quick Start

1. Download `unbramble-win-x64.zip` from the [latest release](https://github.com/i-snyder/unbramble/releases/latest).
2. Create a folder you'll keep, such as `C:\Users\your-name\Apps\UnBramble`, and extract the ZIP into it.
3. Add your UnBramble folder to your user PATH.
4. Open a new terminal at the root of a Unity project and run:

   ```powershell
   unbramble
   ```

UnBramble will walk you through setup and grow its index. Setup adds a small managed instruction block to the project so compatible coding agents know UnBramble is available and when to query it. After that, work with your agent normally. It can find its way through the project whenever it needs dependency context, without you manually mapping connections for every session.

See [installing](docs/installing.md) for checksum verification, upgrades, and uninstalling. Prefer to inspect and compile it yourself? See [building from source](docs/building.md).

## Built from real Unity work

I've worked professionally in games since 2008 and with Unity since 2011. I'm a Unity Certified Instructor, and I've worked with teams ranging from AAA studios to indies. I built UnBramble because I wanted to bring a fully agentic workflow to the most complex Unity project I've worked with, where even small changes can cross dense asset and code relationships.

That project has ~114k files and ~750k dependency links. UnBramble indexes it from scratch in ~90 seconds, then keeps the graph current in real time as files change.

Its design, known gaps, and tests are open for inspection; see the [architecture](docs/architecture.md) and [technical comparison](docs/comparison.md) for nerd lore.

## Command map

Hand the map to your agent or query it yourself:

- `unbramble who-uses <target>` — find what references an asset, file, GUID, type, or member.
- `unbramble uses <target>` — find what an asset or file depends on.
- `unbramble dead-candidates` — screen for conservatively identified removal candidates.
- `unbramble --help` — see every path through the hedge.

Bug reports and focused contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a proposal or pull request, please :)

*All the best,*<br>
*Ian*
