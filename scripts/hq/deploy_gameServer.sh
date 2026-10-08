#!/usr/bin/env bash
set -euo pipefail

echo "location: ${HOME}/wind/data_hq"

cd ${HOME}/wind/server
cp -r wind ${HOME}/wind/data_hq
ls -al wind
