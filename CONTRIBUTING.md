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

### Development Environment

See [README.md](./README.md) for setup instructions. The OSS local stack requires:
- Docker Desktop (WSL2 on Windows / native on Linux/macOS)
- .NET SDK 8.0+
- Node.js 20.19.5+（`.node-version`/`engines` の最低要件、推奨 22.x）

```bash
docker compose -f docker-compose.oss.yaml up -d
```

## Code of Conduct

This project follows the [Contributor Covenant](./CODE_OF_CONDUCT.md).

## License

By contributing, you agree that your contributions will be licensed under the [Apache License 2.0](./LICENSE).
