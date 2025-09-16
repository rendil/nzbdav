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

# FUSE DEBUG: Check if directory exists and try to create it
echo "=== FUSE MOUNT DEBUG ==="
echo "FUSE_ENABLED env var: ${FUSE_ENABLED:-not set}"
echo "FUSE_MOUNT_POINT env var: ${FUSE_MOUNT_POINT:-not set}"
echo "NFS_ENABLED env var: ${NFS_ENABLED:-not set}"

if [ -d "/mnt/nzbwebdav" ]; then
    echo "/mnt/nzbwebdav directory exists"
    ls -la /mnt/nzbwebdav/ || echo "Cannot list /mnt/nzbwebdav contents"
else
    echo "/mnt/nzbwebdav directory does not exist - creating it"
    mkdir -p /mnt/nzbwebdav
    chmod 755 /mnt/nzbwebdav
    chown "$PUID:$PGID" /mnt/nzbwebdav
fi

# Check if FUSE is mounted
mount | grep nzbwebdav || echo "No FUSE mount found"

# Add a delay to let .NET services start, then check again
echo "Waiting 10 seconds for .NET FUSE service to start..."
sleep 10 &
SLEEP_PID=$!

echo "=== END FUSE DEBUG ==="

# Run backend as appuser in background
gosu "$USER_NAME" ./NzbWebDAV &
BACKEND_PID=$!

# Start NFS server if enabled (after backend starts and FUSE is mounted)
if [ "${NFS_ENABLED:-false}" = "true" ] && [ -n "${NFS_EXPORT_PATH:-}" ]; then
    echo "NFS server will be started after backend is healthy..."
fi

# Wait for backend health check
echo "Waiting for backend to start."
MAX_BACKEND_HEALTH_RETRIES=${MAX_BACKEND_HEALTH_RETRIES:-30}
MAX_BACKEND_HEALTH_RETRY_DELAY=${MAX_BACKEND_HEALTH_RETRY_DELAY:-1}
i=0
while true; do
    echo "Checking backend health: $BACKEND_URL/health ..."
    if curl -s -o /dev/null -w "%{http_code}" "$BACKEND_URL/health" | grep -q "^200$"; then
        echo "Backend is healthy."
        
        # Check if FUSE mounted after backend startup
        echo "=== POST-STARTUP FUSE CHECK ==="
        mount | grep nzbwebdav || echo "FUSE still not mounted after backend startup"
        if [ -d "${NFS_EXPORT_PATH:-/mnt/nzbwebdav}" ]; then
            ls -la "${NFS_EXPORT_PATH:-/mnt/nzbwebdav}/" 2>/dev/null || echo "Cannot access FUSE mount point"
        fi
        
        # Check if backend process is running
        if kill -0 "$BACKEND_PID" 2>/dev/null; then
            echo "Backend process (PID $BACKEND_PID) is running"
        else
            echo "Backend process (PID $BACKEND_PID) is not running - this could be why FUSE isn't mounting"
        fi
        echo "=== END POST-STARTUP FUSE CHECK ==="
        
        # Start NFS server if enabled (now that backend is healthy and FUSE should be mounted)
        if [ "${NFS_ENABLED:-false}" = "true" ] && [ -n "${NFS_EXPORT_PATH:-}" ]; then
            echo "Starting NFS server for path: ${NFS_EXPORT_PATH}"
            
            # Wait for FUSE to be fully ready and check if mount point is accessible
            sleep 3
            
            # Check if FUSE mount is accessible and fix permissions if needed
            if [ -d "${NFS_EXPORT_PATH}" ]; then
                # Try to access the directory as root to verify FUSE is working
                ls "${NFS_EXPORT_PATH}" > /dev/null 2>&1 || echo "FUSE mount may not be ready yet"
                
                # Ensure NFS can access the mount point
                chmod 755 "${NFS_EXPORT_PATH}" || echo "Could not set permissions on ${NFS_EXPORT_PATH}"
            else
                echo "Warning: ${NFS_EXPORT_PATH} does not exist - FUSE may not be mounted"
            fi
            
            # Create NFS exports file
            echo "${NFS_EXPORT_PATH} *(ro,sync,no_subtree_check,no_root_squash,insecure,crossmnt,fsid=0)" > /etc/exports
            
            # Start RPC services in proper order
            echo "Starting rpcbind..."
            # Kill any existing rpcbind and start fresh
            pkill -f rpcbind || true
            rpcbind -f &
            sleep 2
            
            # Verify rpcbind is running
            if ! pgrep rpcbind > /dev/null; then
                echo "Warning: rpcbind failed to start"
            else
                echo "rpcbind started successfully"
            fi
            
            echo "Starting NFS services..."
            # Start nfsd first
            rpc.nfsd 8
            
            # Export filesystems
            exportfs -rav
            
            # Start mountd with fixed port
            rpc.mountd --no-nfs-version 2 --no-nfs-version 3 --port 33333 &
            
            # Give services time to register
            sleep 2
            
            echo "NFS server started and exporting ${NFS_EXPORT_PATH}"
            
            # Show exports (with retry)
            for i in 1 2 3; do
                if showmount -e localhost 2>/dev/null; then
                    break
                fi
                echo "Waiting for NFS services to be ready... (attempt $i/3)"
                sleep 2
            done
        fi
        
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