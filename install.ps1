param(
    [switch] $Prerelease,
    [switch] $SelfContained,
    [string] $Version,
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'CodexSync/bin'),
    [string] $SkillDir = (Join-Path $env:USERPROFILE '.agents/skills/codexsync-setup'),
    [switch] $NoPath
)

$ErrorActionPreference = 'Stop'
$api = 'https://api.github.com/repos/gameknight963/codexsync/releases'
$headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'CodexSync-Installer' }

try {
    $architecture = $env:PROCESSOR_ARCHITEW6432
    if (!$architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
    if ($architecture -ne 'AMD64') { throw 'This installer currently supports Windows x64 only.' }
    $installPath = [IO.Path]::GetFullPath($InstallDir)
    $skillPath = [IO.Path]::GetFullPath($SkillDir)
    if ($installPath -eq $skillPath) { throw 'Executable and skill directories must be separate.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    if ($Version) {
        $release = Invoke-RestMethod "$api/tags/$([Uri]::EscapeDataString($Version))" -Headers $headers
    } else {
        $release = $null
        # Follow pages so older stable releases are still found among prereleases.
        for ($page = 1; !$release; $page++) {
            $response = Invoke-RestMethod "${api}?per_page=100&page=$page" -Headers $headers
            $releases = @($response)
            $release = $releases | Where-Object { !$_.draft -and ($Prerelease -or !$_.prerelease) } |
                Select-Object -First 1
            if ($releases.Count -lt 100) { break }
        }
        if (!$release) {
            throw 'No matching release found. If only prereleases exist, rerun with -Prerelease or -Version <tag>.'
        }
    }
    if ($release.draft) { throw 'A draft release cannot be installed.' }
    $binaryName = if ($SelfContained) { 'codexsync-selfcontained.exe' } else { 'codexsync.exe' }
    $binary = @($release.assets | Where-Object name -EQ $binaryName)
    $skill = @($release.assets | Where-Object name -EQ 'SKILL.md')
    if ($binary.Count -ne 1 -or $skill.Count -ne 1) {
        throw "Release $($release.tag_name) is missing required assets. It may still be building; try again after the Release assets workflow finishes."
    }

    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('CodexSync-Install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    try {
        $binaryDownload = Join-Path $temporary 'codexsync.exe'
        $skillDownload = Join-Path $temporary 'SKILL.md'
        Write-Host "Downloading CodexSync $($release.tag_name) ($binaryName)..."
        Invoke-WebRequest $binary[0].browser_download_url -Headers $headers -OutFile $binaryDownload -UseBasicParsing
        Invoke-WebRequest $skill[0].browser_download_url -Headers $headers -OutFile $skillDownload -UseBasicParsing
        if ((Get-Item $binaryDownload).Length -eq 0 -or (Get-Item $skillDownload).Length -eq 0) {
            throw 'A downloaded asset is empty.'
        }
        # Check the download before replacing an existing installation.
        & $binaryDownload --help | Out-Null
        if ($LASTEXITCODE -ne 0) {
            if (!$SelfContained) { throw 'Startup check failed. Install the .NET 10 runtime or rerun with -SelfContained.' }
            throw 'The downloaded executable failed its startup check.'
        }

        New-Item -ItemType Directory -Path $installPath, $skillPath -Force | Out-Null
        Copy-Item -LiteralPath $binaryDownload -Destination (Join-Path $installPath 'codexsync.exe') -Force
        Copy-Item -LiteralPath $skillDownload -Destination (Join-Path $skillPath 'SKILL.md') -Force
    } finally {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ([IO.Path]::GetDirectoryName($resolvedTemporary) -ne $temporaryRoot) {
            throw 'Refusing to remove files outside the installer temporary directory.'
        }
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }

    if (!$NoPath) {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $entries = @($userPath -split ';' | Where-Object { $_ })
        if (!($entries | Where-Object { $_.TrimEnd('\', '/') -ieq $installPath.TrimEnd('\', '/') })) {
            [Environment]::SetEnvironmentVariable('Path', (($entries + $installPath) -join ';'), 'User')
        }
        if (!(($env:Path -split ';') | Where-Object { $_.TrimEnd('\', '/') -ieq $installPath.TrimEnd('\', '/') })) {
            $env:Path = $installPath + ';' + $env:Path
        }
        Write-Host 'PATH configured. Other terminals may need to be reopened.'
    }
    Write-Host "Installed CodexSync $($release.tag_name) to $installPath"
    Write-Host "Installed setup skill to $skillPath"
    Write-Host 'Restart Codex to load the skill, then ask it to configure CodexSync. Existing configuration is preserved.'
} catch {
    throw "CodexSync installation failed: $($_.Exception.Message)"
}
