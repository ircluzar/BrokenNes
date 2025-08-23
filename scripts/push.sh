#!/usr/bin/env bash
# push.sh - Deploy to staging server via FTP

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CREDENTIALS_FILE="$SCRIPT_DIR/ftp_credentials.conf"

if [[ ! -f "$CREDENTIALS_FILE" ]]; then
    echo "Error: FTP credentials file not found at $CREDENTIALS_FILE" >&2
    exit 1
fi

# Source the credentials
source "$CREDENTIALS_FILE"

# Optional: allow choosing ftp or ftps (explicit TLS) via creds file
# Defaults to plain ftp if not provided
FTP_SCHEME="${FTP_SCHEME:-ftp}"
FTP_PORT_PART=""
if [[ -n "${FTP_PORT:-}" ]]; then
        FTP_PORT_PART=":${FTP_PORT}"
fi
BASE_URL="${FTP_SCHEME}://${FTP_HOST}${FTP_PORT_PART}"

# Precompute TLS settings for lftp (avoid inline conditionals inside heredocs)
if [[ "$FTP_SCHEME" == "ftps" ]]; then
    LFTP_TLS_SETTINGS=$'set ftp:ssl-allow yes\nset ftp:ssl-force yes\nset ftp:ssl-protect-data yes\nset ftp:ssl-protect-list yes'
else
    LFTP_TLS_SETTINGS=$'set ftp:ssl-allow no'
fi

# Verify credentials were loaded
if [[ -z "${FTP_HOST:-}" ]] || [[ -z "${FTP_USER:-}" ]] || [[ -z "${FTP_PASS:-}" ]] || [[ -z "${FTP_PATH:-}" ]]; then
    echo "Error: Missing required FTP credentials" >&2
    exit 1
fi

# Check if curl is available
if ! command -v curl >/dev/null 2>&1; then
    echo "Error: curl is not available" >&2
    exit 1
fi

echo "Connecting to FTP server: $FTP_HOST"
echo "Listing directory: $FTP_PATH"
echo

# Use curl to list FTP directory contents
curl -s --connect-timeout 10 --max-time 30 \
    --user "$FTP_USER:$FTP_PASS" \
    "$BASE_URL$FTP_PATH/" \
     2>/dev/null || {
    echo "Error: Failed to connect to FTP server or list directory" >&2
    echo "Please check your credentials and network connection" >&2
    exit 1
}

echo
echo "FTP directory listing complete"
echo

echo "Attempting to empty target directory: $FTP_PATH"
echo "WARNING: This will attempt to delete all files in the remote directory!"
echo

# Check if lftp is available for better FTP operations
if command -v lftp >/dev/null 2>&1; then
    echo "Using lftp for directory cleanup..."
    
    # Get the file list and delete files one by one since rm -rf * doesn't work
    echo "Getting file list for individual deletion..."
    
    # Create a temporary script for lftp commands
    TEMP_SCRIPT=$(mktemp)
    trap 'rm -f "$TEMP_SCRIPT"' EXIT
    
    # Generate delete commands for each file/directory
    curl -s --connect-timeout 10 --max-time 30 \
         --user "$FTP_USER:$FTP_PASS" \
         "$BASE_URL$FTP_PATH/" 2>/dev/null | \
    while IFS= read -r line; do
        [[ -z "$line" ]] && continue
        # Extract filename (last field, handling spaces in names)
        filename=$(echo "$line" | awk '{for(i=9;i<=NF;i++) printf "%s%s", $i, (i<NF?" ":""); print ""}' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
        # Skip if no filename or special entries
        [[ -z "$filename" || "$filename" == "." || "$filename" == ".." ]] && continue
        # Check if it's a directory (starts with 'd') and add appropriate commands
        if [[ "$line" =~ ^d ]]; then
            echo "rm -r \"$filename\"" >> "$TEMP_SCRIPT"
        else
            echo "rm \"$filename\"" >> "$TEMP_SCRIPT"
        fi
    done

    if [[ -s "$TEMP_SCRIPT" ]]; then
        echo "Executing individual deletion commands..."
        # Execute the deletion script
        {
            echo "set ssl:verify-certificate no"
            printf '%s\n' "$LFTP_TLS_SETTINGS"
            echo "set ftp:passive-mode yes"
            echo "set ftp:prefer-epsv false"
            echo "set ftp:use-allo false"
            echo "set ftp:use-mdtm false"
            echo "set ftp:use-stat false"
            echo "set net:socket-buffer 1048576"
            echo "open ${FTP_SCHEME}://$FTP_USER:$FTP_PASS@$FTP_HOST${FTP_PORT_PART}"
            echo "cd $FTP_PATH"
            cat "$TEMP_SCRIPT"
            echo "quit"
        } | lftp 2>/dev/null

        echo "Individual file deletion completed"

        # Check if directory is empty now
        remaining_files=$(curl -s --connect-timeout 10 --max-time 30 \
                          --user "$FTP_USER:$FTP_PASS" \
                          "$BASE_URL$FTP_PATH/" 2>/dev/null | wc -l)
        if [[ "$remaining_files" -eq 0 ]]; then
            echo "✓ Directory successfully emptied"
        else
            echo "⚠ Directory still contains $remaining_files items"
            echo "Some files or directories may have failed to delete"
        fi
    else
        echo "No files found to delete or could not generate deletion commands"
    fi

else
    echo "lftp not available - skipping directory cleanup"
    echo "Files will be overwritten during upload process"
fi

echo
echo "Directory preparation complete"

# Build the application using flatpublish.sh
echo
echo "==> Building application with flatpublish.sh"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FLATPUBLISH_SCRIPT="$SCRIPT_DIR/flatpublish.sh"

if [[ ! -f "$FLATPUBLISH_SCRIPT" ]]; then
    echo "Error: flatpublish.sh not found at $FLATPUBLISH_SCRIPT" >&2
    exit 1
fi

# Run flatpublish script
if ! "$FLATPUBLISH_SCRIPT"; then
    echo "Error: flatpublish.sh failed" >&2
    exit 1
fi

# Check if flatpublish directory exists and has content
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
FLATPUBLISH_DIR="$ROOT_DIR/flatpublish"

if [[ ! -d "$FLATPUBLISH_DIR" ]]; then
    echo "Error: flatpublish directory not found at $FLATPUBLISH_DIR" >&2
    exit 1
fi

# Count files to upload
FILE_COUNT=$(find "$FLATPUBLISH_DIR" -type f | wc -l | tr -d ' ')
echo "Found $FILE_COUNT files to upload from $FLATPUBLISH_DIR"

if [[ "$FILE_COUNT" -eq 0 ]]; then
    echo "Error: No files found in flatpublish directory" >&2
    exit 1
fi

# Upload files using lftp
echo
echo "==> Uploading files to FTP server"

if command -v lftp >/dev/null 2>&1; then
    echo "Using lftp for file upload with optimized settings..."
    
    # Use lftp with optimized settings for faster transfers (plain FTP or FTPS, PASV, no EPSV)
    lftp << EOF
set ssl:verify-certificate no
${LFTP_TLS_SETTINGS}
set ftp:passive-mode yes
set ftp:prefer-epsv false
set ftp:sync-mode off
set ftp:use-allo false
set ftp:use-mdtm false
set ftp:use-stat false
set net:socket-buffer 1048576
set net:timeout 10
set net:max-retries 1
set net:connection-limit 12
set net:limit-rate 0
set cmd:parallel 8
set cmd:queue-parallel 8
open ${FTP_SCHEME}://$FTP_USER:$FTP_PASS@$FTP_HOST${FTP_PORT_PART}
cd $FTP_PATH
set mirror:use-pget-n 4
mirror -R \
    --delete \
    --parallel=8 \
    --use-pget-n=4 \
    --no-perms --no-umask \
    --only-newer \
    --verbose=0 \
    "$FLATPUBLISH_DIR/" .
quit
EOF
    
    echo "File upload completed"
    
    # Verify upload by checking file count on server
    echo "Verifying upload..."
    uploaded_count=$(curl -s --connect-timeout 10 --max-time 30 \
                          --user "$FTP_USER:$FTP_PASS" \
                          "$BASE_URL$FTP_PATH/" 2>/dev/null | wc -l | tr -d ' ')
    
    echo "Server now contains $uploaded_count items (expected: ~$FILE_COUNT)"
    
    if [[ "$uploaded_count" -gt 0 ]]; then
        echo "✅ Deployment successful!"
        echo "🌐 Your site should now be live at your staging URL"
    else
        echo "⚠️  Upload verification failed - server appears empty"
    fi
    
else
    echo "Error: lftp not available for file upload" >&2
    exit 1
fi

echo
echo "==> Deployment complete"
