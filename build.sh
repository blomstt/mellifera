#!/bin/sh
# Builds the native binary and installs it to ~/.local/bin/hive.
set -e
cd "$(dirname "$0")"
dotnet publish -c Release -o out --nologo -v quiet
install -Dm755 out/hive "$HOME/.local/bin/hive"
ls -l "$HOME/.local/bin/hive"
