#!/bin/zsh
cd ~/dev/whisparrot || exit 1
nohup ~/.local/bin/codex exec -m gpt-6.1-sol -c model_reasoning_effort='"medium"' -s workspace-write --cd ~/dev/whisparrot - < design-prompt.txt > ~/dev/whisparrot-design.log 2>&1 &
echo "started pid $!"
