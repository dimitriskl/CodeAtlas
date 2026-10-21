# Hosted qualification activation proposal

Prepared and locally checked on 21 October 2026 inside the required CodeAtlas.Net directory. The full Release build has zero warnings/errors; 368 .NET and 37 Python tests pass. Exact package payload/notices, isolated local install and explicit upgrade from 0.00.22 all pass. A pack attempt with duplicated notice subdirectories failed and is retained; the final metadata correction repacks the unchanged tested binaries correctly.

Activate the exact `build/qualification.yml` at the Git-root `.github/workflows/qualification.yml`, commit this repair candidate and push `codex/production-roadmap` to the existing public `dimitriskl/CodeAtlas` repository. The push trigger runs qualification on `windows-2025` and `ubuntu-24.04`; manual dispatch remains available. Each build host performs locked restore, Release build, serial tests and payload verification. Separate install jobs have no source checkout and install the same Windows-built package with fresh tool/cache directories and local-only package feeds.

The workflow has read-only repository permissions and pinned action commits. It uploads only the CodeAtlas tool, public fixture, standalone installer smoke and qualification logs/metadata. Private eConnector source, indexes, retrieval questions and local evaluation artifacts are excluded. These jobs qualify a candidate; they do not create a release or upload a NuGet.org package. Publication remains gated on all roadmap acceptance evidence and a final versioned release build.

AGENTS.md requires explicit approval before development in another folder or machine. Activation changes a file outside CodeAtlas.Net and executes CodeAtlas on hosted machines, so both exceptions require user acceptance before action. The MIT license, GitHub Release destination and eventual version 1 publication are already authorized and are not being requested again.

## User decision

On 21 October 2026 the user explicitly answered: "Approve workflow location, branch push and hosted checks." The activated Git-root workflow is an exact copy of the reviewed template. Qualification runs and final publication still require their actual success evidence.
