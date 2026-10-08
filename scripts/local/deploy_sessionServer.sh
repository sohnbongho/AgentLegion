#!/usr/bin/env bash
set -euo pipefail

echo "location: ${HOME}/wind/data_local"

cp -r ${HOME}/wind/serverBinary/session-develop/sessionServer ${HOME}/wind/data_local
