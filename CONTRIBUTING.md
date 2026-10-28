# Contributing

Open an issue describing the problem, expected behavior and minimal reproduction. Avoid personal or workplace recordings and use synthetic data.

Keep pull requests focused. Explain the resulting behavior, relevant tradeoffs and validation. Run the test project and build each affected application. Android changes also need device testing when audio, lifecycle or permissions change.

Document public contracts and non-obvious invariants. Add tests for meaningful regressions rather than duplicating implementation details.

Use Conventional Commits, for example feat(android): improve recording controls. Use your own Git identity. Never commit credentials, databases, audio, model weights, generated build directories or signing keys.

Development history should reflect actual changes; do not invent historical dates or fabricate earlier pull requests.
