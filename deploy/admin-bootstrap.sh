#!/bin/sh
# One-time step, run as a cluster admin on the k8s host (Jenkins' own account cannot do it):
#   curl -fsSL https://raw.githubusercontent.com/jatupolman3CS/siri_autopost_backend/main/deploy/admin-bootstrap.sh | sh
# Creates namespace siriautopost and lets the Jenkins service account ci:jenkins-deployer manage it
# (ClusterRole `edit`, bound in this namespace only).
set -eu

NS=siriautopost
SA=system:serviceaccount:ci:jenkins-deployer

kubectl get namespace "$NS" >/dev/null 2>&1 || kubectl create namespace "$NS"
kubectl -n "$NS" create rolebinding jenkins-deployer --clusterrole=edit --serviceaccount=ci:jenkins-deployer \
    --dry-run=client -o yaml | kubectl apply -f -

for verb_res in "create secrets" "patch deployments" "create services" "create configmaps"; do
    # shellcheck disable=SC2086
    printf '%-20s %s\n' "$verb_res" "$(kubectl -n "$NS" auth can-i $verb_res --as="$SA")"
done
