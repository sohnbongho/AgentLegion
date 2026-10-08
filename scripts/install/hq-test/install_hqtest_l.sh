#!/bin/bash
# ------------------------------------------------------------
# Installer for the hq-test-l server environment.
#
#   1) ~/wind/data_hqtest          : check out DATA_URL (the data branch)
#   2) ~/wind/serverBinary        : check out BIN_URL (the server binaries)
#   3) data_hqtest/wind            : symlink to the wind binary in serverBinary
#      data_hqtest/sessionServer   : symlink to the sessionServer binary
#                                  (a symlink stores its target, so "ls -al" shows
#                                   which environment's binary is in use;
#                                   a hard link would not record that)
#   4) RUN.sh / UPDATE.sh / STOP.sh / REDIS_CLEAR.sh
#                                  : copied from this folder into data_hqtest
#      files under serverconfig/     : copied from this folder into data_hqtest
#                                  (DBs.local and friends are not in svn, so a
#                                   checkout alone never creates them)
#
#   5) on success                 : opens a shell in data_hqtest
#
# Safe to run repeatedly: existing working copies are updated, then relinked.
# ------------------------------------------------------------
set -euo pipefail

# internal SVN root URL (e.g. https://<svn-host>/svn)
SVN_ROOT=""
# data branch and server binary paths under SVN_ROOT (e.g. <data-repo>/branches/<branch>)
DATA_PATH=""
BIN_PATH=""
DATA_URL="${SVN_ROOT}/${DATA_PATH}"
BIN_URL="${SVN_ROOT}/${BIN_PATH}"

DATA_DIR="$HOME/wind/data_hqtest"
BIN_DIR="$HOME/wind/serverBinary"
# Binaries to link, as "path inside serverBinary|name inside data_hqtest"
BINARIES=(
  "wind-HQTEST-L-TEST/wind|wind"
  "session-HQTEST-L-TEST/sessionServer|sessionServer"
)

# Update the working copy if it exists, otherwise check it out.
checkout_or_update() {
  local url="$1" dir="$2" name="$3"

  if [ -d "$dir/.svn" ]; then
    echo "[$name] working copy found, updating: $dir"
    svn update "$dir"
  else
    echo "[$name] checking out: $dir"
    mkdir -p "$dir"
    # The target directory must be given explicitly. Without it svn creates
    # an extra subdirectory named after the last URL component, which puts
    # every path below one level too deep.
    svn checkout "$url" "$dir"
  fi
}

echo "============================================"
echo " hq-test-l environment setup"
echo " location: $DATA_DIR"
echo "============================================"
echo
echo "* If svn asks for a username and password, enter your SVN account."
echo

# 1) Game data.
# "wind" is also a versioned file on the data branch. Updating while our
# symlink is in place makes svn raise a tree conflict and prompt for a
# resolution, so revert it first. (sessionServer is not versioned there,
# so the revert is simply ignored for it.)
if [ -d "$DATA_DIR/.svn" ]; then
  for entry in "${BINARIES[@]}"; do
    svn revert -q "$DATA_DIR/${entry#*|}" 2>/dev/null || true
  done
fi
checkout_or_update "$DATA_URL" "$DATA_DIR" "data_hqtest"
echo

# 2) Server binaries.
checkout_or_update "$BIN_URL" "$BIN_DIR" "serverBinary"
echo

# 3) Link the server binaries (wind, sessionServer).
for entry in "${BINARIES[@]}"; do
  src="$BIN_DIR/${entry%%|*}"
  dst="$DATA_DIR/${entry#*|}"

  if [ ! -f "$src" ]; then
    echo "[ERROR] server binary not found: $src"
    exit 1
  fi

  if [ -e "$dst" ] || [ -L "$dst" ]; then
    echo "[link] removing existing file: $dst"
    rm -f "$dst"
  fi

  echo "[link] $dst  ->  $src"
  # Use a symbolic link. A hard link does not record its target, so there
  # would be no way to tell from "ls" which binary is actually in use.
  ln -s "$src" "$dst"
  chmod +x "$dst"
done

# 4) Copy the helper scripts and local config.
#    This folder is the original; the data_hqtest side is a copy.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
if [ "$SCRIPT_DIR" != "$DATA_DIR" ]; then
  for f in RUN.sh UPDATE.sh STOP.sh REDIS_CLEAR.sh; do
    if [ -f "$SCRIPT_DIR/$f" ]; then
      cp -p "$SCRIPT_DIR/$f" "$DATA_DIR/$f"
      chmod +x "$DATA_DIR/$f"
      echo "[copy] $DATA_DIR/$f"
    else
      echo "[warn] source missing, skipped: $SCRIPT_DIR/$f"
    fi
  done

  # Local config under serverconfig/ (DBs.local and the like).
  # These are not in svn, so a checkout never creates them; deploy them here.
  # Only files present in this folder are overwritten, so versioned files
  # such as DBs.wind are left alone.
  if [ -d "$SCRIPT_DIR/serverconfig" ]; then
    mkdir -p "$DATA_DIR/serverconfig"
    for src in "$SCRIPT_DIR"/serverconfig/*; do
      [ -f "$src" ] || continue
      cp -p "$src" "$DATA_DIR/serverconfig/"
      echo "[copy] $DATA_DIR/serverconfig/$(basename "$src")"
    done
  fi
fi

echo
echo "============================================"
echo " setup complete"
echo "============================================"
for entry in "${BINARIES[@]}"; do
  ls -al "$DATA_DIR/${entry#*|}"
done

# 5) Leave the caller sitting in data_hqtest.
#    A script cannot change the working directory of the shell that started it,
#    so a plain "cd" here would be lost the moment this exits. Open a fresh
#    shell in data_hqtest instead - "exit" returns to where the install started.
#    With no terminal attached (CI, piped output) just print the command; an
#    interactive shell there would hang.
if [ -t 0 ] && [ -t 1 ]; then
  echo
  echo "Opening a shell in $DATA_DIR  (type 'exit' to come back)"
  cd "$DATA_DIR"
  # SHELL may be unset, and "set -u" would abort on a bare $SHELL.
  exec "${SHELL:-/bin/bash}"
else
  echo
  echo "cd $DATA_DIR"
fi
