#!/usr/bin/env bash
# Starts the root docker-compose.yml as a project of its own and checks the demo, without a model and without TED:
# the start order, the demo data, the sign-in through the issuer and the answers of the services.
# Needs Docker with Compose and curl. Run it from anywhere: scripts/check-compose.sh
#
# It uses the images tender-scout-* that are there, as scripts/check-images.sh leaves them, and builds the missing
# ones. It publishes the ports 25150, 25151 and 28180 on the loopback address, so a demo running on the default
# ports is not in the way. It removes its project with its volumes when it ends, also when a check fails.
# More compose files can be added for a run through COMPOSE_FILE, as docker-compose.yml:<other file>.
set -euo pipefail
cd "$(dirname "$0")/.."

project="tender-scout-check-compose-$(date +%s)-$$"
export API_PORT=25150 MCP_PORT=25151 KEYCLOAK_PORT=28180
# No model answers at these addresses: nothing listens on port 9 of the container itself.
export OLLAMA_ENDPOINT=http://127.0.0.1:9 CHAT_PROVIDER=Ollama OPENROUTER_API_KEY= INGESTION_ENABLED=false
compose() { docker compose --progress quiet --project-name "$project" "$@"; }

cleanup() { compose down --volumes --remove-orphans >/dev/null 2>&1 || true; }

# fail <message>: ends the check with the state of every container and the end of each service's log.
fail() {
    echo "FAILED: $1" >&2
    compose ps --all >&2 || true
    compose logs --no-color --tail 30 >&2 || true
    exit 1
}

# status <port> <path> [curl options]: the HTTP status the service answers with, 000 when it does not answer.
status() {
    local port=$1 path=$2
    shift 2
    curl --silent --output /dev/null --max-time 10 --write-out '%{http_code}' "$@" "http://127.0.0.1:$port$path" || true
}

# expect <status> <port> <path> [curl options]
expect() {
    local actual
    actual=$(status "$@")
    [ "$actual" = "$1" ] || actual=$(status "${@:2}")
    [ "$actual" = "$1" ] || fail "port $2 answered $actual to $3, expected $1"
}

# The demo data as it is stored: the number of rows and one digest of every row of the three tables.
stored() {
    compose exec -T postgres psql --quiet --tuples-only --no-align --username postgres --dbname tenderscout --command "
        select (select count(*) from notices) || ' ' || (select count(*) from documents) || ' '
            || (select count(*) from document_chunks) || ' '
            || (select count(*) from document_chunks where vector_dims(embedding) = 1024 and embedding_model = 'bge-m3') || ' '
            || md5((select string_agg(n::text, '|' order by n::text) from notices n)
                || (select string_agg(d::text, '|' order by d::text) from documents d)
                || (select string_agg(c::text, '|' order by c::text) from document_chunks c))"
}

echo "== Configuration"
# Quiet: the rendered file can hold the values of the environment.
compose config --quiet || fail "the compose file is not valid"
[ -z "$(docker ps --all --quiet --filter "label=com.docker.compose.project=$project")" ] || fail "project $project exists already"
trap cleanup EXIT

echo "== Start"
compose up --detach --wait --wait-timeout 300 || fail "the stack did not become ready"
for job in migrations demo-data; do
    [ "$(docker inspect --format '{{.State.ExitCode}}' "$(compose ps --all --quiet "$job")")" = 0 ] || fail "$job did not end with code 0"
done
loaded=$(compose logs --no-color demo-data)
[[ "$loaded" == *"Demo data loaded: 10 notices, 5 documents, 30 passages"* ]] || fail "the demo data were not loaded"
data=$(stored)
[[ "$data" == "10 5 30 30 "* ]] || fail "the database holds $data instead of 10 notices, 5 documents and 30 passages with their vectors"

worker=$(compose logs --no-color worker)
[[ "$worker" == *"Bus started"* ]] || fail "the worker did not connect to the broker"
[[ "$worker" == *"Polling TED is switched off"* ]] || fail "the worker did not switch polling off"

echo "== Page, health and sign-in"
expect 200 "$API_PORT" /health
expect 200 "$MCP_PORT" /health
expect 200 "$API_PORT" /
settings=$(curl --silent --max-time 10 "http://127.0.0.1:$API_PORT/sign-in-settings")
issuer="http://localhost:$KEYCLOAK_PORT/realms/tender-scout"
[[ "$settings" == *"\"$issuer\""* ]] || fail "the page does not sign in at $issuer: $settings"

# A token of the machine client from the issuer the browser uses; the token is never printed.
secret=$(sed -n 's/.*"secret": "\(.*\)".*/\1/p' keycloak/tender-scout-realm.json)
token=$(curl --silent --max-time 20 "$issuer/protocol/openid-connect/token" \
    --data grant_type=client_credentials --data client_id=tender-scout-machine --data-urlencode "client_secret=$secret" \
    --data-urlencode "scope=notices:read documents:ask" | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p')
[ -n "$token" ] || fail "Keycloak gave no token to the machine client"
payload=$(cut -d . -f 2 <<<"$token" | tr '_-' '/+')
while [ $((${#payload} % 4)) != 0 ]; do payload+='='; done
[[ "$(base64 -d <<<"$payload")" == *"\"iss\":\"$issuer\""* ]] || fail "the token was not issued by $issuer"
bearer=(--header "Authorization: Bearer $token")

json=(--header 'Content-Type: application/json' --data '{"question": "Jakie wadium jest wymagane w postępowaniu 100101-2026?"}')
initialize=(--header 'Content-Type: application/json' --header 'Accept: application/json, text/event-stream' --data
    '{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "check", "version": "1"}}}')

# Without a token 401; with it the API and the MCP server fetch the keys inside the network and accept it.
expect 401 "$API_PORT" /notices
expect 401 "$API_PORT" /ask "${json[@]}"
expect 401 "$MCP_PORT" /mcp "${initialize[@]}"
notices=$(curl --silent --max-time 10 "${bearer[@]}" "http://127.0.0.1:$API_PORT/notices")
[[ "$notices" == *'"total":10'* ]] || fail "/notices did not list the 10 demo notices"
expect 200 "$MCP_PORT" /mcp "${initialize[@]}" "${bearer[@]}"
# The questions need a model, and none answers: 503, not an error and not an answer.
expect 503 "$API_PORT" /ask "${json[@]}" "${bearer[@]}"
expect 503 "$API_PORT" /agent "${json[@]}" "${bearer[@]}"

echo "== Demo data a second time, and after a restart"
compose run --rm --no-deps demo-data >/dev/null 2>&1 || fail "the second load did not end with code 0"
[ "$(stored)" = "$data" ] || fail "the second load changed the demo data"
compose stop >/dev/null 2>&1
compose up --detach --wait --wait-timeout 300 || fail "the stack did not become ready again after a stop"
[ "$(stored)" = "$data" ] || fail "the restart changed the demo data"
expect 200 "$API_PORT" /health

echo "== Passed"
