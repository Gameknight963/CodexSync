set -x

dotnet publish ./CodexSync.Cli/CodexSync.Cli.csproj \
    -r win-x64 \
    -p:PublishSingleFile=true \
    --self-contained=false

dotnet publish ./CodexSync.Cli/CodexSync.Cli.csproj \
    -r linux-x64 \
    -p:PublishSingleFile=true \
    --self-contained=false

destination="./x64"

mkdir -p "$destination"

# linux before windows otherwise it fucks up if you're on windows

mv -f \
    "./CodexSync.Cli/bin/Release/net10.0/linux-x64/publish/codexsync" \
    "$destination/codexsync"

mv -f \
    "./CodexSync.Cli/bin/Release/net10.0/win-x64/publish/codexsync.exe" \
    "$destination/codexsync.exe"