#!/usr/bin/env bash
# Shared identity helpers for local deploys and GitHub Actions.

agent_label_from_name() {
  printf '%s' "$1" |
    tr '[:upper:]' '[:lower:]' |
    sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//'
}

agent_resource_suffix() {
  agent_label_from_name "$1"
}
