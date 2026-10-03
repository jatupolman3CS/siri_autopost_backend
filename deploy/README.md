# Deploy: https://siriautopost.siristudiophoto.com

Same Jenkins and cluster as SIRISTUDIOPHOTO (https://jenkins.siristudiophoto.com/): docker + kubectl on the
Jenkins host, images in the local registry `localhost:5000`, TLS terminated by Cloudflare in front of the cluster.

| | |
|---|---|
| Namespace | `siriautopost` |
| Jenkins jobs | `SIRIAUTOPOST-BACKEND` (this repo, `Jenkinsfile`), `SIRIAUTOPOST-WEB` (siri_autopost_ui, `Jenkinsfile`) |
| Branch | `*/main` |
| Images | `localhost:5000/siriautopost-api`, `localhost:5000/siriautopost-web` |
| Database | Postgres 18 StatefulSet `postgres` in the namespace, database `siriautopost` (10Gi PVC) |
| Routing | Ingress `siriautopost` (`overlays/prd/ingress.yaml`): `/api`, `/healthz` → `api:8080`, `/` → `ui:80` |

## One-time setup

1. **DNS (Cloudflare, zone siristudiophoto.com):** add `siriautopost` exactly like the record that serves
   `jenkins` / the root today, proxied (orange cloud):
   - A record → the same origin IP, or
   - if the zone uses a Cloudflare Tunnel: add a public hostname `siriautopost.siristudiophoto.com` on that tunnel
     pointing at the same ingress service as `siristudiophoto.com`.
2. **Jenkins credential** `siriautopost-env-file` (Secret file), plain `KEY=value` lines, no quotes:
   ```
   POSTGRES_USER=siriautopost
   POSTGRES_PASSWORD=<strong password>
   ConnectionStrings__Default=Host=postgres;Database=siriautopost;Username=siriautopost;Password=<same password>;Gss Encryption Mode=Disable
   Jwt__Key=<32+ random characters>
   Admin__Email=<platform admin email>
   Admin__Password=<platform admin password>
   ```
3. **Jenkins jobs** (Pipeline, "Pipeline script from SCM", Git, branch `*/main`, Script Path `Jenkinsfile`):
   - `SIRIAUTOPOST-BACKEND` → https://github.com/jatupolman3CS/siri_autopost_backend.git
   - `SIRIAUTOPOST-WEB` → https://github.com/jatupolman3CS/siri_autopost_ui.git
4. Build `SIRIAUTOPOST-BACKEND` first (it creates Service `api`, which the dashboard's nginx needs), then
   `SIRIAUTOPOST-WEB`.

## Check

```
kubectl -n siriautopost get pods,svc,ingress
curl -fsS https://siriautopost.siristudiophoto.com/healthz
```

The API runs EF migrations on startup (`Database__MigrateOnStartup=true`) and creates the platform admin from
`Admin__Email` / `Admin__Password` if it does not exist.
