#!/bin/bash

# Test script for FUSE and NFS functionality
# Usage: ./test-fuse-nfs.sh [server-ip]

set -e

SERVER_IP=${1:-localhost}
MOUNT_POINT="/tmp/nzbdav-test"
NFS_MOUNT_POINT="/tmp/nzbdav-nfs-test"

echo "🧪 Testing FUSE and NFS functionality for nzbwebdav"
echo "   Server: $SERVER_IP"
echo ""

# Function to clean up on exit
cleanup() {
    echo "🧹 Cleaning up..."
    if mountpoint -q "$MOUNT_POINT" 2>/dev/null; then
        sudo umount "$MOUNT_POINT" || true
    fi
    if mountpoint -q "$NFS_MOUNT_POINT" 2>/dev/null; then
        sudo umount "$NFS_MOUNT_POINT" || true
    fi
    rmdir "$MOUNT_POINT" 2>/dev/null || true
    rmdir "$NFS_MOUNT_POINT" 2>/dev/null || true
}

trap cleanup EXIT

# Test 1: Check API status
echo "1️⃣ Testing API status..."
if curl -s "http://$SERVER_IP:3000/api/fuse/status" | grep -q '"enabled"'; then
    echo "   ✅ API is responding"
    curl -s "http://$SERVER_IP:3000/api/fuse/status" | jq '.' 2>/dev/null || echo "   📄 Raw response received"
else
    echo "   ❌ API not responding or FUSE disabled"
    exit 1
fi
echo ""

# Test 2: Check NFS server
echo "2️⃣ Testing NFS server..."
if timeout 5 rpcinfo -p "$SERVER_IP" 2>/dev/null | grep -q nfs; then
    echo "   ✅ NFS server is running"
    echo "   📋 Available NFS exports:"
    timeout 5 showmount -e "$SERVER_IP" 2>/dev/null || echo "   ⚠️  Could not list exports"
else
    echo "   ❌ NFS server not responding"
    echo "   💡 Make sure docker-compose with NFS is running"
fi
echo ""

# Test 3: Try NFS mount
echo "3️⃣ Testing NFS mount..."
mkdir -p "$NFS_MOUNT_POINT"

if timeout 10 sudo mount -t nfs -o vers=4,ro,soft,timeo=5 "$SERVER_IP:/nzbwebdav" "$NFS_MOUNT_POINT" 2>/dev/null; then
    echo "   ✅ NFS mount successful"
    
    echo "   📁 Directory structure:"
    ls -la "$NFS_MOUNT_POINT" 2>/dev/null || echo "   ⚠️  Could not list contents"
    
    echo "   🔍 Testing read access..."
    if [ -d "$NFS_MOUNT_POINT/content" ]; then
        find "$NFS_MOUNT_POINT/content" -type f -name "*.mkv" -o -name "*.mp4" -o -name "*.avi" | head -3 | while read file; do
            echo "   📹 Found: $(basename "$file")"
        done
    fi
    
    sudo umount "$NFS_MOUNT_POINT"
    echo "   ✅ NFS unmount successful"
else
    echo "   ❌ NFS mount failed"
    echo "   💡 Check if NFS server is running and accessible"
fi
echo ""

# Test 4: Performance test
echo "4️⃣ Testing performance..."
echo "   📊 NFS Performance test..."
mkdir -p "$NFS_MOUNT_POINT"

if timeout 10 sudo mount -t nfs -o vers=4,ro,soft,timeo=5 "$SERVER_IP:/nzbwebdav" "$NFS_MOUNT_POINT" 2>/dev/null; then
    echo "   ⏱️  Directory listing performance:"
    time ls "$NFS_MOUNT_POINT" >/dev/null 2>&1 || echo "   ⚠️  Could not time directory listing"
    
    # Find a test file
    TEST_FILE=$(find "$NFS_MOUNT_POINT/content" -type f -size +1M 2>/dev/null | head -1)
    if [ -n "$TEST_FILE" ]; then
        echo "   📖 Testing read performance on: $(basename "$TEST_FILE")"
        echo "      Reading first 1MB..."
        time dd if="$TEST_FILE" of=/dev/null bs=1M count=1 2>/dev/null || echo "   ⚠️  Could not test read performance"
    else
        echo "   ⚠️  No suitable test files found"
    fi
    
    sudo umount "$NFS_MOUNT_POINT"
else
    echo "   ❌ Could not mount for performance testing"
fi
echo ""

# Test 5: Connection info
echo "5️⃣ Connection Information..."
echo "   🌐 WebDAV: http://$SERVER_IP:3000/"
echo "   📂 FUSE: Check container logs for mount status"
echo "   🔗 NFS: mount -t nfs -o vers=4,ro $SERVER_IP:/nzbwebdav /your/mount/point"
echo ""

# Test 6: Client examples
echo "6️⃣ Client Examples..."
echo "   🐧 Linux NFS mount:"
echo "      sudo mount -t nfs -o vers=4,ro $SERVER_IP:/nzbwebdav /mnt/nzbdav"
echo ""
echo "   🍎 macOS NFS mount:"
echo "      sudo mount -t nfs -o vers=4,ro $SERVER_IP:/nzbwebdav /Volumes/nzbdav"
echo ""
echo "   🪟 Windows NFS mount:"
echo "      mount -o anon \\\\$SERVER_IP\\nzbwebdav Z:"
echo ""

# Test 7: Troubleshooting
echo "7️⃣ Troubleshooting..."
echo "   📋 Useful commands:"
echo "      docker-compose logs nfs-server     # Check NFS logs"
echo "      docker-compose ps                 # Check service status"
echo "      curl http://$SERVER_IP:3000/api/fuse/status  # Check FUSE status"
echo "      rpcinfo -p $SERVER_IP             # Check RPC services"
echo "      showmount -e $SERVER_IP           # List NFS exports"
echo ""

echo "✅ Test completed!"
echo "   If you see errors above, check the FUSE_NFS_SETUP.md documentation"
echo "   for detailed troubleshooting steps."
