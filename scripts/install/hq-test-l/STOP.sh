#!/bin/bash

# Stop the hq-test-l servers.
#
# Processes are not matched by name. wind renames itself to WindServer while
# starting up, so even within one batch some processes are called "wind" and
# others "WindServer" - "killall wind" catches only about half of them.
# Matching on the working directory plus the executable catches all of them
# regardless of name, and leaves other environments (data_local, ...) alone.

DATA_DIR="$HOME/wind/data_hqtest"
WAIT_SEC=20

# List the pids of wind / sessionServer processes running out of data_hqtest.
find_pids() {
  local d
  for d in /proc/[0-9]*; do
    [ "$(readlink "$d/cwd" 2>/dev/null)" = "$DATA_DIR" ] || continue
    case "$(basename "$(readlink "$d/exe" 2>/dev/null)")" in
      wind|sessionServer) echo "${d#/proc/}" ;;
    esac
  done
}

echo "$DATA_DIR"

targets=$(find_pids)
if [ -z "$targets" ]; then
  echo "No servers are running."
  exit 0
fi

# 1) Ask for a graceful shutdown.
n=0
for p in $targets; do
  kill -s 2 "$p" 2>/dev/null && n=$((n+1))
done
echo "Sent SIGINT to $n process(es)."

# 2) Wait for them to go away.
for _ in $(seq "$WAIT_SEC"); do
  [ -z "$(find_pids)" ] && break
  sleep 1
done

left=$(find_pids)
if [ -z "$left" ]; then
  echo "All servers shut down cleanly."
  exit 0
fi

# 3) Force whatever is still up.
#    Shutdown can stall near the end when the Kafka / Redis / WebAPI threads
#    fail to wind down - common in local and test environments where those
#    services are not reachable.
echo "Still running after ${WAIT_SEC}s - forcing them down with SIGKILL:"
for p in $left; do
  echo "  pid=$p $(tr '\0' ' ' < /proc/$p/cmdline 2>/dev/null)"
  kill -9 "$p" 2>/dev/null
done
sleep 1

still=$(find_pids)
if [ -z "$still" ]; then
  echo "All servers stopped."
else
  echo "[ERROR] still running: $still"
  exit 1
fi
