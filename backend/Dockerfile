# Build the project
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env
WORKDIR /nzb-webdav
COPY ./ ./
RUN dotnet restore
RUN dotnet publish -c Release -r linux-x64 -o ./publish

# Build the production image - using Ubuntu for better FUSE compatibility
FROM mcr.microsoft.com/dotnet/aspnet:9.0-jammy
EXPOSE 8080
WORKDIR /nzb-webdav
RUN mkdir /config

# Set environment variables for native library loading
ENV LD_LIBRARY_PATH="/usr/lib:/lib:/usr/local/lib:/usr/lib/x86_64-linux-gnu"

# Install required packages including FUSE3
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        fuse3 \
        libfuse3-3 \
        libfuse3-dev \
        ffmpeg \
        gosu && \
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
COPY --from=build-env /nzb-webdav/publish ./
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
CMD ["/entrypoint.sh"]