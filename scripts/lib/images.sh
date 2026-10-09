# Shared by the deploy scripts: build an image, tag it immutably and push it to the cluster's registry.
# (Copied into each project's deploy script in their own repositories; kept here once to avoid drift.)

VM_NAME="${VM_NAME:-microk8s-vm}"

# Unique, immutable tag per build: the commit, plus a timestamp when the working tree has local changes.
image_tag() {
  local dir="$1" tag
  tag="$(git -C "$dir" rev-parse --short HEAD 2>/dev/null || echo dev)"
  if [[ -n "$(git -C "$dir" status --porcelain 2>/dev/null)" ]]; then
    tag="$tag-dirty-$(date +%Y%m%d%H%M%S)"
  fi
  echo "$tag"
}

# Pods always pull from localhost:32000 (MicroK8s' built-in registry, trusted by the node). From the developer machine
# the same registry is reached through the VM's address (macOS/Windows) or localhost (Linux, CI).
push_image() {
  local local_image="$1" remote_path="$2"
  if multipass info "$VM_NAME" >/dev/null 2>&1; then
    local vm_ip archive
    vm_ip="$(multipass info "$VM_NAME" --format csv | awk -F, 'NR==2 {print $3}')"
    archive="$(mktemp -d)/image.tar"
    docker save "$local_image" -o "$archive"
    # Plain-HTTP registry inside the local VM; crane pushes without changing Docker Desktop's settings.
    crane push --insecure "$archive" "$vm_ip:32000/$remote_path" >/dev/null
    rm -rf "$(dirname "$archive")"
  else
    docker tag "$local_image" "localhost:32000/$remote_path"
    docker push -q "localhost:32000/$remote_path" >/dev/null
  fi
}
