# LabAcacia.NPS.Conformance

Small CI-friendly conformance primitives for NPS Node self-certification.

```csharp
var manifest = NpsConformanceManifest.Create(
    profile: NpsConformanceProfiles.NodeL1,
    iutName: "my-node",
    iutVersion: "0.1.0",
    iutNid: "urn:nps:node:example.test:node-1",
    peerName: "nps-dotnet-reference",
    peerVersion: "1.0.0-alpha.18",
    results: myCaseResults);

var validation = NpsConformanceValidator.Validate(manifest);
if (!validation.Valid) throw new InvalidOperationException(validation.Message);
```

The package ships the complete L1 v0.1 and L2 v0.7 case catalogs, a manifest
model, and validation helpers. L2 validation requires the current AaaS
L2-01..L2-05 cases and a written SHOULD exception for L2-06/07 `na` results.
It also enforces the suite's conditional
applicability rules: TLS, Bridge-inbound, Anchor-HA, and Registry-HA families
must each be entirely `pass` or entirely `na`, and multi-Anchor HA is mutually
exclusive with the single-Anchor compatibility case. Manifest versions and
summary totals are validated as well as case IDs and results.
