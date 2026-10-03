# Deploy: https://siriautopost.siristudiophoto.com

Same Jenkins and host as SIRISTUDIOPHOTO (https://jenkins.siristudiophoto.com/): docker + kubectl on the Jenkins
host, images in the local registry `localhost:5000`, and the Cloudflare tunnel `siri-monitor` (cloudflared on the
host, locally managed) in front, which routes each hostname to a k8s NodePort on `172.17.0.1`.

| | |
|---|---|
| Namespace | `siriautopost` |
| Jenkins jobs | `SIRIAUTOPOST-API` (this repo, `Jenkinsfile`), `SIRIAUTOPOST-WEB` (siri_autopost_ui, `Jenkinsfile`) |
| Manifests | `deploy/k8s/overlays/prd` here (Deployment + Service `api`, shared config) and `deploy/k8s/overlays/prd` in siri_autopost_ui (Deployment + Service `ui`); each job applies its own with `kubectl apply -k` |
| Branch | `main` (both repos are public; the jobs still check out with credential `gitlab-auth-id`, like the other systems) |
| Images | `localhost:5000/siriautopost-api`, `localhost:5000/siriautopost-web` |
| Database | the server named by `ConnectionStrings__Default` in the env-file credential. `deploy/k8s/postgres` is an optional Postgres 18 StatefulSet (database `SIRIAUTOPOST_PRD`, 10Gi volume) that no pipeline applies: `kubectl apply -k deploy/k8s/postgres` by hand, after creating its `postgres-env` secret (`POSTGRES_USER`, `POSTGRES_PASSWORD`) |
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
3. **Jenkins credential** `siriautopost-env-file` (Secret file): a `.env` file like the other jobs use. The job puts it into
   the secret `api-env` **as it is** (BOM and CRLF stripped), and every key in it becomes an environment variable of the
   API pod, so use the API's own names (`Section__Key`):

   | Key | |
   |---|---|
   | `ConnectionStrings__Default` | **required**, e.g. `Host=...;Database=SIRIAUTOPOST_PRD;Username=...;Password=...;Gss Encryption Mode=Disable` |
   | `Jwt__Key` | **required**, 32+ characters (the API does not start without it) |
   | `Admin__Email`, `Admin__Password` | creates the platform admin on first start. Optional for the pod, but without it nobody can reach the admin area (customers, refunds, plans) |
   | `Stripe__SecretKey` | the Stripe secret key (`sk_live_...`). Without it online payment is off: the dashboard says so and paid plans cannot be bought |
   | `Stripe__WebhookSecret` | the signing secret (`whsec_...`) of the webhook endpoint below. Without it Stripe's calls are rejected, so a paid plan would never be applied or renewed |
   | `Google__ClientId` | optional, enables "Sign in with Google" (add the site origin as an Authorized JavaScript origin on that OAuth client) |

   `Cors__AllowedOrigins__0` and `Stripe__ReturnBaseUrl` are pinned to the public address in `deploy/k8s/overlays/prd`
   (the config map wins over the secret). `deploy/prepare-env.sh` (an older helper that filtered the file and derived
   `Jwt__Key` from `AppSettings__Secret`) is not used by the pipelines.
4. **Jenkins jobs** (Pipeline, "Pipeline script": paste `deploy/jenkins/SIRIAUTOPOST-API.groovy` and `SIRIAUTOPOST-WEB.groovy`, or use the `Jenkinsfile` of each repo; the API one is the same script as the `.groovy` file. The web repo's `Jenkinsfile` also checks that Service `api` exists and smoke-tests the NodePort):
   - `SIRIAUTOPOST-API` → https://github.com/jatupolman3CS/siri_autopost_backend.git. Builds the image, writes the `api-env` secret, applies `deploy/k8s/overlays/prd` with the build's image tag and waits for the rollout.
   - `SIRIAUTOPOST-WEB` → https://github.com/jatupolman3CS/siri_autopost_ui.git
5. **Stripe** (dashboard.stripe.com, live mode):
   - Developers → Webhooks → add the endpoint `https://siriautopost.siristudiophoto.com/api/webhooks/stripe` with the events
     `checkout.session.completed`, `customer.subscription.created`, `customer.subscription.updated`,
     `customer.subscription.deleted`, `invoice.paid`, `invoice.payment_failed`, `refund.created`, `refund.updated`;
     copy its signing secret into `Stripe__WebhookSecret`. The dashboard's nginx passes `/api/` through, so the endpoint
     needs no tunnel route of its own.
   - Settings → Billing → Customer portal: switch it on (payment methods and invoices; plan changes are made in the
     dashboard, not in the portal). To use a specific portal configuration, put its id in `Stripe__PortalConfigurationId`.
   - Prices come from the plan settings in the admin area (baht, whole units; `Stripe__Currency` defaults to `thb`). The API
     creates one Stripe product per plan (`autopost_<plan>`) on first use and sends the price inline with each Checkout, so a
     changed price applies to new purchases and plan changes, not to subscriptions already running. Try everything with a test key
     (`sk_test_...`, a test webhook endpoint, card `4242 4242 4242 4242`) before the live key goes in. Nothing in this
     repository has been run against Stripe itself: the tests use a fake gateway and signed sample events.
6. Build `SIRIAUTOPOST-API` first (it creates Service `api`, which the dashboard's nginx needs), then
   `SIRIAUTOPOST-WEB`.

## Check

```
kubectl -n siriautopost get pods,svc
curl -fsS http://172.17.0.1:30907/
curl -fsS https://siriautopost.siristudiophoto.com/
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
