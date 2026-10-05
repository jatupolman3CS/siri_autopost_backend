pipeline {
    agent any

    environment {
        REGISTRY_URL  = "localhost:5000"
        IMAGE_NAME    = "siriautopost-web"
        // Tags carry the environment so DEV and PRD builds (separate Jenkins build counters) never overwrite each other.
        IMAGE_TAG     = "dev-${env.BUILD_NUMBER}"
        GIT_URL       = "https://github.com/jatupolman3CS/siri_autopost_ui.git"
        GIT_BRANCH    = "main"
        K8S_NAMESPACE = "siriautopost-dev"
    }

    stages {
        stage('Checkout') {
            steps {
                git branch: "${GIT_BRANCH}",
                    credentialsId: 'gitlab-auth-id',
                    url: "${GIT_URL}"
            }
        }

        stage('Build Docker Image') {
            steps {
                script {
                    sh "docker build --no-cache -t ${REGISTRY_URL}/${IMAGE_NAME}:${IMAGE_TAG} ."
                    sh "docker tag ${REGISTRY_URL}/${IMAGE_NAME}:${IMAGE_TAG} ${REGISTRY_URL}/${IMAGE_NAME}:dev-latest"
                }
            }
        }

        stage('Push to Local Registry') {
            steps {
                script {
                    sh "docker push ${REGISTRY_URL}/${IMAGE_NAME}:${IMAGE_TAG}"
                    sh "docker push ${REGISTRY_URL}/${IMAGE_NAME}:dev-latest"
                }
            }
        }

        stage('Deploy to Kubernetes') {
            steps {
                // Deployment + Service `ui` (NodePort 30908) come from deploy/k8s/overlays/dev, so the first build creates them.
                // The tag is pinned in the workspace copy only.
                sh """
                    set -eu
                    sed -i "s/newTag: .*/newTag: ${IMAGE_TAG}/" deploy/k8s/overlays/dev/kustomization.yaml
                    kubectl apply -k deploy/k8s/overlays/dev
                """
                sh "kubectl -n ${K8S_NAMESPACE} rollout status deployment/ui --timeout=180s"
            }
        }
    }

    post {
        cleanup {
            sh "docker image prune -f"
        }
    }
}
