param([Parameter(Mandatory)][string] $Executable)

$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('CodexSync-InstallerTest-' + [guid]::NewGuid().ToString('N'))
$binaryFixture = (Resolve-Path -LiteralPath $Executable).Path
$skillFixture = (Resolve-Path -LiteralPath '.agents/skills/codexsync-setup/SKILL.md').Path
$releaseFixture = [pscustomobject]@{
    tag_name = 'v-test'; draft = $false; prerelease = $true
    assets = @(
        [pscustomobject]@{ name = 'codexsync-selfcontained.exe'; browser_download_url = 'https://fixture/binary' }
        [pscustomobject]@{ name = 'SKILL.md'; browser_download_url = 'https://fixture/skill' }
    )
}

# Stub only network requests; install the real published executable in a temporary directory.
function Invoke-RestMethod($Uri, $Headers) {
    if ($Uri -like '*/tags/*') { return $releaseFixture }
    return ,@($releaseFixture)
}
function Invoke-WebRequest($Uri, $Headers, $OutFile, [switch] $UseBasicParsing) {
    $source = if ($Uri -eq 'https://fixture/binary') { $binaryFixture } else { $skillFixture }
    Copy-Item -LiteralPath $source -Destination $OutFile
}
function Assert-Rejected([string] $Expected) {
    $message = $null
    try { & ./install.ps1 @installerArguments } catch { $message = $_.Exception.Message }
    if (!$message -or !$message.Contains($Expected)) { throw "Expected '$Expected'; got '$message'." }
}

try {
    $installDirectory = Join-Path $fixtureRoot 'bin'
    $skillDirectory = Join-Path $fixtureRoot 'skill'
    $installerArguments = @{ NoPath = $true; InstallDir = $installDirectory; SkillDir = $skillDirectory }
    Assert-Rejected 'No matching release'
    if (Test-Path -LiteralPath $installDirectory) { throw 'Failed selection wrote installation files.' }

    & ./install.ps1 @installerArguments -Prerelease
    if (!(Test-Path (Join-Path $installDirectory 'codexsync.exe'))) { throw 'Executable was not installed.' }
    if ((Get-Content (Join-Path $skillDirectory 'SKILL.md') -Raw) -ne (Get-Content $skillFixture -Raw)) {
        throw 'Skill did not match the release fixture.'
    }
    Set-Content (Join-Path $installDirectory 'preserve.txt') 'existing configuration'
    & ./install.ps1 @installerArguments -Version v-test
    if (!(Test-Path (Join-Path $installDirectory 'preserve.txt'))) { throw 'Update removed unrelated files.' }

    $releaseFixture.prerelease = $false
    & ./install.ps1 @installerArguments
    $releaseFixture.assets = @()
    Assert-Rejected 'may still be building'
    Write-Host 'Windows installer tests passed.'
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolvedFixture) -ne $temporaryRoot) {
        throw 'Refusing to remove a fixture outside the temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
