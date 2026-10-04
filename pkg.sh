#!/usr/bin/env bash
set -eux

for self_contained in false true; do
    destination="./pkg"
    mkdir -p "$destination"

    for runtime in linux-x64 win-x64; do
        publish_directory="./CodexSync.Cli/bin/Release/net10.0/$runtime/publish-$self_contained"
        dotnet publish ./CodexSync.Cli/CodexSync.Cli.csproj \
            -c Release \
            -r "$runtime" \
            -p:PublishSingleFile=true \
            -p:IncludeNativeLibrariesForSelfExtract=true \
            --self-contained="$self_contained" \
            -o "$publish_directory"

        executable="codexsync"
        packaged_executable="$executable"
        if [ "$self_contained" = true ]; then
            packaged_executable="$packaged_executable-selfcontained"
        fi
        if [ "$runtime" = win-x64 ]; then
            executable="$executable.exe"
            packaged_executable="$packaged_executable.exe"
        fi
        mv -f "$publish_directory/$executable" "$destination/$packaged_executable"
    done
done

cp -f "./.agents/skills/codexsync-setup/SKILL.md" "./pkg/SKILL.md"
