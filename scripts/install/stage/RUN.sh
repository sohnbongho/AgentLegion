#!/bin/bash

# Start the stage servers.
# Assumed to sit next to install_stage.sh.

DATA_DIR="$HOME/wind/data_stage"

cd "$DATA_DIR" 2>/dev/null || {
  echo "[ERROR] environment not found: $DATA_DIR"
  echo "        Run install_stage.sh first."
  exit 1
}

# Redirection below fails if the logs directory does not exist yet.
mkdir -p ./logs

#stop
#./STOP.sh

#remove core
#rm core.*

# Why setsid:
# Ctrl+C at the "tail -f" below sends SIGINT to the whole foreground process
# group. Background jobs started with "&" belong to that same group, and SIGINT
# is exactly how wind shuts down - so the servers would go down with the tail.
# setsid puts each server in its own session, out of reach of that SIGINT.

#session
setsid ./sessionServer -d 0 --logoutput=console wind1000 > ./logs/log.session 2>&1 & 

#wind
setsid ./wind wind0 > ./logs/log.wind0 2>&1 & #master
setsid ./wind wind1 > ./logs/log.wind1 2>&1 & #sync
setsid ./wind wind2 > ./logs/log.wind2 2>&1 & #login
setsid ./wind wind11 > ./logs/log.wind11 2>&1 & #local

echo "servers started: $DATA_DIR"
echo "logs: $DATA_DIR/logs/"
echo
echo "-------------------------------------------"
echo " Tailing log.wind11. Ctrl+C leaves the tail only - the servers keep running."
echo " Run STOP.sh to shut the servers down."
echo "-------------------------------------------"

# Give the servers a moment to create the log file before tailing it.
sleep 1
tail -f ./logs/log.wind11
