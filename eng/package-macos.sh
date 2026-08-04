#!/bin/sh
set -eu

if [ "$#" -ne 2 ]; then
  echo "Usage: $0 <publish-directory> <output-directory>" >&2
  exit 2
fi

publish_directory=$1
output_directory=$2
bundle_directory="$output_directory/Orvian.app"

if [ ! -x "$publish_directory/Orvian" ]; then
  echo "Published Orvian executable was not found." >&2
  exit 1
fi

if [ -e "$bundle_directory" ]; then
  echo "Output bundle already exists: $bundle_directory" >&2
  exit 1
fi

mkdir -p "$bundle_directory/Contents/MacOS"
mkdir -p "$bundle_directory/Contents/Resources"
cp "eng/macos/Info.plist" "$bundle_directory/Contents/Info.plist"
ditto "$publish_directory" "$bundle_directory/Contents/MacOS"
chmod 755 "$bundle_directory/Contents/MacOS/Orvian"
codesign --force --deep --sign - "$bundle_directory"
codesign --verify --deep --strict "$bundle_directory"

echo "$bundle_directory"
