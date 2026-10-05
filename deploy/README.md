# Deploy: DEV and PRD, fully separated

Same Jenkins and host as SIRISTUDIOPHOTO (https://jenkins.siristudiophoto.com/): docker + kubectl on the Jenkins
host, images in the local registry `localhost:5000`, and the Cloudflare tunnel `siri-monitor` (cloudflared on the
host, locally managed) in front, which routes each hostname to a k8s NodePort on `172.17.0.1`.

| | PRD | DEV |
|---|---|---|
| URL | https://siriautopost.siristudiophoto.com | https://siriautopost-dev.siristudiophoto.com |
| Namespace | `siriautopost-prd` | `siriautopost-dev` |
| Database | `SIRIAUTOPOST_PRD` | `SIRIAUTOPOST` |
| NodePort (Service `ui`) | 30907 | 30908 |
| Jenkins jobs | `SIRIAUTOPOST-API-PRD`, `SIRIAUTOPOST-WEB-PRD` | `SIRIAUTOPOST-API-DEV`, `SIRIAUTOPOST-WEB-DEV` |
| Jenkinsfile (both repos) | `Jenkinsfile` | `Jenkinsfile.dev` |
| Manifests | `deploy/k8s/overlays/prd` | `deploy/k8s/overlays/dev` |
| Env-file credential | `siriautopost-env-file-prd` | `siriautopost-env-file` |
| Image tags | `prd-<build>`, `prd-latest` | `dev-<build>`, `dev-latest` |
| Postgres | shared `infra/postgres` (`postgres.infra.svc.cluster.local:5432`), one server, database per env | same server |

Both environments use the same image names (`localhost:5000/siriautopost-api`, `siriautopost-web`); nothing else is shared:
own namespace (`<app>-prd` / `<app>-dev`, like the other systems), own `api-env` secret (own `Jwt__Key`, DB credentials, Stripe keys), own database, own hostname, own NodePort.

- Branch: all jobs build `main` (`GIT_BRANCH` in each Jenkinsfile; point the dev ones at `develop` once that branch exists).
- Each job is a plain "Build Now" (no parameters), like the other systems on this Jenkins.
- The old jobs `SIRIAUTOPOST-API` / `SIRIAUTOPOST-WEB` are replaced by the `-PRD` ones: rename or disable them.
- Routing per environment: tunnel → `http://172.17.0.1:<NodePort>` (Service `ui`) → nginx serves the dashboard and proxies `/api` to Service `api:8080` **of the same namespace**.

## One-time setup

0. **Namespaces + Jenkins rights (cluster admin, on the k8s host).** Jenkins deploys as `ci:jenkins-deployer`,
   which cannot create namespaces. This creates both `siriautopost` and `siriautopost-dev`:
   ```
   curl -fsSL https://raw.githubusercontent.com/jatupolman3CS/siri_autopost_backend/main/deploy/admin-bootstrap.sh | sh
   ```
1. **DNS + tunnel routes (both hosts).** `siri-monitor` is locally managed, so its routes live in the cloudflared config file on the
   host, not in the dashboard. `sh deploy/cloudflare-dns.sh [/etc/cloudflared/config.yml]` does both steps (idempotent), then restart cloudflared:
   - DNS (Cloudflare, zone siristudiophoto.com): proxied CNAMEs `siriautopost` and `siriautopost-dev` →
     `d7ef791e-9da8-416f-a040-d25f144e08eb.cfargotunnel.com` (`cloudflared tunnel route dns siri-monitor <host>`)
   - ingress rules, before the catch-all:
     ```yaml
     - hostname: siriautopost.siristudiophoto.com
       service: http://172.17.0.1:30907
     - hostname: siriautopost-dev.siristudiophoto.com
       service: http://172.17.0.1:30908
     ```
2. **Databases.** One shared server (`infra/postgres`); the API creates its database on first start (migrations), so the DB user needs CREATEDB: `SIRIAUTOPOST_PRD` (PRD) and `SIRIAUTOPOST` (DEV). Both env files use `Host=postgres.infra.svc.cluster.local`.
3. **Jenkins credentials** `siriautopost-env-file-prd` (PRD) and `siriautopost-env-file` (DEV), both "Secret file" (same naming as `siriphoto-env-file[-prd]`): a `.env` file like the other jobs use
   (never copy values from one to the other). The job puts it into
   the secret `api-env` **as it is** (BOM and CRLF stripped), and every key in it becomes an environment variable of the
   API pod, so use the API's own names (`Section__Key`):

   | Key | |
   |---|---|
   | `ConnectionStrings__Default` | **required**, e.g. `Host=...;Database=SIRIAUTOPOST_PRD;Username=...;Password=...;Gss Encryption Mode=Disable` (DEV: `Database=SIRIAUTOPOST`) |
   | `Jwt__Key` | **required**, 32+ characters (the API does not start without it); different per environment |
   | `Admin__Email`, `Admin__Password` | creates the platform admin on first start. Optional for the pod, but without it nobody can reach the admin area (customers, refunds, plans) |
   | `Stripe__SecretKey` | the Stripe secret key (`sk_live_...`; DEV `sk_test_...`). Without it online payment is off: the dashboard says so and paid plans cannot be bought |
   | `Stripe__WebhookSecret` | the signing secret (`whsec_...`) of the webhook endpoint below. Without it Stripe's calls are rejected, so a paid plan would never be applied or renewed |
   | `Google__ClientId` | optional, enables "Sign in with Google" (add each site origin as an Authorized JavaScript origin on that OAuth client) |

   `Cors__AllowedOrigins__0` and `Stripe__ReturnBaseUrl` are pinned to the environment's public address in `deploy/k8s/overlays/<env>`
   (the config map wins over the secret), as is `ASPNETCORE_ENVIRONMENT` (PRD `Production`, DEV `Staging`: `Development` would expose `/openapi` anonymously).
   `deploy/prepare-env.sh` (an older helper that filtered the file and derived `Jwt__Key` from `AppSettings__Secret`) is not used by the pipelines.
4. **Jenkins jobs** (Pipeline, "Pipeline script": paste the matching file of `deploy/jenkins/`: `SIRIAUTOPOST-API-PRD.groovy`, `SIRIAUTOPOST-API-DEV.groovy`,
   `SIRIAUTOPOST-WEB-PRD.groovy`, `SIRIAUTOPOST-WEB-DEV.groovy`; or "Pipeline script from SCM" with `Jenkinsfile` / `Jenkinsfile.dev`, which are the same scripts).
   Each builds the image, (API) writes the `api-env` secret, applies its overlay with the build's image tag and waits for the rollout.
   - `SIRIAUTOPOST-API-*` → https://github.com/jatupolman3CS/siri_autopost_backend.git
   - `SIRIAUTOPOST-WEB-*` → https://github.com/jatupolman3CS/siri_autopost_ui.git
5. **Stripe** (dashboard.stripe.com). DEV uses **test mode** only (`sk_test_...`, its own webhook endpoint
   `https://siriautopost-dev.siristudiophoto.com/api/webhooks/stripe`, card `4242 4242 4242 4242`). PRD, live mode:
   - Developers → Webhooks → add the endpoint `https://siriautopost.siristudiophoto.com/api/webhooks/stripe` with the events
     `checkout.session.completed`, `customer.subscription.created`, `customer.subscription.updated`,
     `customer.subscription.deleted`, `invoice.paid`, `invoice.payment_failed`, `refund.created`, `refund.updated`;
     copy its signing secret into `Stripe__WebhookSecret`. The dashboard's nginx passes `/api/` through, so the endpoint
     needs no tunnel route of its own.
   - Settings → Billing → Subscriptions and emails: keep "retry failed payments" on (a failed card then shows as past due and
     Stripe retries it; the dashboard says so), and the customer emails you want Stripe to send (receipts, failed payment).
   - Settings → Billing → Customer portal: switch it on (payment methods and invoices; plan changes are made in the
     dashboard, not in the portal). To use a specific portal configuration, put its id in `Stripe__PortalConfigurationId`.
   - Prices come from the plan settings in the admin area (baht, whole units; `Stripe__Currency` defaults to `thb`). The API
     creates one Stripe product per plan (`autopost_<plan>`) on first use and sends the price inline with each Checkout, so a
     changed price applies to new purchases and plan changes, not to subscriptions already running. Try everything with a test key
     before the live key goes in. Nothing in this repository has been run against Stripe itself: the tests use a fake gateway and signed sample events.
6. Per environment, build the API job first (it creates Service `api`, which the dashboard's nginx needs), then the WEB job.

## Check

```
kubectl -n siriautopost-prd get pods,svc        # PRD
kubectl -n siriautopost-dev get pods,svc        # DEV
curl -fsS http://172.17.0.1:30907/              # PRD NodePort
curl -fsS http://172.17.0.1:30908/              # DEV NodePort
curl -fsS https://siriautopost.siristudiophoto.com/
curl -fsS https://siriautopost-dev.siristudiophoto.com/
```

The API runs EF migrations on startup (`Database__MigrateOnStartup=true`) and creates the platform admin from
`Admin__Email` / `Admin__Password` if it does not exist. The host is linux/arm64; all base images are multi-arch.

After setting the Stripe keys, the admin overview's health panel shows payments as connected, and the billing page of any
workspace owner offers "pay by card" for a paid plan. A webhook that Stripe cannot deliver (wrong secret, wrong address)
shows up under Developers → Webhooks → the endpoint → recent deliveries, and as a 400 in the API log.

## Limits that must agree

| Where | Value | Why |
|---|---|---|
| UI nginx `client_max_body_size` | 300m for the upload paths | campaign videos go up as base64 JSON (200 MB video ≈ 270 MB); the API accepts 300 MB there |
| API pod memory limit | 2Gi | the same upload is held as text and as bytes |
| nginx `proxy_read_timeout` | 120s (3600s for the event stream) | the extension's sync is held up to 25 s; the event stream lives up to 30 minutes |
| Cloudflare (in front of the tunnel) | request bodies of at most 100 MB on the Free and Pro plans (more on higher plans) | a video over about 75 MB (base64 makes it a third larger) is refused before it reaches nginx, whatever the limits behind it say. The web app accepts videos up to 200 MB, so on those plans keep campaign videos smaller |
