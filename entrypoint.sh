#!/bin/bash

wait_either() {
    local pid1=$1
    local pid2=$2

    while true; do
        if ! kill -0 "$pid1" 2>/dev/null; then
            wait "$pid1"
            EXITED_PID=$pid1
            REMAINING_PID=$pid2
            return $?
        fi

        if ! kill -0 "$pid2" 2>/dev/null; then
            wait "$pid2"
            EXITED_PID=$pid2
            REMAINING_PID=$pid1
            return $?
        fi

        sleep 0.5
    done
}

# Signal handling for graceful shutdown
terminate() {
    echo "Caught termination signal. Shutting down..."
    if [ -n "$BACKEND_PID" ] && kill -0 "$BACKEND_PID" 2>/dev/null; then
        kill "$BACKEND_PID"
    fi
    if [ -n "$FRONTEND_PID" ] && kill -0 "$FRONTEND_PID" 2>/dev/null; then
        kill "$FRONTEND_PID"
    fi
    # Wait for children to exit
    wait
    exit 0
}
trap terminate TERM INT

# Use env vars or default to 1000
PUID=${PUID:-1000}
PGID=${PGID:-1000}

# Create group if it doesn't exist, or use existing one with same GID
if ! getent group appgroup >/dev/null; then
    if getent group "$PGID" >/dev/null; then
        # Use existing group with that GID
        EXISTING_GROUP=$(getent group "$PGID" | cut -d: -f1)
        echo "Using existing group $EXISTING_GROUP with GID $PGID"
        GROUP_NAME="$EXISTING_GROUP"
    else
        groupadd -g "$PGID" appgroup
        GROUP_NAME="appgroup"
    fi
else
    GROUP_NAME="appgroup"
fi

# Create user if it doesn't exist, or use existing one with same UID
if ! id appuser >/dev/null 2>&1; then
    if id "$PUID" >/dev/null 2>&1; then
        # Use existing user with that UID
        EXISTING_USER=$(id -nu "$PUID")
        echo "Using existing user $EXISTING_USER with UID $PUID"
        USER_NAME="$EXISTING_USER"
    else
        useradd -M -u "$PUID" -g "$PGID" appuser
        USER_NAME="appuser"
    fi
else
    USER_NAME="appuser"
fi

# Set environment variables
if [ -z "${BACKEND_URL}" ]; then
    export BACKEND_URL="http://localhost:8080"
fi

if [ -z "${FRONTEND_BACKEND_API_KEY}" ]; then
    export FRONTEND_BACKEND_API_KEY=$(head -c 32 /dev/urandom | od -A n -t x1 | tr -d ' \n')
fi

# Change permissions on /config directory to the given PUID and PGID
chown $PUID:$PGID /config

# Ensure FUSE mount point has correct permissions
if [ -d "/mnt/nzbwebdav" ]; then
    chown $PUID:$PGID /mnt/nzbwebdav
    chmod 755 /mnt/nzbwebdav
fi

# Run backend database migration
cd /app/backend
echo "Running database maintenance."
gosu "$USER_NAME" ./NzbWebDAV --db-migration
if [ $? -ne 0 ]; then
    echo "Database migration failed. Exiting with error code $?."
    exit $?
fi
echo "Done with database maintenance."

# Run backend as appuser in background
gosu "$USER_NAME" ./NzbWebDAV &
BACKEND_PID=$!

# Wait for backend health check
echo "Waiting for backend to start."
MAX_BACKEND_HEALTH_RETRIES=${MAX_BACKEND_HEALTH_RETRIES:-30}
MAX_BACKEND_HEALTH_RETRY_DELAY=${MAX_BACKEND_HEALTH_RETRY_DELAY:-1}
i=0
while true; do
    echo "Checking backend health: $BACKEND_URL/health ..."
    if curl -s -o /dev/null -w "%{http_code}" "$BACKEND_URL/health" | grep -q "^200$"; then
        echo "Backend is healthy."
        break
    fi

    i=$((i+1))
    if [ "$i" -ge "$MAX_BACKEND_HEALTH_RETRIES" ]; then
        echo "Backend failed health check after $MAX_BACKEND_HEALTH_RETRIES retries. Exiting."
        kill $BACKEND_PID
        wait $BACKEND_PID
        exit 1
    fi

    sleep "$MAX_BACKEND_HEALTH_RETRY_DELAY"
done

# Run frontend as appuser in background
cd /app/frontend
gosu "$USER_NAME" npm run start &
FRONTEND_PID=$!

# Wait for either to exit
wait_either $BACKEND_PID $FRONTEND_PID
EXIT_CODE=$?

# Determine which process exited
if [ "$EXITED_PID" -eq "$FRONTEND_PID" ]; then
    echo "The web-frontend has exited. Shutting down the web-backend..."
else
    echo "The web-backend has exited. Shutting down the web-frontend..."
fi

# Kill the remaining process
kill $REMAINING_PID

# Exit with the code of the process that died first
exit $EXIT_CODE