#!/bin/bash

# Wipe the redis instance used by the hq-test-l servers.
#
# Defaults come from serverconfig/ServerConfigs.wind, gameDomain 0 - the domain
# RUN.sh starts (wind0/1/2/11):
#
#   RedisCacheConfig    0  127.0.0.1  6379  NO
#   RedisRelayConfig    0  127.0.0.1  6379  NO
#   RedisStorageConfig  0  127.0.0.1  6379  NO
#
# Cache, relay and storage all live on the same instance there, so FLUSHALL
# clears every one of them - including any other environment (data_local, ...)
# pointed at the same local redis. Stop the servers first (STOP.sh); flushing
# underneath a running wind leaves it holding keys that no longer exist.
#
# Runs without asking anything - it flushes as soon as it is started.
#
# Usage:
#   ./REDIS_CLEAR.sh
#   REDIS_HOST=... REDIS_PORT=... REDIS_PASSWORD=... ./REDIS_CLEAR.sh

set -euo pipefail

HOST="${REDIS_HOST:-127.0.0.1}"
PORT="${REDIS_PORT:-6379}"
PASSWORD="${REDIS_PASSWORD:-}"

if ! command -v redis-cli >/dev/null 2>&1; then
  echo "[ERROR] redis-cli not found in PATH."
  exit 1
fi

# The password goes through the environment rather than "-a": redis-cli prints
# a warning to stderr about passwords on the command line, and the value would
# show up in "ps" output.
[ -n "$PASSWORD" ] && export REDISCLI_AUTH="$PASSWORD"

echo "target: $HOST:$PORT"

# Report an unreachable instance clearly - in test environments redis is often
# simply not running, and "Could not connect" from redis-cli alone is easy to
# miss when this runs as part of a larger reset.
if [ "$(redis-cli -h "$HOST" -p "$PORT" ping 2>&1)" != "PONG" ]; then
  echo "[ERROR] no response from $HOST:$PORT - is redis running?"
  exit 1
fi

# Print the count before and after, so the log shows how much was thrown away.
echo "keys: $(redis-cli -h "$HOST" -p "$PORT" dbsize)"

result=$(redis-cli -h "$HOST" -p "$PORT" flushall)
echo "FLUSHALL: $result"
echo "keys: $(redis-cli -h "$HOST" -p "$PORT" dbsize)"
