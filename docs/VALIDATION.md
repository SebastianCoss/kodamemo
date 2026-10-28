# Publication preparation validation

Validated locally on 2026-10-28:

- Web/shared compilation through the test project.
- Seven MSTest tests passed: quotation presence versus user confirmation, stale summaries, Spanish normalization, interrupted WAV repair, missing configuration, an empty initial store and persisted meetings.
- HTTP smoke check passed: anonymous meeting/audio access rejected, login without an antiforgery token rejected, valid cookie login accepted and valid bearer access accepted.

The smoke check used temporary random credentials, an empty database and a separate local port.

The initial restore encountered NU1900. After moving the NuGet HTTP/scratch caches to an accessible development directory, restore with NuGetAudit enabled and all seven tests completed without warnings or reported advisories. This does not replace an independent security review.

Android C# compilation (net11.0-android, Compile target) completed with zero warnings and errors. APK packaging, installation and physical-device validation of this publication copy remain pending; do not treat previous AudioIaLocal device reports as proof for the renamed copy.

The workflow checks web/shared tests only. Android needs separate workload installation and device validation.
