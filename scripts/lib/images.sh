# Shared by the deploy scripts: tag images immutably and make them available to the target cluster.
# Works with Docker Desktop's Kubernetes (images are used straight from the local Docker engine) and with MicroK8s
# (images are pushed to its built-in registry at localhost:32000).

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

cluster_kind() {
  case "$(kubectl config current-context 2>/dev/null)" in
    docker-desktop) echo docker-desktop ;;
    *) echo microk8s ;;
  esac
}

# Prints the image reference the cluster should use, after making the image available to it.
publish_image() {
  local image="$1"
  if [[ "$(cluster_kind)" == docker-desktop ]]; then
    # Docker Desktop's Kubernetes runs on the same engine that built the image: nothing to push.
    echo "$image"
    return
  fi

  # MicroK8s: pods pull from localhost:32000 (the node's own registry). From this machine that registry is
  # reached through the VM address (Multipass on macOS/Windows) or localhost (Linux, CI).
  if multipass info "$VM_NAME" >/dev/null 2>&1; then
    local vm_ip archive
    vm_ip="$(multipass info "$VM_NAME" --format csv | awk -F, 'NR==2 {print $3}')"
    archive="$(mktemp -d)/image.tar"
    docker save "$image" -o "$archive"
    # Plain-HTTP registry inside the local VM; crane pushes without changing Docker Desktop's settings.
    crane push --insecure "$archive" "$vm_ip:32000/$image" >/dev/null 2>&1
    rm -rf "$(dirname "$archive")"
  else
    docker tag "$image" "localhost:32000/$image"
    docker push -q "localhost:32000/$image" >/dev/null
  fi
  echo "localhost:32000/$image"
}
