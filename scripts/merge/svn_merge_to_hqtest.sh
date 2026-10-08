#!/bin/bash
#
# Merge the SRC_URL branch into the ~/wind/data_hqtest working copy and commit.
#
# Usage:
#   ./svn-merge-stage.sh            # merge + commit (runs in $TARGET_WC)
#   ./svn-merge-stage.sh --dry-run  # stop right before the commit
#                                   (update/merge DO run - the working copy is
#                                    left modified; commit or 'svn revert -R .')
#
#   TARGET_WC=/other/wc ./svn-merge-stage.sh   # override the target working copy
#
# svn runs fully interactively: it may prompt for credentials, SSL cert trust
# and conflict resolution. Step 3 only stops on conflicts still left unresolved
# (status 'C') after those prompts.
# Requires an EUC-KR shell locale (LANG=ko_KR.EUC-KR).
#
set -euo pipefail

# Locale is inherited from the shell (no LC_ALL / LC_CTYPE / LC_MESSAGES override).
# Note: this repository has EUC-KR encoded filenames, so svn needs an EUC-KR
# locale (e.g. LANG=ko_KR.EUC-KR). Running under LC_ALL=C aborts with
#   svn: E000022: Error converting entry in directory '...' to UTF-8

# internal SVN root URL (e.g. https://<svn-host>/svn)
SVN_ROOT=""
# source branch path under SVN_ROOT (e.g. <data-repo>/branches/<branch>)
SRC_PATH=""
SRC_URL="${SVN_ROOT}/${SRC_PATH}"
TARGET_WC="${TARGET_WC:-${HOME}/wind/data_hqtest}"
# reviewer name put in the svn commit message
REVIEWER=""
DRY_RUN=0
[ "${1:-}" = "--dry-run" ] && DRY_RUN=1

info() { printf '\n==> %s\n' "$*"; }
die()  { printf '\n[STOP] %s\n' "$*" >&2; exit 1; }

# conflict markers: text C (col 1), property C (col 2), tree conflict C (col 7)
# STATUS is captured first so grep never has to SIGPIPE a long 'svn status'.
STATUS=""
read_status() { STATUS="$(svn status)"; }
has_conflict() { grep -qE '^(C|.C|......C)' <<<"$STATUS"; }
# 'L' in column 3 = working copy lock left behind by an interrupted svn run
has_lock() { grep -qE '^..L' <<<"$STATUS"; }
show_lock() { grep -E '^..L' <<<"$STATUS" | head -10 || true; }
show_conflict() { grep -E '^(C|.C|......C)' <<<"$STATUS" || true; }

# ---------------------------------------------------------------- 1. update
info "Step 1/4: svn update ($TARGET_WC)"

[ -d "$TARGET_WC" ] || die "target working copy not found: $TARGET_WC"
cd "$TARGET_WC"

WC_ROOT="$(svn info --show-item wc-root . 2>/dev/null | tr -d ' \n')" \
    || die "not an SVN working copy: $TARGET_WC"
[ -n "$WC_ROOT" ] || die "not an SVN working copy: $TARGET_WC"
[ "$WC_ROOT" = "$(pwd -P)" ] \
    || die "TARGET_WC must be the working copy root ($WC_ROOT), not a subdirectory."

WC_URL="$(svn info --show-item url . | tr -d ' \n')"
echo "working copy : $(pwd -P)"
echo "wc url       : $WC_URL"
echo "merge source : $SRC_URL"

read_status
if has_lock; then
    show_lock
    die "working copy is locked by an interrupted svn operation. run: (cd $TARGET_WC && svn cleanup)"
fi

if [ -n "$(svn status -q)" ]; then
    svn status -q
    die "working copy has local modifications. commit or revert them first."
fi

svn update || die "svn update failed."
read_status
has_conflict && { show_conflict; die "conflicts after update. resolve them first."; }

# ------------------------------------------------- 2. source revision + merge
# SRC_REV is only used for the commit message; the merge itself goes to HEAD.
SRC_REV="$(svn info --show-item last-changed-revision "$SRC_URL" | tr -d ' \n')" \
    || die "failed to read last changed revision of $SRC_URL"
[ -n "$SRC_REV" ] || die "failed to read last changed revision of $SRC_URL"

info "Step 2/4: svn merge $SRC_URL (source r$SRC_REV)"
svn merge "$SRC_URL" . \
    || die "svn merge failed. working copy left as-is."

# -------------------------------------------------------- 3. conflict check
info "Step 3/4: svn status (conflict check)"
read_status
printf '%s\n' "$STATUS"

if has_conflict; then
    echo
    echo "conflicted:"
    show_conflict
    die "merge conflicts detected. nothing was committed - resolve manually (or 'svn revert -R .')."
fi

if [ -z "$(svn status -q)" ]; then
    info "nothing to merge - already in sync with stage@$SRC_REV. no commit."
    exit 0
fi

# ---------------------------------------------------------------- 4. commit
MSG="Merged revision(s) from stage: revision $SRC_REV Sync DataSource. reviewer: $REVIEWER"

if [ "$DRY_RUN" -eq 1 ]; then
    info "Step 4/4: --dry-run, skipping commit"
    echo "would commit with message:"
    echo "  $MSG"
    exit 0
fi

info "Step 4/4: svn commit"
echo "message: $MSG"
echo

# final confirmation - anything other than y/yes aborts (default: No)
ANSWER=""
read -r -p "Commit these changes? [y/N] " ANSWER || ANSWER=""
case "$ANSWER" in
    y | Y | yes | YES | Yes) ;;
    *) die "aborted by user. nothing was committed - the merge is still in the working copy (svn commit / svn revert -R .)." ;;
esac

svn commit -m "$MSG" || die "svn commit failed."

info "done. merged stage@$SRC_REV into $WC_URL"
