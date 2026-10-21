#!/bin/sh
# Runs every test project: DocTests (Doc.cs), StoreTests (the Store, no GTK)
# and WindowTests (the window, headless under its own gtk4-broadwayd).
# Exits 1 if any of them has a failing check.
cd "$(dirname "$0")/.." || exit 1
status=0
for t in DocTests StoreTests WindowTests; do
    echo "== $t"
    dotnet run --project "tests/$t" -- "$@" || status=1
    echo
done
exit $status
