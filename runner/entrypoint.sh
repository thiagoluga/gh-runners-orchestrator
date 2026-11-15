#!/usr/bin/env bash
# Runs one just-in-time runner for one job and exits; the orchestrator then removes the
# container with its volumes. No registration step and no state: the JIT configuration
# already registers the runner (docs/adr/0004-ephemeral-runner-lifecycle.md).
set -euo pipefail

if [ -z "${ACTIONS_RUNNER_INPUT_JITCONFIG:-}" ]; then
  echo "ACTIONS_RUNNER_INPUT_JITCONFIG is not set; this image only runs just-in-time runners." >&2
  exit 64
fi

cd /actions-runner
# The runner reads its --jitconfig argument from this variable, so the configuration never
# appears on a command line. It is single-use: GitHub accepts it once, at session start.
exec ./run.sh
