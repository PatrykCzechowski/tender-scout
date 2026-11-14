# ADR-0008: Bearer tokens validated locally, two scopes, Keycloak for local runs only

## Status

Accepted — 2026-11-14

## Context

The API and the MCP server answered anyone who could reach them, so they could only
run on a development machine. Before a deployment they must know who calls them and
what the caller may do. The callers are of three kinds: the web page, a script or
another service, and an MCP client.

Constraints: no Azure account yet, so the identity provider must run locally; a later
provider, such as Entra ID, must need no code change; the web page has no build step
and no backend of its own; tests run without a network.

## Options considered

| Question | **Chosen** | Rejected |
| --- | --- | --- |
| How a request is trusted | A JWT bearer token, validated in the service from the issuer's public keys | Asking the issuer about every token: a network call per request. API keys: no expiry, no scopes, and a store of our own |
| What a caller may do | Two scopes, `notices:read` and `documents:ask`, one policy for each | Roles and users: nothing in the data belongs to a user. One scope: reading a notice and asking a model about documents cost very differently |
| Identity provider for local runs | Keycloak as a plain container in the AppHost, with a realm file from the repository | The Aspire Keycloak packages: preview only. A token made by `dotnet user-jwts`: no sign-in for the page, and nothing like a real provider |
| Where the issuer comes from | The framework's own settings under `Authentication:Schemes:Bearer` | Settings of our own: binding code, and a second name for the same thing |
| Tokens in the tests | Signed by a test key that the hosts are given through the same settings | Keycloak in `dotnet test`: a large image and a slow start in every test run, and it cannot make an expired or wrongly signed token. A fake handler: the real checks would not run |
| MCP clients | Send the token as a header | OAuth discovery for MCP clients: protected resource metadata and client registration, a project of its own |
| The web page | Authorization code with PKCE, the token in memory only, no refresh token | The implicit flow: tokens in the address. A backend for the page that keeps the tokens: a second service. Tokens in browser storage: any script on the page can read them |

## Decision

- **Both services validate the token themselves.** The signature against the issuer's
  keys, the issuer, the audience and the lifetime are checked on every request. That
  the issuer and the audience are checked is set in code and cannot be switched off;
  which values are valid comes from the settings.
- **One audience for both services,** because they serve the same data under the same
  scopes.
- **A scope is a whole entry of the `scope` claim.** `GET /notices` and the two notice
  tools need `notices:read`; `POST /ask`, `POST /agent` and the document search tool
  need `documents:ask`. `/mcp` itself needs either one: a client with one scope sees
  and can call only the tools of that scope, and a call to another tool ends as a
  protocol error.
- **`/agent` needs only `documents:ask`,** although the agent reads notices too. The
  agent calls the tools in process, and those calls are not checked against the
  caller's scopes: the caller asked a question, it did not ask for notices.
- **401 and 403 come before the request is read.** No token, or a token that fails a
  check, gets 401 with a bearer challenge; a valid token without the scope gets 403.
- **What stays open:** `/health`, `/alive`, the files of the web page, the OpenAPI
  document, the reference page in Development, and the two values the page needs to
  sign in. No fallback rule protects "everything else"; a test lists every route of
  both services and fails when one outside this list answers without a token.
- **A service without an issuer or an audience does not start.** There is no setting
  and no environment that lets requests through without a token.
- **Keycloak is for local runs only.** The AppHost starts it with the realm file and
  no stored data, so the file is the realm. The realm has a public client for the
  page, a confidential client for machines, the two scopes as optional scopes and one
  invented user. Its fixed password and client secret are written in the file and say
  that they are local; the AppHost makes the admin password itself and keeps it in
  its user secrets.
- **The real realm is checked by the image check,** once for every pull request: it
  starts Keycloak with the file and calls the containers with a token of the machine
  client.

## Consequences

### Easier

- Another identity provider needs other values of two settings.
- A revoked right is a scope taken from a client at the provider; the services keep
  no list of callers.
- Every kind of invalid token is one line in a test, because the tests write their
  own tokens.
- The OpenAPI document says how to call each operation and which scope it needs.

### Costs and risks

- **A token stays valid until it expires,** 5 minutes with the local realm. A token
  that leaks cannot be withdrawn earlier; the services do not ask the issuer.
- **The issuer in the settings must be the one written in the tokens.** A service in
  a container must reach the issuer under the same address as the callers do.
- **The scope claim is `scope`.** A provider that writes scopes elsewhere, as Entra ID
  does with `scp`, needs a mapping at the provider or a change of one line.
- **Scopes say what may be called, not what may be seen.** Every caller with
  `notices:read` sees every notice.
- **The local user, its password and the machine secret are in the repository.** They
  are invented and open nothing but a Keycloak on a development machine, but the
  realm file must never be imported anywhere else.
- **An MCP client must be given a token by hand.** Clients that expect to discover the
  authorization server from the MCP server cannot sign in by themselves.
- **The page asks for a new sign-in after a reload,** because the token is only in
  memory. While the session at the provider lasts, that is one redirect.
- **Not checked:** an identity provider other than Keycloak, and what a protected call
  answers while the issuer cannot be reached for the first time (it is not a success;
  a test shows that).
