#!/usr/bin/env bash
set -euo pipefail

echo "location: ${HOME}/wind/data_stage"

cd ${HOME}/wind/server
cp -r wind ${HOME}/wind/data_stage
ls -al wind
