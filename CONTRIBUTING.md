# Contributing to Building OS

Thank you for considering contributing to Building OS!

## How to Contribute

### Reporting Bugs

Please open a [GitHub Issue](../../issues) with:
- A clear description of the problem
- Steps to reproduce
- Expected vs. actual behavior
- Relevant logs or screenshots

### Suggesting Features

Open an issue with the `enhancement` label. Describe the use case and why the feature would be useful.

### Pull Requests

1. Fork the repository and create a branch from `main`:
   ```bash
   git checkout -b feat/your-feature-name
   ```

2. Follow the coding conventions in [CLAUDE.md](./CLAUDE.md):
   - **.NET**: always pass `CancellationToken`, use structured logging, `AsNoTracking()` for read-only queries
   - **Frontend**: run `yarn typecheck && yarn lint` before committing
   - **Tests**: add unit tests for new logic; integration tests for infrastructure changes

3. Run tests locally before opening a PR:
   ```bash
   # Backend
   cd DotNet && dotnet test --filter "FullyQualifiedName!~IntegrationTest"

   # Frontend
   cd web-client && yarn typecheck && yarn lint && yarn build
   ```

4. Open a PR against `main` with a clear description of what changes and why.

### Branching and merging

**Branch names** follow `<type>/<issue-number>-<short-description>`, where `<type>` matches the
commit type (`feat`, `fix`, `docs`, `chore`, `refactor`, `test`), e.g. `feat/600-release-point`.

**Commit messages** follow `type(scope): summary (#issue)`, e.g.
`feat(health): add the persistent health-event model (#455)`.

**Small changes** (a fix, a doc, a self-contained feature) go in a branch cut from `main` and are
merged back to `main` with a normal PR.

**Larger features** (spanning the twin, API and web, or split across several PRs) use an
*integration branch* so that `main` never holds a half-finished feature:

1. Cut `feat/<issue>-<name>` from `main` and push it. This is the integration branch.
2. Open each step as a PR **into the integration branch**, not into `main`.
3. Keep the integration branch current by merging `main` into it (do not rebase a shared branch).
4. When the feature is complete, open one PR from the integration branch into `main`.

> **Why this matters:** a merge to `main` triggers `harbor-push` and then `argocd-image-update`,
> which publishes images and updates the deployment manifests. Anything merged to `main` is
> treated as releasable.

**Verification.** `pr-check` only runs for PRs that target `main`, and the other test workflows
are manual (`workflow_dispatch`). PRs into an integration branch therefore get no automatic
checks, so run them locally before pushing:

- Backend: `cd DotNet && dotnet test --filter "FullyQualifiedName!~IntegrationTest"`
- Frontend: `cd web-client && yarn typecheck && yarn lint && yarn test`
- If the REST API changed: `./Tools/sync-type.bash`, and commit `docs/schema/swagger.yaml` and
  the generated Aspida types together (CI fails if either is stale).

The final PR into `main` is the one `pr-check` validates; trigger the manual workflows from the
Actions tab when the change warrants it.

### Development Environment

See [README.md](./README.md) for setup instructions. The OSS local stack requires:
- Docker Desktop (WSL2 on Windows / native on Linux/macOS)
- .NET SDK 8.0+
- Node.js 22.12.0+（`.node-version`/`engines` の最低要件。CI と Web の Docker イメージも 22.x）

```bash
docker compose -f docker-compose.oss.yaml up -d
```

## Code of Conduct

This project follows the [Contributor Covenant](./CODE_OF_CONDUCT.md).

## License

By contributing, you agree that your contributions will be licensed under the [Apache License 2.0](./LICENSE).
