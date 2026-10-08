#!/usr/bin/env bash
set -euo pipefail

echo "location: ${HOME}/wind/data"

cd ${HOME}/wind/server
cp -r wind ${HOME}/wind/data
ls -al wind

