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

**Compatibility:** requires **Fleans 0.6.0+** (`IWorkflowInstanceCallback.GetVariablesIfActive`).
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

Next to the [engine compose bundle](https://nightbaker.github.io/fleans/guides/self-host-docker-compose/),
add a `docker-compose.override.yaml`:

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

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings__orleans-redis` | *required* | The engine's Redis: clustering, PubSubStore, reminders, streams. |
| `Orleans__ClusterId`, `Orleans__ServiceId` | *required* | The engine's cluster ids. |
| `ConnectionStrings__camunda-external-tasks` | *required* | Database for the `camunda_external_tasks` table. The database and table are created at startup. |
| `CamundaExternalTasks__Provider` | `Postgres` | `Postgres` or `Sqlite`. Use PostgreSQL with more than one replica. |
| `CamundaExternalTasks__BasePath` | `/engine-rest` | Route prefix. |
| `Fleans__Streaming__QueueCount` | `8` | Must equal the engine's Redis stream queue count. |

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

| Endpoint | Notes |
|---|---|
| `POST /external-task/fetchAndLock` | `workerId`, `maxTasks`, `usePriority`, `asyncResponseTimeout` (long polling, up to 30 min), per topic `topicName`, `lockDuration`, `variables`, `localVariables`, `processDefinitionId(In)`, `processDefinitionKey(In)`. |
| `POST /external-task/{id}/complete` | `variables` go to the workflow scope. `localVariables` are visible only to output mappings. |
| `POST /external-task/{id}/failure` | `retries > 0`: the task is fetchable again after `retryTimeout`. `retries = 0`: the activity fails. |
| `POST /external-task/{id}/bpmnError` | Fails the activity with `errorCode`. An error boundary event with that code catches it. |
| `POST /external-task/{id}/extendLock`, `/lock`, `/unlock` | Same semantics as Camunda. |
| `PUT /external-task/{id}/retries`, `/priority` | Setting retries to `0` fails the activity. |
| `GET /external-task`, `/count`, `/{id}`, `/topic-names` | Filters: `topicName`, `workerId`, `locked`, `notLocked`, `withRetriesLeft`, `noRetriesLeft`, `processInstanceId`, `activityId`, `processDefinitionId`, `processDefinitionKey`, `firstResult`, `maxResults`. |

Errors use Camunda's `{ "type", "message" }` body and status codes: `404` for an unknown task,
`500` when the task is locked by another worker, and `400` for an invalid request. Responses use
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
- **Scope:** only `/external-task`. Start processes and correlate messages through the
  [Fleans REST API](https://nightbaker.github.io/fleans/reference/api/).
- **Clocks:** lock expiry uses each replica's clock, so keep replicas NTP-synced.

Logs use EventIds 9350–9367: 9350 queued, 9360 locked, 9361 completed, 9362 failure with
retries left, 9363 retries exhausted, 9364 BPMN error, 9365 stale task removed.

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

## License

[PolyForm Internal Use License 1.0.0](LICENSE), the same as Fleans.
