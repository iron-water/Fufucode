#!/bin/sh
# Run in WSL2 after installing Docker Engine (API >=1.54), Rust, Node >=22.19,
# a C compiler and /dev/fuse. All paths are Linux paths, on a Linux filesystem.
set -eu
if test "$#" -ne 4; then
  printf '%s\n' 'Usage: sudo sh install-wsl.sh SOURCE_CHECKOUT BUILT_TARGET NODE_DIRECTORY IMAGE_ID'
  exit 2
fi
source_path=$(realpath "$1")
target_path=$(realpath "$2")
node_path=$(realpath "$3")
image_id=$4
base=/opt/fufucode-layerfs
state=/var/lib/fufucode-layerfs
case "$image_id" in sha256:*) ;; *) printf '%s\n' 'Use the exact Docker image ID'; exit 2;; esac
for item in "$source_path/api/runtime/src/index.js" "$source_path/api/agent/sdk/src/index.js" "$target_path/layerfs-daemon" "$target_path/liblayerfs_node.so" "$node_path/bin/node"; do
  test -f "$item" || { printf 'Missing: %s\n' "$item"; exit 2; }
done
test ! -e "$state/config.json" || { printf '%s\n' 'Existing configuration retained; choose explicit upgrade steps.'; exit 2; }
install -d -m 755 "$base"
install -d -m 700 "$state" "$state/home"
ln -s "$source_path" "$base/source"
ln -s "$node_path" "$base/node"
install -m 755 "$target_path/layerfs-daemon" "$base/layerfs-daemon"
install -m 755 "$target_path/liblayerfs_node.so" "$base/layerfs.node"
export PATH="$node_path/bin:$PATH"
npm --prefix "$source_path/api" install --ignore-scripts --no-audit --no-fund
npm --prefix "$source_path/api/agent/cli" install --ignore-scripts --no-audit --no-fund
node -e 'const fs=require("node:fs");const [base,state,image]=process.argv.slice(1);fs.writeFileSync(state+"/config.json",JSON.stringify({source:base+"/source",native:base+"/layerfs.node",daemon:base+"/layerfs-daemon",home:state+"/home",image},null,2),{mode:0o600})' "$base" "$state" "$image_id"
sha256sum "$base/layerfs-daemon" "$base/layerfs.node"
printf 'Installed config: %s/config.json\n' "$state"
