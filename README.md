## Semantic Kernel Console App (Ollama + SQLite)

This is a small interactive console chat app that:
- Uses **Ollama** (via **Microsoft Semantic Kernel**) to recommend 1 place to visit in Madeira (JSON output).
- (Optional) Fetches weather from **OpenWeather**.
- Stores places in a **SQLite** database (created automatically at startup).

---

## Configuration (environment variables / `.env`)
The app reads configuration from environment variables.
When running locally, it also loads variables from a `.env` file in the project folder (if present).
In Docker Hub images, `.env` is **not** included (pass `-e ...` on `docker run` or use Compose).

Recommended (the app has safe defaults):
- `LOCATIONS_DB_CONNECTION_STRING=Data Source=locations.db` (default: `Data Source=locations.db`)
- `OLLAMA_MODEL=llama3.1:latest` (default: `llama3.1:latest`)

Ollama endpoint (choose one approach; if none is set it defaults to `http://localhost:11434` locally and `http://host.docker.internal:11434` in Docker):
- `OLLAMA_ENDPOINT=http://...` (explicit override), OR
- `OLLAMA_ENDPOINT_LOCAL=http://localhost:11434` (used when running locally), AND
- `OLLAMA_ENDPOINT_DOCKER=http://host.docker.internal:11434` (used when running in Docker)

Optional:
- `API_KEY=` (OpenWeather)
- `APP_TIMEZONE=Europe/Lisbon` (display `LastUpdated` in your timezone)
- `APP_DEBUG=true` (show `[debug ...]` logs even in Docker/Release)

Notes:
- `API_KEY` can be empty; the app still works, just without temperatures.
- DB timestamps are stored in UTC and converted to `APP_TIMEZONE` for display.

---

## Run locally (no Docker)
1) Start Ollama:
- Install: `irm https://ollama.com/install.ps1 | iex`
- Pull model: `ollama run llama3.1`
- Serve: `ollama serve`

2) Run the app:
- `dotnet run`

Self-tests:
- `dotnet run -- --self-test`

If you get an Ollama connection error, confirm:
- Ollama is running (`ollama serve`)
- `.env` has `OLLAMA_ENDPOINT_LOCAL=http://localhost:11434`

---

## Run with Docker (recommended)
Important: this app is interactive, so you must run with `-it`.

Build locally:
- `docker build -t kbresearch/development:semantickernel.1.0 .`

Run by passing container parameters (env vars):
- `docker run -it --rm --name development -e LOCATIONS_DB_CONNECTION_STRING="Data Source=locations.db" -e OLLAMA_ENDPOINT_DOCKER=http://host.docker.internal:11434 -e OLLAMA_MODEL=llama3.1:latest kbresearch/development:semantickernel.1.0`

Run with defaults (simplest):
- `docker run -it --rm --name development kbresearch/development:semantickernel.1.0`

To show weather informations:
- `docker run -it --rm --name semantickernel-afonso -e API_KEY=71a1931ef95f22c8dabe8092c37d7b33 kbresearch/semantickernel-afonso:semantickernel.1.0`

Linux note (if `host.docker.internal` does not work):
- Add `--add-host=host.docker.internal:host-gateway`

---

## Run from Docker Hub (pull + run)
Pull:
- `docker pull kbresearch/development:semantickernel.1.0`

Run:
- `docker run -it --rm --name development kbresearch/development:semantickernel.1.0`

---

## Run with Docker Compose
`docker compose` will pass environment variables into the container.

Use `.env` for convenience (Compose uses it for `${VAR}` substitution):
- `docker compose --env-file .env up --build`

Or set them in your shell and run:
- `docker compose up --build`

---

## Code map
- `Program.cs`: loads env, configures Kernel/agents, initializes DB, chat loop
- `AppLogic.cs`: pure parsing + table formatting
- `Services/ApiService.cs`: OpenWeather call (optional)
- `Services/LocationsDbContext.cs`: EF Core + SQLite
- `SelfTests.cs`: lightweight self-tests (`--self-test`)
