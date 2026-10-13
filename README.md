# Fleans Camunda External Task

Run your **Camunda 7 external-task workers on [Fleans](https://github.com/nightBaker/fleans)
without rewriting them.** This plugin serves the Camunda 7 `/engine-rest/external-task` REST API
on top of Fleans, so existing workers usually need only a new base URL. That covers
[`camunda-external-task-client-js`](https://github.com/camunda/camunda-external-task-client-js),
the Java `camunda-external-task-client`, and hand-written HTTP clients.

The Fleans engine has no Camunda-specific code. The plugin is a regular Fleans custom-task
plugin that runs in its own silo:

```text
Fleans engine ──ExecuteCustomTaskEvent (camunda:<topic>)──▶ plugin host ──▶ camunda_external_tasks table
                                                                 ▲
Camunda workers ──fetchAndLock / complete / failure / bpmnError──┘ (/engine-rest/external-task)
       complete → CompleteActivity(variables)   failure(retries=0) / bpmnError → FailActivity(code)
```

| Path | What |
|---|---|
| `src/Fleans.Plugins.CamundaExternalTask` | The NuGet package: stream handler grain, table, REST endpoints. |
| `src/Fleans.CamundaExternalTask.Host` | Ready-to-run plugin host (`Fleans:Role=Plugin`) and its container image (`Dockerfile`). |
| `tests/` | REST wire-format tests (SQLite, plus PostgreSQL with `FLEANS_PG_TESTS=1`) and stream-handler tests on an Orleans test cluster. |
| `docs/manual-test-plan/` | End-to-end check with a real Camunda client against a real engine. |

**Compatibility:** requires **Fleans 0.6.0-preview.1 or later** (`IWorkflowInstanceCallback.GetVariablesIfActive`). Run it against an engine built from the same Fleans version or newer.
`FleansVersion` in `Directory.Build.props` pins the `Fleans.Worker` package the plugin compiles against.

## 1. Adapt the BPMN

```xml
<!-- Camunda 7 -->
<bpmn:serviceTask id="charge" camunda:type="external" camunda:topic="charge-card" />
<!-- Fleans -->
<bpmn:serviceTask id="charge" type="camunda:charge-card" />
```

The topic is everything after `camunda:`. Workers keep subscribing to `charge-card`.
A mechanical rewrite (check the diff afterwards):

```bash
sed -E -i.bak 's/camunda:type="external"[[:space:]]+camunda:topic="([^"]+)"/type="camunda:\1"/g' processes/*.bpmn
```

Camunda's `<camunda:inputOutput>` is not read. Use Fleans
[`<zeebe:ioMapping>`](https://nightbaker.github.io/fleans/concepts/custom-tasks/#mapping-grammar)
instead: inputs appear to the worker as local variables, and outputs read the variables the
worker sends on `complete`.

## 2. Run the plugin host

The host joins the engine's Orleans cluster through the engine's Redis and serves the API on port 8080.

Released images are published as `ghcr.io/nightbaker/fleans-camunda-external-task:<version>`.
To build one yourself:

```bash
docker build -t fleans-camunda-external-task .
```

With the [engine compose bundle](https://nightbaker.github.io/fleans/guides/self-host-docker-compose/):

Start the engine first. The bundle creates the `fleans_default` network that the plugin host
joins, and the network only gets that name when the compose project is called `fleans`:

```bash
cd <unzipped-engine-compose-bundle>
echo "COMPOSE_PROJECT_NAME=fleans" >> .env   # names the network fleans_default
docker compose up -d                          # engine first: creates the network
```

Then add a `docker-compose.override.yaml` next to the bundle's `docker-compose.yaml`:

```yaml
services:
  camunda-external-task:
    image: fleans-camunda-external-task:latest
    networks: [fleans_default]
    ports: ["8090:8080"]
    environment:
      ConnectionStrings__orleans-redis: "redis:6379,password=${ORLEANS_REDIS_PASSWORD}"
      Orleans__ClusterId: "fleans"          # must match the engine bundle's CLUSTER_CLUSTER_ID
      Orleans__ServiceId: "fleans"          # must match CLUSTER_SERVICE_ID
      ConnectionStrings__camunda-external-tasks: "Host=postgres;Database=camunda_external_tasks;Username=postgres;Password=${POSTGRES_PASSWORD}"
    depends_on: [redis, postgres]
networks:
  fleans_default:
    external: true
    name: fleans_default
```

Then run `docker compose up -d` again from the bundle directory. Compose merges the override and
starts the plugin host on the existing network. If you skip the engine-first step or the project
name, it fails with `network fleans_default declared as external, but could not be found`.

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings__orleans-redis` | *required* | The engine's Redis: clustering, PubSubStore, reminders, streams. |
| `Orleans__ClusterId`, `Orleans__ServiceId` | *required* | The engine's cluster ids. |
| `ConnectionStrings__camunda-external-tasks` | *required* | Database for the `camunda_external_tasks` table. The database and table are created at startup. |
| `CamundaExternalTasks__Provider` | `Postgres` | `Postgres` or `Sqlite`. Use PostgreSQL with more than one replica. |
| `Fleans__Streaming__Redis__TotalQueueCount` | `8` | The same setting the engine reads. Set it to the same value as the engine, or stream events land on queues this host doesn't read. |

Only the Redis stream provider (the engine's default) is supported by this host. The endpoints
are anonymous. Put the host behind your gateway, or fork `Program.cs` and add authentication.
`MapCamundaExternalTaskApi()` returns the route group, so you can call `.RequireAuthorization()` on it.

To embed the plugin in your own plugin host instead, add the `Fleans.Plugins.CamundaExternalTask`
package and register it:

```csharp
builder.Services.AddCamundaExternalTasks(o => o.UseNpgsql(connectionString));
var app = builder.Build();
app.MapCamundaExternalTaskApi();   // → /engine-rest/external-task/*
```

## 3. Point the workers at the host

```java
ExternalTaskClient.create().baseUrl("http://camunda-external-task:8080/engine-rest").build();
```

```js
new Client({ baseUrl: "http://camunda-external-task:8080/engine-rest" });
```

## Supported endpoints

These are the endpoints the official Java and JS external-task clients call:

| Endpoint | Notes |
|---|---|
| `POST /external-task/fetchAndLock` | `workerId`, `maxTasks`, `asyncResponseTimeout` (long polling, up to 30 min), per topic `topicName`, `lockDuration`, `variables`, `localVariables`, `processDefinitionId(In)`, `processDefinitionKey(In)`. |
| `POST /external-task/{id}/complete` | `variables` go to the workflow scope. `localVariables` are visible only to output mappings. |
| `POST /external-task/{id}/failure` | `retries > 0`: the task is fetchable again after `retryTimeout`. `retries = 0`: the activity fails. |
| `POST /external-task/{id}/bpmnError` | Fails the activity with `errorCode`. An error boundary event with that code catches it. |
| `POST /external-task/{id}/extendLock`, `/lock`, `/unlock` | Same semantics as Camunda. |

Errors use Camunda's `{ "type", "message" }` body and status codes:
- `404` for an unknown task, including a task whose activity already ended, for example cancelled or interrupted by a boundary event. A worker that reports on such a task learns its result was not applied.
- `500` when the task is locked by another worker or its result is being reported.
- `400` for an invalid request. Responses use
the bare `application/json` content type, the only one the JS client parses.

## Differences from Camunda 7

- **No incidents.** `failure` with `retries = 0` fails the activity, with error code `500` and
  the worker's `errorMessage` plus `errorDetails`. The failure is visible in Fleans, and an error
  boundary event can catch it.
- **`bpmnError` variables are ignored** (a warning is logged).
- **Variable types:** `String`, `Boolean`, `Integer`, `Short`, `Long`, `Double`, `Date` (kept as a
  string), `Null`, `Json`, and `Object` with `serializationDataFormat: application/json`.
  Objects and lists come back as `Json`. Whole numbers come back as `Integer` when they fit in
  32 bits and as `Long` otherwise, so Java workers should read them as `Number`. `Bytes`, `File`
  and Java-serialized objects are rejected with `400`.
- **Identifiers:** task `id`, `executionId` and `activityInstanceId` are the Fleans activity
  instance id. `processInstanceId` is the workflow instance id, and `processDefinitionKey` is the
  process id. `businessKey`, `tenantId` and `processDefinitionVersionTag` are always `null`, and
  fetch filters on them are ignored.
- **Stale tasks:** a task whose activity ended without a worker (cancelled, or interrupted by a
  boundary event) is removed the next time a worker fetches from its topic.
- **Scope:** only the worker-facing endpoints above. The operator endpoints (`GET /external-task`,
  `/count`, `/topic-names`, `PUT /retries`, `PUT /priority`) are not provided. Tasks have no
  priority, so `usePriority` is accepted but has no effect. Both are tracked in
  [issue #1](https://github.com/nightBaker/fleans-camunda-external-task/issues/1). Start processes
  and correlate messages through the [Fleans REST API](https://nightbaker.github.io/fleans/reference/api/).
- **Rejected results:** if the engine refuses a `complete` outright (for example, outputs it cannot
  store), the activity is failed and the worker gets `400`. This is the same rule Fleans applies to
  its other plugins. A transient engine error returns `500`, and the worker can send `complete` again.
- **Clocks:** lock expiry uses each replica's clock, so keep replicas NTP-synced.
- **Long polling** wakes up immediately when a task becomes available on the same replica. Tasks
  that appear on another replica are found by a re-check every 2 seconds.

Logs use EventIds 9350–9369: 9350 queued, 9360 locked, 9361 completed, 9362 failure with
retries left, 9363 retries exhausted, 9364 BPMN error, 9365 stale task removed, 9367 task could
not be handed out, 9368 result rejected by the workflow.

## Development

```bash
dotnet build
dotnet test                         # SQLite + Orleans test cluster
FLEANS_PG_TESTS=1 dotnet test       # + PostgreSQL via Testcontainers (Docker)
```

To build against an unreleased Fleans, pack `Fleans.Domain.Abstractions`,
`Fleans.Application.Abstractions` and `Fleans.Worker` from a fleans checkout
(`dotnet pack -p:Version=<FleansVersion> -o <feed>`). Then build with
`-p:RestoreAdditionalProjectSources=<feed>`, using a separate `NUGET_PACKAGES` folder so the
stand-in packages don't shadow the published ones later.

## Releasing

Push a tag `v<version>` that matches `VersionPrefix`/`VersionSuffix` in `Directory.Build.props`.
`release.yml` runs the tests, then publishes the NuGet package and the
`ghcr.io/nightbaker/fleans-camunda-external-task` image.

NuGet uses [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing),
so there is no API key secret. On nuget.org, the `nightBaker` account needs a Trusted Publishing
policy with Repository Owner `nightBaker`, Repository `fleans-camunda-external-task` and
Workflow File `release.yml`.

## License

[PolyForm Internal Use License 1.0.0](LICENSE), the same as Fleans.
