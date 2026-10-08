#!/usr/bin/env bash
set -euo pipefail

echo "location: ${HOME}/wind/data_hqtest_L_Test"

cd ${HOME}/wind/server
cp -r wind ${HOME}/wind/data_hqtest_L_Test
ls -al wind
