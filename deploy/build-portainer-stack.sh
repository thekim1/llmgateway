#!/bin/sh
# Generates compose.portainer.yaml from compose.portainer.template.yaml by inlining the files
# named in "@INCLUDE path" lines (indented, with $ escaped as $$ for Compose interpolation).
# Run after changing the template, portainer/bootstrap.sh, or the postgres/redis config files.
set -eu
cd "$(dirname "$0")"
awk '
  match($0, /^ *@INCLUDE /) {
    indent = substr($0, 1, RLENGTH - 9); file = substr($0, RLENGTH + 1)
    while ((getline line < file) > 0) {
      gsub(/\$/, "$$", line)
      print (line == "" ? "" : indent line)
    }
    close(file); next
  }
  { print }
' compose.portainer.template.yaml > compose.portainer.yaml
echo "Wrote deploy/compose.portainer.yaml"
