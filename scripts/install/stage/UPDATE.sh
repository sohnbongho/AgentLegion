#!/bin/bash

# Update the game data and the server binaries.
#
# "wind" is a versioned file on the data branch, but we replace it with a
# symlink to the binary in serverBinary. Running "svn up" in that state
# produces a conflict and leaves ~60MB of .mine/.rNNN leftovers behind,
# so revert it first, update, then relink.

svn revert -q ~/wind/data_stage/wind 2>/dev/null

svn up ~/wind/data_stage/
svn up ~/wind/serverBinary/

ln -sf ~/wind/serverBinary/wind-stage/wind ~/wind/data_stage/wind
ln -sf ~/wind/serverBinary/session-stage/sessionServer ~/wind/data_stage/sessionServer

# A binary that arrives without the executable bit makes RUN.sh die with
# "Permission denied".
chmod +x ~/wind/data_stage/wind ~/wind/data_stage/sessionServer

ls -al ~/wind/data_stage/wind ~/wind/data_stage/sessionServer
