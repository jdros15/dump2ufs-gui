#!/bin/bash
set -euo pipefail

FUSE_PID=""
OUTPUT_FILENAME=""
UFS_LABEL=""
NO_CONFIRMATION=false
INPUT_PATH=""

# Parse command-line arguments
while getopts "o:l:i:y" opt; do
    case $opt in
        o)
            OUTPUT_FILENAME="$OPTARG"
            ;;
        l)
            UFS_LABEL="$OPTARG"
            ;;
        i)
            INPUT_PATH="$OPTARG"
            ;;
        y)
            NO_CONFIRMATION=true
            ;;
        *)
            echo "Usage: $0 -i input_path [-l ufs_label] [-y] -o output_filename"
            echo "  -i: Input path (file or directory) (required)"
            echo "  -l: UFS filesystem label (max 16 chars, default: auto-generated from title info)"
            echo "  -y: Skip confirmation prompt"
            echo "  -o: Output filename (required)"
            exit 1
            ;;
    esac
done

# Validate required parameters
if [ -z "$OUTPUT_FILENAME" ]; then
    echo "Error: -o output_filename is required"
    echo "Usage: $0 -i input_path [-l ufs_label] [-y] -o output_filename"
    exit 1
fi

if [ -z "$INPUT_PATH" ]; then
    echo "Error: -i input_path is required"
    echo "Usage: $0 -i input_path [-l ufs_label] [-y] -o output_filename"
    exit 1
fi

# Cleanup function to shut down fuse-archive
cleanup() {
    if [ -n "$FUSE_PID" ]; then
        echo "Shutting down fuse-archive..."
        kill "$FUSE_PID" 2>/dev/null || true
        wait "$FUSE_PID" 2>/dev/null || true
    fi
    if mountpoint -q /archive 2>/dev/null; then
        umount /archive 2>/dev/null || true
    fi
}

trap cleanup EXIT

# Check if /output is mounted
if [ ! -d /output ]; then
    echo "Error: /output directory does not exist. Please mount an output directory."
    exit 1
fi

# Validate UFS_LABEL if provided
if [ -n "$UFS_LABEL" ]; then
    if [ ${#UFS_LABEL} -gt 16 ]; then
        echo "Error: UFS_LABEL can't exceed 16 chars (provided: '$UFS_LABEL')"
        exit 1
    fi
    if [[ ! "$UFS_LABEL" =~ ^[A-Za-z0-9._-]+$ ]]; then
        echo "Error: UFS_LABEL can only contain letters, numbers, dots, underscores, and hyphens (provided: '$UFS_LABEL')"
        exit 1
    fi
fi

# Function to check if we have SYS_ADMIN capability
has_sys_admin() {
    # CAP_SYS_ADMIN is bit 21 (0x200000 in hex)
    # Read effective capabilities from /proc/self/status
    local cap_eff=$(grep CapEff /proc/self/status 2>/dev/null | awk '{print $2}')
    if [ -n "$cap_eff" ]; then
        local cap_dec=$((16#$cap_eff))
        local sys_admin_bit=2097152  # CAP_SYS_ADMIN is bit 21 (1 << 21)
        if [ $(( cap_dec & sys_admin_bit )) -ne 0 ]; then
            return 0
        fi
    fi
    return 1
}

# Function to mount an archive file with fuse-archive
mount_with_fuse_archive() {
    local source_file="$1"
    echo "Mounting $source_file with fuse-archive..."
    
    # Start fuse-archive in the background, redirecting its output to stdout
    fuse-archive -o nocache,nospecials,nosymlinks,nohardlinks,noxattrs,umask=0000,dmask=0000,fmask=0000,clone_fd -f -v "$source_file" /archive 2>&1 &
    FUSE_PID=$!
    
    # Wait until /archive is available
    echo "Waiting for /archive/ to become available..."
    while true; do
        # Check if fuse-archive process is still running
        if ! kill -0 $FUSE_PID 2>/dev/null; then
            echo "Error: fuse-archive process exited before /archive became available."
            if ! has_sys_admin; then
                echo "Most likely because the SYS_ADMIN capability has not been granted to the container."
            fi
            echo "When providing an archive file as input, make sure to use a format supported by fuse-archive, ensure that the file is not corrupted, and add --device /dev/fuse --cap-add SYS_ADMIN to the docker run command."
            exit 1
        fi
        
        if mountpoint -q /archive 2>/dev/null || [ "$(ls -A /archive 2>/dev/null)" ]; then
            echo "/archive/ is now available"
            break
        fi
        sleep 0.5
    done
    
    # Check for sce_sys/param.json file
    if [ -f /archive/sce_sys/param.json ]; then
        echo "Found sce_sys/param.json in /archive"
        SOURCE_DIR="/archive"
    else
        echo "sce_sys/param.json not in root, checking subdirectories (one level deep)..."
        found=false
        while IFS= read -r subdir; do
            if [ -f "$subdir/sce_sys/param.json" ]; then
                echo "Found sce_sys/param.json in $subdir"
                SOURCE_DIR="$subdir"
                found=true
                break
            fi
        done < <(find /archive -maxdepth 1 -type d ! -path /archive)
        
        if [ "$found" = false ]; then
            echo "Error: sce_sys/param.json file not found in the root of, or any subdirectory of the archive: $source_file. Are you sure this is a valid PS5 dump?"
            exit 1
        fi
    fi
}

# Check if input path exists and is a file or directory
if [ -f "$INPUT_PATH" ]; then
    echo "Detected input as a file: $INPUT_PATH"
    
    # Verify FUSE requirements for archive files
    if [ ! -e /dev/fuse ]; then
        echo "Error: /dev/fuse not found. Add --device /dev/fuse to the docker run command."
        exit 1
    fi
    
    mount_with_fuse_archive "$INPUT_PATH"
elif [ -d "$INPUT_PATH" ]; then
    INPUT_PATH="$(realpath "$INPUT_PATH")"
    echo "Detected input as a directory: $INPUT_PATH"
    
    # Check for sce_sys/param.json file
    if [ -f "$INPUT_PATH/sce_sys/param.json" ]; then
        echo "Found sce_sys/param.json, using directory directly"
        SOURCE_DIR="$INPUT_PATH"
    else
        echo "Error: sce_sys/param.json not found in $INPUT_PATH"
        echo "For archive files, provide the full path to the archive file with -i"
        echo "Contents of $INPUT_PATH:"
        ls -1shpd --quoting-style=escape --group-directories-first --color=auto "$INPUT_PATH"/*
        exit 1
    fi
else
    echo "Error: Input path does not exist or is not accessible: $INPUT_PATH"
    exit 1
fi

# Try different block sizes to find the optimal one for the smallest resulting image size
b_values=(4096 8192 16384 32768 65536)

best_size=""
best_b=""
best_f=""

for b in "${b_values[@]}"; do
    f=$(( b / 8 )) # always best

    rm -f /tmp/test.out

    # Capture error message which contains the calculated size of the image with the given block and fragment sizes
    output=$(makefs -b 0 -o b=$b,f=$f,m=0,v=2,o=space -s $b /tmp/test.out "$SOURCE_DIR" 2>&1 || true)

    # Extract the image size from the error message
    size=$(printf '%s\n' "$output" | sed -n 's/.* size of \([0-9]\+\) .*/\1/p' | head -n1)

    if [[ -n "$size" ]]; then
        if [[ -z "$best_size" || "$size" -lt "$best_size" ]]; then
            best_size="$size"
            best_b="$b"
            best_f="$f"
        fi
    fi
done

gb_int=$(( best_size / 1073741824 ))
gb_frac=$(( (best_size % 1073741824) * 10 / 1073741824 ))

DIR_LISTING=$(ls -1shpd --quoting-style=escape --group-directories-first --color=always "$SOURCE_DIR"/*)

# Parse game title and ID from param.json
PARAM_JSON="$SOURCE_DIR/sce_sys/param.json"
# Parse titleId
TITLE_ID=$(jq -r '.titleId // empty' "$PARAM_JSON")
if [ -z "$TITLE_ID" ]; then
    echo "Error: Failed to parse titleId from param.json"
    exit 1
fi
# Parse title name
DEFAULT_LANG=$(jq -r '.localizedParameters.defaultLanguage // empty' "$PARAM_JSON")
if [ -n "$DEFAULT_LANG" ]; then
    TITLE_NAME=$(jq -r --arg lang "$DEFAULT_LANG" '.localizedParameters[$lang].titleName // empty' "$PARAM_JSON")
else
    # Fallback to en-US if defaultLanguage is not present
    TITLE_NAME=$(jq -r '.localizedParameters["en-US"].titleName // empty' "$PARAM_JSON")
fi

if [ -z "$TITLE_NAME" ]; then
    echo "Error: Failed to parse titleName from param.json"
    exit 1
fi

# If no label provided, construct default from title info
if [ -z "$UFS_LABEL" ]; then
    TITLE_NAME_CLEAN=$(echo "$TITLE_NAME" | tr -cd 'A-Za-z0-9')
    TITLE_ID_CLEAN=$(echo "$TITLE_ID" | tr -cd 'A-Za-z0-9')
    UFS_LABEL="${TITLE_ID_CLEAN: -5}${TITLE_NAME_CLEAN:0:11}"
fi

echo ""
echo "Source directory for makefs will be: $SOURCE_DIR"
echo "Content of source directory:"
echo "$DIR_LISTING"
echo "Detected game title: $TITLE_NAME, ID: $TITLE_ID"
echo "The filesystem label will be: $UFS_LABEL"
echo "Detected optimal block size for smallest image: $best_b, fragment size: $best_f"
echo "Resulting UFS2 filesystem image size will be: $best_size bytes (~ ${gb_int}.${gb_frac} GB)"
echo "The output filename will be: $OUTPUT_FILENAME"
echo ""
echo "Will run makefs with this command line:"
echo "makefs -b 0 -Z -o \"b=$best_b,f=$best_f,m=0,v=2,o=space${UFS_LABEL:+,l=$UFS_LABEL}\" \"/output/$OUTPUT_FILENAME\" \"$SOURCE_DIR\""
echo ""
if [ "$NO_CONFIRMATION" = false ]; then
    while true; do
        read -p "Please verify the above is correct. Continue? (y/n): " -r
        if [[ $REPLY =~ ^[Yy]$ ]]; then
            break
        elif [[ $REPLY =~ ^[Nn]$ ]]; then
            echo "Aborted by user."
            exit 1
        else
            echo "Invalid input. Please enter Y or N."
        fi
    done
else
    echo "-y is set, skipping confirmation prompt."
fi

makefs -b 0 -Z -o "b=$best_b,f=$best_f,m=0,v=2,o=space${UFS_LABEL:+,l=$UFS_LABEL}" "/output/$OUTPUT_FILENAME" "$SOURCE_DIR"