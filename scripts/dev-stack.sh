#!/usr/bin/env bash
#
# Local development stack for Backer — written for Windows 11 + Git Bash.
#
# Starts the API, the Poe web UI and the BackerAgent from source, all pointed
# at localhost, so you can exercise a backend change (git storages, rules,
# jobs) without installing anything.
#
#   scripts/dev-stack.sh              # start everything, follow the logs
#   scripts/dev-stack.sh --no-agent   # API + UI only (use this to sign up first)
#   scripts/dev-stack.sh stop         # kill whatever holds the stack's ports
#   scripts/dev-stack.sh status       # what is listening right now
#   scripts/dev-stack.sh logs         # follow the logs of a running stack
#   scripts/dev-stack.sh test         # headless: the E2E git tests, no servers
#
# WHY THE ENVIRONMENT OVERRIDES BELOW MATTER: BackerAgent/appsettings.Development.json
# points at https://api.essentialvault.de (the localhost values sit there
# disabled as "-BaseUrl"), and BackerAgent/config.json — written by the control
# app, untracked — usually holds the production URL and your real account.
# config.json outranks appsettings; environment variables outrank both. Without
# these exports a locally started agent logs into production and starts taking
# real jobs. The stack refuses to start if any resolved URL is not localhost.
#
# Prerequisites: .NET SDK, git >= 2.31 (the floor GitWorkerService probes for),
# and a PostgreSQL on localhost:5432 holding a "hannibal" database. rclone is
# NOT needed — the agent leaves it stopped unless Autostart is on, while the
# git worker acquires jobs independently.

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN_DIR="$REPO_ROOT/.dev-stack"
LOG_DIR="$RUN_DIR/logs"
ENV_FILE="$RUN_DIR/dev-stack.env"

API_PORT=5288
UI_PORT=5162
AGENT_PORT=5931
DB_PORT=5432

API_URL="http://localhost:$API_PORT"
UI_URL="http://localhost:$UI_PORT"
AGENT_URL="http://localhost:$AGENT_PORT"

MIN_GIT_MAJOR=2
MIN_GIT_MINOR=31

WITH_UI=1
WITH_AGENT=1
DO_BUILD=1
FOLLOW=1
COMMAND="up"
STOPPED=0

# Logs of the services this run actually started, so --follow does not also
# replay a previous run's leftovers or the build log.
FOLLOW_LOGS=()

# ---------------------------------------------------------------- output ----

if [ -t 1 ]; then
    C_RESET=$'\033[0m'; C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'
    C_RED=$'\033[31m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'; C_CYAN=$'\033[36m'
else
    C_RESET=""; C_BOLD=""; C_DIM=""; C_RED=""; C_GREEN=""; C_YELLOW=""; C_CYAN=""
fi

say()  { printf '%s\n' "$*"; }
info() { printf '%s==>%s %s\n' "$C_CYAN" "$C_RESET" "$*"; }
ok()   { printf '%s  ok%s %s\n' "$C_GREEN" "$C_RESET" "$*"; }
warn() { printf '%swarn%s %s\n' "$C_YELLOW" "$C_RESET" "$*" >&2; }
die()  { printf '%sfail%s %s\n' "$C_RED" "$C_RESET" "$*" >&2; exit 1; }

# ------------------------------------------------------------- utilities ----

usage() {
    cat <<'USAGE'
Local development stack for Backer (Windows 11 + Git Bash).

  scripts/dev-stack.sh              start everything, follow the logs
  scripts/dev-stack.sh --no-agent   API + UI only (use this to sign up first)
  scripts/dev-stack.sh stop         kill whatever holds the stack's ports
  scripts/dev-stack.sh status       what is listening right now
  scripts/dev-stack.sh logs         follow the logs of a running stack
  scripts/dev-stack.sh test         headless: the E2E git tests, no servers

Options
  --no-ui       do not start the Poe web UI
  --no-agent    do not start the BackerAgent
  --no-build    skip the build step (faster restarts)
  --no-follow   start in the background instead of tailing the logs

Credentials for the agent come from .dev-stack/dev-stack.env (a template is
written on first run) or from BACKER_DEV_EMAIL / BACKER_DEV_PASSWORD.
USAGE
    exit 0
}

# True when something is listening on the port. Uses bash's /dev/tcp rather
# than a tool that may not be on a Git Bash PATH.
port_in_use() {
    (exec 3<>"/dev/tcp/127.0.0.1/$1") >/dev/null 2>&1
}

# The Windows PID listening on a port, empty when nothing is.
pid_on_port() {
    netstat -ano \
        | tr -d '\r' \
        | awk -v port=":$1" '$1 == "TCP" && $2 ~ port"$" && $4 == "LISTENING" { print $5; exit }'
}

# taskkill accepts POSIX-looking "-f -t -pid" flags, which sidesteps MSYS
# turning "/F" into a path. -t takes the child processes with it.
kill_port() {
    local port="$1" label="$2" pid
    pid="$(pid_on_port "$port")"
    if [ -z "$pid" ]; then
        return 1
    fi
    taskkill -f -t -pid "$pid" >/dev/null 2>&1
    ok "stopped $label (pid $pid, port $port)"
    return 0
}

wait_for_port() {
    local port="$1" label="$2" timeout="${3:-90}" waited=0
    while [ "$waited" -lt "$timeout" ]; do
        if port_in_use "$port"; then
            return 0
        fi
        sleep 1
        waited=$((waited + 1))
    done
    return 1
}

# ------------------------------------------------------------- preflight ----

check_dotnet() {
    command -v dotnet >/dev/null 2>&1 || die "dotnet is not on PATH."
    ok "dotnet $(dotnet --version)"
}

check_git() {
    local raw major minor
    raw="$(git --version 2>/dev/null)" || die "git is not on PATH."
    if [[ ! "$raw" =~ version[[:space:]]+([0-9]+)\.([0-9]+) ]]; then
        die "could not parse a version out of: $raw"
    fi
    major="${BASH_REMATCH[1]}"; minor="${BASH_REMATCH[2]}"
    if [ "$major" -lt "$MIN_GIT_MAJOR" ] || { [ "$major" -eq "$MIN_GIT_MAJOR" ] && [ "$minor" -lt "$MIN_GIT_MINOR" ]; }; then
        die "$raw is below the $MIN_GIT_MAJOR.$MIN_GIT_MINOR the agent requires; it would never advertise the \"git\" capability."
    fi
    ok "$raw (>= $MIN_GIT_MAJOR.$MIN_GIT_MINOR, the git capability probe will pass)"
}

check_postgres() {
    if ! port_in_use "$DB_PORT"; then
        die "nothing is listening on localhost:$DB_PORT. Start PostgreSQL — the API needs a \"hannibal\" database (it applies pending migrations itself)."
    fi
    ok "PostgreSQL answering on localhost:$DB_PORT"
}

check_ports_free() {
    local port label pid busy=0
    for spec in "$API_PORT:API" "$UI_PORT:UI" "$AGENT_PORT:agent"; do
        port="${spec%%:*}"; label="${spec##*:}"
        case "$label" in
            UI)    [ "$WITH_UI" -eq 1 ]    || continue ;;
            agent) [ "$WITH_AGENT" -eq 1 ] || continue ;;
        esac
        if port_in_use "$port"; then
            pid="$(pid_on_port "$port")"
            warn "port $port ($label) is already in use by pid ${pid:-unknown}"
            busy=1
        fi
    done
    [ "$busy" -eq 0 ] || die "run '$0 stop' first, or pass --no-ui / --no-agent."
}

# ----------------------------------------------------------- credentials ----

# The agent authenticates as a normal user (AutoAuthHandler binds the
# RCloneService section and calls the identity API), so it needs an account
# that exists in the LOCAL database - your production login does not.
write_env_template() {
    mkdir -p "$RUN_DIR"
    cat > "$ENV_FILE" <<'TEMPLATE'
# Credentials the local BackerAgent logs in with. This is a LOCAL identity
# database: sign up at http://localhost:5162 first, then put those same
# credentials here. Do not reuse your production account.
#
# This file lives under .dev-stack/, which is gitignored.
BACKER_DEV_EMAIL=""
BACKER_DEV_PASSWORD=""

# Optional: exercise the rclone engine too. Leave unset and the agent keeps
# rclone stopped while the git worker runs - which is all a git test needs.
#BACKER_DEV_RCLONE_PATH="/c/Users/you/bin/rclone.exe"

# Optional: a different database than the built-in localhost default
# (Host=localhost;Port=5432;Database=hannibal;Username=postgres;Password=admin).
#BACKER_DEV_DB_CONNECTION="Host=localhost;Port=5432;Database=hannibal;Username=postgres;Password=..."
TEMPLATE
    ok "wrote a template to ${ENV_FILE#$REPO_ROOT/}"
}

load_credentials() {
    if [ -f "$ENV_FILE" ]; then
        # shellcheck disable=SC1090
        set -a; . "$ENV_FILE"; set +a
    fi

    BACKER_DEV_EMAIL="${BACKER_DEV_EMAIL:-}"
    BACKER_DEV_PASSWORD="${BACKER_DEV_PASSWORD:-}"

    if [ -n "$BACKER_DEV_EMAIL" ] && [ -n "$BACKER_DEV_PASSWORD" ]; then
        ok "agent will log in as $BACKER_DEV_EMAIL"
        return 0
    fi

    [ -f "$ENV_FILE" ] || write_env_template

    warn "no local credentials yet — starting without the agent."
    say ""
    say "  1. leave this running and open $UI_URL"
    say "  2. sign up (the local identity database is empty)"
    say "  3. put that email and password into ${ENV_FILE#$REPO_ROOT/}"
    say "  4. re-run $0"
    say ""
    WITH_AGENT=0
    return 1
}

# ------------------------------------------------------------- launching ----

# Runs the built DLL rather than "dotnet run": the launched process is then the
# app itself, so a port maps to exactly one PID and shutdown is not a guessing
# game. The working directory is the project directory, matching what
# "dotnet run" gives ConfigHelper and the appsettings lookup.
launch() {
    local name="$1" project_dir="$2" dll="$3" port="$4"
    shift 4

    local log="$LOG_DIR/$name.log"
    : > "$log"

    info "starting $name on port $port"
    (
        cd "$project_dir" || exit 1
        for assignment in "$@"; do
            export "$assignment"
        done
        exec dotnet "$dll"
    ) >>"$log" 2>&1 &

    if ! wait_for_port "$port" "$name" 90; then
        warn "$name did not open port $port within 90s. Last lines of ${log#$REPO_ROOT/}:"
        tail -n 20 "$log" >&2
        stop_all
        die "$name failed to start."
    fi

    FOLLOW_LOGS+=("$log")
    ok "$name listening on http://localhost:$port"
}

build_projects() {
    info "building (pass --no-build to skip)"
    local project
    for project in "Api/Api.csproj" "frontend/Poe/Poe.csproj" "BackerAgent/BackerAgent.csproj"; do
        case "$project" in
            frontend/*)   [ "$WITH_UI" -eq 1 ]    || continue ;;
            BackerAgent/*) [ "$WITH_AGENT" -eq 1 ] || continue ;;
        esac
        if ! dotnet build "$REPO_ROOT/$project" -c Debug --nologo -v quiet >"$LOG_DIR/build.log" 2>&1; then
            tail -n 30 "$LOG_DIR/build.log" >&2
            die "build failed ($project). Full log: ${LOG_DIR#$REPO_ROOT/}/build.log"
        fi
    done
    ok "build up to date"
}

stop_all() {
    trap - INT TERM EXIT
    [ "$STOPPED" -eq 0 ] || return 0
    STOPPED=1
    if [ -n "${TAIL_PID:-}" ]; then
        kill "$TAIL_PID" 2>/dev/null
        TAIL_PID=""
    fi
    say ""
    info "shutting the stack down"
    kill_port "$AGENT_PORT" "agent" || true
    kill_port "$UI_PORT"    "UI"    || true
    kill_port "$API_PORT"   "API"   || true
}

# -------------------------------------------------------------- commands ----

cmd_up() {
    mkdir -p "$LOG_DIR"

    info "preflight"
    check_dotnet
    check_git
    check_postgres

    [ "$WITH_AGENT" -eq 1 ] && load_credentials
    check_ports_free

    [ "$DO_BUILD" -eq 1 ] && build_projects

    # Everything below must resolve to localhost. Asserted, not assumed - the
    # committed appsettings point at production.
    case "$API_URL" in
        http://localhost:*|http://127.0.0.1:*) ;;
        *) die "refusing to start: API_URL is $API_URL, not localhost." ;;
    esac

    trap 'stop_all' INT TERM

    launch "api" "$REPO_ROOT/Api" "bin/Debug/net9.0/Api.dll" "$API_PORT" \
        "ASPNETCORE_ENVIRONMENT=Development" \
        "ASPNETCORE_URLS=$API_URL" \
        ${BACKER_DEV_DB_CONNECTION:+"HANNIBAL_DB_CONNECTION=$BACKER_DEV_DB_CONNECTION"}

    # StartupMigrator runs inside the API's startup path, so a healthy /health
    # means the schema is current too.
    if command -v curl >/dev/null 2>&1; then
        if curl -fsS --max-time 10 "$API_URL/health" >/dev/null 2>&1; then
            ok "API healthy, database migrated"
        else
            warn "API is listening but /health did not answer — check ${LOG_DIR#$REPO_ROOT/}/api.log"
        fi
    fi

    if [ "$WITH_UI" -eq 1 ]; then
        launch "ui" "$REPO_ROOT/frontend/Poe" "bin/Debug/net9.0/Poe.dll" "$UI_PORT" \
            "ASPNETCORE_ENVIRONMENT=Development" \
            "ASPNETCORE_URLS=$UI_URL" \
            "HannibalServiceClient__BaseUrl=$API_URL" \
            "HigginsServiceClient__BaseUrl=$API_URL"
    fi

    if [ "$WITH_AGENT" -eq 1 ]; then
        local rclone_env=()
        if [ -n "${BACKER_DEV_RCLONE_PATH:-}" ]; then
            rclone_env=(
                "RCloneService__RClonePath=$BACKER_DEV_RCLONE_PATH"
                "RCloneService__Autostart=true"
            )
            warn "rclone enabled ($BACKER_DEV_RCLONE_PATH) — it will start and take rclone jobs too"
        else
            # Belt and braces: config.json could say otherwise.
            rclone_env=("RCloneService__Autostart=false")
        fi

        launch "agent" "$REPO_ROOT/BackerAgent" "bin/Debug/net9.0/BackerAgent.dll" "$AGENT_PORT" \
            "ASPNETCORE_ENVIRONMENT=Development" \
            "ASPNETCORE_URLS=$AGENT_URL" \
            "HannibalServiceClient__BaseUrl=$API_URL" \
            "IdentityApiServiceClient__BaseUrl=$API_URL" \
            "RCloneService__UrlSignalR=$API_URL" \
            "RCloneService__BackerUsername=$BACKER_DEV_EMAIL" \
            "RCloneService__BackerPassword=$BACKER_DEV_PASSWORD" \
            "GitWorker__CacheRoot=$RUN_DIR/git-cache" \
            "${rclone_env[@]}"
    fi

    print_summary

    if [ "$FOLLOW" -eq 1 ]; then
        say "${C_DIM}following logs — Ctrl-C stops the whole stack${C_RESET}"
        say ""
        tail -n 0 -f "${FOLLOW_LOGS[@]}" &
        TAIL_PID=$!
        # Polled rather than "wait $TAIL_PID": under MSYS a trap can sit
        # deferred while bash blocks in wait on a native child, and shutdown
        # must not depend on that. Ctrl-C signals the whole process group and
        # is instant either way; this only bounds the plain-SIGTERM case.
        while kill -0 "$TAIL_PID" 2>/dev/null; do
            sleep 1
        done
        stop_all
    else
        trap - INT TERM
        say "Stack left running in the background. Stop it with: $0 stop"
    fi
}

print_summary() {
    say ""
    say "${C_BOLD}Backer dev stack${C_RESET}"
    say "  API     $API_URL        (swagger at $API_URL/swagger)"
    [ "$WITH_UI" -eq 1 ]    && say "  UI      $UI_URL"
    [ "$WITH_AGENT" -eq 1 ] && say "  agent   $AGENT_URL"
    say "  logs    ${LOG_DIR#$REPO_ROOT/}/"
    say "  cache   ${RUN_DIR#$REPO_ROOT/}/git-cache"
    say ""
    if [ "$WITH_AGENT" -eq 1 ]; then
        say "  Watch for: ${C_BOLD}GitWorkerService: git <version> found, ready to acquire git jobs.${C_RESET}"
        say "  Then, in the UI: two git storages (leave Networks EMPTY, or jobs are"
        say "  skipped unless the agent's network name matches), endpoints with"
        say "  owner/repo paths, and a rule between them. Use a disposable"
        say "  destination repo — the destination is the baseline and Sync deletes"
        say "  refs there."
    fi
    say ""
}

cmd_stop() {
    info "stopping"
    local stopped=0
    kill_port "$AGENT_PORT" "agent" && stopped=1
    kill_port "$UI_PORT"    "UI"    && stopped=1
    kill_port "$API_PORT"   "API"   && stopped=1
    [ "$stopped" -eq 1 ] || say "  nothing was running on $API_PORT/$UI_PORT/$AGENT_PORT"
}

cmd_status() {
    local spec port label pid
    for spec in "$DB_PORT:PostgreSQL" "$API_PORT:API" "$UI_PORT:UI" "$AGENT_PORT:agent"; do
        port="${spec%%:*}"; label="${spec##*:}"
        pid="$(pid_on_port "$port")"
        if [ -n "$pid" ]; then
            printf '  %-12s %sup%s   port %-5s pid %s\n' "$label" "$C_GREEN" "$C_RESET" "$port" "$pid"
        else
            printf '  %-12s %sdown%s port %s\n' "$label" "$C_DIM" "$C_RESET" "$port"
        fi
    done
}

cmd_logs() {
    local files=() name
    for name in api ui agent; do
        [ -f "$LOG_DIR/$name.log" ] && files+=("$LOG_DIR/$name.log")
    done
    [ "${#files[@]}" -gt 0 ] || die "no logs yet — start the stack first."
    tail -n 50 -f "${files[@]}"
}

# Needs the same local PostgreSQL; hosts the API in memory and a real agent, so
# it proves the loop without any of the servers above.
cmd_test() {
    check_dotnet
    check_git
    check_postgres
    info "running the E2E git tests"
    dotnet test "$REPO_ROOT/tests/Backer.E2ETests" --filter "FullyQualifiedName~Git" --nologo
}

# ------------------------------------------------------------------ main ----

while [ $# -gt 0 ]; do
    case "$1" in
        up|stop|status|logs|test) COMMAND="$1" ;;
        --no-ui)      WITH_UI=0 ;;
        --no-agent)   WITH_AGENT=0 ;;
        --no-build)   DO_BUILD=0 ;;
        --no-follow)  FOLLOW=0 ;;
        -h|--help)    usage ;;
        *)            die "unknown argument: $1 (try --help)" ;;
    esac
    shift
done

case "$COMMAND" in
    up)     cmd_up ;;
    stop)   cmd_stop ;;
    status) cmd_status ;;
    logs)   cmd_logs ;;
    test)   cmd_test ;;
esac
