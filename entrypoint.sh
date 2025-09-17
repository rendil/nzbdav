#!/bin/bash

wait_either() {
    local pid1=$1
    local pid2=$2
    local pid3=$3

    while true; do
        if ! kill -0 "$pid1" 2>/dev/null; then
            wait "$pid1"
            EXITED_PID=$pid1
            REMAINING_PIDS=($pid2 $pid3)
            return $?
        fi

        if ! kill -0 "$pid2" 2>/dev/null; then
            wait "$pid2"
            EXITED_PID=$pid2
            REMAINING_PIDS=($pid1 $pid3)
            return $?
        fi

        if [ -n "$pid3" ] && ! kill -0 "$pid3" 2>/dev/null; then
            wait "$pid3"
            EXITED_PID=$pid3
            REMAINING_PIDS=($pid1 $pid2)
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
    if [ -n "$RCLONE_PID" ] && kill -0 "$RCLONE_PID" 2>/dev/null; then
        kill "$RCLONE_PID"
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

# Run backend database migration
cd /app/backend
echo "Running database maintenance."
su-exec "$USER_NAME" ./NzbWebDAV --db-migration
if [ $? -ne 0 ]; then
    echo "Database migration failed. Exiting with error code $?."
    exit $?
fi
echo "Done with database maintenance."

# Run backend as appuser in background
su-exec "$USER_NAME" ./NzbWebDAV &
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
su-exec "$USER_NAME" npm run start &
FRONTEND_PID=$!

# Start rclone NFS server if enabled
if [ "${NFS_ENABLED:-false}" = "true" ]; then
    echo "Starting rclone NFS server..."
    
    # Wait a bit for backend to be fully ready
    sleep 3
    
    rclone serve nfs nzbdav: \
        --addr 0.0.0.0:2049 \
        --vfs-cache-mode=full \
        --buffer-size=1024 \
        --dir-cache-time=1s \
        --vfs-cache-max-size=5G \
        --vfs-cache-max-age=180m \
        --links \
        --use-cookies \
        --allow-other \
        --uid=1000 \
        --gid=1000 &
    RCLONE_PID=$!
    
    echo "Rclone NFS server started with PID ${RCLONE_PID}"
else
    # If NFS is disabled, just wait for backend and frontend
    RCLONE_PID=""
fi

# Wait for processes to exit
if [ -n "$RCLONE_PID" ]; then
    # Wait for any of the three processes
    wait_either $BACKEND_PID $FRONTEND_PID $RCLONE_PID
    EXIT_CODE=$?
else
    # Only wait for backend and frontend
    wait_either $BACKEND_PID $FRONTEND_PID
    EXIT_CODE=$?
fi

# Determine which process exited
if [ "$EXITED_PID" -eq "$FRONTEND_PID" ]; then
    echo "The web-frontend has exited. Shutting down the web-backend and rclone..."
elif [ "$EXITED_PID" -eq "$RCLONE_PID" ]; then
    echo "The rclone NFS server has exited. Shutting down the web-frontend and web-backend..."
else
    echo "The web-backend has exited. Shutting down the web-frontend and rclone..."
fi

# Kill remaining processes
for pid in "${REMAINING_PIDS[@]}"; do
    if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
        kill "$pid"
    fi
done

# Exit with the code of the process that died first
exit $EXIT_CODE