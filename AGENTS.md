# Repository Guidelines

## Project Structure & Module Organization

DSE Growth Loop connects internal problems with experienced people and past knowledge. The repository now contains the initial application foundation and Bicep definitions; full ingestion, AI, and collaboration workflows remain unimplemented.

- `src/`: .NET 10 Domain, Infrastructure, API, Worker, and Aspire AppHost.
- `web/`: React/TypeScript/Vite UI and Playwright tests.
- `infra/`: subscription-scoped Bicep for `dse-growth-loop-rg`.
- `scripts/`: environment injection and explicit deployment commands.
- `tests/`: domain/API tests and environment-loader tests.
- `docs/implementation-status.md`, `docs/deployment.md`: current implementation limits and reproducible setup.

- `README.md`: project overview and design entry points.
- `docs/architecture.md`: architecture, data flows, assumptions, and proposed implementation choices.
- `docs/technology-research-2026-09-21.md`: dated research and selection rationale.
- `docs/retrieval-validation-plan.md`: retrieval and reranking evaluation plan.
- `docs/diagrams/`: editable `architecture.json`, standalone HTML, screenshots, and validation evidence.

The implemented stack uses React/TypeScript/Vite and C#/.NET 10 API/Worker. Architecture documents and diagrams still describe the target system, not a fully deployed system.

## Build, Test, and Development Commands

Run these from the repository root:

- `open docs/diagrams/architecture.html`: preview the diagram in a browser on macOS.
- `python3 -m json.tool docs/diagrams/architecture.json > /dev/null`: check diagram JSON syntax, if Python is installed.
- `git diff --check`: check tracked changes for whitespace errors.
- `git status --short`: identify changed and untracked artifacts before committing.

- `dotnet restore --locked-mode` and `dotnet build --no-restore`: restore and build the solution.
- `dotnet test tests/Dse.Tests --collect:"XPlat Code Coverage"`: domain and API tests.
- `npm --prefix web ci` and `npm --prefix web run build`: frontend build.
- `dotnet run --project src/Dse.AppHost`: local dummy-data application.
- From `web/`, `npx playwright install chromium` then `npm run test:e2e`: browser tests; keep ports 5173/5180 free.
- `python3 scripts/deploy.py validate`: local Bicep compilation (Azure CLI/Bicep required).
- `python3 -m unittest discover -s tests/scripts`: configuration script tests.

## Coding Style & Naming Conventions

Keep design prose in Japanese to match existing documents. Use descriptive Markdown headings, relative links, and backticks for identifiers and paths. Use lowercase, hyphen-separated document names; date research records with `YYYY-MM-DD`. Preserve existing JSON indentation and structure. No repository-wide formatter or source-code indentation standard exists yet.

Keep the README, architecture, retrieval plan, and diagrams consistent. Distinguish confirmed decisions, proposals, hypothetical values, and measured results.

## Testing Guidelines

Use xUnit for domain/API tests and Playwright for UI flows. For documentation changes, check links and JSON syntax. For diagram changes, update the source and rendered artifact together, inspect light/dark layouts, and refresh affected validation evidence. Follow `docs/diagrams/validation.md`; never report old evidence as a new test run. Retrieval quality evaluations should follow `docs/retrieval-validation-plan.md`; fixture tests are not measured AI quality results.

## Commit & Pull Request Guidelines

The repository has no commits yet, so no historical convention exists. Use concise, imperative messages, such as `docs: clarify retrieval evaluation criteria`. PRs should describe the purpose, affected design decisions, and checks performed. Link relevant issues and attach screenshots for diagram changes.

## Security & Configuration

Use dummy data only. Never commit credentials or real employee records. Design changes do not authorize Azure provisioning or paid service calls.
