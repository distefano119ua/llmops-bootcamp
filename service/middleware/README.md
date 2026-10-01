# Infrastructure logging

Configuration: `logging_config.json`. All console events use flat JSON, one event per line.

- `Logging.LogLevel`: global and per-category levels (`Trace`, `Debug`, `Information`,
  `Warning`, `Error`, `Critical`, `None`). HTTP client events are disabled by default.
- `InfrastructureLogging.ServiceName`: the `service` field.
- `TimestampFormat`: UTC timestamp format; keep the date and timezone for ingestion.
- `IncludeCategory`: include the source component.
- `IncludeExceptionStackTrace`: add a flat `exception_stack_trace` string.
- `DatabaseHealthIntervalSeconds`: delay between dedicated PostgreSQL probes (default 5s).
- `DatabaseHealthTimeoutSeconds`: probe timeout (default 3s). Both values have a minimum of 1s.
- `ExcludedPaths`: exact GET paths whose successful HTTP events are suppressed
  (case insensitive). Defaults cover health checks and console polling. HTTP 4xx/5xx,
  unrelated exceptions and cancellation remain visible; POST is never excluded.
  Known database outages on polling routes return HTTP 503 quietly: availability
  is already reported by the shared `db.unavailable` event.
- `StateFields`: permitted scalar structured properties. Reserved fields cannot be overwritten.
  `status_code` is always emitted for completed HTTP responses.
- `FrameworkMessages`: short message overrides keyed by `category:event_id`.
  Kestrel event 13 uses `Unhandled application exception`; its structured connection
  and request IDs become `connection_id` and `request_id` instead of appearing in the
  message. Exception fields are preserved. Other framework events retain their text.

Configuration reloads on change. In Docker, rebuild the service after editing the file,
or mount it at `/app/middleware/logging_config.json` to change it without rebuilding.
Environment variables override JSON, e.g. `InfrastructureLogging__ServiceName` or
`Logging__LogLevel__LlmOps.Middleware.PostgresOperationLogger=Warning`.

## Events

- `http.request.received` (Debug): incoming method/path and a generated `request_id`.
- `http.response.completed`: actual HTTP status and elapsed time for the full request pipeline.
- `http.request.failed`: unhandled exception; rethrown to ASP.NET Core.
- `http.request.aborted`: request cancellation.
- `db.operation.completed` (Debug) / `db.operation.failed` (Error): Postgres select/update/insert,
  including connection opening, execution and cleanup in `duration_ms`.
  Connectivity failures use shared `db.unavailable` instead of per-operation errors.
  SQL/schema/permission errors remain individual `db.operation.failed` events.
- `db.available` (Information): the initial probe succeeds.
- `db.unavailable` (Error) / `db.recovered` (Information): availability transitions.
  A bounded `SELECT 1` probe runs independently of HTTP traffic. Unchanged probe
  results do not generate logs. Probes and repositories share one availability state,
  so requests during the same outage do not produce repeated availability events.
  SQL/schema errors in application operations do not
  mean that the database itself is unavailable.
- `registry.no_active_prompt` (Warning): a successful registry read finds no active
  support prompt. `registry.active_prompt.restored` (Information) reports its return.
  `registry.active_prompt.available` (Information) confirms the first successful
  observation of an active support prompt.
  The health monitor checks the registry immediately on startup once PostgreSQL is
  reachable, then on each probe (default every 5s), without waiting for HTTP traffic.
  It reuses the probe connection and reads only whether an active support prompt exists.
  Each `db.available` / `db.recovered` forces one fresh registry state event even
  if the active prompt state is unchanged. If that read fails, the snapshot remains
  pending until a successful read; subsequent healthy probes do not repeat it.
  These events are emitted once per observed transition, also from registry reads in
  `/prompts` or `/chat`; a database read failure is not reported as an empty registry.
- `registry.check.failed` (Error) / `registry.check.recovered` (Information): registry
  read failure/recovery, e.g. a missing table or insufficient permissions. An unchanged
  read failure is logged only once and does not mark a reachable database unavailable.
  Unavailable PostgreSQL skips registry checking; loss of connection or timeout during
  the check produces only `db.unavailable`, never a registry failure or missing-prompt event.
- Framework messages: named framework event where available, otherwise `system.log`.

Logs are written to stdout. `X-Request-ID` is returned in HTTP responses; the same ID
is used by HTTP logs, DB logs and the existing chat response/DB request record.
Request/response bodies, query strings, SQL, SQL parameters, prompts, model names
and token usage are not included in infrastructure events. The existing business
records in Postgres remain separate.

Custom infrastructure messages are short fixed text. Operation, duration and HTTP
status appear only in their separate fields, not repeated in `message`.

## Verify

```sh
docker compose up -d --build service
curl -i http://localhost:8080/prompts
docker compose logs --no-log-prefix service
```

Successful console polling is quiet by default. To inspect the full request flow,
set `Logging.LogLevel.LlmOps.Middleware` to `Debug` and remove `/prompts` from
`ExcludedPaths`. The request then produces incoming HTTP, Postgres operation and
completed HTTP events with the same `request_id`. Normal user actions produce one
completed HTTP event at Information level. Framework startup events have no request ID.
An unavailable Postgres produces `db.unavailable` once per observed outage.
For propagated connectivity errors, middleware returns HTTP 503 without rethrowing
to Kestrel, avoiding duplicate `http.request.failed` and `ApplicationError` events.
Non-polling requests still produce a response summary with status 503. Errors after
response headers have been sent are rethrown because the status cannot be changed.
Endpoints with existing fallback behavior keep it; a failed insert still does not
interrupt chat. Recovery is reported by the next successful health probe.

To verify recovery, stop only the PostgreSQL container and wait for `db.unavailable`,
then start it and wait for `db.recovered`, without making an HTTP request. Leave it
running and confirm that further probes produce no repeated availability messages.
Immediately after recovery, expect `registry.active_prompt.available` (or
`registry.active_prompt.restored` if previously missing), otherwise `registry.no_active_prompt`.
To verify the registry, start the service without making any HTTP request: expect
`registry.active_prompt.available` if an active support prompt exists, otherwise
`registry.no_active_prompt`. Temporarily deactivate support prompts and wait for a
probe: expect one warning. Subsequent probes do not duplicate it. Restore an active
version: expect `registry.active_prompt.restored` on the next successful probe.
If PostgreSQL starts later than the service, these checks begin after it becomes reachable.
