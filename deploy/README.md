# Deploy: https://siriautopost.siristudiophoto.com

Same Jenkins and host as SIRISTUDIOPHOTO (https://jenkins.siristudiophoto.com/): docker + kubectl on the Jenkins
host, images in the local registry `localhost:5000`, and the Cloudflare tunnel `siri-monitor` (cloudflared on the
host, locally managed) in front, which routes each hostname to a k8s NodePort on `172.17.0.1`.

| | |
|---|---|
| Namespace | `siriautopost` |
| Jenkins jobs | `SIRIAUTOPOST-BACKEND` (this repo, `Jenkinsfile`), `SIRIAUTOPOST-WEB` (siri_autopost_ui, `Jenkinsfile`) |
| Branch | `*/main` (both repos are public, no git credential needed) |
| Images | `localhost:5000/siriautopost-api`, `localhost:5000/siriautopost-web` |
| Database | Postgres 18 StatefulSet `postgres` in the namespace, database `siriautopost` (10Gi PVC) |
| Routing | tunnel → `http://172.17.0.1:30907` (Service `ui`, NodePort) → nginx serves the dashboard and proxies `/api` to Service `api:8080` |

## One-time setup

1. **Tunnel route.** `siri-monitor` is locally managed, so its routes live in the cloudflared config file on the
   host, not in the dashboard. Add, before the catch-all rule, then restart cloudflared:
   ```yaml
   - hostname: siriautopost.siristudiophoto.com
     service: http://172.17.0.1:30907
   ```
2. **DNS (Cloudflare, zone siristudiophoto.com):** CNAME `siriautopost` →
   `d7ef791e-9da8-416f-a040-d25f144e08eb.cfargotunnel.com`, proxied (or `cloudflared tunnel route dns siri-monitor siriautopost.siristudiophoto.com`).
3. **Jenkins credential** `siriautopost-env-file` (Secret file), plain `KEY=value` lines, no quotes:
   ```
   POSTGRES_USER=siriautopost
   POSTGRES_PASSWORD=<strong password>
   ConnectionStrings__Default=Host=postgres;Database=siriautopost;Username=siriautopost;Password=<same password>;Gss Encryption Mode=Disable
   Jwt__Key=<32+ random characters>
   Admin__Email=<platform admin email>
   Admin__Password=<platform admin password>
   ```
4. **Jenkins jobs** (Pipeline, "Pipeline script from SCM", Git, branch `*/main`, Script Path `Jenkinsfile`):
   - `SIRIAUTOPOST-BACKEND` → https://github.com/jatupolman3CS/siri_autopost_backend.git
   - `SIRIAUTOPOST-WEB` → https://github.com/jatupolman3CS/siri_autopost_ui.git
5. Build `SIRIAUTOPOST-BACKEND` first (it creates Service `api`, which the dashboard's nginx needs), then
   `SIRIAUTOPOST-WEB`.

## Check

```
kubectl -n siriautopost get pods,svc
curl -fsS http://172.17.0.1:30907/
curl -fsS https://siriautopost.siristudiophoto.com/
```

The API runs EF migrations on startup (`Database__MigrateOnStartup=true`) and creates the platform admin from
`Admin__Email` / `Admin__Password` if it does not exist. The host is linux/arm64; all base images are multi-arch.
