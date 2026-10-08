#!/usr/bin/env bash
set -euo pipefail

echo "location: ${HOME}/wind/data_local"

cd ${HOME}/wind/server
cp -r wind ${HOME}/wind/data_local
ls -al wind
