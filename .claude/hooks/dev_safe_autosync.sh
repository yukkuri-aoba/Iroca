#!/usr/bin/env bash
# dev_safe（隔離private repo）の作業内容をセッション終了時に自動でcommit+pushする。
# dev_safeは公開されない専用リポなので、通常のgit.mdのpush確認ルールの例外として扱う。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEV_SAFE_DIR="$(cd "$SCRIPT_DIR/../../dev_safe" 2>/dev/null && pwd)"

if [ -z "$DEV_SAFE_DIR" ] || [ ! -d "$DEV_SAFE_DIR/.git" ]; then
  exit 0
fi

cd "$DEV_SAFE_DIR" || exit 0

git add -A

if git diff --cached --quiet; then
  COMMIT_MSG="差分なし"
else
  TS="$(date '+%Y-%m-%d %H:%M:%S')"
  if git commit -q -m "chore(dev_safe): auto-sync ${TS}"; then
    COMMIT_MSG="commit実行"
  else
    echo '{"systemMessage": "dev_safe auto-sync: commit失敗（要確認）"}'
    exit 0
  fi
fi

PUSH_OUT="$(git push origin main 2>&1)"
PUSH_STATUS=$?

if [ $PUSH_STATUS -ne 0 ]; then
  LAST_LINE="$(printf '%s' "$PUSH_OUT" | tail -1 | sed 's/"/\\"/g')"
  echo "{\"systemMessage\": \"dev_safe auto-sync: ${COMMIT_MSG}／push失敗（ローカルcommitは保持済み。要手動確認: ${LAST_LINE}）\"}"
else
  if [ "$COMMIT_MSG" = "差分なし" ]; then
    exit 0
  fi
  echo "{\"systemMessage\": \"dev_safe auto-sync: ${COMMIT_MSG}／push成功\"}"
fi

exit 0
