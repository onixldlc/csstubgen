#!/bin/sh
set -eu

case "${1:-}" in
    strip)
        echo "[csstubgen] Stripping DLLs with Refasmer..."
        count=0
        for dll in /input/*.dll; do
            [ -f "$dll" ] || continue
            name=$(basename "$dll")
            cp "$dll" /output/"$name"
            if refasmer -v --omit-non-api-members false -O /output /output/"$name"; then
                echo "  ✓ $name"
                count=$((count + 1))
            else
                rm -f /output/"$name"
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
