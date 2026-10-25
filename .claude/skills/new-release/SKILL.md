---
name: new-release
description: Make a new release of the time tracker (commit, push, tag vX.Y.Z, release notes). Use whenever the user asks to release, publish a version, tag, or "push and make a release".
---

# Making a release

Pushing a `vX.Y.Z` tag starts `.github/workflows/release.yml`, which builds the exe and creates the GitHub release **from the tagged commit**. A tag on the wrong commit publishes the wrong code, and deleting a pushed tag needs the user's permission. So the tag is only created once the commit is verified, in its own step.

## What went wrong once (v1.2.0, 2026-10-25)

`git commit -F -` fed by a PowerShell here-string failed (PowerShell passed the message as a path), but the commands were chained with `;`, so `git tag` and `git push origin vX.Y.Z` still ran and tagged the **previous** commit. Fixed by cancelling the workflow run and the user deleting the tag.

## Rules

- **Never chain commit, push and tag in one command.** One step per tool call, and read its output before the next one.
- In PowerShell, `;` continues after an error. Use `&&`, or separate calls.
- Write the commit message to a file in the scratchpad and use `git commit -F <file>`. No here-string piped into `git commit -F -`.

## Steps

1. **Version**: `git tag --sort=-v:refname` for the latest. New feature → minor (`1.1.1` → `1.2.0`), fix only → patch. Say which version and why.
2. **Build**: `dotnet build src/TimeTracker` must succeed with no errors. If the running app locks `bin\Debug`, build with `-o` to a temp folder.
3. **Commit** (message from a file, with the attribution lines), then **check**: `git log --oneline -1` shows the new commit and `git status --short` is empty.
4. **Push main**: `git push origin main`, then check `git rev-parse HEAD` equals `git rev-parse origin/main`.
5. **Tag the verified commit explicitly**, in its own call: `git tag vX.Y.Z <sha from step 4>`, then `git show -s --format="%h %s" vX.Y.Z` to confirm it is that commit.
6. **Push the tag**: `git push origin vX.Y.Z`.
7. **Wait for the workflow**: `gh run list --limit 1`, then wait for it to complete (background `until` loop, not chained sleeps). Check `gh release view vX.Y.Z` has `TimeTracker.exe`.
8. **Release notes**: replace the auto notes with `gh release edit vX.Y.Z --notes-file <file>`, in English, in the style of v1.1.0 (`gh release view v1.1.0`): a bold one-line summary per change with a short explanation, the **Upgrade:** paragraph (quit the app with right click → Exit, replace `TimeTracker.exe`, start it again; settings and data are kept), what is not yet verified, and keep the **Full Changelog** link at the end. Show the notes to the user.
9. **Local to-do**: update `TODO.local.md` (not in git) if the release closes or changes an item.

## If a tag went out on the wrong commit

1. Cancel the workflow run right away: `gh run list --limit 3`, then `gh run cancel <id>`.
2. Ask the user to delete the remote tag (`git push origin :refs/tags/vX.Y.Z`), and the release if one was already created. Don't reuse the version before that's done.
3. Delete the local tag (`git tag -d vX.Y.Z`), then restart from step 3.
