#!/usr/bin/env bash
set -uo pipefail

FLAG_PATH="${FLAG_PATH:-/workspace/flag.txt}"
SERVE_PORT="${OPENCODE_SERVE_PORT:-4096}"
BASE="http://127.0.0.1:${SERVE_PORT}"
CODE=0

echo "[sandbox] task=${TASK_ID:-unknown} name=${TASK_NAME:-n/a}"
echo "[sandbox] run-as: uid=$(id -u 2>/dev/null || echo '?') user=$(id -un 2>/dev/null || echo '?') (opencode 以 root 启动)"
echo "[sandbox] target=${TARGET_URL:-<未设置>}"
echo "[sandbox] model=${OPENCODE_MODEL:-<默认>}"
echo "[sandbox] opencode=http://127.0.0.1:${SERVE_PORT} (serve mode)"
if command -v curl >/dev/null 2>&1; then
    if curl -fsSI --max-time 8 https://opencode.ai/inference/v1/models >/dev/null 2>&1; then
        echo "[sandbox] inference endpoint reachable"
    else
        echo "[sandbox] WARN: inference endpoint is not reachable" >&2
    fi

    provider="${OPENCODE_MODEL%%/*}"
    case "$provider" in
        deepseek)
            if [ -z "${DEEPSEEK_API_KEY:-}" ]; then
                echo "[sandbox] ERROR: DEEPSEEK_API_KEY is not present in the sandbox" >&2
            else
                deepseek_code="$(curl -sS -o /tmp/deepseek-models.json -w '%{http_code}' \
                    --max-time 10 \
                    -H "Authorization: Bearer ${DEEPSEEK_API_KEY}" \
                    https://api.deepseek.com/models 2>/tmp/deepseek-curl.err || true)"
                if [ "$deepseek_code" = "200" ]; then
                    echo "[sandbox] DeepSeek API preflight: HTTP 200"
                else
                    echo "[sandbox] DeepSeek API preflight: HTTP ${deepseek_code:-network-error}" >&2
                    if [ -s /tmp/deepseek-curl.err ]; then
                        sed 's/Authorization: Bearer [^ ]*/Authorization: Bearer <redacted>/g' /tmp/deepseek-curl.err >&2
                    fi
                fi
            fi
            ;;
    esac
fi
echo "[sandbox] workspace contents:"
ls -la /workspace 2>/dev/null | sed 's/^/[sandbox] | /' || true
echo "[sandbox] ------------------------------------------------"

PROMPT_TEXT="${PROMPT:-你是 CTF 自动解题 Agent。请审计当前目录源码，编写脚本攻击目标 ${TARGET_URL:-<未设置>}，找到 flag 后按 /workspace/AGENTS.md 的收尾流程写好 flag.txt / failure.txt / writeup.md。}

提示：沙箱仅预装基础工具（python3/curl/git/ripgrep 等）。如需其他工具（nmap、sqlmap 等），本环境以 root 运行，可直接 apt-get install / pip3 install --break-system-packages 安装后使用（apt/pip 均已配置国内镜像）。}"

cd /workspace || exit 1

# 收尾流程文档随镜像预置，每次运行同步到工作区（复用容器也刷新）。
cp /opt/sandbox/AGENTS.md /workspace/AGENTS.md 2>/dev/null || true

if [ -n "${OPENCODE_CONFIG_CONTENT:-${OPENCODE_CONFIG_JSON:-}}" ]; then
    mkdir -p /root/.config/opencode
    printf '%s' "${OPENCODE_CONFIG_CONTENT:-$OPENCODE_CONFIG_JSON}" > /root/.config/opencode/opencode.json
fi

fail() {
    echo "[sandbox] ERROR: $1" >&2
    printf '%s' "$1" > /workspace/failure.txt
    CODE=1
}

say() { printf '%s\n' "$1"; }

# ---- 结构化事件输出（对齐 OpenCode 官方 Web UI 的 part 模型）----
# 每个 SSE 事件转换为一行 `EVT {json}`，编排器解析后驱动前端的
# 文本 Markdown / 工具卡片（title+subtitle+args+状态+可展开输出）渲染。
emit_text() { # id status text
    printf 'EVT %s\n' "$(jq -cn --arg id "$1" --arg s "$2" --arg t "$3" \
        '{k:"text",id:$id,status:$s,text:$t}' 2>/dev/null)"
}
emit_reasoning() { # id status text
    printf 'EVT %s\n' "$(jq -cn --arg id "$1" --arg s "$2" --arg t "$3" \
        '{k:"reasoning",id:$id,status:$s,text:$t}' 2>/dev/null)"
}
emit_tool() { # id tool status label argsjson text err
    printf 'EVT %s\n' "$(jq -cn --arg id "$1" --arg tool "$2" --arg s "$3" \
        --arg label "$4" --argjson args "${5:-[]}" --arg t "$6" --arg e "$7" \
        '{k:"tool",id:$id,tool:$tool,status:$s,label:$label,args:$args,text:$t,err:$e}' 2>/dev/null)"
}
emit_error() { # err
    printf 'EVT %s\n' "$(jq -cn --arg e "$1" '{k:"error",err:$e}' 2>/dev/null)"
}

flush_text() {
    local mid="$1" full="${2:-}"
    local f="/tmp/oc-acc-$mid" o="/tmp/oc-off-$mid"
    if [ -n "$full" ]; then
        emit_text "$mid" "done" "$full"
        : > "$f" 2>/dev/null || true
        printf '%s' "${#full}" > "$o" 2>/dev/null || true
        touch /tmp/oc-saw-text
        return
    fi
    [ -f "$f" ] || return
    local total sent chunk
    total=$(wc -c < "$f" | tr -d ' ')
    sent=$(cat "$o" 2>/dev/null || echo 0); sent=${sent:-0}
    if [ "$total" -gt "$sent" ]; then
        chunk=$(tail -c +$((sent + 1)) "$f" | tr -d '\r')
        if [ -n "$chunk" ]; then
            emit_text "$mid" "running" "$chunk"
            touch /tmp/oc-saw-text
        fi
        printf '%s' "$total" > "$o"
    fi
}

accumulate_text() {
    local mid="$1" delta="$2"
    [ -n "$delta" ] || return
    [ -n "$mid" ] || mid="default"
    local f="/tmp/oc-acc-$mid" o="/tmp/oc-off-$mid" t="/tmp/oc-t-$mid"
    printf '%s' "$delta" >> "$f"
    local total sent pending now last
    total=$(wc -c < "$f" | tr -d ' ')
    sent=$(cat "$o" 2>/dev/null || echo 0); sent=${sent:-0}
    pending=$((total - sent))
    now=$(date +%s%3N)
    last=$(cat "$t" 2>/dev/null || echo 0)
    if [ "$pending" -ge 160 ] || { [ "$pending" -gt 0 ] && [ $((now - last)) -ge 1500 ]; }; then
        flush_text "$mid" ""
        printf '%s' "$now" > "$t"
    fi
}

flush_all_text() {
    local f mid
    for f in /tmp/oc-acc-*; do
        [ -f "$f" ] || continue
        mid="${f#/tmp/oc-acc-}"
        if [ -s "$f" ]; then
            emit_text "$mid" "done" "$(cat "$f")"
            : > "$f"
        fi
    done
}

b64d() { printf '%s' "$1" | base64 -d 2>/dev/null; }

handle_event() {
    local line="$1" json parsed typ mid tid name delta text err out prog tool
    case "$line" in
        data:*) ;;
        *) return ;;
    esac
    json="${line#data:}"
    json="${json# }"
    [ -n "$json" ] || return
    # 字段经 base64 编码后以逗号拼接，避免 IFS 空白折叠与转义问题。
    parsed=$(printf '%s' "$json" | jq -r '[
        (.type // ""),
        (.data.assistantMessageID // .data.messageID // ""),
        (.data.id // ""),
        (.data.name // .data.tool // ""),
        (.data.delta // ""),
        (.data.text // ""),
        (if (.data.error? | type) == "object" then (.data.error.message // "")
         elif (.data.error? | type) == "string" then .data.error
         else "" end),
        (if (.data.output? | type) == "string" then .data.output
         elif (.data.output? != null) then (.data.output | tostring)
         elif (.data.content? | type) == "array"
             then ([.data.content[]? | select(.type == "text") | (.text // "")] | join("\n"))
         else "" end),
        (if (.data.progress? | type) == "string" then .data.progress else "" end)
    ] | map(@base64) | join(",")' 2>/dev/null) || return
    local IFS=,
    local -a B
    read -r -a B <<< "$parsed"
    # 末尾字段可能为空（base64 空串），read 会丢弃尾部空字段，全部用 :- 兜底。
    typ=$(b64d "${B[0]:-}"); mid=$(b64d "${B[1]:-}"); tid=$(b64d "${B[2]:-}")
    name=$(b64d "${B[3]:-}"); delta=$(b64d "${B[4]:-}"); text=$(b64d "${B[5]:-}")
    err=$(b64d "${B[6]:-}"); out=$(b64d "${B[7]:-}"); prog=$(b64d "${B[8]:-}")
    case "$typ" in
        session.execution.started)
            say "[opencode] 开始执行"
            ;;
        session.execution.succeeded)
            flush_all_text
            say "[opencode] 执行完成"
            ;;
        session.execution.failed)
            flush_all_text
            emit_error "${err:-执行失败}"
            say "[opencode:error] ${err:-执行失败}"
            ;;
        session.execution.interrupted)
            flush_all_text
            say "[opencode] 执行被中断"
            ;;
        session.step.failed)
            emit_error "${err:-step failed}"
            say "[opencode:error] ${err:-step failed}"
            ;;
        session.text.delta)
            accumulate_text "$mid" "$delta"
            ;;
        session.text.ended)
            flush_text "$mid" "$text"
            ;;
        session.reasoning.delta)
            accumulate_text "r-$mid" "$delta"
            ;;
        session.reasoning.ended)
            if [ -n "$text" ]; then
                emit_reasoning "$mid" "done" "$text"
            elif [ -s "/tmp/oc-acc-r-$mid" ]; then
                emit_reasoning "$mid" "done" "$(cat "/tmp/oc-acc-r-$mid")"
                : > "/tmp/oc-acc-r-$mid"
            fi
            ;;
        session.tool.input.started)
            if [ -n "$name" ]; then
                emit_tool "$tid" "$name" "running" "" '[]' "" ""
                touch /tmp/oc-saw-tool
                [ -n "$tid" ] && printf '%s' "$name" > "/tmp/oc-toolname-$tid"
            fi
            ;;
        session.tool.input.ended)
            local label argsjson
            tool="$name"
            if [ -z "$tool" ] && [ -n "$tid" ]; then
                tool=$(cat "/tmp/oc-toolname-$tid" 2>/dev/null || true)
            fi
            label=$(printf '%s' "$text" | jq -r '
                if (.command|type)=="string" then .command
                elif (.query|type)=="string" then .query
                elif (.url|type)=="string" then .url
                elif (.filePath|type)=="string" then .filePath
                elif (.pattern|type)=="string" then .pattern
                elif (.path|type)=="string" then .path
                elif (.name|type)=="string" then .name
                elif (.description|type)=="string" then .description
                else "" end' 2>/dev/null || true)
            argsjson=$(printf '%s' "$text" | jq -c '
                [ to_entries[]
                  | select((["command","query","url","filePath","pattern","path","name","description"] | index(.key)) == null)
                  | select(.value|type=="string" or .value|type=="number" or .value|type=="boolean")
                  | "\(.key)=\(.value)" ] | .[0:3]' 2>/dev/null || echo '[]')
            [ -n "$argsjson" ] || argsjson='[]'
            emit_tool "$tid" "${tool:-unknown}" "running" "${label:-}" "$argsjson" "" ""
            touch /tmp/oc-saw-tool
            ;;
        session.tool.called)
            if [ -n "$tid" ]; then
                tool=$(cat "/tmp/oc-toolname-$tid" 2>/dev/null || true)
                emit_tool "$tid" "${tool:-${name:-tool}}" "running" "" '[]' "" ""
            fi
            ;;
        session.tool.progress)
            if [ -n "$tid" ]; then
                tool=$(cat "/tmp/oc-toolname-$tid" 2>/dev/null || true)
                emit_tool "$tid" "${tool:-${name:-tool}}" "running" "" '[]' "${prog:-$text}" ""
            fi
            ;;
        session.tool.success)
            if [ -n "$tid" ]; then
                tool=$(cat "/tmp/oc-toolname-$tid" 2>/dev/null || true)
                emit_tool "$tid" "${tool:-${name:-tool}}" "done" "" '[]' "$(printf '%s' "$out" | head -c 4000)" ""
                touch /tmp/oc-saw-tool
            fi
            ;;
        session.tool.failed)
            if [ -n "$tid" ]; then
                tool=$(cat "/tmp/oc-toolname-$tid" 2>/dev/null || true)
                emit_tool "$tid" "${tool:-${name:-tool}}" "error" "" '[]' "$(printf '%s' "$out" | head -c 2000)" "${err:-工具执行失败}"
                touch /tmp/oc-saw-tool
            fi
            ;;
        session.shell.started)
            emit_tool "" "shell" "running" "" '[]' "" ""
            touch /tmp/oc-saw-tool
            ;;
        session.permission.created)
            say "[opencode] 等待权限确认（已配置为自动允许）"
            ;;
        *)
            ;;
    esac
}

if ! command -v opencode >/dev/null 2>&1; then
    echo "[sandbox] ERROR: opencode CLI not found in image" >&2
    CODE=127
else
    OC_PASSWORD="${OPENCODE_SERVER_PASSWORD:-}"
    if [ -z "$OC_PASSWORD" ]; then
        OC_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
    fi
    AUTH=(-u "opencode:${OC_PASSWORD}")
    SERVER_PID=""
    SSE_PID=""
    PERM_PID=""

    cleanup() {
        [ -n "$SSE_PID" ] && kill "$SSE_PID" 2>/dev/null
        [ -n "$PERM_PID" ] && kill "$PERM_PID" 2>/dev/null
        [ -n "$SERVER_PID" ] && kill "$SERVER_PID" 2>/dev/null
        return 0
    }
    on_term() {
        echo "[sandbox] 收到终止信号，正在停止 opencode serve" >&2
        cleanup
        exit 143
    }
    trap on_term TERM INT
    trap cleanup EXIT

    echo "[sandbox] launching: opencode serve (HTTP API + SSE)"
    OPENCODE_SERVER_PASSWORD="$OC_PASSWORD" \
        opencode serve --port "$SERVE_PORT" --hostname 127.0.0.1 \
        > /tmp/opencode-serve.log 2>&1 &
    SERVER_PID=$!

    ready=0
    for i in $(seq 1 60); do
        if ! kill -0 "$SERVER_PID" 2>/dev/null; then
            break
        fi
        probe=$(curl -sS -o /dev/null -w '%{http_code}' "${AUTH[@]}" "$BASE/api/info" 2>/dev/null || true)
        if [ "$probe" = "200" ]; then
            ready=1
            echo "[sandbox] opencode serve ready after ${i}s"
            break
        fi
        sleep 1
    done

    if [ "$ready" -ne 1 ]; then
        echo "[sandbox] opencode serve 日志：" >&2
        tail -n 40 /tmp/opencode-serve.log >&2 2>/dev/null || true
        fail "opencode serve 未能在 127.0.0.1:${SERVE_PORT} 启动"
    fi

    SID=""
    if [ "$ready" -eq 1 ]; then
        SID="${OPENCODE_SESSION_ID:-}"
        if [ -n "$SID" ]; then
            probe=$(curl -sS -o /dev/null -w '%{http_code}' "${AUTH[@]}" "$BASE/api/session/$SID" 2>/dev/null || true)
            if [ "$probe" != "200" ]; then
                echo "[sandbox] 会话 $SID 不可恢复（HTTP $probe），创建新会话"
                SID=""
            else
                echo "[sandbox] 恢复已有 OpenCode 会话: $SID"
            fi
        fi

        if [ -z "$SID" ] && [ -s /workspace/.opencode-session-id ]; then
            SID="$(cat /workspace/.opencode-session-id 2>/dev/null || true)"
            if [ -n "$SID" ]; then
                probe=$(curl -sS -o /dev/null -w '%{http_code}' "${AUTH[@]}" "$BASE/api/session/$SID" 2>/dev/null || true)
                if [ "$probe" != "200" ]; then
                    echo "[sandbox] 工作区记录的会话 $SID 不可恢复（HTTP $probe），创建新会话"
                    SID=""
                else
                    echo "[sandbox] 恢复工作区持久化的 OpenCode 会话: $SID"
                fi
            fi
        fi

        if [ -z "$SID" ]; then
            model_json=""
            if [ -n "${OPENCODE_MODEL:-}" ]; then
                prov="${OPENCODE_MODEL%%/*}"
                mdl="${OPENCODE_MODEL#*/}"
                if [ -n "$prov" ] && [ "$prov" != "$OPENCODE_MODEL" ] && [ -n "$mdl" ]; then
                    model_json=$(jq -n --arg p "$prov" --arg m "$mdl" '{"model":{"providerID":$p,"id":$m}}')
                else
                    echo "[sandbox] WARN: 无法解析 OPENCODE_MODEL=${OPENCODE_MODEL}，使用默认模型" >&2
                fi
            fi
            payload=$(jq -n --arg t "${TASK_NAME:-CTF}" '{title:$t, location:{directory:"/workspace"}}')
            if [ -n "$model_json" ]; then
                payload=$(printf '%s' "$payload" | jq -c --argjson m "$model_json" '. + $m')
            fi
            sc=$(curl -sS "${AUTH[@]}" -H 'Content-Type: application/json' -X POST \
                --data "$payload" "$BASE/api/session" -o /tmp/oc-session.json -w '%{http_code}' 2>/dev/null || true)
            SID=$(jq -r '.data.id // empty' /tmp/oc-session.json 2>/dev/null || true)
            if [ "$sc" != "200" ] || [ -z "$SID" ]; then
                fail "创建 OpenCode 会话失败 (HTTP ${sc:-none}): $(head -c 300 /tmp/oc-session.json 2>/dev/null)"
            fi
        fi

        if [ -n "$SID" ]; then
            printf '%s' "$SID" > /workspace/.opencode-session-id
            echo "[sandbox] opencode session: $SID"

            curl -sS -N --no-buffer "${AUTH[@]}" -H 'Accept: text/event-stream' "$BASE/api/event" 2>/dev/null \
                | tee /tmp/opencode-events.raw \
                | while IFS= read -r line; do handle_event "$line"; done &
            SSE_PID=$!

            (
                while :; do
                    pending=$(curl -sS "${AUTH[@]}" "$BASE/api/permission/request" 2>/dev/null || true)
                    ids=$(printf '%s' "$pending" | jq -r '.data[]?.id // empty' 2>/dev/null || true)
                    for rid in $ids; do
                        code=$(curl -sS "${AUTH[@]}" -H 'Content-Type: application/json' -X POST \
                            --data '{"decision":"always"}' \
                            "$BASE/api/session/$SID/permission/$rid/reply" \
                            -o /dev/null -w '%{http_code}' 2>/dev/null || true)
                        if [ "$code" = "204" ] || [ "$code" = "200" ]; then
                            say "[opencode] 自动允许工具权限请求"
                        fi
                    done
                    sleep 1
                done
            ) &
            PERM_PID=$!

            sleep 1
            T0=$(date +%s%3N)
            prompt_payload=$(jq -n --arg t "$PROMPT_TEXT" '{text:$t, resume:true}')
            sc=$(curl -sS "${AUTH[@]}" -H 'Content-Type: application/json' -X POST \
                --data "$prompt_payload" "$BASE/api/session/$SID/prompt" \
                -o /tmp/oc-prompt.json -w '%{http_code}' 2>/dev/null || true)

            if [ "$sc" != "200" ]; then
                fail "发送 Prompt 失败 (HTTP ${sc:-none}): $(head -c 300 /tmp/oc-prompt.json 2>/dev/null)"
            else
                echo "[sandbox] prompt 已通过 HTTP API 提交，等待执行结果..."
                TMO="${OPENCODE_TIMEOUT_SECONDS:-0}"
                case "$TMO" in ''|*[!0-9]*) TMO=0 ;; esac
                deadline=0
                [ "$TMO" -gt 0 ] && deadline=$(( $(date +%s) + TMO ))
                outcome=""
                idle_error=""
                interrupt_wait=0
                msgs=""
                while :; do
                    msgs=$(curl -sS "${AUTH[@]}" "$BASE/api/session/$SID/message" 2>/dev/null || true)
                    idle=$(printf '%s' "$msgs" | jq -r --argjson t "$T0" \
                        '[.data[]? | select(.type=="idle" and ((.time.created // 0) > $t))] | first // {} | .outcome // empty' \
                        2>/dev/null || true)
                    if [ -n "$idle" ]; then
                        outcome="$idle"
                        idle_error=$(printf '%s' "$msgs" | jq -r --argjson t "$T0" \
                            '[.data[]? | select(.type=="idle" and ((.time.created // 0) > $t))] | first | .error.message // empty' \
                            2>/dev/null || true)
                        break
                    fi
                    if [ "$deadline" -gt 0 ] && [ "$(date +%s)" -ge "$deadline" ]; then
                        echo "[sandbox] WARN: OpenCode 执行超过 ${TMO}s，发送 interrupt" >&2
                        curl -sS "${AUTH[@]}" -X POST "$BASE/api/session/$SID/interrupt" \
                            -o /dev/null 2>/dev/null || true
                        deadline=0
                        interrupt_wait=15
                    fi
                    if [ "$interrupt_wait" -gt 0 ]; then
                        interrupt_wait=$((interrupt_wait - 1))
                        if [ "$interrupt_wait" -eq 0 ]; then
                            outcome="interrupted"
                            break
                        fi
                    fi
                    sleep 1
                done

                case "$outcome" in
                    succeeded)
                        echo "[sandbox] OpenCode 执行完成 (outcome=succeeded)"
                        CODE=0
                        ;;
                    interrupted)
                        fail "OpenCode 执行被中断（超时 ${TMO}s 或被取消）"
                        ;;
                    *)
                        if [ -z "$outcome" ]; then
                            fail "等待 OpenCode 执行结果超时（会话无 idle 结果）"
                        else
                            fail "OpenCode 执行失败: ${idle_error:-未知错误}"
                        fi
                        ;;
                esac

                if [ ! -f /tmp/oc-saw-text ]; then
                    fb=0
                    while IFS= read -r l; do
                        [ -n "$l" ] || continue
                        fb=$((fb + 1))
                        emit_text "fallback-$fb" "done" "$l"
                    done < <(printf '%s' "$msgs" | jq -r \
                        '.data[]? | select(.type=="assistant") | .content[]? | select(.type=="text") | .text' \
                        2>/dev/null)
                fi
                if [ ! -f /tmp/oc-saw-tool ]; then
                    fb=0
                    while IFS=$'\t' read -r tname tst terr; do
                        [ -n "$tname" ] || continue
                        fb=$((fb + 1))
                        emit_tool "fallback-t-$fb" "$tname" "$tst" "" '[]' "" "$terr"
                    done < <(printf '%s' "$msgs" | jq -r \
                        '.data[]? | select(.type=="assistant") | .content[]? | select(.type=="tool")
                         | [(.name // "tool"),
                            (if .state.status=="completed" then "done"
                             elif .state.status=="error" then "error"
                             else "running" end),
                            (.state.error.message // "")] | @tsv' \
                        2>/dev/null)
                fi
                flush_all_text
            fi
        fi
    fi
fi

echo "[sandbox] ------------------------------------------------"
if [ -s "$FLAG_PATH" ]; then
    echo "[sandbox] $FLAG_PATH contents:"
    sed 's/^/[sandbox] | /' "$FLAG_PATH"
    # 确保 flag 同时出现在标准输出中，便于调度器正则捕获
    echo
fi

exit $CODE
