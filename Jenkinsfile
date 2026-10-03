// PRD pipeline for siri_autopost_backend (Jenkins job: SIRIAUTOPOST-BACKEND).
// Builds SIRIAUTOPOST.Api into the local registry and deploys the API and Postgres for
// https://siriautopost.siristudiophoto.com into namespace siriautopost (deploy/k8s/overlays/prd).
//
// Same conventions as the SIRISTUDIOPHOTO jobs: docker + kubectl on the Jenkins host, registry localhost:5000.
// Run this job before SIRIAUTOPOST-WEB the first time: the dashboard's nginx needs Service `api` to exist.
//
// Credential required (Jenkins "Secret file"):
//   siriautopost-env-file   plain KEY=value lines, no quotes:
//                             POSTGRES_USER=siriautopost
//                             POSTGRES_PASSWORD=<strong password>
//                             ConnectionStrings__Default=Host=postgres;Database=siriautopost;Username=<POSTGRES_USER>;Password=<POSTGRES_PASSWORD>;Gss Encryption Mode=Disable
//                             Jwt__Key=<32+ random characters>
//                             Admin__Email=<platform admin email>
//                             Admin__Password=<platform admin password>

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

                        # A file saved on Windows may carry a UTF-8 BOM and CRLF line ends; both would end up in
                        # the key names / values of the secrets, so work on a normalized copy.
                        ENVN="$(mktemp)"
                        PGENV="$(mktemp)"
                        trap 'rm -f "$ENVN" "$PGENV"' EXIT
                        sed -e '1s/^\\xEF\\xBB\\xBF//' -e 's/\\r$//' "$ENV_FILE" > "$ENVN"
                        ENV_FILE="$ENVN"

                        for required_key in POSTGRES_USER POSTGRES_PASSWORD ConnectionStrings__Default Jwt__Key Admin__Email Admin__Password; do
                            if ! grep -Eq "^${required_key}=.+" "$ENV_FILE"; then
                                echo "env file must contain ${required_key}=" >&2
                                # Key names and encoding only, never values.
                                echo "encoding: $(file -b "$ENV_FILE" 2>/dev/null || echo unknown)" >&2
                                echo "keys found:" >&2
                                grep -oE '^[^=#]+=' "$ENV_FILE" | sed 's/=$//' | sed -n '1,30l' >&2 || true
                                exit 1
                            fi
                        done

                        kubectl create namespace "$K8S_NAMESPACE" --dry-run=client -o yaml | kubectl apply -f -

                        # Secrets come from the Jenkins credential and are never written to git.
                        $K create secret generic api-env --from-env-file="$ENV_FILE" --dry-run=client -o yaml | $K apply -f -

                        # Postgres only gets its own two keys, not the whole env file.
                        grep -E '^POSTGRES_(USER|PASSWORD)=' "$ENV_FILE" > "$PGENV" || true
                        if [ "$(wc -l < "$PGENV")" -ne 2 ]; then
                            echo "env file must contain exactly one POSTGRES_USER= and one POSTGRES_PASSWORD= line" >&2
                            exit 1
                        fi
                        $K create secret generic postgres-env --from-env-file="$PGENV" --dry-run=client -o yaml | $K apply -f -

                        # Pin the image built above (workspace copy only, not committed).
                        sed -i "s/newTag: .*/newTag: $IMAGE_TAG/" "$OVERLAY/kustomization.yaml"
                        kubectl apply -k "$OVERLAY"

                        $K rollout status statefulset/postgres --timeout=300s
                        $K rollout status deployment/api --timeout=300s
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
