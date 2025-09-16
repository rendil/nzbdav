# FUSE and NFS Integration Guide

This guide explains how to set up and use the FUSE filesystem and NFS sharing capabilities of nzbwebdav.

## Overview

The nzbwebdav project now supports exposing content through multiple interfaces:

1. **WebDAV** - Original web-based file access (existing)
2. **FUSE Filesystem** - Direct filesystem mount on Linux (new)
3. **NFS** - Network File System sharing (new)

This allows you to access your nzb content through standard filesystem operations and share it over the network using NFS.

## Architecture

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   nzbwebdav     │    │  FUSE Mount      │    │   NFS Server    │
│   (database +   │───▶│  /mnt/nzbwebdav  │───▶│   Port 2049     │
│   usenet)       │    │                  │    │                 │
└─────────────────┘    └──────────────────┘    └─────────────────┘
         │                       │                       │
         ▼                       ▼                       ▼
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   WebDAV        │    │  Local FS        │    │ Remote Clients  │
│   Clients       │    │  Access          │    │ (NFS mounts)    │
└─────────────────┘    └──────────────────┘    └─────────────────┘
```

## Prerequisites

### For FUSE Support

- Linux system with FUSE kernel module
- `/dev/fuse` device access
- `CAP_SYS_ADMIN` capability or privileged container

### For NFS Support

- Docker and Docker Compose
- Network access to NFS ports (2049 for NFSv4)
- NFS client software on accessing machines

## Quick Start

### 1. Enable FUSE Filesystem

Add these environment variables to your nzbwebdav configuration:

```bash
# Enable FUSE
FUSE_ENABLED=true
# Set mount point (optional, defaults to /mnt/nzbwebdav)
FUSE_MOUNT_POINT=/mnt/nzbwebdav
```

### 2. Start with Docker Compose

Use the provided Docker Compose configuration:

```bash
# Start both nzbwebdav and NFS server
docker-compose -f docker-compose.yml -f docker-compose.override.yml up -d
```

### 3. Access Your Content

#### Local Filesystem Access

```bash
# List content
ls /mnt/nzbwebdav
# Shows: nzbs/ content/ completed-symlinks/ .ids/

# Access files directly
cat "/mnt/nzbwebdav/content/Movie Name (2023)/movie.mkv"
```

#### NFS Mount on Client

```bash
# Mount on client machine
sudo mkdir -p /mnt/nzbwebdav-nfs
sudo mount -t nfs -o vers=4,ro your-server-ip:/nzbwebdav /mnt/nzbwebdav-nfs

# Access files
ls /mnt/nzbwebdav-nfs
```

## Configuration

### Environment Variables

| Variable           | Default          | Description            |
| ------------------ | ---------------- | ---------------------- |
| `FUSE_ENABLED`     | `false`          | Enable FUSE filesystem |
| `FUSE_MOUNT_POINT` | `/mnt/nzbwebdav` | Local mount point      |

### Database Configuration

You can also configure these settings via the web interface or database:

```sql
INSERT INTO ConfigItems (ConfigName, ConfigValue) VALUES
('fuse.enabled', 'true'),
('fuse.mount-point', '/mnt/nzbwebdav');
```

## Docker Compose Configuration

### Option 1: Using Override File (Recommended)

Create or update `docker-compose.override.yml`:

```yaml
version: "3.8"
services:
  nzbwebdav:
    environment:
      FUSE_ENABLED: "true"
      FUSE_MOUNT_POINT: "/mnt/nzbwebdav"
    volumes:
      - fuse-mount:/mnt/nzbwebdav:shared
    privileged: true
    devices:
      - /dev/fuse:/dev/fuse
    cap_add:
      - SYS_ADMIN

  nfs-server:
    image: erichough/nfs-server:latest
    privileged: true
    restart: unless-stopped
    ports:
      - "2049:2049"
    volumes:
      - fuse-mount:/nzbwebdav:ro
      - ./nfs-exports.txt:/etc/exports:ro
    environment:
      NFS_EXPORT_0: "/nzbwebdav *(ro,sync,no_subtree_check,no_root_squash,insecure,crossmnt)"
    depends_on:
      - nzbwebdav

volumes:
  fuse-mount:
    driver: local
```

### Option 2: Standalone NFS Configuration

Use `docker-compose.nfs.yml` for a complete setup:

```bash
docker-compose -f docker-compose.nfs.yml up -d
```

## NFS Export Configuration

Create `nfs-exports.txt` to configure NFS sharing:

```
# Read-only access from any IP (adjust for security)
/nzbwebdav *(ro,sync,no_subtree_check,no_root_squash,insecure,crossmnt)

# Restrict to specific networks
/nzbwebdav 192.168.1.0/24(ro,sync,no_subtree_check,no_root_squash,insecure,crossmnt)
/nzbwebdav 10.0.0.0/8(ro,sync,no_subtree_check,no_root_squash,insecure,crossmnt)

# Specific hosts only
/nzbwebdav client1.example.com(ro,sync,no_subtree_check,no_root_squash,insecure,crossmnt)
```

### NFS Export Options Explained

- `ro` - Read-only access (recommended for safety)
- `sync` - Synchronous writes (safer but slower)
- `no_subtree_check` - Disable subtree checking (better performance)
- `no_root_squash` - Allow root access (adjust based on security needs)
- `insecure` - Allow connections from ports > 1024
- `crossmnt` - Allow crossing mount points

## Client Access

### NFS v4 Mount (Recommended)

```bash
# Create mount point
sudo mkdir -p /mnt/nzbwebdav-remote

# Mount with NFSv4
sudo mount -t nfs -o vers=4,ro server-ip:/nzbwebdav /mnt/nzbwebdav-remote

# Permanent mount in /etc/fstab
echo "server-ip:/nzbwebdav /mnt/nzbwebdav-remote nfs vers=4,ro,defaults 0 0" >> /etc/fstab
```

### NFS v3 Mount (Legacy)

```bash
# Mount with NFSv3 (requires additional ports)
sudo mount -t nfs -o vers=3,ro server-ip:/nzbwebdav /mnt/nzbwebdav-remote
```

### Windows Client

```cmd
# Mount on Windows (requires NFS client feature)
mount -o anon \\server-ip\nzbwebdav Z:
```

### macOS Client

```bash
# Mount on macOS
sudo mkdir -p /Volumes/nzbwebdav
sudo mount -t nfs -o vers=4,ro server-ip:/nzbwebdav /Volumes/nzbwebdav
```

## Monitoring and Status

### Check FUSE Status via API

```bash
# Get FUSE status
curl http://your-server/api/fuse/status

# Get mount information
curl http://your-server/api/fuse/mount-info
```

### Check NFS Server Status

```bash
# Check NFS server is running
docker-compose ps nfs-server

# View NFS logs
docker-compose logs nfs-server

# Check NFS exports
docker-compose exec nfs-server exportfs -v
```

### Check Mount Status

```bash
# Check if FUSE is mounted
mount | grep fuse

# Check if NFS is accessible
showmount -e your-server-ip

# Test NFS connectivity
rpcinfo -p your-server-ip
```

## Directory Structure

The FUSE filesystem exposes the same structure as WebDAV:

```
/mnt/nzbwebdav/
├── nzbs/                    # Raw NZB files
├── content/                 # Extracted content
├── completed-symlinks/      # Symlinks to completed downloads
└── .ids/                   # Files accessible by ID
```

### Content Examples

```
/mnt/nzbwebdav/content/
├── Movie.Name.2023.1080p.BluRay.x264/
│   ├── movie.mkv
│   ├── sample.mkv
│   └── subtitles.srt
└── TV.Show.S01E01.1080p.WEB.x264/
    └── episode.mkv
```

## Troubleshooting

### FUSE Issues

**FUSE mount fails:**

```bash
# Check FUSE is available
ls -la /dev/fuse

# Check kernel module
lsmod | grep fuse

# Load FUSE module if needed
sudo modprobe fuse
```

**Permission denied:**

```bash
# Ensure container has required capabilities
# Add to docker-compose.yml:
privileged: true
cap_add:
  - SYS_ADMIN
devices:
  - /dev/fuse:/dev/fuse
```

**Mount point busy:**

```bash
# Unmount existing mount
fusermount -u /mnt/nzbwebdav

# Or force unmount
sudo umount -l /mnt/nzbwebdav
```

### NFS Issues

**NFS server won't start:**

```bash
# Check privileged mode
# Ensure container runs with: privileged: true

# Check port conflicts
netstat -tulpn | grep 2049

# View detailed logs
docker-compose logs -f nfs-server
```

**Clients can't connect:**

```bash
# Check firewall
sudo ufw allow 2049

# For NFSv3, allow additional ports:
sudo ufw allow 111
sudo ufw allow 32765:32767
```

**Mount hangs or times out:**

```bash
# Test with different NFS version
mount -t nfs -o vers=3 server:/path /mount

# Use soft mount to avoid hangs
mount -t nfs -o soft,timeo=10 server:/path /mount
```

### Performance Issues

**Slow read performance:**

- Consider using NFSv4 instead of NFSv3
- Increase read buffer size: `rsize=65536`
- Use async mounts (less safe): `async`

**High memory usage:**

- The FUSE filesystem caches file handles
- Monitor container memory usage
- Restart service periodically if needed

## Security Considerations

### Network Security

- Restrict NFS access to trusted networks only
- Use firewall rules to limit access
- Consider VPN for remote access

### File Permissions

- NFS exports are read-only by default
- `no_root_squash` allows root access - use carefully
- Consider `all_squash` to map all users to anonymous

### Container Security

- FUSE requires elevated privileges (`CAP_SYS_ADMIN`)
- Run containers with minimal required permissions
- Regularly update base images

## Performance Tuning

### NFS Performance

```bash
# Optimized NFS mount options
mount -t nfs -o vers=4,rsize=65536,wsize=65536,hard,intr server:/path /mount
```

### FUSE Performance

- FUSE filesystem caches frequently accessed files
- Performance depends on usenet connection speed
- Consider SSD storage for database and cache

## Advanced Configuration

### Custom NFS Exports

You can override the default exports by providing your own `nfs-exports.txt`:

```
# Multiple export paths
/nzbwebdav/content *(ro,sync,no_subtree_check)
/nzbwebdav/completed-symlinks *(ro,sync,no_subtree_check)

# Different permissions per network
/nzbwebdav 192.168.1.0/24(ro,no_root_squash)
/nzbwebdav 10.0.0.0/8(ro,root_squash,all_squash)
```

### Multiple Mount Points

```yaml
# docker-compose.override.yml
services:
  nzbwebdav:
    volumes:
      - fuse-content:/mnt/content:shared
      - fuse-nzbs:/mnt/nzbs:shared
    environment:
      FUSE_ENABLED: "true"
      FUSE_MOUNT_POINT: "/mnt/nzbwebdav"
```

## Integration Examples

### Media Server Integration

```bash
# Mount in Plex/Jellyfin media directory
sudo mount -t nfs -o vers=4,ro server:/nzbwebdav/content /var/lib/plexmediaserver/media
```

### Backup Integration

```bash
# Read-only backup of completed content
rsync -av /mnt/nzbwebdav-nfs/completed-symlinks/ /backup/media/
```

### Monitoring Integration

```bash
# Monitor new content
inotifywait -mr /mnt/nzbwebdav-nfs/content --format '%w%f %e' -e create
```

## Support

For issues and questions:

1. Check the API status endpoint: `/api/fuse/status`
2. Review Docker logs: `docker-compose logs`
3. Check the main project documentation
4. Report issues on the project repository

## References

- [FuseDotNet Documentation](https://github.com/LTRData/FuseDotNet)
- [docker-nfs-server Documentation](https://github.com/ehough/docker-nfs-server)
- [Linux NFS Documentation](https://nfs.sourceforge.net/)
- [NFSv4 Specification](https://tools.ietf.org/html/rfc7530)
