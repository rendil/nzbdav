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

# Install FUSE3 and create proper symlinks for LTRData.FuseDotNet
RUN apk add --no-cache libc6-compat shadow su-exec ffmpeg fuse3 fuse3-dev fuse3-libs && \
    # Debug: Show what FUSE files are available \
    find /usr -name "*fuse*" -type f 2>/dev/null || echo "No FUSE files found" && \
    # Create the symlinks that LTRData.FuseDotNet expects \
    ln -sf /usr/lib/libfuse3.so.3 /usr/lib/libfuse3.so 2>/dev/null || \
    ln -sf /lib/libfuse3.so.3 /usr/lib/libfuse3.so 2>/dev/null || \
    echo "Could not create libfuse3.so symlink" && \
    # Create alternative names the library might look for \
    ln -sf /usr/lib/libfuse3.so /usr/lib/fuse3.so 2>/dev/null || true && \
    # Verify what we have \
    ls -la /usr/lib/*fuse* /lib/*fuse* 2>/dev/null || echo "No FUSE libraries found" && \
    rm -rf /var/cache/apk/*
COPY --from=build-env /nzb-webdav/publish ./
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
CMD ["/entrypoint.sh"]