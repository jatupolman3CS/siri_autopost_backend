pipeline {
    agent any

    environment {
        REGISTRY_URL    = "localhost:5000"
        IMAGE_NAME      = "siriautopost-api"
        IMAGE_TAG       = "${env.BUILD_NUMBER}"
        GIT_URL         = "https://github.com/jatupolman3CS/siri_autopost_backend.git"
        DOCKERFILE_PATH = "src/SIRIAUTOPOST.Api/Dockerfile"
        K8S_NAMESPACE   = "siriautopost"
    }

    stages {
        stage('Checkout') {
            steps {
                git branch: 'main',
                    credentialsId: 'gitlab-auth-id',
                    url: "${GIT_URL}"
                    sh "ls -lah"
            }
        }

        stage('Build Docker Image') {
            steps {
                script {
                    echo "Building using Dockerfile at: ${DOCKERFILE_PATH}"
                    sh "docker build --no-cache -t ${REGISTRY_URL}/${IMAGE_NAME}:${IMAGE_TAG} -f ${DOCKERFILE_PATH} ."
                    sh "docker tag ${REGISTRY_URL}/${IMAGE_NAME}:${IMAGE_TAG} ${REGISTRY_URL}/${IMAGE_NAME}:latest"
                }
            }
        }

        stage('Push to Local Registry') {
            steps {
                script {
                    sh "docker push ${REGISTRY_URL}/${IMAGE_NAME}:${IMAGE_TAG}"
                    sh "docker push ${REGISTRY_URL}/${IMAGE_NAME}:latest"
                }
            }
        }

        stage('Deploy to Kubernetes') {
            steps {
                withCredentials([file(credentialsId: 'siriautopost-env-file', variable: 'ENV_FILE')]) {
                    // The env file may come from Windows (UTF-8 BOM, CRLF): strip both before it becomes the secret.
                    sh '''
                        set +x
                        CLEAN="$(mktemp)"
                        trap 'rm -f "$CLEAN"' EXIT
                        sed -e '1s/^\\xEF\\xBB\\xBF//' -e 's/\\r$//' "$ENV_FILE" > "$CLEAN"
                        kubectl -n "$K8S_NAMESPACE" create secret generic api-env --from-env-file="$CLEAN" --dry-run=client -o yaml | kubectl apply -f -
                    '''
                }
                sh "kubectl -n ${K8S_NAMESPACE} set image deployment/api api=${REGISTRY_URL}/${IMAGE_NAME}:${IMAGE_TAG}"
                sh "kubectl -n ${K8S_NAMESPACE} rollout status deployment/api --timeout=300s"
            }
        }
    }

    post {
        cleanup {
            sh "docker image prune -f"
        }
    }
}
