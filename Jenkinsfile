// PRD pipeline for siri_autopost_backend (Jenkins job: SIRIAUTOPOST-BACKEND).
// Builds SIRIAUTOPOST.Api into the local registry and deploys the API for
// https://siriautopost.siristudiophoto.com into namespace siriautopost (deploy/k8s/overlays/prd).
//
// Same conventions as the SIRISTUDIOPHOTO jobs: docker + kubectl on the Jenkins host, registry localhost:5000.
// Run this job before SIRIAUTOPOST-WEB the first time: the dashboard's nginx needs Service `api` to exist.
//
// Credential required (Jenkins "Secret file"): siriautopost-env-file, a .env file like the other jobs use.
// deploy/prepare-env.sh picks what the API needs from it (only those keys reach the pod):
//   ConnectionStrings__Default  or AppSettings__ConnectionStrings (same server, database SIRIAUTOPOST_PRD,
//                               override with SIRIAUTOPOST_DB_NAME), or POSTGRES_USER/POSTGRES_PASSWORD for an
//                               in-cluster Postgres (deploy/k8s/postgres)
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
                        set -eu
                        K="kubectl -n $K8S_NAMESPACE"

                        APIENV="$(mktemp)"
                        PGENV="$(mktemp)"
                        trap 'rm -f "$APIENV" "$PGENV"' EXIT
                        sh deploy/prepare-env.sh "$ENV_FILE" "$APIENV" "$PGENV"

                        kubectl create namespace "$K8S_NAMESPACE" --dry-run=client -o yaml | kubectl apply -f -

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
                // In-cluster check through the API server proxy: the public host only reaches the API via the
                // dashboard's nginx (SIRIAUTOPOST-WEB), which may not be deployed yet.
                sh 'kubectl get --raw "/api/v1/namespaces/$K8S_NAMESPACE/services/http:api:8080/proxy/healthz"'
            }
        }
    }

    post {
        cleanup {
            sh 'docker image prune -f'
        }
    }
}
