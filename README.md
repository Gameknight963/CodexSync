<img src=banner.png width=100%>

# CodexSync

Easily sync selected Codex conversations between machines using Git, even when your project folders have different paths.

CodexSync stores session histories and project associations in a Git repository. Each machine keeps its own checkout paths and folder overrides. When you sync, shared sessions are adapted to those local paths and registered with Codex.

The CLI is written in C# and supports Windows and Linux x64. A Codex skill handles most of the actual mapping, so all you have to do is run the command to sync it.

> If you need CodexSync for another enviornment or architecture, let me know by opening an issue. If you build it yourself without single file it should work anywhere though.

> banner.blend contains the blender file used to render the above banner

## Installation

See [INSTALL.md](INSTALL.md) for one-command Windows and Linux installers.

You will need Git and Codex available on PATH for the tool to function. Git must be able to pull from and push to your archive repository.

## Setup with Codex

The installer also installs the `codexsync-setup` skill. Restart Codex after installation, then ask it to configure CodexSync. For example:

> "Set up CodexSync using my archive at `<archive-folder>` and bind project `MyProject` to `<project-folder>`."

The skill can configure the archive, associate projects with local checkouts, select existing chats, and set exceptional folder overrides. If a folder association is ambiguous, it asks you to choose.

Registering a project does not automatically include its conversations. Ask to include the chats you want. No chats are included automatically, so a conversation created solely to set up syncing stays excluded by default.

The agent only performs setup. **Close Codex before running `codexsync sync` yourself**. It might be fine as long as it's not actively working, but no guarantees.

If you or the agent runs `codexsync sync` while the agent is running, the effects may be anywhere from incomplete conversations to corrupted conversation data.

## Manual usage

### 1. Choose an archive

An archive is a local clone of a Git repository dedicated to shared conversations. It is separate from your project checkouts and Codex's data directory.

Create a repository with an initial commit, then clone it and configure CodexSync:

```bash
git clone <archive-repository-url> <archive-folder>
codexsync archive <archive-folder>
```

Git stores the remote and upstream branch; CodexSync uses those settings. The archive must be the root of its working repository and have a clean working tree before sync.

The archive contains YOUR conversation histories, so I'd recommend making it private lol.

### 2. Bind a project and select chats

```bash
codexsync project add MyProject <local-project-checkout>
codexsync list
codexsync include <session-id> --project MyProject
```

Project identity comes from the checkout's `origin` remote. Equivalent SSH and HTTPS remote forms identify the same project. The local checkout path stays machine-local.

For a conversation whose working directory is below the repository root:

```bash
codexsync include <session-id> --project MyProject --subfolder src/service
```

New projects and folder mappings do not select chats automatically. Existing mappings from older CodexSync versions migrate as included for compatibility.

### 3. Sync

After closing Codex:

```bash
codexsync sync
```

Sync pulls the archive, publishes pending setup changes, exports and imports selected sessions, registers them with Codex, then commits and pushes archive changes.

### 4. Set up another machine

Clone the same archive repository, configure it with `codexsync archive`, and bind each project using the same project name and its local checkout:

```bash
codexsync project add MyProject <checkout-on-this-machine>
```

Selections and project associations travel through the archive. You do not need to include each chat again or create a mapping for every project-associated session. Close Codex and run sync, then reopen Codex to resume the imported conversations.

## Selection and folder overrides

To stop syncing a chat:

```text
codexsync exclude <session-id>
```

Exclusion is shared on the next sync. It retains existing archived and local logs; it does not delete history.

A session-specific folder mapping takes precedence over its project binding:

```text
codexsync map <session-id> <local-folder>
```

For a chat without a Git project, map it first, then run `codexsync include <session-id>`. Other machines need their own folder mapping for that chat.

Setup changes remain private until your next sync publishes them. This keeps setup from making the archive dirty before the next Git pull.

## Commands

| Command | Purpose |
| --- | --- |
| `list [--full-paths]` | Show local sessions, selection status, and folder associations. |
| `projects [--full-paths]` | Show projects and local checkout bindings. |
| `project add <name> <folder>` | Register or bind a project checkout. |
| `include <id> [--project <name>] [--subfolder <folder>]` | Select a conversation for sync. |
| `exclude <id>` | Stop syncing a conversation. |
| `map <id> <folder>` | Set a machine-local session folder override. |
| `archive <folder>` | Configure the archive clone. |
| `archive-path` | Print the configured archive location. |
| `mapping-path` | Print the machine-local mapping file location. |
| `sync` | Pull, synchronize selected sessions, commit, and push. |

`import <session-file>` and `export <session-id> [archive-folder]` are also available for individual transfers. Like sync, these should be run outside an active Codex session.

Run `codexsync --help` for options, including `--mapping-file`, `--codex-home`, and `--sessions-dir`. `CODEX_HOME` is also respected.

## Conflicts

If the same conversation continues independently on two machines, CodexSync preserves the divergent histories as conflict snapshots and reports the conflict. It does not automatically combine their conversation order. Other selected sessions can still be processed.

Git pulls use fast-forward-only updates. If the archive has uncommitted changes or divergent Git commits, resolve those before retrying sync.

## Development

With the .NET 10 SDK installed:

```text
dotnet build
dotnet test
dotnet run --project CodexSync.Cli -- --help
```

`bash pkg.sh` publishes runtime-dependent and self-contained executables for Windows and Linux into `pkg/`, along with the skill file.

GitHub Actions test builds and installers on both platforms. Publishing a GitHub release triggers tested asset uploads: four executables and one shared `SKILL.md`. Assets appear after the release workflow finishes.
