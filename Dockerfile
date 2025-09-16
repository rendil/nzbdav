# syntax=docker/dockerfile:1.4

# -------- Stage 1: Build frontend --------
FROM --platform=$BUILDPLATFORM node:alpine AS frontend-build

WORKDIR /frontend
COPY ./frontend ./

RUN npm install
RUN npm run build
RUN npm run build:server
RUN npm prune --omit=dev

# -------- Stage 2: Build backend --------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS backend-build

WORKDIR /backend
COPY ./backend ./

# Accept build-time architecture as ARG (e.g., x64 or arm64)
ARG TARGETARCH
RUN dotnet restore
RUN dotnet publish -c Release -r linux-${TARGETARCH} -o ./publish

# -------- Stage 3: Combined runtime image - using Ubuntu for FUSE compatibility --------
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble

WORKDIR /app

# Set environment variables for native library loading
ENV LD_LIBRARY_PATH="/usr/lib:/lib:/usr/local/lib:/usr/lib/x86_64-linux-gnu"

# Prepare environment with Ubuntu packages
RUN mkdir /config && \
    mkdir -p /mnt/nzbwebdav && \
    apt-get update && \
    apt-get install -y --no-install-recommends \
        nodejs \
        npm \
        bash \
        curl \
        ffmpeg \
        gosu \
        fuse3 \
        libfuse3-3 \
        libfuse3-dev \
        nfs-kernel-server \
        rpcbind && \
    echo "=== FUSE Installation Debug ===" && \
    # Show what packages were installed \
    dpkg -l | grep fuse && \
    # Show all FUSE files \
    find /usr /lib -name "*fuse*" -type f 2>/dev/null | sort && \
    # Show library directories \
    ls -la /usr/lib/x86_64-linux-gnu/ | grep -i fuse || echo "No fuse in /usr/lib/x86_64-linux-gnu" && \
    ls -la /lib/x86_64-linux-gnu/ | grep -i fuse || echo "No fuse in /lib/x86_64-linux-gnu" && \
    # Create comprehensive symlinks for .NET library discovery \
    mkdir -p /usr/local/lib && \
    # Standard Ubuntu FUSE3 library locations and symlinks \
    if [ -f /usr/lib/x86_64-linux-gnu/libfuse3.so.3 ]; then \
        echo "Found FUSE3 library, creating symlinks..."; \
        ln -sf /usr/lib/x86_64-linux-gnu/libfuse3.so.3 /usr/lib/x86_64-linux-gnu/libfuse3.so; \
        ln -sf /usr/lib/x86_64-linux-gnu/libfuse3.so.3 /usr/lib/libfuse3.so; \
        ln -sf /usr/lib/x86_64-linux-gnu/libfuse3.so.3 /usr/local/lib/libfuse3.so; \
        ln -sf /usr/lib/x86_64-linux-gnu/libfuse3.so.3 /usr/lib/fuse3.so; \
        ln -sf /usr/lib/x86_64-linux-gnu/libfuse3.so.3 /usr/local/lib/fuse3.so; \
    fi && \
    # Final verification \
    echo "=== Final library check ===" && \
    ls -la /usr/lib/*fuse* /lib/*fuse* /usr/local/lib/*fuse* /usr/lib/x86_64-linux-gnu/*fuse* 2>/dev/null || echo "No FUSE libraries found" && \
    echo "=== FUSE Binary Check ===" && \
    which fusermount3 && \
    ldd /usr/bin/fusermount3 && \
    echo "=== End FUSE Debug ===" && \
    # Clean up \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Copy frontend
COPY --from=frontend-build /frontend/node_modules ./frontend/node_modules
COPY --from=frontend-build /frontend/package.json ./frontend/package.json
COPY --from=frontend-build /frontend/dist-node/server.js ./frontend/dist-node/server.js
COPY --from=frontend-build /frontend/build ./frontend/build

# Copy backend
COPY --from=backend-build /backend/publish ./backend

# Entry and runtime setup
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

EXPOSE 3000
ARG NZBDAV_VERSION
ENV NZBDAV_VERSION=${NZBDAV_VERSION}
ENV NODE_ENV=production
ENV LOG_LEVEL=warning

CMD ["/entrypoint.sh"]
