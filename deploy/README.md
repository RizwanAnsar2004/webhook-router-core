# Deploying webhookrouter

The app runs as root under systemd, listens on `127.0.0.1:5180`, and sits behind nginx. It needs a reachable Redis, which Hangfire uses for job storage under the `webhookrouter:hangfire:` key prefix.

## First install

```bash
# 1. Publish and copy the build.
dotnet publish WebhookRouter -c Release -o out
sudo mkdir -p /var/dotnet/webhook-router
sudo cp -r out/* /var/dotnet/webhook-router/

# 2. Secrets (the webhook secret and Redis). Routes live in appsettings.json.
sudo mkdir -p /etc/webhookrouter
sudo cp deploy/webhookrouter.env.example /etc/webhookrouter/webhookrouter.env
sudo nano /etc/webhookrouter/webhookrouter.env
sudo chmod 600 /etc/webhookrouter/webhookrouter.env

# 3. Install the service.
sudo cp deploy/webhookrouter.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now webhookrouter
sudo systemctl status webhookrouter
curl http://127.0.0.1:5180/v1/api/health

# 4. nginx.
sudo cp deploy/webhook-router /etc/nginx/sites-available/webhook-router
sudo ln -s /etc/nginx/sites-available/webhook-router /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d webhookroute.hostinger.bitscollision.net
```

On a distribution without `sites-available`, copy the nginx file to `/etc/nginx/conf.d/webhook-router.conf` instead.

`HyperPay:WebhookSecretEnabled` in appsettings.json is `false` for now, so every webhook goes unread to `Routing:DefaultUrl`. Once you have the secret, put it in the env file and set `HyperPay__WebhookSecretEnabled=true` there, or set the flag in appsettings. While it is `true`, the app refuses to start if `HyperPay__WebhookSecret` is not a 64-character hex key. It also refuses to start if `Routing:DefaultUrl` is not an absolute URL, or if a `Routing:Services` entry is missing its identifier or URL. Check `journalctl -u webhookrouter` for the exact message.

Register `https://webhookroute.hostinger.bitscollision.net/v1/api/webhooks/hyperpay` as the webhook URL in the HyperPay dashboard.

## Updates

```bash
dotnet publish WebhookRouter -c Release -o out
sudo cp -r out/* /var/dotnet/webhook-router/
sudo systemctl restart webhookrouter
```

Jobs that are queued or scheduled for retry stay in Redis across restarts.

## Hangfire dashboard

The dashboard is off unless `Hangfire__DashboardEnabled=true`. nginx returns 404 for `/hangfire`. To use the dashboard, open an SSH tunnel with `ssh -L 5180:127.0.0.1:5180 <server>`, then go to `http://localhost:5180/hangfire`.
