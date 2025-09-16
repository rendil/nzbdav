#!/bin/bash

# Use env vars or default to 1000
PUID=${PUID:-1000}
PGID=${PGID:-1000}

# Create group if it doesn't exist
if ! getent group appgroup >/dev/null; then
    groupadd -g "$PGID" appgroup
fi

# Create user if it doesn't exist
if ! id appuser >/dev/null 2>&1; then
    useradd -M -u "$PUID" -g appgroup appuser
fi

# Switch to new user and run app
exec gosu appuser ./NzbWebDAV