# JSON serialization, the OpenAPI document and error responses

## JSON serialization

`PrintLogJsonSerializerContext` (#67) supplies compile-time metadata for the highest-volume
responses: the two cached summary lists, the six analytics tabs and the two sync responses (#234).
It is **partial by design** and
is `Insert`ed at position 0 of `JsonSerializerOptions.TypeInfoResolverChain`, with ASP.NET Core's
`DefaultJsonTypeInfoResolver` still behind it. Assigning `TypeInfoResolver` instead would drop that
fallback, and the failure surfaces on some unrelated endpoint rather than at the edit.

Adding a `[JsonSerializable]` root is the whole change — the generator walks the type graph, so
nested types need no attribute. Do **not** add `required` or `[JsonRequired]` to make a type fit;
see the DTO rules in AGENTS.md, both are enforced by `System.Text.Json` and turn a tolerated missing field
into a 400.

`JsonSourceGenerationTests` covers this from two directions. The structural test enumerates the
closure off the context's own generated `JsonTypeInfo<T>` properties — so it needs no maintenance
when a root's graph changes — and compares member names, order, types, accessors and required-ness
against the reflection resolver for all ~100 types. The endpoint test additionally pairs each root
with a URL that produces one and asserts the body is byte-identical either way; that pairing _is_
hand-maintained, so a new `[JsonSerializable]` root needs a new row in `HotResponses()`.

Worth knowing when judging how much test coverage a change here needs: a resolver supplies metadata
only. Converters are shared by both paths, so the two cannot disagree on how a `decimal`, `DateOnly`
or `DateTimeOffset` is _formatted_ — only on which members exist, their names and their order.
Fixture variety in values proves nothing here; structural comparison proves everything.

Two non-obvious facts, both verified rather than assumed:

- **MVC serializes through a _copy_ of the registered options.**
  `SystemTextJsonOutputFormatter` substitutes `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` when
  `JsonOptions` leaves `Encoder` null, which this app does. So a real response body escapes `+` and
  `<` differently from anything serialized with the options DI hands out, and a test that compares
  the two must reproduce the copy.
- **The generated fast-path writer is unreachable here, and that is fine.** It requires
  `Encoder` to be null, which the copy above rules out for every MVC response. What the context
  buys is the metadata path — compile-time property discovery and build-time member accessors in
  place of runtime reflection and IL emit. The `[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]`
  on the context is not what enables that; it only keeps `Default.Options` from being PascalCase
  for anyone serializing through the context directly.

## OpenAPI document

`/swagger/v1/swagger.json` is generated in `OpenApi/OpenApiSetup.cs` (#126). Agents turn it into
function-calling tools, so it has to be valid OpenAPI. CI lints it with Redocly: `OpenApiDocumentTests`
writes the document to `$OPENAPI_DOCUMENT_OUTPUT`, and the next step lints that file.

- **Operation ids are `{Controller}_{Action}`.** Bare method names collided across controllers
  (`GetMaterials` on five of them). Two actions with the same name in one controller still collide,
  and the test names them.
- **`servers` comes from `OpenApi:ServerUrl`.** `appsettings.json` holds the production URL. The local
  environments blank it, which falls back to the request's origin. Without that blank, Swagger UI's
  "Try it out" sends local requests to production. A local `appsettings.Development.json` copied
  from an old example needs the blank added.
- **The `oauth2` scheme lists no data scopes, on purpose.** REST tokens (audience
  `Auth0:ApiIdentifier`) are checked for audience only. `read:printdata`/`write:printdata` exist
  only on the MCP audience. Listing them here would describe a check the API does not make. Add
  them when REST starts enforcing them.
- **Security requirements come from each endpoint's own authorization metadata.** `[AllowAnonymous]`
  adds an empty requirement, and `InteractiveUserOnly` drops the API-key schemes. Don't hand-edit
  them per operation.
- **Enums stay integers on the wire; their names travel as `x-enumNames`/`x-enum-varnames`** (#152).
  The web app, slicer plugins and connectors all send the numbers, so a `JsonStringEnumConverter`
  would be a breaking change, not a doc fix. `EnumNamesSchemaFilter` is registered after
  `IncludeXmlComments` because that filter replaces the description it extends.
- **JSON request bodies list `application/json` only.** MVC also accepts `text/json` and
  `application/*+json` and still does; the document drops them because openapi-python-client
  silently skips any operation that lists them (33 write endpoints).
- **A body the action reads itself needs `[OpenApiRequestBody(typeof(T))]`** (the Moonraker
  notifier), and a `[FromForm]` field bound with `JsonModelBinder` is restored by
  `JsonFormPartsOperationFilter` (the OctoPrint webhook). ApiExplorer describes neither.

## Error responses without a body (#127)

`ProblemDetailsStatusCodePages` gives every body-less 4xx/5xx an `application/problem+json` body
(`type`, `title`, `status`, `detail`, `traceId`, nothing more), whatever the `Accept` header says.
It only fills an empty response, so existing bodies are untouched; changing those is #73, because
the slicer plugins parse them. Every REST 401 also gets
`WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource/api"`.
**`/mcp` is excluded from both**: its 401 header drives MCP OAuth discovery.

- **REST has its own metadata document, on purpose.** The root `/.well-known/oauth-protected-resource`
  describes the MCP audience, and the REST bearer scheme rejects MCP-audience tokens.
- **It is served by middleware before `UseAuthentication`, not by `MapGet`.** The MCP SDK's
  metadata handler runs inside `UseAuthentication` and answers every path under
  `/.well-known/oauth-protected-resource` with the MCP document, so an endpoint there never runs.
