#!/usr/bin/env bash
# Deletes the local cluster VM and everything in it (database included).
set -euo pipefail
VM_NAME="${VM_NAME:-microk8s-vm}"
multipass delete "$VM_NAME"
multipass purge
