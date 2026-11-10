# Deployment

The remote web host (`servers/project-insight/src-web`) runs on Azure App Service. The Azure
Functions host and its deploy scripts are gone.

- **One-time setup** (infrastructure, deploy identity, GitHub secrets, first deploy): follow
  [`bicep/README.md`](../../../bicep/README.md). The owner does this once.
- **Later deploys:** a push to `main` that touches the web host runs
  `.github/workflows/deploy.yml`, which signs in to Azure with OIDC and publishes the app. Each
  deploy restarts the app.
- **App settings:** see [CONFIGURATION.md](../../../CONFIGURATION.md).
- **Connecting a client:** see [docs/REMOTE.md](../../../docs/REMOTE.md).
- **Design and accepted risks:** `docs/superpowers/specs/2026-10-30-phase2-remote-host-design.md`.

The workflow fails at `azure/login` until the one-time setup has created its secrets.
