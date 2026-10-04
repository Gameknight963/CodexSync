param(
    [Parameter(Mandatory)]
    [string] $Executable
)

$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('CodexSync-Smoke-' + [guid]::NewGuid().ToString('N'))
$archive = Join-Path $fixtureRoot 'archive'
$checkout = Join-Path $fixtureRoot 'checkout'
$sessions = Join-Path $fixtureRoot 'sessions'
$mapping = Join-Path $fixtureRoot 'private/mappings.json'
$sessionId = [guid]::NewGuid().ToString('D')

function Invoke-Cli([string[]] $CliArguments) {
    $output = & $executablePath @CliArguments --mapping-file $mapping
    if ($LASTEXITCODE -ne 0) { throw "CLI failed: $($CliArguments -join ' ')" }
    return $output
}

try {
    New-Item -ItemType Directory -Path $archive, $checkout, $sessions -Force | Out-Null
    $metadata = @{
        type = 'session_meta'
        payload = @{ id = $sessionId; cwd = $checkout }
    } | ConvertTo-Json -Depth 3 -Compress
    [IO.File]::WriteAllText((Join-Path $sessions 'sample.jsonl'), $metadata + "`n", [Text.UTF8Encoding]::new($false))

    Invoke-Cli @('--help') | Out-Null
    Invoke-Cli @('archive', $archive) | Out-Null
    Invoke-Cli @('map', $sessionId, $checkout) | Out-Null
    $mapped = Get-Content -LiteralPath $mapping -Raw | ConvertFrom-Json
    if ($mapped.sessions.$sessionId -ne $checkout) { throw 'Mapping was not persisted.' }

    $listing = (Invoke-Cli @('list', '--sessions-dir', $sessions, '--full-paths')) -join "`n"
    if ($listing -notmatch "$sessionId\s+excluded" -or !$listing.Contains($checkout)) {
        throw 'A newly mapped chat should be excluded and display its local folder.'
    }

    Invoke-Cli @('include', $sessionId) | Out-Null
    $listing = (Invoke-Cli @('list', '--sessions-dir', $sessions)) -join "`n"
    if ($listing -notmatch "$sessionId\s+included") { throw 'Inclusion was not persisted.' }

    Invoke-Cli @('exclude', $sessionId) | Out-Null
    $listing = (Invoke-Cli @('list', '--sessions-dir', $sessions)) -join "`n"
    if ($listing -notmatch "$sessionId\s+excluded") { throw 'Exclusion was not persisted.' }
    if (Get-ChildItem -LiteralPath $archive -Force) { throw 'Setup modified the shared archive.' }
    Write-Host "Smoke test passed: $executablePath"
} finally {
    # Only delete the unique temporary fixture created by this script.
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ([IO.Path]::GetDirectoryName($resolvedFixture) -ne $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)) {
        throw 'Refusing to remove a fixture outside the temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
