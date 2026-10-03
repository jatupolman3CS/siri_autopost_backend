pipeline {
    agent any

    environment {
        REGISTRY_URL  = "localhost:5000"
        IMAGE_NAME    = "siriautopost-web"
        IMAGE_TAG     = "${env.BUILD_NUMBER}"
        GIT_URL       = "https://github.com/jatupolman3CS/siri_autopost_ui.git"
        K8S_NAMESPACE = "siriautopost"
    }

    stages {
        stage('Checkout') {
            steps {
                git branch: 'main',
                    credentialsId: 'gitlab-auth-id',
                    url: "${GIT_URL}"
            }
        }

        stage('Build Docker Image') {
            steps {
                script {
                    sh "docker build --no-cache -t ${REGISTRY_URL}/${IMAGE_NAME}:${IMAGE_TAG} ."
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
                sh "kubectl -n ${K8S_NAMESPACE} set image deployment/ui ui=${REGISTRY_URL}/${IMAGE_NAME}:${IMAGE_TAG}"
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
