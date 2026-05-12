#!/bin/bash
set -euo pipefail

case "${1:-}" in
    strip)
        echo "[csstubgen] Stripping DLLs with Refasmer..."
        count=0
        for dll in /input/*.dll; do
            [ -f "$dll" ] || continue
            name=$(basename "$dll")
            if refasmer -v -O /output "$dll" 2>/dev/null; then
                echo "  ✓ $name"
                ((count++))
            else
                echo "  ✗ $name (skipped)"
            fi
        done
        echo "[csstubgen] Done. $count DLLs stripped."
        ;;
    generate)
        shift
        exec /app/csstubgen "$@"
        ;;
    *)
        echo "Usage: <strip|generate> [options]"
        exit 1
        ;;
esac
