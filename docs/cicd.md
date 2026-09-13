# CI/CD integration

Complete pipeline examples for GitHub Actions, Azure DevOps, and GitLab CI.
Each example covers the full flow: serialize on source, commit YAML, deploy
to target, deserialize under strict mode, fail the pipeline on any warning.

## Table of contents

- [The intended flow](#the-intended-flow)
- [Secrets and credentials](#secrets-and-credentials)
- [GitHub Actions](#github-actions)
- [Azure DevOps](#azure-devops)
- [GitLab CI](#gitlab-ci)
- [Pre-commit: run the link sweep locally](#pre-commit-run-the-link-sweep-locally)
- [Gotchas](#gotchas)

## The intended flow

The pipeline is split into two pieces that run against different hosts.

**Serialize pipeline (source → repo).** Scheduled or on-demand. Typically runs
against the source of truth environment (dev or QA). Produces a YAML diff that
someone reviews and merges.

```
 Source DW ----(A) serialize---->  CI runner ---(B) git commit & PR---->  GitHub/DevOps/GitLab
```

**Deploy pipeline (repo → target).** On merge to `main` or on release tag.
Deploys code to the target, copies YAML into the target's `Files` volume, then
triggers deserialize under strict mode.

```
 GitHub/DevOps/GitLab ---(C) deploy code--->  Target DW
                       ---(D) sync YAML --->  Target DW filesystem
                       ---(E) deserialize-->  Target DW
                                              (strict mode fails pipeline on any warning)
```

Most teams run both pipelines side-by-side. The serialize pipeline produces
PRs; the deploy pipeline applies merged PRs to target environments.

## Secrets and credentials

Every pipeline needs three secrets per environment:

| Secret | Used for | Source |
|--------|----------|--------|
| `DW_HOST_URL` | Base URL of the DW instance (`https://shop-test.example.com`) | Env-specific, often public |
| `DW_API_KEY` | DW Management API bearer token (`CLD.xxx...`) | Created in admin: `Settings > Integration > API management` |
| Code-deploy credentials | Deploying the app (Azure App Service publish profile, SSH key, etc.) | Platform-specific |

Store `DW_API_KEY` in the CI provider's secret manager (GitHub `secrets`,
Azure DevOps `Library`, GitLab `CI/CD variables`). Never commit it.

In production, fetch it from Azure Key Vault at pipeline start and pass it
into the curl step as an env var. The examples below use provider-native
secret refs; swap to Key Vault as your infrastructure dictates.

## GitHub Actions

### Serialize pipeline

Runs nightly against the source environment, serializes, and opens a PR with
the diff.

```yaml
# .github/workflows/serialize-nightly.yml
name: Serialize nightly baseline

on:
  schedule:
    - cron: "0 2 * * *"   # 02:00 UTC daily
  workflow_dispatch:

jobs:
  serialize:
    runs-on: ubuntu-latest
    permissions:
      contents: write
      pull-requests: write
    steps:
      - uses: actions/checkout@v4

      - name: Serialize from source
        env:
          DW_HOST: ${{ secrets.DW_SOURCE_HOST }}
          DW_API_KEY: ${{ secrets.DW_SOURCE_API_KEY }}
        run: |
          # mode=replace runs first for structural data
          curl -fsSL -X POST "$DW_HOST/Admin/Api/Serialize?mode=replace" \
            -H "Authorization: Bearer $DW_API_KEY" \
            -o serialize-replace.log
          cat serialize-replace.log

          # mode=merge runs second for customer-owned content
          curl -fsSL -X POST "$DW_HOST/Admin/Api/Serialize?mode=merge" \
            -H "Authorization: Bearer $DW_API_KEY" \
            -o serialize-merge.log
          cat serialize-merge.log

      - name: Pull YAML from source host
        env:
          AZURE_STORAGE_ACCOUNT: ${{ secrets.AZURE_STORAGE_ACCOUNT }}
          AZURE_STORAGE_SAS: ${{ secrets.AZURE_STORAGE_SAS }}
        run: |
          # Source host's Files volume is on an Azure Files share.
          # Sync the SerializeRoot tree into the repo.
          mkdir -p serialize-root
          azcopy sync \
            "https://$AZURE_STORAGE_ACCOUNT.file.core.windows.net/files/System/Serializer/SerializeRoot?$AZURE_STORAGE_SAS" \
            "serialize-root/" \
            --recursive

      - name: Open PR with diff
        uses: peter-evans/create-pull-request@v6
        with:
          commit-message: "baseline: nightly serialize from source"
          branch: baseline/nightly-${{ github.run_number }}
          title: "Baseline: nightly serialize"
          body: |
            Automated baseline refresh from the source environment.
            Review the YAML diff before merging.
          labels: baseline, automation
```

### Deploy pipeline

On merge to `main`, deploys code to the target then triggers deserialize under
strict mode. The pipeline fails if deserialize returns any warnings.

```yaml
# .github/workflows/deploy.yml
name: Deploy to target

on:
  push:
    branches: [main]

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "8.0.x"

      - name: Build
        run: dotnet build src/Truvio.Commerce.Serializer/ -c Release

      - name: Deploy app to Azure
        uses: azure/webapps-deploy@v3
        with:
          app-name: ${{ secrets.AZURE_WEBAPP_NAME }}
          publish-profile: ${{ secrets.AZURE_WEBAPP_PUBLISH_PROFILE }}
          package: ./publish

      - name: Fetch the edition from the Distribution
        run: |
          # Git-clone consumption — no release archives. Pin the composition to
          # an annotated edition tag so every deploy applies a byte-identical set.
          git clone --depth 1 --branch editions/swift-demo/2.3.0 \
            https://github.com/justdynamics/Truvio.Commerce.Distribution.git dist
          # Compose the edition's layers into one SerializeRoot. Each layer ships
          # replace/ (source-wins) + merge/ (field-level) mode trees; stage them
          # under replace/ and merge/ for the serializer.
          mkdir -p edition-content/replace edition-content/merge
          for ref in $(jq -r '.from, .add[]?' dist/editions/swift-demo.json); do
            layer="${ref%@*}"
            [ -d "dist/layers/$layer/replace" ] && cp -R "dist/layers/$layer/replace/." edition-content/replace/
            [ -d "dist/layers/$layer/merge" ]   && cp -R "dist/layers/$layer/merge/."   edition-content/merge/
          done

      - name: Sync edition YAML to target's Files volume
        env:
          AZURE_STORAGE_ACCOUNT: ${{ secrets.AZURE_STORAGE_ACCOUNT }}
          AZURE_STORAGE_SAS: ${{ secrets.AZURE_STORAGE_SAS }}
        run: |
          azcopy sync \
            "edition-content/" \
            "https://$AZURE_STORAGE_ACCOUNT.file.core.windows.net/files/System/Serializer/SerializeRoot?$AZURE_STORAGE_SAS" \
            --recursive \
            --delete-destination true

      - name: Deserialize on target (Replace mode, strict)
        env:
          DW_HOST: ${{ secrets.DW_TARGET_HOST }}
          DW_API_KEY: ${{ secrets.DW_TARGET_API_KEY }}
        run: |
          # ?strictMode=true escalates every recoverable warning to HTTP 4xx.
          # -f makes curl exit non-zero on 4xx/5xx, failing the pipeline.
          curl -fsSL -X POST \
            "$DW_HOST/Admin/Api/Deserialize?mode=replace&strictMode=true" \
            -H "Authorization: Bearer $DW_API_KEY" \
            -o deserialize-replace.log
          cat deserialize-replace.log

      - name: Deserialize on target (Merge mode, strict)
        env:
          DW_HOST: ${{ secrets.DW_TARGET_HOST }}
          DW_API_KEY: ${{ secrets.DW_TARGET_API_KEY }}
        run: |
          curl -fsSL -X POST \
            "$DW_HOST/Admin/Api/Deserialize?mode=merge&strictMode=true" \
            -H "Authorization: Bearer $DW_API_KEY" \
            -o deserialize-merge.log
          cat deserialize-merge.log

      - name: Archive logs
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: deserialize-logs
          path: deserialize-*.log
```

## Azure DevOps

### Serialize pipeline

```yaml
# azure-pipelines-serialize.yml
trigger: none
schedules:
  - cron: "0 2 * * *"
    displayName: Nightly baseline refresh
    branches:
      include: [main]
    always: true

pool:
  vmImage: ubuntu-latest

variables:
  - group: dw-source-env      # Library group with DW_SOURCE_HOST and DW_SOURCE_API_KEY

steps:
  - checkout: self
    persistCredentials: true

  - script: |
      curl -fsSL -X POST "$(DW_SOURCE_HOST)/Admin/Api/Serialize?mode=replace" \
        -H "Authorization: Bearer $(DW_SOURCE_API_KEY)" \
        -o serialize-replace.log
      cat serialize-replace.log

      curl -fsSL -X POST "$(DW_SOURCE_HOST)/Admin/Api/Serialize?mode=merge" \
        -H "Authorization: Bearer $(DW_SOURCE_API_KEY)" \
        -o serialize-merge.log
      cat serialize-merge.log
    displayName: Serialize source

  - task: AzureCLI@2
    displayName: Sync YAML from source Files share
    inputs:
      azureSubscription: dw-subscription
      scriptType: bash
      scriptLocation: inlineScript
      inlineScript: |
        mkdir -p serialize-root
        az storage file download-batch \
          --account-name $(AZURE_STORAGE_ACCOUNT) \
          --source files/System/Serializer/SerializeRoot \
          --destination serialize-root \
          --pattern "*"

  - script: |
      git config user.email "ci@example.com"
      git config user.name  "Azure Pipelines"
      git checkout -b baseline/nightly-$(Build.BuildNumber)
      git add serialize-root/
      if git diff --cached --quiet; then
        echo "No content changes — skipping PR"
        exit 0
      fi
      git commit -m "content: nightly serialize $(Build.BuildNumber)"
      git push --set-upstream origin baseline/nightly-$(Build.BuildNumber)
      az repos pr create \
        --source-branch baseline/nightly-$(Build.BuildNumber) \
        --target-branch main \
        --title "Baseline: nightly serialize $(Build.BuildNumber)" \
        --auto-complete false
    displayName: Open baseline PR
```

### Deploy pipeline

```yaml
# azure-pipelines-deploy.yml
trigger:
  branches:
    include: [main]

pool:
  vmImage: ubuntu-latest

variables:
  - group: dw-target-env      # DW_TARGET_HOST, DW_TARGET_API_KEY, AZURE_WEBAPP_NAME, AZURE_STORAGE_ACCOUNT

stages:
  - stage: Deploy
    jobs:
      - job: Publish
        steps:
          - task: UseDotNet@2
            inputs:
              version: "8.0.x"

          - script: dotnet build src/Truvio.Commerce.Serializer/ -c Release
            displayName: Build

          - task: AzureWebApp@1
            displayName: Deploy to App Service
            inputs:
              azureSubscription: dw-subscription
              appName: $(AZURE_WEBAPP_NAME)
              package: ./publish

          - script: |
              # Git-clone consumption — no release archives. Pin to an annotated
              # edition tag so every deploy applies a byte-identical set.
              git clone --depth 1 --branch editions/swift-demo/2.3.0 \
                https://github.com/justdynamics/Truvio.Commerce.Distribution.git dist
              mkdir -p edition-content/replace edition-content/merge
              for ref in $(jq -r '.from, .add[]?' dist/editions/swift-demo.json); do
                layer="${ref%@*}"
                [ -d "dist/layers/$layer/replace" ] && cp -R "dist/layers/$layer/replace/." edition-content/replace/
                [ -d "dist/layers/$layer/merge" ]   && cp -R "dist/layers/$layer/merge/."   edition-content/merge/
              done
            displayName: Fetch edition from the Distribution

          - task: AzureCLI@2
            displayName: Sync edition YAML
            inputs:
              azureSubscription: dw-subscription
              scriptType: bash
              scriptLocation: inlineScript
              inlineScript: |
                az storage file upload-batch \
                  --account-name $(AZURE_STORAGE_ACCOUNT) \
                  --destination files/System/Serializer/SerializeRoot \
                  --source edition-content \
                  --pattern "*"

          - script: |
              curl -fsSL -X POST \
                "$(DW_TARGET_HOST)/Admin/Api/Deserialize?mode=replace&strictMode=true" \
                -H "Authorization: Bearer $(DW_TARGET_API_KEY)" \
                -o deserialize-replace.log
              cat deserialize-replace.log

              curl -fsSL -X POST \
                "$(DW_TARGET_HOST)/Admin/Api/Deserialize?mode=merge&strictMode=true" \
                -H "Authorization: Bearer $(DW_TARGET_API_KEY)" \
                -o deserialize-merge.log
              cat deserialize-merge.log
            displayName: Deserialize under strict mode

          - task: PublishPipelineArtifact@1
            condition: always()
            inputs:
              targetPath: .
              artifact: deserialize-logs
              publishLocation: pipeline
              artifactType: pipeline
              patterns: "**/deserialize-*.log"
```

## GitLab CI

A single `.gitlab-ci.yml` with both flows. The `serialize` job runs on a
schedule; the `deploy` job runs on pushes to `main`.

```yaml
# .gitlab-ci.yml
stages:
  - serialize
  - build
  - deploy

variables:
  DOTNET_VERSION: "8.0"

# --- Serialize: scheduled nightly refresh from source ---
serialize:source:
  stage: serialize
  image: mcr.microsoft.com/azure-cli:latest
  only:
    - schedules
  script:
    - |
      curl -fsSL -X POST "$DW_SOURCE_HOST/Admin/Api/Serialize?mode=replace" \
        -H "Authorization: Bearer $DW_SOURCE_API_KEY" \
        -o serialize-replace.log
      cat serialize-replace.log
    - |
      curl -fsSL -X POST "$DW_SOURCE_HOST/Admin/Api/Serialize?mode=merge" \
        -H "Authorization: Bearer $DW_SOURCE_API_KEY" \
        -o serialize-merge.log
      cat serialize-merge.log
    - |
      mkdir -p serialize-root
      az storage file download-batch \
        --account-name "$AZURE_STORAGE_ACCOUNT" \
        --source files/System/Serializer/SerializeRoot \
        --destination serialize-root
    - |
      git config user.email "ci@example.com"
      git config user.name  "GitLab CI"
      git checkout -b "baseline/nightly-$CI_PIPELINE_ID"
      git add serialize-root/
      git diff --cached --quiet && exit 0
      git commit -m "content: nightly serialize $CI_PIPELINE_ID"
      git push -o merge_request.create \
               -o merge_request.target=main \
               --set-upstream origin "baseline/nightly-$CI_PIPELINE_ID"
  artifacts:
    paths:
      - serialize-*.log

# --- Build: compile the DLL on every push ---
build:dll:
  stage: build
  image: mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}
  only:
    - main
  script:
    - dotnet build src/Truvio.Commerce.Serializer/ -c Release
  artifacts:
    paths:
      - src/Truvio.Commerce.Serializer/bin/Release/net8.0/Truvio.Commerce.Serializer.dll

# --- Deploy: on merge to main, deploy + deserialize under strict mode ---
deploy:target:
  stage: deploy
  image: mcr.microsoft.com/azure-cli:latest
  only:
    - main
  needs: [build:dll]
  script:
    # 1. Deploy the app — mechanism depends on hosting; this example assumes
    #    an App Service deploy via publish profile. Swap for rsync/scp/etc.
    - az webapp deploy --resource-group "$AZURE_RG" --name "$AZURE_WEBAPP" \
        --src-path src/Truvio.Commerce.Serializer/bin/Release/net8.0/Truvio.Commerce.Serializer.dll \
        --type static --target-path "site/wwwroot/bin/Truvio.Commerce.Serializer.dll"

    # 2. Fetch the edition from the Distribution (git-clone consumption, pinned
    #    to an annotated edition tag) and compose its layers into a SerializeRoot.
    - |
      git clone --depth 1 --branch editions/swift-demo/2.3.0 \
        https://github.com/justdynamics/Truvio.Commerce.Distribution.git dist
      mkdir -p edition-content/replace edition-content/merge
      for ref in $(jq -r '.from, .add[]?' dist/editions/swift-demo.json); do
        layer="${ref%@*}"
        [ -d "dist/layers/$layer/replace" ] && cp -R "dist/layers/$layer/replace/." edition-content/replace/
        [ -d "dist/layers/$layer/merge" ]   && cp -R "dist/layers/$layer/merge/."   edition-content/merge/
      done
    # 3. Sync the composed YAML into the target's Files volume
    - az storage file upload-batch \
        --account-name "$AZURE_STORAGE_ACCOUNT" \
        --destination files/System/Serializer/SerializeRoot \
        --source edition-content

    # 4. Deserialize under strict mode. -f fails the job on HTTP 4xx/5xx.
    - |
      curl -fsSL -X POST \
        "$DW_TARGET_HOST/Admin/Api/Deserialize?mode=replace&strictMode=true" \
        -H "Authorization: Bearer $DW_TARGET_API_KEY" \
        -o deserialize-replace.log
      cat deserialize-replace.log
    - |
      curl -fsSL -X POST \
        "$DW_TARGET_HOST/Admin/Api/Deserialize?mode=merge&strictMode=true" \
        -H "Authorization: Bearer $DW_TARGET_API_KEY" \
        -o deserialize-merge.log
      cat deserialize-merge.log
  artifacts:
    when: always
    paths:
      - deserialize-*.log
```

## Pre-commit: run the link sweep locally

`BaselineLinkSweeper` runs at serialize time and fails the API call if any
`Default.aspx?ID=N` reference is unresolvable inside the baseline. Surface
that same failure locally so a developer sees it before pushing.

If your team has a local dev DW host, the simplest pattern is a pre-commit
hook that triggers a serialize against the dev host and checks the HTTP code:

```bash
# .git/hooks/pre-commit
#!/usr/bin/env bash
set -e

# Skip if no staged changes under src/ or serialize-root/
if git diff --cached --quiet -- src/ serialize-root/; then
  exit 0
fi

status=$(curl -o /tmp/serialize.log -s -w "%{http_code}" \
  -X POST "${DW_LOCAL_HOST:-https://localhost:54035}/Admin/Api/Serialize?mode=replace&strictMode=true" \
  -H "Authorization: Bearer $DW_LOCAL_API_KEY")

if [ "$status" != "200" ]; then
  echo "Pre-commit: Serialize returned HTTP $status" >&2
  tail -n 40 /tmp/serialize.log >&2
  echo "Fix the baseline link issues above and re-commit." >&2
  exit 1
fi
```

Alternative: `tools/e2e/full-clean-roundtrip.ps1` runs the full source →
target round-trip against two local hosts from a freshly-restored bacpac.
Use it as a heavyweight pre-release gate.

## Gotchas

### `?mode=merge` query-param binding

Query-string binding for POST requests on DW's `CommandBase` does not bind by
default. The serializer has an explicit query-param fallback (D-38-11) so
`?mode=merge` works, but be explicit in scripts — ambiguity here caused real
incidents before the fallback shipped. When in doubt, send the JSON body
variant:

```bash
curl -X POST "$DW_HOST/Admin/Api/Deserialize" \
  -H "Authorization: Bearer $DW_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"Mode":"merge","StrictMode":true}'
```

### Exit code vs. HTTP status

`curl -f` makes curl exit non-zero on HTTP ≥ 400. That is the right default
for CI. If you run without `-f`, the job succeeds even when deserialize
returned `HasErrors=true`. Always use `-fsSL`.

### Strict mode default at request time

`strictMode` precedence is request param > config > entry-point default.
API/CLI default is on; admin UI default is off. For CI, either set
`config.strictMode: true` once and rely on the default, or pass
`?strictMode=true` on every call. Passing is more defensive because config
errors that flip the value to `null` still fail the pipeline.

### Mode order matters

Inside a mode, predicates run in the order they appear in config. For
cross-mode ordering, Replace runs before Merge in the pipeline — run the API
calls in that order. Merge-mode link resolution (for `Default.aspx?ID=N` in
Merge predicates) depends on the Replace-mode page map being built first.

### Secrets in deserialize logs

The log output from deserialize sometimes contains row data (especially on
fresh-target creates, which echo the inserted values). If any SqlTable
predicate captures a table with secrets and the predicate didn't list them
in `excludeFields`, the secret lands in the log. Review `excludeFields`
coverage for sensitive tables before wiring the log upload step. See
[`runtime-exclusions.md`](runtime-exclusions.md#credential-handling) for the
credential column checklist.

### First-run schema alignment

A fresh target DB that was bootstrapped on a different DW NuGet version may
have a different `Area` / `EcomProducts` schema shape than the source.
`TargetSchemaCache` warn-and-skip handles missing columns at write time, but
strict mode escalates the warning. Align DW NuGet versions between source
and target hosts before the first deploy, or document the drift explicitly.
See [Troubleshooting](troubleshooting.md#source-column-tc-not-present-on-target-schema--skipping)
for the full pattern.

## See also

- [Getting started](getting-started.md) — first serialize and deserialize
- [Strict mode](strict-mode.md) — what escalates, entry-point defaults
- [Configuration](configuration.md) — `Serializer.config.json` reference
- [Troubleshooting](troubleshooting.md) — common pipeline failures
- [`tools/e2e/README.md`](../tools/e2e/README.md) — reference test harness for
  local bacpac-driven round-trips
