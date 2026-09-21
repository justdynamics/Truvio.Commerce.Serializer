# Getting started

This page takes you from a fresh repo clone to a working round-trip: serialize
data from one Truvio Commerce instance, commit the YAML, deserialize into a second
instance, and verify the result. You need about twenty minutes and two reachable
DW hosts (local or cloud).

## Table of contents

- [Prerequisites](#prerequisites)
- [Install the DLL](#install-the-dll)
- [Create a minimal config](#create-a-minimal-config)
- [Serialize from the source](#serialize-from-the-source)
- [Commit the YAML](#commit-the-yaml)
- [Deserialize into the target](#deserialize-into-the-target)
- [Verify the round-trip](#verify-the-round-trip)
- [What next](#what-next)

## Prerequisites

- .NET 8.0 SDK
- Two Truvio Commerce 10.17.5+ instances you can reach over HTTP(S) and deploy a
  DLL to. Local `dotnet run` hosts work fine for a first pass.
- A Management API bearer token on each host. Create one in admin under
  `Settings > Integration > API management` if you don't have one.
- SQL Server (or SQL Azure) — whatever DW is already using.

Optional but recommended:

- Git for version control of the YAML output.
- `sqlcmd` on `PATH` if you plan to run the cleanup scripts under
  `tools/swift22-cleanup/` against a reference install.

## Install the DLL

From the repo root:

```bash
dotnet build src/Truvio.Commerce.Serializer/ -c Release
```

Copy the built DLL to both DW hosts:

```bash
# Repeat for the target host
cp src/Truvio.Commerce.Serializer/bin/Release/net8.0/Truvio.Commerce.Serializer.dll \
   /path/to/your-dw-host/bin/
```

Restart each host. When it comes back up, sign in and confirm the
`Settings > Developer > Serialize` navigation node is present — that's the
serializer's admin UI root.

## Create a minimal config

The serializer reads its configuration from
`Files/System/Serializer/Serializer.config.json` — inside the serializer
folder, so config and YAML travel together. The admin UI at
`Settings > Developer > Serialize` reads and writes this file.

For a first round-trip, create the following at
`/path/to/your-dw-host/Files/System/Serializer/Serializer.config.json`:

```json
{
  "outputDirectory": "Serializer",
  "predicates": [
    {
      "name": "EcomOrderFlow",
      "mode": "Replace",
      "providerType": "SqlTable",
      "table": "EcomOrderFlow",
      "nameColumn": "OrderFlowName"
    }
  ]
}
```

This picks a single SQL table — `EcomOrderFlow` — as the smallest useful test.
Drop the same file into the target host's `Files/System/Serializer/` folder as
well. Both hosts need the config to understand which tables participate.

Strict mode is resolved per entry point — API/CLI calls run strict (warnings
escalate), admin UI actions run lenient. See [`strict-mode.md`](strict-mode.md)
for what escalates.

## Serialize from the source

With the hosts running, trigger the serialize from the source:

```bash
curl -X POST https://source.example.com/Admin/Api/Serialize \
  -H "Authorization: Bearer CLD.your-source-api-key"
```

Expected response:

```
HTTP/1.1 200 OK

Serialization complete (Replace). 3 YAML files written to
/path/to/your-dw-host/Files/System/Serializer/SerializeRoot/replace.
predicates=1 rows=3.
```

Look at the output directory:

```bash
ls /path/to/your-dw-host/Files/System/Serializer/SerializeRoot/replace/_sql/EcomOrderFlow/
# Default.yml
# Quote.yml
# ... (one file per row, named by nameColumn)
```

A row file looks like this:

```yaml
orderFlowId: 1
orderFlowName: Default
orderFlowDescription: Default order flow for B2C
orderFlowActive: true
```

Each SqlTable predicate writes one file per row. Content predicates write a
mirror-tree of pages, grid rows, and paragraphs. See [`concepts.md`](concepts.md)
for the full folder layout.

## Commit the YAML

The source host's `Files/System/Serializer/SerializeRoot/` directory holds the
canonical baseline. Copy (or mount, or sync) that tree into your own repo at any
stable path:

```bash
mkdir -p serialize-root/
cp -R /path/to/your-dw-host/Files/System/Serializer/SerializeRoot/replace \
      serialize-root/

git add serialize-root/
git commit -m "content: Replace snapshot"
git push
```

The `replace/` and `merge/` subfolders match the config's `replaceOutputSubfolder`
and `mergeOutputSubfolder` settings. Downstream environments read from the same
layout.

## Deserialize into the target

Get the committed YAML onto the target host's filesystem at
`Files/System/Serializer/SerializeRoot/replace/`. Any mechanism works: rsync
from a CI agent, `git pull` on the host, an Azure Files share, a
`cp` during an App Service deployment step.

Then trigger the deserialize:

```bash
curl -X POST https://target.example.com/Admin/Api/Deserialize \
  -H "Authorization: Bearer CLD.your-target-api-key"
```

Expected response:

```
HTTP/1.1 200 OK

[Replace] predicates=1 created=0 updated=3 skipped=0 failed=0.
```

If this is the first run on an empty target, you'll see `created=3` instead.
On subsequent runs, unchanged rows go to `skipped` (if `compareColumns` is set
on the predicate) or `updated` (default).

## Verify the round-trip

Two quick checks prove end-to-end fidelity:

1. **Diff the YAML.** Re-run serialize on the target immediately after
   deserialize. The resulting YAML should be byte-identical to the source's:

   ```bash
   curl -X POST https://target.example.com/Admin/Api/Serialize \
     -H "Authorization: Bearer CLD.your-target-api-key"

   diff -r \
     serialize-root/replace/_sql/EcomOrderFlow/ \
     /path/to/target-dw-host/Files/System/Serializer/SerializeRoot/replace/_sql/EcomOrderFlow/
   # empty output = round-trip clean
   ```

2. **Compare row counts.** SQL-level sanity:

   ```sql
   -- Source
   SELECT COUNT(*) FROM EcomOrderFlow;

   -- Target
   SELECT COUNT(*) FROM EcomOrderFlow;
   -- Both counts should match.
   ```

If counts match and `diff` returns empty, the predicate is stable across
serialize → commit → deserialize → serialize.

## What next

- **Expand the config.** Start with the shipped starter config at
  `src/Truvio.Commerce.Serializer/Configuration/swift-starter.json` — a Replace-mode
  site-framework Content predicate plus the commerce-framework SqlTable predicates
  (countries, currencies, languages, VAT, shops, payments, shippings, order flow,
  URL paths), and Merge-mode predicates for the customer-owned content surfaces and
  the starter catalog. Trim to what your project needs.
- **Start from ready-made content.** The
  [Truvio.Commerce.Distribution](https://github.com/justdynamics/Truvio.Commerce.Distribution)
  ships gate-proven editions (`base-only`, `swift-demo`, `headless-demo`) as
  versioned layers you can clone and deserialize instead of capturing your own.
- **Turn on strict mode for CI.** Flip `"strictMode": true` in config, or pass
  `?strictMode=true` on the API call, so any warning fails the run. See
  [`strict-mode.md`](strict-mode.md).
- **Wire a pipeline.** [`cicd.md`](cicd.md) has complete GitHub Actions, Azure
  DevOps, and GitLab CI examples including the Replace/Merge split.
- **Understand the mental model.** [`concepts.md`](concepts.md) covers predicates,
  GUID identity, folder layout, and the three-pass link resolution.

## See also

- [Concepts](concepts.md) — predicates, GUID identity, folder layout
- [Configuration](configuration.md) — every config key and admin UI screen
- [CI/CD integration](cicd.md) — pipeline examples for three providers
- [Troubleshooting](troubleshooting.md) — common errors and remedies
