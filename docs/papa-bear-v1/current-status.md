# Current release and roadmap status

Status checked against local first-parent history, GitHub pull requests,
GitHub Actions, application version metadata and the active Papa Bear
specifications on 2026-07-23.

## Current source baseline

| Field | Current value |
| --- | --- |
| Application version | `0.9.1` |
| Baseline name | Robust Context-on-Demand |
| Branch | `main` |
| Integration commit | `d7269ad` |
| GitHub pull request | None for the post-0.8 integration |
| Git tag | None |
| Published GitHub Release | None |
| Windows CI | Passed for `d7269ad` |
| Deterministic desktop tests | 320 passing at this documentation audit |
| Live Arma acceptance | Pending for the complete 0.9.1 baseline |

“Current source baseline” does not mean “published GitHub Release” or “live
accepted.” Those states are recorded separately and must not be inferred from
the assembly version.

## Recorded release and acceptance history

| Baseline | Repository status | Live acceptance recorded |
| --- | --- | --- |
| 0.1–0.2 | Foundation and duplex environment query merged | No complete release-level live gate recorded |
| 0.3 / Milestone 1 | Text assistant and stable tool loop merged through PRs #4 and #6 | Yes |
| 0.4 / Milestone 2 | PR #7 merged | Yes |
| 0.5 / Milestone 3 | PR #8 merged | Yes for session, friendly-force and existing tool behavior |
| 0.6 | Development candidate folded into 0.7 | No independent release |
| 0.7 | PR #11 merged | Yes |
| 0.8 | PR #12 merged; Windows CI passed | The repository records the final expanded live matrix as pending |
| 0.8.1 | Main-branch radio hotfix baseline, later folded into 0.9.1 | No independent live gate or published release |
| 0.9.0 | Internal context-on-demand development phase only | Not an independently versioned assembly or release |
| 0.9.1 | Integrated directly into `main`; Windows CI passed | Pending |

PRs #9 and #10 were closed without merge and do not describe the current
product.

## Roadmap boundary

There is no active next-milestone or release roadmap. Superseded roadmap,
milestone and release-plan documents were removed from the repository before
the 0.9.1 integration.

Until a new plan is explicitly approved:

- the active specifications in this directory define the current product;
- `CHANGELOG.md` records completed history only;
- closed pull requests and deleted plans are not implementation authority;
- deferred features remain out of scope.

## Updating this status

Update this file and the root changelog together when any of these changes:

- application version;
- merged integration or release pull request;
- Git tag or published GitHub Release;
- CI result used as release evidence;
- completed live Arma acceptance;
- approved next-milestone specification.
