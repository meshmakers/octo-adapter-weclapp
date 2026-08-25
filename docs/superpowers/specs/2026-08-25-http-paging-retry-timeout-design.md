# MakeHttpRequest@1: paged fetch, retry, timeout and configured access

Product half of the standard-node redesign for the WeClapp source pipelines. The adapter half
(YAML switch of `weclapp-articles-to-as.yaml`, `weclapp-articles-to-ck.yaml`,
`weclapp-orders-to-ai.yaml`, plus the `WeClappApi.SendAsync` timeout fix) follows in a separate
change once the extended node ships.

## Problem

The three source pipelines extract through the adapter's own `WeClappFetchStep@1`. Almost
everything that node does is generic HTTP work, but `MakeHttpRequest@1` cannot do it today:

- **No paging.** The node issues exactly one request. `WeClappFetchCore.FetchAllPagesAsync`
  (`WeClappFetchTriggerNode.cs:424`) walks `?page=N&pageSize=M` until a page comes back empty or
  short, collecting the elements under `$.result`.
- **No retry.** `WeClappFetchCore.GetWithRetryAsync` (`WeClappFetchTriggerNode.cs:460`) retries
  5xx, 408, 429 and `HttpRequestException` with `base * 2^(attempt-1)` backoff and fails a
  non-transient status immediately.
- **No timeout.** The node resolves the shared default `HttpClient`
  (`ServiceCollectionExtensions.cs:161`, `services.AddHttpClient()`), so the 100 s client default
  applies, and `SendAsync(request)` is called without a cancellation token.
- **No configured access.** Header values come from the node configuration or the data context.
  An API key would therefore have to sit in the pipeline definition (visible in the studio) or
  travel through the pipeline data.
- **Failures are silent.** On a non-success status and on any exception the node calls
  `nodeContext.Error(...)` and returns without invoking `next`. `INodeContext.Error` only logs
  (`NodeContext.cs:110`, `DefaultPipelineLogger.cs:26`), so the execution finishes green while the
  rest of the chain never ran.

The last point is load-bearing. `WeClappFetchStep@1` throws, and the operational alerting is built
on failed executions. Swapping in the node as it stands would turn a loud failure into a silent
no-op.

## Goal

Four additive capabilities on `MakeHttpRequest@1`, each inert unless configured, so a pipeline can
compose the WeClapp fetch from standard nodes:

```
before                                       after
------                                       -----
WeClappFetchStep@1                           MakeHttpRequest@1
  apiConfiguration: WeClappApi                 apiConfiguration: WeClappApi
  entity: article                              authHeaderName: AuthenticationToken
  pageSize: 100                                url: /article
  (paging, retry, join, shaping, throwing)     paging: { itemsPath: $.result, pageSize: 100 }
                                               retry: { maxAttempts: 4, backoffBaseSeconds: 1 }
                                               timeoutSeconds: 60
                                               onHttpError: Throw
```

An existing consumer that sets none of the new properties keeps today's behaviour byte for byte,
including the silent failure path.

## Design

### 1. Configured access - `apiConfiguration` and the auth header

Three new node properties:

| Property | Default | Meaning |
|---|---|---|
| `ApiConfiguration` | unset | Name of a GlobalConfiguration entry supplying `{ baseUrl, apiKey }` |
| `AuthHeaderName` | `Authorization` | Header the key is sent in |
| `AuthHeaderValuePrefix` | empty | Scheme prefix such as `Bearer `; without it the key is sent verbatim |

With both defaults in place the key goes out as `Authorization: <key>`, scheme-less. That suits a
target expecting a bare token and is wrong for one expecting `Bearer`, which is exactly what the
prefix is for; the node adds no scheme on its own.

Resolution follows the pattern every other configured node in this repository uses
(`EMailSenderNode`, `GrafanaProvisionTenantNode`, `SendMicrosoftGraphEmailNode`,
`SftpServerSettingsResolver`): `etlContext.GlobalConfiguration.IsDefined(name)`, then
`GetValue<HttpApiSettings>(name)`, both failures reported through a typed
`MeshAdapterPipelineExecutionException` naming the node and the entry.

`HttpApiSettings` is a two-string record, `BaseUrl` and `ApiKey`, with a `ToString` that masks the
key - the shape `WeClappConnectionSettings` uses in the adapter today. The tenant side already
exists: `System.Communication/WeClappConfiguration` (CCS) declares `Host` under the **name**
`BaseUrl`, plus `ApiKey`, and the running adapter deserializes exactly those two keys. Unlike the
host-key and timeout fields of the SFTP layer, this needs no CCS model change.

URL composition: when `ApiConfiguration` is set, the resolved `Url`/`UrlPath` is a path relative to
`BaseUrl` and is appended with a single separator, the base's trailing slash trimmed. An **absolute
URL together with `ApiConfiguration` is rejected before any request goes out**: the combination
would send the configured key to whatever host the URL names, so a typo in a pipeline definition
becomes a credential leak. Nothing needs it - the pipelines here address one API through its base
URL - and refusing it costs no compatibility, because `ApiConfiguration` is new. A caller who wants
an absolute URL keeps supplying the header itself, as today. Without `ApiConfiguration` nothing
about URL handling changes.

The entry is read when the pipeline is deployed and cached for the adapter's lifetime, so rotating
a key takes effect only after a redeploy or an adapter reconnect. That is how every configured node
here behaves; it belongs in the node documentation rather than in this node's code.

### 2. Paging - the `paging` section

A nested optional configuration object. Absent means one request, as today.

| Property | Default | Meaning |
|---|---|---|
| `ItemsPath` | required when paging | JSONPath to the array inside one response, e.g. `$.result` |
| `PageParameterName` | `page` | Query parameter carrying the page number |
| `PageSizeParameterName` | `pageSize` | Query parameter carrying the page size |
| `PageSize` | 100 | Requested elements per page |
| `FirstPageNumber` | 1 | Some APIs start at 0 |
| `StopOnShortPage` | `true` | Treat a page with fewer elements than requested as the last one |
| `MaxPages` | 500 | Safety cap; reaching it is a failure, never a quiet truncation |

The node walks pages, appending the two parameters to the query the URL already carries, and
collects the elements of every page into one **flat array** written to `TargetPath`. Nothing is
wrapped and nothing is added; a consumer that needs elements wrapped or enriched does that
downstream. Stop rules:

- **Empty array** at `ItemsPath` - stop, the normal end.
- **Short page** with `StopOnShortPage` - stop. Proven correct for WeClapp by the fetch core in
  production; the switch exists because an API that silently caps `pageSize` server-side would make
  every page look short and lose everything after page one.
- **`ItemsPath` resolves to nothing, or to a non-array** - failure, not a stop. Without that
  distinction a changed response shape reads as "zero elements, execution green".
- **`MaxPages` reached** - failure. A target that ignores the page parameter returns the same full
  page forever, and a cap that truncates quietly is the same class of silent data loss.

The section is named for page-number paging (`Page*`) so a cursor mode can be added later as
`paging.mode` plus `paging.cursor*` without renaming anything. No cursor support is built now -
there is no consumer for it.

### 3. Retry - the `retry` section

| Property | Default | Meaning |
|---|---|---|
| `MaxAttempts` | 1 | Total attempts per request, so the default is "no retry" |
| `BackoffBaseSeconds` | 1 | Delay before attempt n is `base * 2^(n-1)`; 0 disables waiting |

Transient, and therefore retried until the attempts are used up: status 5xx, 408 and 429,
`HttpRequestException`, and `TaskCanceledException`. The last one is the half of the timeout
question that belongs to the product node: a client timeout is a transient condition and should
consume a retry rather than end the run. There is no ambient caller token to distinguish against -
`INodeContext` exposes none - so inside this node every `TaskCanceledException` originates from its
own timeout. The caller-cancellation distinction is real only in the adapter's
`WeClappApi.SendAsync` and is handled there.

Everything else - any other 4xx, any other exception - is non-transient and ends the request at
once without consuming further attempts.

Retry is **per request**, which in a paged run means per page: the attempts are spent on the page
that failed, and only a page that then succeeds lets the walk move on to the next one. A page whose
attempts run out ends the whole run through the failure path - there is no skipping ahead over a
page, and a partially collected run is never written to `TargetPath`. What "per request" rules out
is the opposite mistake: a retry never restarts pagination at the first page, and pages already
collected are never fetched again.

Delays go through an injected `TimeProvider` (constructor parameter defaulting to
`TimeProvider.System`, the pattern `WeClappFetchStepNode` uses), so tests pin the backoff without
waiting for it.

### 4. Timeout - `timeoutSeconds`

One optional integer, applied **per attempt** through a `CancellationTokenSource` handed to
`SendAsync`. The injected `HttpClient` is shared across nodes and its `Timeout` is process-wide and
immutable once a request has gone out, so it is never touched. Unset keeps today's behaviour, the
client default. Every retry attempt gets its own full budget.

### 5. Failure semantics - `onHttpError`

| Value | Behaviour |
|---|---|
| `LogAndStop` (default) | Exactly today: the error is logged, `next` is not invoked, the execution stays green |
| `Throw` | A typed `MeshAdapterPipelineExecutionException` carrying the status code, the number of attempts and the response body truncated to 300 characters, without splitting a surrogate pair (`WeClappFetchCore.Truncate` parity) |

The default stays as it is because `MakeHttpRequest@1` is released and its consumers cannot be
enumerated; flipping it would turn other tenants' pipelines red on their next chart update. The
house pattern for a behaviour change is a new node version (`ApplyChanges@1` to `@2`), a product
conversation this change does not need to open. The default value is named `LogAndStop` because
that is what happens - the branch stops, the execution does not fail - and deliberately not
"continue": `ForEach@1`'s `continueOnError` has the opposite polarity and a different subject
(iterations, not requests).

`OnHttpError` governs **runtime outcomes only**: the HTTP status of a response, retries running
out, the paging cap, and an `ItemsPath` that does not resolve to an array in a response that did
arrive.

**Configuration and resolution errors on the new properties always throw**, whatever `OnHttpError`
says: an `ApiConfiguration` entry that is not defined, an entry whose `baseUrl` or `apiKey` is
blank, an absolute URL combined with `ApiConfiguration`, and a `paging` section without
`ItemsPath`. None of these can be answered by a retry or a different target, and none should be
survivable: a mistyped configuration name that merely logged under the default would leave an
operator with a green execution that never called anything. This matches how every configured node
in the repository behaves - `SftpServerSettingsResolver` throws on a bad entry rather than
reporting it.

The validation the node performs today - method, target path, response format, body content type,
parameter completeness - keeps reporting and returning as it does now. Those paths are reachable
for consumers that set none of the new properties, and the whole point of the additive cut is that
such a consumer sees no change. Every always-throwing case above is reachable only by configuring
something new.

One consequence has to be pinned by a test rather than assumed. `ForEach@1` isolates every
exception **except** `OperationCanceledException` - that is the filter in its iteration body - and
`TaskCanceledException` derives from it. A timeout that outlives the retries must therefore leave
this node as the typed exception and never as the raw cancellation, otherwise one slow request
aborts the whole loop instead of failing its own iteration.

This is what makes per-item isolation possible: with `onHttpError: Throw` on a per-order customer
request inside a `ForEach@1` running with `continueOnError: true`, a customer that fails
permanently fails only its own iteration. That mechanism is proven on staging, where one iteration
failed, the aggregate named the failed index and the following tick was green. The acceptance case
for the composition is therefore: the customer of order 2 fails permanently, orders 1 and 3 are
delivered, the failure is reported loudly, and order 2 is picked up again on the next tick.

### 6. Order of operations

Per attempt, per page: resolve settings and URL, attach headers including the auth header, send
with the per-attempt timeout, classify the status, retry or fail, read `ItemsPath`, append to the
accumulator, decide whether a further page follows. `TargetPath` is written once, after the walk
completes, so a failed run leaves no half-filled array behind.

## Tests

Written test-first, grouped as they will appear in `MakeHttpRequestNodeTests`:

**Unchanged behaviour**, with none of the new properties set: one request; the response stored as
today; a failing status logged, `next` not invoked, nothing thrown. And the same with paging, retry
and timeout configured but `OnHttpError` left at its default: attempts run out, the failure is
logged, `next` is not invoked, nothing is thrown and nothing is written to `TargetPath` - using the
new features does not quietly change what a failure does.

**Paging**: three pages ending short; a full page followed by an empty one; `StopOnShortPage:
false` walking to the empty page; a missing `ItemsPath` failing rather than stopping; a non-array
at `ItemsPath` failing; the cap failing with a message that names it; the accumulated array flat
and in page order.

**Retry**: 500, 429, 408, `HttpRequestException` and `TaskCanceledException` each followed by a
success; 400, 401 and 404 failing on the first attempt; exhausted attempts carrying status, attempt
count and truncated body; backoff delays of `base * 2^(n-1)` against a fake `TimeProvider`; a
failing page retried in place while the walk continues at the next page.

**Timeout**: a per-attempt timeout cancelling an attempt while the next one starts with a fresh
budget; unset meaning no per-request cancellation.

**Configured access**: the auth header sent under the configured name, with and without a prefix; a
URL join matrix over a base with and without a trailing slash against a path with and without a
leading slash, each yielding exactly one separator; an absolute URL combined with
`ApiConfiguration` rejected before a request goes out; an undefined entry and an entry with a blank
`baseUrl` or `apiKey` throwing **even with `OnHttpError` unset**, with the entry named; the key
never appearing in a log line or an exception message.

**Configuration robustness**: every new optional integer resolving to its default both when the
property is omitted and when the pipeline definition carries an explicit null, in both cases
without throwing.

**Composition**: the exception raised under `onHttpError: Throw` caught by `ForEach@1` with
`continueOnError: true`, so the isolation the acceptance case depends on is demonstrated rather
than assumed.

## Rollout

Product PR, then a mesh-adapter release, then the chart lift on staging, and only then the YAML
switch - the pipeline deserializer rejects properties the deployed build does not know, so the
order is not negotiable. The adapter half follows as its own change.

## Deliberate non-goals

Cursor and link-header paging (no consumer today, and the naming leaves the door open). Honouring
`Retry-After` on 429. Any change to the shared `HttpClient` or its registration. Streaming or
chunked response handling. Paging over POST bodies. Changing the default failure semantics of `@1`,
which is a `@2` question and stays outside this change.

## Verification before coding

The settings record is pinned against the running tenant, not against the CK definition alone. The
staging entry was exported read-only and inspected for structure only - attribute identifiers and
whether a value is set, never a value - and the export was deleted afterwards:

- `rtWellKnownName: WeClappApi`, `ckTypeId: System.Communication/WeClappConfiguration`
- exactly two attributes, `System.Communication/Host` and `System.Communication/ApiKey`, both set,
  and no others

The CK type maps the identifier `Host` to the attribute **name** `BaseUrl` and `ApiKey` to
`ApiKey`, so the payload the adapter deserializes carries the keys `BaseUrl` and `ApiKey`. The
running system corroborates it: the adapter resolves this entry into a record with exactly those
two property names and fetches successfully on staging, which a payload keyed by identifier could
not do - the resolver would reject it for a missing base URL. `HttpApiSettings` therefore uses
`BaseUrl` and `ApiKey`, and a shape test pins that pair.

## What the adapter half still owns

Named here so the boundary is explicit, not because this change implements any of it: wrapping the
flat item array into the element shape the pipelines consume today (`$.articles` as `{ item }`,
`$.orders` as `{ item, customer }`), the AS batch metadata, the supply-source enrichment, the
per-order customer join with `continueOnError`, and the same `TaskCanceledException` treatment in
`WeClappApi.SendAsync`.
