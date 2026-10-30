# Run Besitos

## Prerequisites

Use .NET SDK 10.0.403 and Aspire CLI 13.6.4.
Use Docker or rootless Podman for the server databases.
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
Database containers and workers use Aspire's `WithExplicitStart` API.
SQLite creates `.data/queue.db` only when its worker starts.
The worker initializes its schema and starts paused.

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
