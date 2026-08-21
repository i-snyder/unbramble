# UnBramble

<p align="center">
  <img src="docs/assets/unbramble-emblem.png" alt="UnBramble mouse peeking over an UnBramble sign" width="560">
</p>

*UnBramble gives coding agents a trustworthy dependency map for complex Unity projects so they can work without getting snagged in the thickets of a real project.*

In a complex Unity project, small changes can have a big impact. No one keeps every connection in their head, and an agent starting fresh sees even less. UnBramble gives coding agents a current, queryable map of how the project fits together so they can accurately trace what a change will touch and discover connections that grep and ordinary code search miss.

## Built from real Unity work

I've worked professionally in games since 2008 and with Unity since 2011. I'm a Unity Certified Instructor, and I've worked with Unity teams ranging from AAA studios to indies.

UnBramble grew out of that experience and my own agentic workflows. It has been tested on real Unity projects, the largest with ~114k files and ~750k dependency links. UnBramble indexes that project from scratch in ~90 seconds, then keeps it current in real time as files change.

Its design, known gaps, and tests are open for inspection; see the [architecture](docs/architecture.md) and [technical comparison](docs/comparison.md) for nerd lore.

## Quick Start

1. Download `unbramble-win-x64.zip` from the [latest release](https://github.com/i-snyder/unbramble/releases/latest).
2. Create a folder you'll keep, such as `C:\Users\your-name\Apps\UnBramble`, and extract the ZIP into it.
3. Add your UnBramble folder to your user PATH.
4. Open a new terminal at the root of a Unity project and run:

   ```powershell
   unbramble
   ```

UnBramble will walk you through setup and grow its index. Setup adds a small managed instruction block to the project so compatible coding agents know UnBramble is available and when to query it. After that, ask your agent to work normally — you don't need to reintroduce the tool or manually map dependencies for every new session.

See [installing](docs/installing.md) for checksum verification, upgrades, and uninstalling. Prefer to inspect and compile it yourself? See [building from source](docs/building.md).

## Command map

- `unbramble who-uses <target>` — find what references an asset, file, type, or member.
- `unbramble uses <target>` — find what an asset or file depends on.
- `unbramble dead-candidates` — screen for conservatively identified removal candidates.
- `unbramble --help` — see every command and option.

Bug reports and focused contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a proposal or pull request, please :)

All the best,
Ian
