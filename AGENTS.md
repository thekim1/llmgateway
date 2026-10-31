# Instructions for agents

Read [HANDOVER.md](HANDOVER.md) first: the current state, the toolchain and the rules of this repository.

## Pull requests and CI

GitHub Actions minutes are limited and shared by everyone ([CI and Actions minutes](docs/local-development.md#ci-and-actions-minutes)),
so CI should run once per finished change, and pass the first time.

- **Open every pull request as a draft** (`gh pr create --draft`) and keep it a draft while you implement. Draft pull
  requests run no CI, so push as often as you need to.
- **Run the checks locally before every push:** `scripts/Test-BeforePush.ps1` (`pwsh scripts/Test-BeforePush.ps1` outside
  Windows) runs what CI runs, including the backend tests. Fix every failure before pushing. `-Quick` (without the
  backend tests) is enough for a work-in-progress push to a draft, never for a pull request that is or becomes ready.
- **Use the .NET SDK CI uses** (`dotnet-version` in `.github/workflows/verify.yml`). The script warns when yours differs;
  install that version then, because another SDK reports other unnecessary usings and the build fails in CI.
- **Mark the pull request ready** (`gh pr ready`) only when the implementation is finished and the full script passes,
  or when you are asked for a review. After that, push follow-up fixes together in one push, not commit by commit.
- **Do not push straight to `main`**; go through a pull request and merge with squash (the merge commit is not built
  again). Do not re-run failed workflows to get past a flaky test: fix the test or say so in the pull request.
- Give every new workflow job a `timeout-minutes`.
