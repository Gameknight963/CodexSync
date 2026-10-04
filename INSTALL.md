# Install CodexSync

The installers download the runtime-dependent x64 executable and setup skill from the same GitHub release by default. This requires the .NET 10 runtime. Select the self-contained option to include the runtime in the download. No administrator/root access is required. Git and Codex are still needed to use CodexSync.

## Windows

Run in PowerShell:

```powershell
& ([scriptblock]::Create((Invoke-RestMethod 'https://raw.githubusercontent.com/Gameknight963/CodexSync/master/install.ps1')))
```

Append `-Prerelease` to include prereleases, or `-Version v0.1.0` to select a tag. While only prereleases exist, one of these is required.

Append `-SelfContained` to download the larger build that includes the .NET runtime. These options can be combined.

The executable goes in `%LOCALAPPDATA%/CodexSync/bin`, which is added to your user PATH. The skill goes in `%USERPROFILE%/.agents/skills/codexsync-setup/SKILL.md`. Reopen other terminals to pick up PATH changes.

## Linux

Install `curl` and `jq` using your distribution's package manager, then run:

```bash
curl -fsSL https://raw.githubusercontent.com/Gameknight963/CodexSync/master/install.sh | bash
```

To include prereleases:

```bash
curl -fsSL https://raw.githubusercontent.com/Gameknight963/CodexSync/master/install.sh | bash -s -- --prerelease
```

Use `--version v0.1.0` instead of `--prerelease` to select a tag.

Add `--self-contained` to download the larger build that includes the .NET runtime. For example, pass `bash -s -- --prerelease --self-contained` at the end of the download command.

The executable goes in `~/.local/bin`; the skill goes in `~/.agents/skills/codexsync-setup/SKILL.md`. The installer adds PATH configuration to `.profile` and the Bash or Zsh startup files where applicable. Open a new terminal afterward.

## Updates and customization

Rerun the same command to update. Existing archive configuration, mappings, and project selections are preserved. Restart Codex to load the setup skill, then ask it to configure CodexSync. Installation does not run sync.

Custom destinations are supported with `-InstallDir`, `-SkillDir`, and `-NoPath` on Windows, or `--install-dir`, `--skill-dir`, and `--no-path` on Linux. Linux destinations must be absolute paths. Skill destinations refer to the `codexsync-setup` folder itself.

If release assets are missing, the installer reports that the release may still be building. Wait for the **Release assets** workflow to finish, then retry.
