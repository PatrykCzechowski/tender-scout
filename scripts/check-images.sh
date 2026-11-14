#!/usr/bin/env bash
# Builds the four container images and checks that they are what the README says and that they start.
# Needs Docker and curl. No model and no registry is used. Run it from anywhere: scripts/check-images.sh
#
# It starts a throwaway PostgreSQL, a Keycloak with the realm file of the repository, and the services on a
# network of its own, publishes their HTTP ports on
# the loopback address only, and removes its containers and network when it ends, also when a check fails.
# The four images stay.
set -euo pipefail
cd "$(dirname "$0")/.."

run="tender-scout-check-$$"
images=(api mcp worker migrations)

cleanup() {
    # The filter matches a part of a name, so it ends with the hyphen that follows the process id:
    # the containers of a run whose id only starts with the same digits are not touched.
    docker ps --all --quiet --filter "name=$run-" | xargs -r docker rm --force --volumes >/dev/null
    docker network rm "$run" >/dev/null 2>&1 || true
}
trap cleanup EXIT

# fail <message> [container]: ends the check; shows the end of the container's log when one is named.
fail() {
    echo "FAILED: $1" >&2
    if [ -n "${2:-}" ]; then
        docker logs --tail 40 "$2" >&2 || true
    fi
    exit 1
}

# wait_for_exit <container> <seconds>: waits until the container has ended and leaves its exit code in $code.
wait_for_exit() {
    for _ in $(seq "$2"); do
        if [ "$(docker inspect --format '{{.State.Running}}' "$1")" = false ]; then
            code=$(docker inspect --format '{{.State.ExitCode}}' "$1")
            return
        fi
        sleep 1
    done
    fail "$1 did not end within $2 seconds" "$1"
}

# status <container> <path> [curl options]: the HTTP status the service answers with, 000 when it does not answer.
status() {
    local container=$1 path=$2
    shift 2
    curl --silent --output /dev/null --max-time 5 --write-out '%{http_code}' "$@" "http://$(docker port "$container" 8080)$path" || true
}

# expect <status> <container> <path> [curl options]: waits up to a minute for that status.
expect() {
    local expected=$1 container=$2 path=$3 actual=
    shift 3
    for _ in $(seq 60); do
        actual=$(status "$container" "$path" "$@")
        if [ "$actual" = "$expected" ]; then
            return
        fi
        sleep 1
    done
    fail "$container answered $actual to $path, expected $expected" "$container"
}

json=(--header 'Content-Type: application/json' --data '{"question": "Jakie wadium jest wymagane?"}')
initialize=(--header 'Content-Type: application/json' --header 'Accept: application/json, text/event-stream' --data
    '{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "check", "version": "1"}}}')

echo "== Build"
for image in "${images[@]}"; do
    docker build --target "$image" --tag "tender-scout-$image" . || fail "tender-scout-$image does not build"
done

echo "== Content of each image"
for image in "${images[@]}"; do
    tag="tender-scout-$image"
    inside() { docker run --rm --entrypoint sh "$tag" -c "$1"; }

    [ "$(inside 'id -u')" != 0 ] || fail "$tag runs as root"
    [ -z "$(inside 'dotnet --list-sdks')" ] || fail "$tag holds an SDK"
    inside 'test -f /usr/share/zoneinfo/Europe/Warsaw' || fail "$tag has no time zone data for Europe/Warsaw"
    leftovers=$(inside "find /app -name '*.cs' -o -name '*.csproj' -o -name 'appsettings.*.json' -o -name launchSettings.json -o -name secrets.json -o -name '.env*'")
    [ -z "$leftovers" ] || fail "$tag holds source or local settings: $leftovers"
done

echo "== Migrations"
docker network create "$run" >/dev/null
# The database trusts every connection on its own network, so no password is written anywhere.
docker run --detach --name "$run-db" --network "$run" --env POSTGRES_HOST_AUTH_METHOD=trust --env POSTGRES_DB=tenderscout pgvector/pgvector:pg17 >/dev/null
for _ in $(seq 60); do
    if docker exec "$run-db" pg_isready --quiet --host localhost; then
        break
    fi
    sleep 1
done
database="ConnectionStrings__tenderscout=Host=$run-db;Database=tenderscout;Username=postgres"

for attempt in first second; do
    docker run --detach --name "$run-migrations-$attempt" --network "$run" --env "$database" tender-scout-migrations >/dev/null
    wait_for_exit "$run-migrations-$attempt" 120
    [ "$code" = 0 ] || fail "the $attempt run of the migrations ended with code $code" "$run-migrations-$attempt"
done

docker run --detach --name "$run-migrations-unset" tender-scout-migrations >/dev/null
wait_for_exit "$run-migrations-unset" 60
[ "$code" != 0 ] || fail "the migrations ended with code 0 without a connection string" "$run-migrations-unset"

echo "== Token issuer"
# Keycloak with the realm file of the repository. Its hostname is fixed to its name on the check's network, so
# a token asked for from outside names the issuer the services reach from inside.
docker run --detach --name "$run-keycloak" --network "$run" --publish 127.0.0.1::8080 \
    --volume "$PWD/keycloak:/opt/keycloak/data/import:ro" --env "KC_HOSTNAME=http://$run-keycloak:8080" \
    quay.io/keycloak/keycloak:26.8 start-dev --import-realm >/dev/null
expect 200 "$run-keycloak" /realms/tender-scout

# A token of the machine client with both scopes. Its secret is read from the realm file; the token is never printed.
secret=$(sed -n 's/.*"secret": "\(.*\)".*/\1/p' keycloak/tender-scout-realm.json)
token=$(curl --silent --max-time 20 "http://$(docker port "$run-keycloak" 8080)/realms/tender-scout/protocol/openid-connect/token" \
    --data grant_type=client_credentials --data client_id=tender-scout-machine --data-urlencode "client_secret=$secret" \
    --data-urlencode "scope=notices:read documents:ask" | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p')
[ -n "$token" ] || fail "Keycloak gave no token to the machine client" "$run-keycloak"
bearer=(--header "Authorization: Bearer $token")
issuer=(--env "Authentication__Schemes__Bearer__Authority=http://$run-keycloak:8080/realms/tender-scout"
    --env Authentication__Schemes__Bearer__ValidAudiences__0=tender-scout-api
    --env Authentication__Schemes__Bearer__RequireHttpsMetadata=false)

echo "== API and MCP server, with a database, the issuer and no model"
docker run --detach --name "$run-api" --network "$run" --publish 127.0.0.1::8080 --env "$database" "${issuer[@]}" tender-scout-api >/dev/null
docker run --detach --name "$run-mcp" --network "$run" --publish 127.0.0.1::8080 --env "$database" "${issuer[@]}" tender-scout-mcp >/dev/null
expect 200 "$run-api" /health
expect 200 "$run-mcp" /health
for container in "$run-api" "$run-mcp"; do
    [ "$(docker exec "$container" stat --format %u /proc/1)" != 0 ] || fail "$container runs its service as root"
done

# The page is public. The data needs a token: 401 without one, and the token of the real realm is accepted.
expect 200 "$run-api" /
expect 401 "$run-api" /notices
expect 401 "$run-api" /ask "${json[@]}"
expect 401 "$run-api" /agent "${json[@]}"
expect 401 "$run-mcp" /mcp "${initialize[@]}"
notices=$(curl --silent --max-time 5 "${bearer[@]}" "http://$(docker port "$run-api" 8080)/notices")
[[ "$notices" == *'"total":0'* && "$notices" == *'"notices":[]'* ]] || fail "/notices did not answer with an empty list: $notices" "$run-api"
expect 503 "$run-api" /ask "${json[@]}" "${bearer[@]}"
expect 503 "$run-api" /agent "${json[@]}" "${bearer[@]}"
expect 200 "$run-mcp" /mcp "${initialize[@]}" "${bearer[@]}"
expect 404 "$run-mcp" /

echo "== Time zone, through the agent"
# With a model name the agent works out today's date in Poland before it calls the model, which is not there.
# So 503 means that Europe/Warsaw resolved in this image; without the time zone data the answer is 500.
docker run --detach --name "$run-api-agent" --network "$run" --publish 127.0.0.1::8080 --env "$database" "${issuer[@]}" --env Agent__Model=no-model tender-scout-api >/dev/null
expect 200 "$run-api-agent" /health
[ "$(status "$run-api-agent" /agent "${json[@]}" "${bearer[@]}")" = 503 ] || fail "/agent did not answer 503 with a model that cannot be reached" "$run-api-agent"

echo "== Worker"
# Started without settings it must end by itself and name the broker it misses. It is not started with a
# broker here, because it would then call the TED API.
# On amd64, where the images are deployed, the process ends after the unhandled exception. With Docker
# Desktop on an Apple Silicon Mac it was seen to keep running instead, so on every other architecture than
# amd64 Docker's init process goes in front.
init=--init
if [ "$(docker version --format '{{.Server.Arch}}')" = amd64 ]; then
    init=
fi
# shellcheck disable=SC2086
docker run --detach $init --name "$run-worker" tender-scout-worker >/dev/null
wait_for_exit "$run-worker" 60
[ "$code" != 0 ] || fail "the worker ended with code 0 without a broker" "$run-worker"
docker logs "$run-worker" 2>&1 | grep --quiet messaging || fail "the worker did not name the missing connection string" "$run-worker"

echo "== Health without the database"
# The health check goes through the retry policy of the database connection, so it answers after about a minute.
docker stop "$run-db" >/dev/null
for container in "$run-api" "$run-mcp"; do
    [ "$(status "$container" /health --max-time 120)" = 503 ] || fail "$container did not turn unhealthy without the database" "$container"
done

echo "== Passed"
docker image ls --format '{{.Repository}}  {{.Size}}' 'tender-scout-*'
