// PRD pipeline for siri_autopost_backend (Jenkins job: SIRIAUTOPOST-BACKEND).
// Builds SIRIAUTOPOST.Api into the local registry and deploys the API for
// https://siriautopost.siristudiophoto.com into namespace siriautopost (deploy/k8s/overlays/prd).
//
// Same conventions as the SIRISTUDIOPHOTO jobs: docker + kubectl on the Jenkins host, registry localhost:5000.
// Run this job before SIRIAUTOPOST-WEB the first time: the dashboard's nginx needs Service `api` to exist.
//
// Credential required (Jenkins "Secret file"): siriautopost-env-file, a .env file like the other jobs use.
// deploy/prepare-env.sh picks what the API needs from it (only those keys reach the pod):
//   Database                    Postgres inside the namespace (deploy/k8s/postgres), password generated once into the
//                               postgres-env secret. Put ConnectionStrings__Default in the env file to use another server.
//   Jwt__Key (32+ chars)        or derived from AppSettings__Secret
//   Admin__Email/Admin__Password  optional: creates the platform admin on first start

pipeline {
    agent any

    options {
        timestamps()
        disableConcurrentBuilds()
        timeout(time: 45, unit: 'MINUTES')
        buildDiscarder(logRotator(numToKeepStr: '20'))
    }

    environment {
        K8S_NAMESPACE = 'siriautopost'
        OVERLAY       = 'deploy/k8s/overlays/prd'
        API_IMAGE     = 'localhost:5000/siriautopost-api'
        PUBLIC_URL    = 'https://siriautopost.siristudiophoto.com'
    }

    stages {
        stage('Prepare') {
            steps {
                script {
                    env.IMAGE_TAG = "prd-${env.BUILD_NUMBER}-${env.GIT_COMMIT.take(7)}"
                }
                echo "Image tag: ${env.IMAGE_TAG}"
            }
        }

        stage('Build and push image') {
            steps {
                // Build context is the repo root: the Dockerfile COPYs src/ relative to it.
                sh '''
                    set -eu
                    docker build -f src/SIRIAUTOPOST.Api/Dockerfile -t "$API_IMAGE:$IMAGE_TAG" .
                    docker push "$API_IMAGE:$IMAGE_TAG"
                '''
            }
        }

        stage('Deploy') {
            steps {
                withCredentials([file(credentialsId: 'siriautopost-env-file', variable: 'ENV_FILE')]) {
                    sh '''
                        set +x
                        set -eu
                        K="kubectl -n $K8S_NAMESPACE"

                        # Database: Postgres in this namespace. Its password is made once and kept in the postgres-env
                        # secret, so no database has to be created by hand on another server. Set
                        # ConnectionStrings__Default in the env file to use an external server instead.
                        if $K get secret postgres-env >/dev/null 2>&1; then
                            SIRIAUTOPOST_PG_USER="$($K get secret postgres-env -o jsonpath='{.data.POSTGRES_USER}' | base64 -d)"
                            SIRIAUTOPOST_PG_PASSWORD="$($K get secret postgres-env -o jsonpath='{.data.POSTGRES_PASSWORD}' | base64 -d)"
                        else
                            SIRIAUTOPOST_PG_USER=siriautopost
                            SIRIAUTOPOST_PG_PASSWORD="$(head -c 64 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 32)"
                        fi
                        export SIRIAUTOPOST_PG_USER SIRIAUTOPOST_PG_PASSWORD

                        APIENV="$(mktemp)"
                        PGENV="$(mktemp)"
                        trap 'rm -f "$APIENV" "$PGENV"' EXIT
                        sh deploy/prepare-env.sh "$ENV_FILE" "$APIENV" "$PGENV"

                        # Jenkins runs as ci:jenkins-deployer, which cannot create namespaces: the namespace and its
                        # RoleBinding are created once by a cluster admin (deploy/README.md in siri_autopost_backend).
                        if ! $K auth can-i create secrets >/dev/null 2>&1 || ! $K auth can-i patch deployments >/dev/null 2>&1; then
                            echo "jenkins-deployer has no rights in namespace $K8S_NAMESPACE yet: run the one-time admin step in siri_autopost_backend/deploy/README.md" >&2
                            exit 1
                        fi

                        # Secrets come from the Jenkins credential and are never written to git.
                        $K create secret generic api-env --from-env-file="$APIENV" --dry-run=client -o yaml | $K apply -f -

                        if [ -s "$PGENV" ]; then
                            $K create secret generic postgres-env --from-env-file="$PGENV" --dry-run=client -o yaml | $K apply -f -
                            kubectl apply -k deploy/k8s/postgres
                            $K rollout status statefulset/postgres --timeout=300s
                        fi

                        # Pin the image built above (workspace copy only, not committed).
                        sed -i "s/newTag: .*/newTag: $IMAGE_TAG/" "$OVERLAY/kustomization.yaml"
                        kubectl apply -k "$OVERLAY"
                        # api-env is not part of the pod template, so a changed secret alone would not roll the pod.
                        $K rollout restart deployment/api

                        if ! $K rollout status deployment/api --timeout=300s; then
                            $K get pods -l app.kubernetes.io/name=api -o wide || true
                            $K logs deployment/api --tail=80 || true
                            exit 1
                        fi
                    '''
                }
            }
        }

        stage('Smoke test') {
            steps {
                // The api readiness probe is GET /healthz, so a ready replica means the API answers.
                sh '''
                    set -eu
                    ready="$(kubectl -n "$K8S_NAMESPACE" get deployment api -o jsonpath='{.status.readyReplicas}')"
                    echo "api ready replicas: ${ready:-0}"
                    [ "${ready:-0}" -ge 1 ]
                '''
            }
        }
    }

    post {
        cleanup {
            sh 'docker image prune -f'
        }
    }
}
