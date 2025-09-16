# Build the project
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env
WORKDIR /nzb-webdav
COPY ./ ./
RUN dotnet restore
RUN dotnet publish -c Release -r linux-musl-x64 -o ./publish

# Build the production image
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
EXPOSE 8080
WORKDIR /nzb-webdav
RUN mkdir /config

# Set environment variables for native library loading
ENV LD_LIBRARY_PATH="/usr/lib:/lib:/usr/local/lib"
ENV LD_DEBUG=""

# Install FUSE3 and create proper symlinks for LTRData.FuseDotNet
RUN apk add --no-cache libc6-compat shadow su-exec ffmpeg fuse3 fuse3-dev fuse3-libs && \
    echo "=== FUSE Installation Debug ===" && \
    # Show what packages were installed \
    apk list | grep fuse && \
    # Show all FUSE files \
    find /usr /lib -name "*fuse*" -type f 2>/dev/null | sort && \
    # Show library directories \
    ls -la /usr/lib/ | grep -i fuse || echo "No fuse in /usr/lib" && \
    ls -la /lib/ | grep -i fuse || echo "No fuse in /lib" && \
    # Create comprehensive symlinks in multiple locations \
    mkdir -p /usr/local/lib && \
    # Try to find the actual FUSE library and create symlinks \
    for fusefile in $(find /usr /lib -name "*fuse3*" -name "*.so*" 2>/dev/null); do \
        echo "Found FUSE file: $fusefile"; \
        ln -sf "$fusefile" /usr/lib/libfuse3.so 2>/dev/null || true; \
        ln -sf "$fusefile" /usr/lib/fuse3.so 2>/dev/null || true; \
        ln -sf "$fusefile" /usr/local/lib/libfuse3.so 2>/dev/null || true; \
        ln -sf "$fusefile" /usr/local/lib/fuse3.so 2>/dev/null || true; \
        ln -sf "$fusefile" /lib/libfuse3.so 2>/dev/null || true; \
        ln -sf "$fusefile" /lib/fuse3.so 2>/dev/null || true; \
    done && \
    # Final verification \
    echo "=== Final library check ===" && \
    ls -la /usr/lib/*fuse* /lib/*fuse* /usr/local/lib/*fuse* 2>/dev/null || echo "Still no FUSE libraries found" && \
    echo "=== End FUSE Debug ===" && \
    # Test FUSE binary availability \
    which fusermount3 || echo "fusermount3 not found" && \
    ldd /usr/bin/fusermount3 2>/dev/null || echo "Cannot check fusermount3 dependencies" && \
    rm -rf /var/cache/apk/*
COPY --from=build-env /nzb-webdav/publish ./
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
CMD ["/entrypoint.sh"]