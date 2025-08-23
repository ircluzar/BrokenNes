#!/usr/bin/env bash
# env.sh - Developer convenience aliases for this repo

# Determine repo root robustly for bash and zsh when sourced
if [ -n "${BASH_SOURCE:-}" ]; then
  _ENV_FILE="${BASH_SOURCE[0]}"
elif [ -n "${ZSH_VERSION:-}" ]; then
  # zsh-specific expansion to the sourced file path
  _ENV_FILE="${(%):-%N}"
else
  _ENV_FILE="$0"
fi
REPO_ROOT="$(cd "$(dirname "${_ENV_FILE}")" && pwd)"

# Detect if this file is sourced (so aliases persist in current shell)
_IS_SOURCED=0
if [ -n "${BASH_SOURCE:-}" ]; then
  [ "${BASH_SOURCE[0]}" != "$0" ] && _IS_SOURCED=1
elif [ -n "${ZSH_VERSION:-}" ]; then
  case "${ZSH_EVAL_CONTEXT:-}" in
    *:file) _IS_SOURCED=1 ;;
  esac
fi

if [ "$_IS_SOURCED" -ne 1 ]; then
  cat <<'MSG'
This script must be sourced to register aliases in your current shell.

Use:
  source ./env.sh

Optional (zsh): add once to auto-load:
  echo "alias env='source ~/Documents/GitHub/BrokenNes/env.sh'" >> ~/.zshrc && source ~/.zshrc

After sourcing, you'll have:
  - staging -> ./staging.sh
  - build   -> ./build.sh
MSG
  exit 0
fi

# Aliases (absolute paths so they work anywhere once sourced)
alias staging="\"$REPO_ROOT\"/staging.sh"
alias build="\"$REPO_ROOT\"/build.sh"

unset _ENV_FILE _IS_SOURCED
echo "Aliases set: staging, build"
