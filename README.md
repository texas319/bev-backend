# BEV Backend — Deploy Repo

Push a build here and it deploys to Azure automatically. No PowerShell.

- `hive/`     — Hive Function App. Push -> deploys to bev-hive-fn. Label in `hive/BUILD_LABEL.txt`.
- `server/`   — Server Function App. Push -> deploys to bev-server-fn.
- `gateway/`  — Gateway. Push (or run by hand) -> builds the MSI on a Windows runner,
                uploads it, and points the update manifest at it. Label in `gateway/BUILD_LABEL.txt`.
- `.github/workflows/` — the three pipelines (no editing needed).

FIRST TIME: do `SETUP_ONE_TIME.md` once. After that, deploys are automatic.

Manual deploy: Actions tab -> pick the workflow -> Run workflow.
(For Gateway by hand, it asks for the build label + a unique MSI name.)
