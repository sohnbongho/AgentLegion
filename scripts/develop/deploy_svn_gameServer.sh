#!/usr/bin/env bash
set -euo pipefail

#cd ${HOME}/wind/server
cd ~/wind/serverBinary
svn update

ls -al wind-develop/wind
cp -r wind-develop/wind ~/wind/data

