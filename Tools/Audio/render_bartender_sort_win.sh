#!/bin/zsh
set -euo pipefail

script_directory=${0:A:h}
project_directory=${script_directory:h:h}
render_temp_directory=$(mktemp -d /tmp/bartender-sort-render.XXXXXX)

cleanup() {
    if [[ -d "$render_temp_directory" && "$render_temp_directory" == /tmp/bartender-sort-render.* ]]; then
        rm -R "$render_temp_directory"
    fi
}
trap cleanup EXIT

swiftc \
    "$script_directory/RenderBartenderSortStems.swift" \
    -o "$render_temp_directory/render-stems"

mkdir -p "$render_temp_directory/stems"
"$render_temp_directory/render-stems" "$render_temp_directory/stems"

python3 \
    "$script_directory/ComposeBartenderSortWin.py" \
    "$render_temp_directory/stems" \
    "$project_directory/AudioProduction/BartenderSortGenerated" \
    "$project_directory/Assets/Resources/Audio/BartenderSortGenerated"
