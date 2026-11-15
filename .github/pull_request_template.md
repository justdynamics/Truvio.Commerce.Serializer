## What and why


## Checks run

- [ ] `dotnet test tests/Truvio.Commerce.Serializer.Tests --configuration Release`: N passed, 0 failed

### Release PRs that touch `src/` (docs/releasing.md)

- [ ] Live strict redelivery: `pwsh tools/e2e/Test-Redelivery.ps1` on a scratch host
  - Disposition:
  - Engine productVersion / sha256:
  - Host Dynamicweb version:
  - Per-delivery totals and ignored-column changes:
- [ ] Not needed: this release changes no file under `src/`
