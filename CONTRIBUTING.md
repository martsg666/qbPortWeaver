# Contributing to qbPortWeaver

Pull requests are welcome. This file covers the branch and release strategy, branch naming,
and the release workflow.

## External contributors

Fork the repository and open your pull request against the **current release branch** (the highest `2.x.y` branch), not `master`. `master` only ever moves when a finished release is merged into it, so a PR targeting `master` will be retargeted. If you are unsure which release branch is active, ask in the pull request or an issue.

## Branch and Release Strategy

**`master`** always reflects the latest published release. Do not commit directly to `master`; it is updated only by merging a completed release branch (step 3 below).

### Branch naming

| Purpose | Base branch | Name pattern |
|---|---|---|
| Release | Previous release branch | `2.x.y` |
| QA | Release branch | `qa/<version>` |
| Release candidate | Release branch | `rc/<version>-rc<n>` |
| Hotfix | Corresponding release branch | `fix/<description>` |
| Feature | Corresponding release branch | `feature/<description>` |

Hotfix and feature branches are merged into the release branch via pull request. QA and release-candidate branches stage a batch of changes for final testing before the release is tagged; they take direct commits, and only `master` and the bare `2.x.y` release branches require a pull request to update.

"Direct commits" means no pull request is needed **per change**, not that the branch has none: open one for the batch as soon as you cut a `qa/` or `rc/` branch. That is how it has always been done (every `rc/` branch back to 2.6.0 had a PR), and it is also what gives the branch any CI at all - `build.yml` and `sonarcloud.yml` both trigger on `pull_request`, so once a PR is open every later push to the branch is checked. Before that PR exists, pushes to a `qa/` or `rc/` branch run nothing.

### Workflow diagram

```
master  ──────────────────────────────────────────────────────────────► (always latest release)
           │                                                          ▲
           │  git checkout -b <new-release> origin/<previous-release>│ git merge --no-ff <new-release>
           ▼                                                          │ then read the SonarCloud run on master
<new-release> ──┬─────────────────────────────────────────────────────┴── git tag v<new-release>
           │                                                                  │
           ├── fix/some-bug   → PR → merge into <new-release>                 └─► CI/CD pipeline triggers
           └── feature/new-ui → PR → merge into <new-release>                      ├─ dotnet publish (self-contained win-x64)
                                                                                   ├─ WiX MSI build
                                                                                   ├─ GitHub Release created
                                                                                   └─ MSI + .nupkg uploaded to release
```

### Workflow steps

1. **Create a release branch** from the previous release branch:
   ```
   git checkout -b <new-release> origin/<previous-release>
   git push -u origin <new-release>
   ```

2. **Create fix or feature branches** off the release branch and open a PR targeting it:
   ```
   git checkout -b fix/my-fix origin/<new-release>
   # or
   git checkout -b feature/my-feature origin/<new-release>
   ```
   Opening the pull request runs the **Build Check** workflow (a Release build with warnings treated as errors); make sure it passes before merging.

3. **Merge the release branch into `master`** once all testing is complete, before tagging:
   ```
   git checkout master
   git merge --no-ff <new-release>
   git push origin master
   ```
   This runs the **SonarCloud** workflow on `master`. Read that run before tagging. Pull request analysis is included on this plan and posts a full quality gate, so a PR's SonarCloud check is readable and worth acting on as you go; what is not readable is *branch* analysis on anything other than `master`, and the `master` run is the only one that analyses the merged release content as it will ship. Merging before tagging is also what carries every contributor's commits into `master`, so they appear in the repository's contributor list.

4. **Tag the release branch** - this triggers the pipeline:
   ```
   git checkout <new-release>
   git pull --ff-only
   git tag v<new-release>
   git push origin v<new-release>
   ```
   Pushing the tag automatically triggers the **Build and Release** pipeline, which builds the app, compiles the MSI installer, creates the GitHub Release, and uploads the MSI and Chocolatey package as release assets. The package managers are then published manually from the Actions tab: run **Publish to winget** (opens the winget-pkgs submission via wingetcreate), and once the previous Chocolatey version is approved, run **Publish to Chocolatey**.

5. **Do not delete release branches.** They serve as the base for future hotfixes. If a branch is accidentally deleted it can be reconstructed from its tag:
   ```
   git checkout -b <new-release> v<new-release>
   git push origin <new-release>
   ```
