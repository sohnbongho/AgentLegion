#!/usr/bin/env bash
set -euo pipefail

cd ~/wind/serverBinary
svn update

ls -al session-develop/sessionServer
cp -r ${HOME}/wind/serverBinary/session-develop/sessionServer ${HOME}/wind/data
