# Deploy: https://siriautopost.siristudiophoto.com

Same Jenkins and host as SIRISTUDIOPHOTO (https://jenkins.siristudiophoto.com/): docker + kubectl on the Jenkins
host, images in the local registry `localhost:5000`, and the Cloudflare tunnel `siri-monitor` (cloudflared on the
host, locally managed) in front, which routes each hostname to a k8s NodePort on `172.17.0.1`.

| | |
|---|---|
| Namespace | `siriautopost` |
| Jenkins jobs | `SIRIAUTOPOST-API` (this repo, `Jenkinsfile`), `SIRIAUTOPOST-WEB` (siri_autopost_ui, `Jenkinsfile`) |
| Branch | `main` (both repos are public; the jobs still check out with credential `gitlab-auth-id`, like the other systems) |
| Images | `localhost:5000/siriautopost-api`, `localhost:5000/siriautopost-web` |
| Database | Postgres 18 StatefulSet `postgres` in the namespace, database `SIRIAUTOPOST_PRD`, 10Gi volume |
| Routing | tunnel → `http://172.17.0.1:30907` (Service `ui`, NodePort) → nginx serves the dashboard and proxies `/api` to Service `api:8080` |

## One-time setup

0. **Namespace + Jenkins rights (cluster admin, on the k8s host).** Jenkins deploys as `ci:jenkins-deployer`,
   which cannot create namespaces:
   ```
   curl -fsSL https://raw.githubusercontent.com/jatupolman3CS/siri_autopost_backend/main/deploy/admin-bootstrap.sh | sh
   ```
1. **Tunnel route.** `siri-monitor` is locally managed, so its routes live in the cloudflared config file on the
   host, not in the dashboard. Add, before the catch-all rule, then restart cloudflared:
   ```yaml
   - hostname: siriautopost.siristudiophoto.com
     service: http://172.17.0.1:30907
   ```
2. **DNS (Cloudflare, zone siristudiophoto.com):** CNAME `siriautopost` →
   `d7ef791e-9da8-416f-a040-d25f144e08eb.cfargotunnel.com`, proxied (or `cloudflared tunnel route dns siri-monitor siriautopost.siristudiophoto.com`).
3. **Jenkins credential** `siriautopost-env-file` (Secret file): a `.env` file like the other jobs use.
   `deploy/prepare-env.sh` takes only what the API needs (other keys never reach the pod):
   - the database needs nothing from the file: Postgres runs in the namespace (`deploy/k8s/postgres`) and its
     password is generated once into the `postgres-env` secret. Put `ConnectionStrings__Default` in the file to use an
     external server instead;
   - `Jwt__Key` (32+ chars), or derived from `AppSettings__Secret`;
   - optional `Admin__Email` / `Admin__Password` to create the platform admin.
   - optional `Google__ClientId` to enable "Sign in with Google" (also add the site origin as an Authorized JavaScript origin on that OAuth client).
4. **Jenkins jobs** (Pipeline, "Pipeline script": paste `deploy/jenkins/SIRIAUTOPOST-API.groovy` and `SIRIAUTOPOST-WEB.groovy`; same shape as SIRIEDUMARKET-API: git checkout with `gitlab-auth-id`, docker build + push, `kubectl set image`, `rollout status`. The `Jenkinsfile` in each repo is the same script. The API secret `api-env` is the `siriautopost-env-file` credential as it is, so it decides the database (`ConnectionStrings__Default`); `deploy/prepare-env.sh` and `deploy/k8s/postgres` are no longer used by the pipeline):
   - `SIRIAUTOPOST-API` → https://github.com/jatupolman3CS/siri_autopost_backend.git
   - `SIRIAUTOPOST-WEB` → https://github.com/jatupolman3CS/siri_autopost_ui.git
5. Build `SIRIAUTOPOST-API` first (it creates Service `api`, which the dashboard's nginx needs), then
   `SIRIAUTOPOST-WEB`.

## Check

```
kubectl -n siriautopost get pods,svc
curl -fsS http://172.17.0.1:30907/
curl -fsS https://siriautopost.siristudiophoto.com/
```

The API runs EF migrations on startup (`Database__MigrateOnStartup=true`) and creates the platform admin from
`Admin__Email` / `Admin__Password` if it does not exist. The host is linux/arm64; all base images are multi-arch.
