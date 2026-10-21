#!/usr/bin/env bash
# Loads vars from .env into the current shell so `dotnet run` picks them up
# (ASP.NET Core reads env vars automatically; double underscores map to config sections).
#
# Usage: source ./scripts/load-env.sh   [must be *sourced*, not executed, to export into your shell]

ENV_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/.env"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "No .env file found at $ENV_FILE (copy .env.example to .env first)." >&2
  return 1 2>/dev/null || exit 1
fi

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a

echo "Loaded env vars from $ENV_FILE"
