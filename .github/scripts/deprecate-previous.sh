#!/usr/bin/env bash
# Deprecate the previously published versions of a package on nuget.org, naming the version just released as
# the alternate — so consumers of an older version see "deprecated → use <Id> <new version>" in Visual Studio
# and `dotnet list package --deprecated`.
#
# Uses nuget.org's (preview) deprecation API: PUT https://www.nuget.org/api/v2/package/{id}/deprecations with an
# X-NuGet-ApiKey header. The key needs the "unlist" scope; the temporary key minted by trusted publishing carries
# the trust policy's scopes, which for a policy created without explicit scopes is `all` — so the same key that
# pushed the package can deprecate the older ones, and no long-lived secret is required. The endpoint is gated
# by a per-account nuget.org feature flag: a 403 with a valid key means it is not enabled for the account yet
# (deprecate via the website in that case).
#
# Usage:
#   deprecate-previous.sh <package-id> <new-version> <mode> <reason> <message> <unlist> [<summary-file>]
#     mode    : plan    — only compute and print the versions that WOULD be deprecated (no network write)
#               stable  — deprecate previous stable versions
#               all     — deprecate previous stable AND prerelease versions
#     reason  : other | legacy | critical-bugs
#     message : custom message (empty = a default "Superseded by …" text); shown on nuget.org only
#     unlist  : true | false — also unlist the deprecated versions (hides them from search)
#   Env: NUGET_API_KEY (required unless mode=plan), NUGET_PACKAGE_INDEX_WAIT_SECONDS (default 0 = no wait).
#
# Exit codes: 0 ok (incl. "nothing to deprecate"), 1 usage/error, 2 the API call failed (package is published
# regardless — deprecate by hand on nuget.org and see the printed response).
set -euo pipefail

ID="${1:?package id}"
NEW="${2:?new version}"
MODE="${3:?mode: plan|stable|all}"
REASON="${4:-other}"
MESSAGE="${5:-}"
UNLIST="${6:-false}"
SUMMARY="${7:-}"

ID_LOWER="$(printf '%s' "$ID" | tr '[:upper:]' '[:lower:]')"
NEW_LOWER="$(printf '%s' "$NEW" | tr '[:upper:]' '[:lower:]')"
FLAT="https://api.nuget.org/v3-flatcontainer/${ID_LOWER}/index.json"
API="${NUGET_DEPRECATE_API_URL:-https://www.nuget.org/api/v2/package/${ID}/deprecations}"   # override for tests only
UA="DotnetCronner-release (+https://github.com/nfMalde/DotnetCronner; GitHub Actions)"
WAIT="${NUGET_PACKAGE_INDEX_WAIT_SECONDS:-0}"   # 0 = do not wait for the new version to be indexed (see below)

case "$MODE" in plan|stable|all) ;; *) echo "::error::mode must be plan|stable|all (got '$MODE')"; exit 1 ;; esac
case "$REASON" in other|legacy|critical-bugs) ;; *) echo "::error::reason must be other|legacy|critical-bugs (got '$REASON')"; exit 1 ;; esac

BT='`'
note() { echo "$*"; [[ -n "$SUMMARY" ]] && echo "$*" >> "$SUMMARY" || true; }

# ── Which versions exist? ──────────────────────────────────────────────────────────────────────────────────
# The flat container lists every published version (listed or not), normalized lowercase, as
# {"versions":["0.0.1","0.0.2",…]}. Parsed without jq so the script also runs on a bare Git Bash. A package
# that was never published yields 404 → no versions; any other failure is fatal (never mistake "could not
# read the index" for "nothing to deprecate").
fetch_versions() {
  local body status
  body="$(mktemp)"
  status="$(curl -sS -o "$body" -w '%{http_code}' -H "User-Agent: $UA" "$FLAT" || echo "000")"
  if [[ "$status" == "404" ]]; then rm -f "$body"; return 0; fi
  if [[ ! "$status" =~ ^2 ]]; then
    echo "::error::Could not read $FLAT (HTTP $status)." >&2
    rm -f "$body"; return 1
  fi
  tr -d ' \n\r\t' < "$body" | sed -E 's/^\{"versions":\[//; s/\]\}$//' | tr ',' '\n' | tr -d '"' | grep -v '^$' || true
  rm -f "$body"
}

# SemVer helpers (enough for our own version scheme: MAJOR.MINOR.PATCH[-prerelease]).
core()     { printf '%s' "${1%%-*}"; }
is_pre()   { [[ "$1" == *-* ]]; }
core_lt()  { [[ "$1" != "$2" ]] && [[ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -n1)" == "$1" ]]; }

# "previous" = strictly older than NEW: a lower core version, or the same core when it is a prerelease of the
# stable NEW (0.0.7-preview.3 is superseded by 0.0.7). Never anything newer — releasing a patch for an older
# line must not deprecate the current line.
is_previous() {
  local v="$1"
  [[ "$v" == "$NEW_LOWER" ]] && return 1
  if core_lt "$(core "$v")" "$(core "$NEW_LOWER")"; then return 0; fi
  if [[ "$(core "$v")" == "$(core "$NEW_LOWER")" ]] && is_pre "$v" && ! is_pre "$NEW_LOWER"; then return 0; fi
  return 1
}

ALL_VERSIONS="$(fetch_versions)"
CANDIDATES=()
while IFS= read -r v; do
  [[ -z "$v" ]] && continue
  is_previous "$v" || continue
  if [[ "$MODE" != "all" ]] && is_pre "$v"; then continue; fi   # plan/stable: stable versions only
  CANDIDATES+=("$v")
done <<< "$ALL_VERSIONS"

# In plan mode also show what 'all' would add, so the approver sees both options.
PRE_EXTRA=()
if [[ "$MODE" == "plan" ]]; then
  while IFS= read -r v; do
    [[ -z "$v" ]] && continue
    is_previous "$v" || continue
    is_pre "$v" && PRE_EXTRA+=("$v")
  done <<< "$ALL_VERSIONS"
fi

if [[ -z "$MESSAGE" ]]; then
  # nuget.org REQUIRES a message for reason 'other' — never send an empty one.
  MESSAGE="Superseded by ${ID} ${NEW}. See the changelog: https://github.com/nfMalde/DotnetCronner/blob/main/CHANGELOG.md"
fi

# ── Already deprecated? (informational) ──────────────────────────────────────────────────────────────────────
# nuget.org UPDATES an existing deprecation in place (status, alternate, message) rather than rejecting it, so
# re-deprecating is safe and wanted — the alternate moves to the newest release. Just show which ones are
# updates. Read from the registration index; best effort, needs jq (CI has it), silently skipped otherwise.
ALREADY=""
if command -v jq >/dev/null 2>&1; then
  REG="https://api.nuget.org/v3/registration5-gz-semver2/${ID_LOWER}/index.json"
  ALREADY="$(curl -fsSL --compressed -H "User-Agent: $UA" "$REG" 2>/dev/null \
    | jq -r '[.items[] | .items[]? | .catalogEntry | select(.deprecation != null) | .version | ascii_downcase] | .[]' 2>/dev/null || true)"
fi
mark() { # prints the version, with a marker when it is already deprecated
  if [[ -n "$ALREADY" ]] && grep -qx "$1" <<< "$ALREADY"; then printf '%s%s%s (already deprecated → updated)' "$BT" "$1" "$BT"; else printf '%s%s%s' "$BT" "$1" "$BT"; fi
}

note "### Deprecation of previous \`$ID\` versions"
note "- **Alternate:** \`$ID $NEW\`"
note "- **Reason:** \`$REASON\`; **unlist:** \`$UNLIST\`"
note "- **Message:** $MESSAGE"
if [[ ${#CANDIDATES[@]} -eq 0 ]]; then
  note "- **Versions:** none to deprecate (no older $([[ "$MODE" == "all" ]] && echo "" || echo "stable ")versions published)"
else
  note "- **Versions ($MODE):** $(for v in "${CANDIDATES[@]}"; do mark "$v"; printf " "; done)"
fi
if [[ "$MODE" == "plan" && ${#PRE_EXTRA[@]} -gt 0 ]]; then
  note "- **Prereleases that 'all' would add:** $(for v in "${PRE_EXTRA[@]}"; do mark "$v"; printf " "; done)"
fi

if [[ "$MODE" == "plan" ]]; then
  note "- _Plan only — nothing was changed on nuget.org._"
  exit 0
fi

if [[ ${#CANDIDATES[@]} -eq 0 ]]; then
  exit 0
fi

: "${NUGET_API_KEY:?NUGET_API_KEY is required to deprecate}"

# ── Optionally wait for the new version to be indexed ────────────────────────────────────────────────────────
# Not required: nuget.org accepts an alternate that is still validating (its lookup has no status filter); the
# "validated packages only" restriction is a UI rule, not an API rule. So by default the call goes out right away
# — the push succeeded, validation takes a few minutes, and a (rare) validation failure is mailed to the owner,
# who then fixes the deprecation by hand. Set NUGET_PACKAGE_INDEX_WAIT_SECONDS > 0 to wait until the new version
# is indexed first (a timeout then skips the deprecation rather than pointing at a version that never appeared).
deadline=$(( $(date +%s) + WAIT ))
until (( WAIT <= 0 )) || fetch_versions | grep -qx "$NEW_LOWER"; do
  if (( $(date +%s) > deadline )); then
    note "- ❌ \`$ID $NEW\` did not appear in the nuget.org index within ${WAIT}s; deprecation skipped. Deprecate the older versions by hand on nuget.org (Manage package → Deprecation) once it is indexed."
    echo "::error::$ID $NEW not indexed within ${WAIT}s — previous versions were NOT deprecated."
    exit 2
  fi
  echo "waiting for $ID $NEW to be indexed…"
  sleep 20
done

# ── The call ─────────────────────────────────────────────────────────────────────────────────────────────────
LISTED_VERB="Unchanged"; [[ "$UNLIST" == "true" ]] && LISTED_VERB="Unlist"
json_str() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | awk 'BEGIN{ORS=""} NR>1{print "\\n"} {print}'; }
VERSIONS_JSON="$(printf '"%s",' "${CANDIDATES[@]}")"; VERSIONS_JSON="[${VERSIONS_JSON%,}]"
BODY="$(printf '{"versions":%s,"isLegacy":%s,"hasCriticalBugs":%s,"isOther":%s,"alternatePackageId":"%s","alternatePackageVersion":"%s","message":"%s","listedVerb":"%s"}' \
  "$VERSIONS_JSON" \
  "$([[ "$REASON" == legacy ]] && echo true || echo false)" \
  "$([[ "$REASON" == critical-bugs ]] && echo true || echo false)" \
  "$([[ "$REASON" == other ]] && echo true || echo false)" \
  "$(json_str "$ID")" "$(json_str "$NEW")" "$(json_str "$MESSAGE")" "$LISTED_VERB")"
echo "request body: $BODY"

attempt=0
while :; do
  attempt=$((attempt + 1))
  RESP_FILE="$(mktemp)"
  STATUS="$(curl -sS -o "$RESP_FILE" -w '%{http_code}' -X PUT "$API" \
    -H "X-NuGet-ApiKey: $NUGET_API_KEY" -H "User-Agent: $UA" -H "Content-Type: application/json" \
    -D "$RESP_FILE.headers" --data "$BODY" || echo "000")"
  if [[ "$STATUS" =~ ^2 ]]; then
    note "- ✅ Deprecated ${#CANDIDATES[@]} version(s) on nuget.org (HTTP $STATUS)."
    exit 0
  fi
  if [[ "$STATUS" == "429" || ( "$STATUS" == "403" && -n "$(grep -i '^Retry-After' "$RESP_FILE.headers" 2>/dev/null || true)" ) ]] && (( attempt < 5 )); then
    RETRY="$(grep -i '^Retry-After' "$RESP_FILE.headers" | head -n1 | tr -dc '0-9' || true)"
    RETRY="${RETRY:-60}"
    echo "rate limited (HTTP $STATUS); retrying in ${RETRY}s…"
    sleep "$RETRY"
    continue
  fi
  BODY_TEXT="$(head -c 2000 "$RESP_FILE" 2>/dev/null || true)"
  note "- ❌ Deprecation request failed: HTTP $STATUS. ${BODY_TEXT:+Response: \`$BODY_TEXT\`}"
  if [[ "$STATUS" == "403" ]]; then
    note "  - A 403 with a valid key usually means nuget.org's deprecation API (preview) is not enabled for this account, or the trust policy's scopes exclude unlist. The package IS published; deprecate the older versions by hand (nuget.org → Manage package → Deprecation)."
  fi
  echo "::error::Deprecation request failed with HTTP $STATUS — previous versions were NOT deprecated (the release itself succeeded)."
  exit 2
done
