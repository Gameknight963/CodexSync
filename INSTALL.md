# Install CodexSync

## Windows (Powershell)

#### Framework-dependent

Use this if you already have .NET 10 installed.

```powershell
& ([scriptblock]::Create((Invoke-RestMethod 'https://raw.githubusercontent.com/Gameknight963/CodexSync/master/install.ps1')))
```

#### Self-contained


```powershell
& ([scriptblock]::Create((Invoke-RestMethod 'https://raw.githubusercontent.com/Gameknight963/CodexSync/master/install.ps1'))) -SelfContained
```

Append `-Prerelease` to include prereleases, or `-Version v0.1.0` to select a tag.

The executable goes in `%LOCALAPPDATA%/CodexSync/bin`, which is added to your user PATH. The skill goes in `%USERPROFILE%/.agents/skills/codexsync-setup/SKILL.md`. Reopen other terminals to pick up PATH changes.

## Linux

First install `curl` and `jq` using your distribution's package manager.

#### Framework-dependent

Use if you already have .NET 10 installed.


```bash
curl -fsSL https://raw.githubusercontent.com/Gameknight963/CodexSync/master/install.sh | bash
```

#### Self-contained:

```bash
curl -fsSL https://raw.githubusercontent.com/Gameknight963/CodexSync/master/install.sh | bash -s -- --self-contained
```

Add `--prerelease` to also include prerleases. Use `--version v0.1.0` to select a tag.

The executable goes in `~/.local/bin`; the skill goes in `~/.agents/skills/codexsync-setup/SKILL.md`. The installer adds PATH configuration to `.profile` and the Bash or Zsh startup files where applicable. Open a new terminal afterward.

## Updates and customization

Rerun the same command to update. Existing archive configuration, mappings, and project selections should be preserved. Restart Codex to load the setup skill, then ask it to configure CodexSync.

Custom destinations are supported with `-InstallDir`, `-SkillDir`, and `-NoPath` on Windows, or `--install-dir`, `--skill-dir`, and `--no-path` on Linux. Linux destinations must be absolute paths. Skill destinations refer to the `codexsync-setup` folder itself.

If release assets are missing, the installer reports that the release may still be building. Wait for the **Release assets** workflow to finish, then retry.
