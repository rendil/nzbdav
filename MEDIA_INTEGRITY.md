# Media Integrity Verification

This feature provides background verification of media files to detect corruption and ensure usability.

## Overview

The media integrity verification feature:

- Runs automatically in the background when enabled
- Uses ffprobe to verify media file integrity
- Tracks which files have been checked and when
- Provides configurable actions for handling corrupt files
- Exposes a WebSocket API for real-time progress updates
- Includes a REST API for manual triggering

## Configuration

The following configuration options are available:

### `integrity.enabled`

- **Type**: boolean (true/false)
- **Default**: false
- **Description**: Enable/disable the integrity checking feature

### `integrity.interval_hours`

- **Type**: integer
- **Default**: 24
- **Description**: Hours between automatic integrity check runs

### `integrity.interval_days`

- **Type**: integer
- **Default**: 7
- **Description**: Days after which files are eligible for re-checking

### `integrity.max_files_per_run`

- **Type**: integer
- **Default**: 100
- **Description**: Maximum number of files to check in a single run

### `integrity.corrupt_file_action`

- **Type**: string
- **Default**: "log"
- **Options**:
  - `log`: Only log corrupt files (default)
  - `delete`: Delete corrupt files and remove from database
  - `quarantine`: Move corrupt files to quarantine folder

## API Endpoints

### POST /api/mediaintegrity/check

Manually trigger an integrity check.

**Response:**

```json
{
  "success": true,
  "data": {
    "message": "Media integrity check started"
  }
}
```

If a check is already running:

```json
{
  "success": true,
  "data": {
    "message": "Media integrity check is already running"
  }
}
```

## WebSocket Events

Subscribe to the `icp` (Integrity Check Progress) topic to receive real-time updates:

- `"starting"` - Check is beginning
- `"X/Y (Z corrupt)"` - Progress update showing processed/total files and corrupt count
- `"complete: X/Y checked, Z corrupt files found"` - Final completion status
- `"cancelled"` - Check was cancelled
- `"failed: error message"` - Check failed with error

## File Storage

Integrity check results are stored in the configuration database:

- `integrity.last_check.{file-id}`: ISO 8601 timestamp of last check
- `integrity.status.{file-id}`: "valid" or "corrupt" status

## Media File Support

The following media file types are checked:

- **Video**: .mp4, .mkv, .avi, .mov, .wmv, .flv, .webm, .m4v, .mpg, .mpeg, .m2ts, .ts, .mts, .vob, .3gp, .f4v
- **Audio**: .mp3, .flac, .aac, .ogg, .wma, .wav, .m4a

## Dependencies

- **ffprobe**: Part of the FFmpeg suite, automatically installed in Docker containers
- For non-Docker deployments, ensure FFmpeg is installed and ffprobe is in the system PATH

## Example Configuration

To enable integrity checking with custom settings:

```bash
# Enable integrity checking
curl -X POST "http://your-server/api/updateconfig" \
  -H "Content-Type: application/json" \
  -d '{
    "configItems": [
      {"configName": "integrity.enabled", "configValue": "true"},
      {"configName": "integrity.interval_hours", "configValue": "12"},
      {"configName": "integrity.interval_days", "configValue": "3"},
      {"configName": "integrity.max_files_per_run", "configValue": "50"},
      {"configName": "integrity.corrupt_file_action", "configValue": "quarantine"}
    ]
  }'
```

## Logging

Integrity check activities are logged at the following levels:

- **Information**: Check start/completion, overall statistics
- **Warning**: Corrupt files detected, missing files
- **Error**: FFprobe execution errors, file system errors

## Performance Considerations

- Files are checked in batches to avoid overwhelming the system
- Only files that haven't been checked recently are processed
- Background checks run at configurable intervals
- Manual checks can be triggered but won't run concurrently

## Troubleshooting

### FFprobe not found

Ensure FFmpeg is installed:

- **Docker**: Already included in the container
- **Manual install**: Install FFmpeg package for your OS

### High CPU usage

Reduce `integrity.max_files_per_run` or increase `integrity.interval_hours`

### Files incorrectly marked as corrupt

Check ffprobe logs for specific error details. Some files may have minor issues that don't affect playability.
