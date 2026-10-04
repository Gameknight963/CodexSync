param([switch] $SelfContained, [string] $Version)

$ErrorActionPreference = 'Stop'
# These checks write normal installation destinations and PATH on disposable CI runners.
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run this test on a disposable GitHub Actions runner.' }
$api = 'https://api.github.com/repos/gameknight963/codexsync/releases'
if ($Version) {
    $release = Invoke-RestMethod "$api/tags/$([Uri]::EscapeDataString($Version))"
} else {
    $releases = Invoke-RestMethod "${api}?per_page=100"
    $release = $releases | Where-Object { !$_.draft } | Select-Object -First 1
}
if (!$release) { throw 'No published release is available for the live installer test.' }
$tag = $release.tag_name
$suffix = if ($SelfContained) { '-selfcontained' } else { '' }

if ($IsWindows) {
    $binaryName = "codexsync$suffix.exe"
    $installPath = Join-Path $env:LOCALAPPDATA 'CodexSync/bin'
    $skillPath = Join-Path $env:USERPROFILE '.agents/skills/codexsync-setup/SKILL.md'
    $installedExecutable = Join-Path $installPath 'codexsync.exe'
    # Exercise the same scriptblock invocation used by the one-command installer.
    $installer = [scriptblock]::Create((Get-Content install.ps1 -Raw))
    & $installer -Version $tag -SelfContained:$SelfContained
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User') -split ';'
    if ($installPath -notin $userPath) { throw 'Installer did not persist the user PATH entry.' }
    if ((Get-Command codexsync).Source -ne $installedExecutable) { throw 'Installed executable is not available on PATH.' }
} else {
    $binaryName = "codexsync$suffix"
    $installPath = Join-Path $env:HOME '.local/bin'
    $skillPath = Join-Path $env:HOME '.agents/skills/codexsync-setup/SKILL.md'
    $installedExecutable = Join-Path $installPath 'codexsync'
    $installerArguments = @('-s', '--', '--version', $tag)
    if ($SelfContained) { $installerArguments += '--self-contained' }
    Get-Content install.sh -Raw | & bash @installerArguments
    if ($LASTEXITCODE -ne 0) { throw 'Linux installer failed.' }
    # A fresh login shell must find the executable through persisted PATH setup.
    $resolvedCommand = & bash -lc 'command -v codexsync'
    if ($LASTEXITCODE -ne 0 -or $resolvedCommand -ne $installedExecutable) {
        throw 'Installed executable is not available on PATH in a new login shell.'
    }
    & bash -lc 'codexsync --help'
    if ($LASTEXITCODE -ne 0) { throw 'Installed executable failed to start from a new login shell.' }
}

# Compare installed bytes with the actual assets from this exact release.
$assetDirectory = Join-Path $env:RUNNER_TEMP 'CodexSync-live-expected'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
foreach ($file in @(
    @{ Name = $binaryName; Installed = $installedExecutable },
    @{ Name = 'SKILL.md'; Installed = $skillPath }
)) {
    $asset = $release.assets | Where-Object name -EQ $file.Name | Select-Object -First 1
    if (!$asset) { throw "Release $tag is missing $($file.Name); its assets may still be building." }
    $expected = Join-Path $assetDirectory $file.Name
    Invoke-WebRequest $asset.browser_download_url -OutFile $expected
    if ((Get-FileHash -LiteralPath $expected).Hash -ne (Get-FileHash -LiteralPath $file.Installed).Hash) {
        throw "Installed $($file.Name) does not match its release asset."
    }
}

& .github/scripts/smoke.ps1 -Executable $installedExecutable
Write-Host "Live installation test passed for $tag ($binaryName), including skill installation and PATH setup."
