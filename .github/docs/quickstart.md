# Run Besitos

## Prerequisites

Use .NET SDK 10.0.403 and Aspire CLI 13.6.4.
Use Docker or rootless Podman for the server databases and database UIs.
SQL Server containers require Linux x86-64. Use an external SQL Server on unsupported hosts.
SQLite needs a local writable directory. Do not use a network filesystem.

The solution pins package versions through Central Package Management.
No NuGet package publication is required. All projects are in `src`.

```bash
dotnet restore Besitos.slnx
dotnet build Besitos.slnx
aspire start --apphost src/QueueDemo.AppHost/QueueDemo.AppHost.csproj --launch-profile http
```

For Podman, prefix the Aspire command with `ASPIRE_CONTAINER_RUNTIME=podman`.
The HTTP profile is for local development. The HTTPS profile uses the local development certificate.

## Activate one store

1. Open the console URL from the Aspire dashboard.
2. Leave unused resources stopped.
3. For SQLite, start `sqlite-worker`.
4. For PostgreSQL, start `postgres`, then `postgres-worker`.
5. For SQL Server, start `sqlserver`, then `sqlserver-worker`.
6. Select the provider in the console.
7. Enqueue a command. Select **Start processing**.

Starting AppHost starts only the console.
Database containers, database UIs, and workers use Aspire's `WithExplicitStart` API.
AppHost creates `.data/queue.db` at startup if the file does not exist.
The worker initializes its schema and starts paused.

### Open a database UI

Start the database and worker using the steps above. Then start its UI from the Aspire dashboard:

| Database | UI resource |
| --- | --- |
| PostgreSQL | `pgadmin` |
| SQLite | `sqlite-queue-sqliteweb` |
| SQL Server | `dbgate` |

Open the UI endpoint shown in the dashboard.
pgAdmin lists only the queue PostgreSQL server.
SQLiteWeb uses the same `queue.db` file as the SQLite worker.
The UIs are available only in run mode. Publish mode excludes their containers.

The following CLI commands provide the same SQLite workflow:

```bash
aspire resource sqlite-worker start --apphost src/QueueDemo.AppHost/QueueDemo.AppHost.csproj
aspire wait sqlite-worker --apphost src/QueueDemo.AppHost/QueueDemo.AppHost.csproj
```

Use **Pause** to stop new claims. Active handlers finish with lease renewal enabled.
Change `Worker__Concurrency`, then restart the worker to change X.

Try these presets:

| Preset | Result |
| --- | --- |
| Success | Complete and delete the queue row |
| Retry twice | Fail twice, then complete |
| Poison | Move directly to dead letters |
| 45s renewal | Run longer than the default lease |

Use the inspector to read payloads and errors as text.
Replay keeps the ID and resets attempts to zero.
A replayed poison command fails again until its handler behavior changes.

## Analyze with SonarQube

SonarQube Community Build runs locally with its own PostgreSQL database.
Both containers require manual startup and stop with AppHost.
Named volumes preserve their data. Queue workers do not depend on SonarQube.
Port `9000` must be available.

Check the container host limits before starting SonarQube:

```bash
sysctl vm.max_map_count fs.file-max
ulimit -n
ulimit -u
```

Required minimums are `vm.max_map_count=524288`, `fs.file-max=131072`, `nofile=131072`, and `nproc=8192`.
Check the container runtime's limits too. Shell limits alone do not configure its service.
With Docker Desktop, check the Linux VM hosting Docker.
See [SonarSource's Linux requirements](https://docs.sonarsource.com/sonarqube-community-build/server-installation/pre-installation/linux).
AppHost does not change host settings.

Aspire generates `sonar-db-password` and stores it in the AppHost user secrets.
To provide your own password, run this before the first database startup:

```bash
dotnet user-secrets set --project src/QueueDemo.AppHost "Parameters:sonar-db-password" "YOUR-STRONG-PASSWORD"
```

Retain this secret while retaining `besitos-sonar-postgres-data`.
Changing the parameter does not change the password inside an existing database.
SonarQube also uses `besitos-sonarqube-data`, `besitos-sonarqube-extensions`, and `besitos-sonarqube-logs`.
Do not delete these volumes to restart the service.

Start AppHost, then start the resources through its dashboard or these commands:

```bash
aspire resource sonar-postgres start --apphost src/QueueDemo.AppHost/QueueDemo.AppHost.csproj --non-interactive
aspire wait sonar-postgres --apphost src/QueueDemo.AppHost/QueueDemo.AppHost.csproj --non-interactive
aspire resource sonarqube start --apphost src/QueueDemo.AppHost/QueueDemo.AppHost.csproj --non-interactive
aspire wait sonarqube --apphost src/QueueDemo.AppHost/QueueDemo.AppHost.csproj --non-interactive
```

Open <http://localhost:9000> and wait for the login page.
Aspire's running state does not guarantee that SonarQube has finished initialization.
Sign in with `admin` / `admin`, then change the password.
Create a local project with key `besitos` and generate an analysis token.

Run the scanner separately from the repository root:

```bash
dotnet tool install --global dotnet-sonarscanner
read -rsp 'SonarQube analysis token: ' SONAR_TOKEN
echo
dotnet sonarscanner begin /k:"besitos" /d:sonar.host.url="http://localhost:9000" /d:sonar.token="$SONAR_TOKEN"
dotnet build Besitos.slnx --no-incremental --disable-build-servers
dotnet sonarscanner end /d:sonar.token="$SONAR_TOKEN"
unset SONAR_TOKEN
```

Skip installation if the scanner is already installed. Run each analysis step only after the previous step succeeds.
The scanner wraps the build and uploads results. AppHost does not run analysis automatically.
Scanner output under `.sonarqube/` is ignored by Git.
See the [SonarScanner instructions](https://docs.sonarsource.com/sonarqube-community-build/analyzing-source-code/scanners/dotnet/using).

## Run without Aspire

Generate one local secret. Share it between the web process and worker processes.
Keep the secret in environment variables or a secret store.

```bash
export Demo__Key="$(openssl rand -hex 32)"
export Demo__WebUrl=http://127.0.0.1:5210
export Queue__Provider=sqlite
export ConnectionStrings__queue="Data Source=$PWD/.data/standalone.db"

dotnet run --project src/QueueDemo.Web -- --urls http://127.0.0.1:5210
```

In another terminal, set the same variables and secret, then start the worker:

```bash
dotnet run --project src/QueueDemo.Worker -- --urls http://127.0.0.1:5211
```

The console defaults to worker ports 5211, 5212, and 5213.
Override `Providers__sqlite`, `Providers__postgres`, or `Providers__sqlserver` when required.
Set `Queue__Provider=postgres` or `sqlserver` with an ordinary driver connection string for another provider.
Use `ConnectionStrings__queue` for external databases too.

Run a second worker against the same store and named queue:

```bash
Worker__AutoProcess=true dotnet run --project src/QueueDemo.Worker -- --urls http://127.0.0.1:5214
```

Each process has its own X loops. Each process must use a different HTTP port.
Heartbeats show all connected processes in the console.
Console start/pause actions target the configured worker endpoint for that provider.
Control other standalone processes through their authenticated endpoint or restart them with suitable configuration.

For library-only hosts, HTTP and the demo secret are unnecessary.
See the [library guide](library.md).

## Demonstrate crash recovery

Enqueue a delay command with `{"milliseconds":120000}` metadata. Start processing.
Terminate only its standalone worker process with `kill -KILL <worker-pid>`.
Restart that worker. Recovery waits for lease expiry and retry delay.
Each new claim increases attempts. Repeated crashes eventually create a dead letter.

A normal Aspire **Stop** requests graceful shutdown first.
It can drain a short handler successfully, so it is not an immediate crash simulation.

Stop the demo with:

```bash
aspire stop --apphost src/QueueDemo.AppHost/QueueDemo.AppHost.csproj
```

Normal shutdown preserves database volumes and the SQLite file.
See [storage operations](storage.md) before resetting local data.
