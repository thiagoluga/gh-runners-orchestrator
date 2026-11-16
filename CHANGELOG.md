# Changelog

Notable changes, newest first. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/) from the first release.

## Unreleased

### Added

- Optional explicit account/installation configuration sharing the same global capacity;
  legacy single-owner configuration remains supported. Tokens, caches, rate limits and
  failures are isolated by installation; cleanup validates qualified identity and labels.
  Activation, migration and rollback are documented in `docs/multi-account.md`.

- Design: architecture, ADRs 0001–0008, plan and roadmap.
- Repository files: contributing guide, code of conduct, security policy, issue forms and
  pull request template.
