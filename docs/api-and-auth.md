# API and authentication

## Authentication

The API and the MCP server serve data only to a request with a JWT bearer token of the configured issuer, and each
endpoint and tool requires one of two scopes. For local runs the AppHost starts [Keycloak](https://www.keycloak.org)
with the realm of `keycloak/tender-scout-realm.json`. The decisions are in
[ADR-0008](adr/0008-bearer-tokens-and-scopes.md).

| Route or tool | Scope |
| --- | --- |
| `GET /notices`; MCP tools `search_notices` and `get_notice` | `notices:read` |
| `POST /ask`, `POST /agent`; MCP tool `search_tender_documents` | `documents:ask` |
| `/mcp` itself | either one |
| `/health`, `/alive`, the files of the web page, `/openapi/v1.json`, `/scalar` (Development), `/sign-in-settings` | none |

A request without a token, or with one that is expired, wrongly signed or made for another issuer or audience, gets
401 with `WWW-Authenticate: Bearer`. A valid token without the scope gets 403. An MCP client with one scope sees and
can call only the tools of that scope.

Get a token for the machine client from the local Keycloak, with one scope or both, and call the API with it:

```sh
TOKEN=$(curl -s http://localhost:8180/realms/tender-scout/protocol/openid-connect/token \
  -d grant_type=client_credentials -d client_id=tender-scout-machine \
  -d client_secret=local-only-machine-secret -d "scope=notices:read documents:ask" \
  | sed 's/.*"access_token":"\([^"]*\)".*/\1/')
curl -H "Authorization: Bearer $TOKEN" http://localhost:5150/notices
```

A token lasts 5 minutes. The examples below and in [the MCP server and agent docs](agent-and-mcp.md) send `$TOKEN`.

- **The realm is for local use only.** It has a public client for the web page (`tender-scout-web`, authorization
  code with PKCE), the machine client above and one test user, `tester` with the password `local-only-password`. The
  user, its password and the secret of the machine client are invented and written in the realm file. They protect
  only a Keycloak on a development machine: do not use them, or this realm file, anywhere else.
- **Keycloak** listens at `http://localhost:8180` and its admin console is at `http://localhost:8180/admin`, user
  `admin`. The admin password is not in the repository: the AppHost generates it on its first start and keeps it in
  its user secrets, under `Parameters:keycloak-admin-password` in the output of
  `dotnet user-secrets list --project src/TenderScout.AppHost`. Keycloak keeps nothing between starts, so a change of
  the realm file takes effect on the next start of the AppHost.
- **The evaluation and the document search command need no token.** They run the search and the agent in process
  and call no endpoint.

The services know nothing about Keycloak. They read the issuer and the audience from settings, so another identity
provider needs other values and no change of the code:

| Key | Meaning |
| --- | --- |
| `Authentication:Schemes:Bearer:Authority` | Address of the issuer, exactly as it is written in the tokens; the signing keys are read from its metadata |
| `Authentication:Schemes:Bearer:ValidAudiences:0` | The audience a token must have |
| `Authentication:Schemes:Bearer:RequireHttpsMetadata` | `false` only for a local issuer that speaks HTTP |
| `WebSignIn:ClientId` | API only: the client the web page signs in with |

The AppHost sets them for local runs. A service without the first two does not start and names the missing setting,
and no setting switches the checks off. The scopes are read from the `scope` claim; a provider that writes them
elsewhere, as Entra ID does with `scp`, needs a mapping at the provider or a change of one line.

## Asking questions

`POST /ask` answers a question from the stored documents, with the document and page of every passage the answer is
based on. It embeds the question with `bge-m3`, takes the five best passages from hybrid search and lets the chat
model answer only from them. When no passage is close enough, or the model cites none, it refuses. The prompt and
the refusal rule are in [ADR-0004](adr/0004-answers-bielik-citations-refusal.md). It needs a token with the scope
`documents:ask`; see [Authentication](#authentication).

The API needs both models on Ollama, which may run on different machines (the chat model may instead be hosted, see
[Chat through OpenRouter](agent-and-mcp.md#chat-through-openrouter)):

```sh
ollama pull SpeakLeash/bielik-4.5b-v3.0-instruct:Q8_0
dotnet user-secrets --project src/TenderScout.Api set "Embedding:Endpoint" "http://<ollama-host>:11434"
dotnet user-secrets --project src/TenderScout.Api set "Chat:Endpoint" "http://<ollama-host>:11434"
```

Start the AppHost and use the address of the `api` resource from the dashboard. `publicationNumber` is optional and
limits the search to one tender:

```sh
curl -X POST http://<api-host>/ask -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"question": "Jaka jest wysokość wadium?", "publicationNumber": "123456-2026"}'
```

```json
{
  "refused": false,
  "answer": "Wadium wynosi 10 000 zł.",
  "citations": [
    { "docId": "swz-001", "publicationNumber": "123456-2026", "page": 13, "passage": "<stored passage text>" }
  ],
  "publicationNumber": "123456-2026",
  "publicationNumberSource": "field",
  "refusalReason": null
}
```

A refusal is also 200, with `"refused": true`, the answer `Nie znalazłem odpowiedzi w dokumentach.`, no citations
and `"refusalReason": "noAnswer"`. Invalid input gives 400 with problem details naming the field; an unreachable model
gives 503. The API starts with Ollama off. An answer takes several seconds; the example above is invented.

**Which tender a question is limited to.** The field always wins. When it is empty, a tender number written in the
question is used instead: 1 to 6 digits, a hyphen and a year from 1900 to 2099, standing on its own. A date such as
`15-12-2026`, a reference such as `ZP.271.5-2026`, `100101/2026` or a number touching a letter does not count. A
tender is known when at least one passage of its documents is stored; the notices are not asked.

| Number field | Tender numbers in the question | Reply |
| --- | --- | --- |
| empty | none | Search over all documents; `publicationNumber` and `publicationNumberSource` are `null` |
| empty | one, with documents | Limited to it, `publicationNumberSource` `"question"`; otherwise the same reply as with that number in the field |
| empty | two or more different | Refusal `"severalTenders"`: `W pytaniu są różne numery ogłoszeń: 111111-2026, 222222-2026. Podaj jeden numer w polu numeru ogłoszenia.` |
| a number with documents | any | Limited to the field's tender, `publicationNumberSource` `"field"`; numbers in the question are not used |
| a number without documents, from the field or the question | — | Refusal `"noDocuments"`: `Nie mam dokumentów ogłoszenia 999999-2026.`, naming that number and where it came from |

The two refusals `"severalTenders"` and `"noDocuments"` search nothing and call no model, so they work with Ollama
off. The question is passed to the search and to the model as it was written. `publicationNumber` is `null` exactly
when `publicationNumberSource` is.

The API describes its endpoints in an OpenAPI document at `/openapi/v1.json`. In the Development environment
`/scalar` shows that document as a page where the endpoints can be tried.

## Listing notices

`GET /notices` lists the stored notices, soonest submission deadline first; notices without a deadline come last. It
takes the filters of the `search_notices` tool, with the same rules, plus a documents filter. All are optional:

| Parameter | Meaning |
| --- | --- |
| `query` | Words that must all occur in the title or the buyer name, 1 to 200 characters |
| `cpvPrefix` | First 2 to 8 digits of the main or any other CPV code, for example `72` |
| `deadlineFrom`, `deadlineTo` | Submission deadline range, `yyyy-MM-dd`, both inclusive |
| `hasDocuments` | `true`: only notices with a stored tender document; `false`: only those without |
| `limit` | 1 to 200; 50 when not given |

```sh
curl -H "Authorization: Bearer $TOKEN" "http://<api-host>/notices?cpvPrefix=72&deadlineFrom=2026-12-01&limit=20"
```

The reply is `{ "total": n, "notices": [...] }`: `total` counts every matching notice whatever the limit, and each
notice has its publication number, title, buyer, main and all CPV codes, publication date, deadline, TED link and
`hasDocuments`. Invalid parameters give 400 with problem details naming each of them. The endpoint only reads.

## Web page

The API also serves a web page at its root: `http://localhost:5150/` when the API runs alone with
`dotnet run --project src/TenderScout.Api`, or the address of the `api` resource in the AppHost dashboard. It has three
views, and the part of the address after `#` says which one is shown:

- **Pulpit** (`/`): counts of open tenders (deadline today or later), of those due within 7 days and of those with
  stored documents, and a calendar of submission deadlines for this week and the next four.
- **Skrzynka** (`#skrzynka`): open tenders by deadline, with a search, five filters (all, with documents, due within 7
  days, CPV 72, CPV 48) and a detail pane with the link to TED. For a tender with stored documents the pane leads to
  the assistant with that tender chosen.
- **Asystent** (`#asystent`): sends each question to `POST /ask`, optionally limited to one tender by its publication
  number, and shows the answer with its sources (document, tender, page and the passage), a refusal, or what went
  wrong. Under an answer or a refusal limited to one tender a line names it and says whether the number came from the
  field or from the question; for several tenders or a tender without documents it shows the API's sentence. Each question is answered on its own and nothing is saved; the next question can be typed while one waits.

The notice views read `GET /notices`. The assistant needs both models, like `/ask`. The page is plain HTML, CSS and
JavaScript in `src/TenderScout.Api/wwwroot`, with no build step.

**Sign-in.** The page first shows a sign-in screen and calls no protected endpoint. "Zaloguj się" sends the browser to
the issuer from `/sign-in-settings` (the local Keycloak; test user `tester`, password `local-only-password`) with the
authorization code flow and PKCE, asking for both scopes, and every call to the API then carries the access token. The
token is kept in memory only, never in browser storage, so a reload signs the page out; there is no refresh token.
When the API answers 401, for example after the token's five minutes, the page goes to the issuer and back while the
issuer's session lasts, keeping the view and a typed question; a 401 right after signing in shows an error instead of
trying again. 403 shows that the account may not do this. "Wyloguj się" also ends the session at the issuer. The realm
accepts the page only at `http://localhost:5150/` and `https://localhost:7150/`, the addresses of the API in the
AppHost and with `dotnet run`, and at the two ports of the [demo](running.md), so sign-in does not work at any other
address of a container.

The fonts Geist, Geist Mono and Newsreader are stored with the page under the SIL Open Font License 1.1; their licence
texts and sources are in `src/TenderScout.Api/wwwroot/fonts`. The icons are part of this project.
