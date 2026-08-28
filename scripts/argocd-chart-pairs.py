#!/usr/bin/env python3
"""Emit the (chart, values) pairs the Argo CD Applications actually deploy.

The per-service charts under kubernetes/helm/ are what Argo CD ships — the umbrella
`building-os` chart is not. CI validates those pairs, and reading them out of
argocd/apps/*.yaml rather than repeating them in a workflow keeps the check honest:
add an Application and it is covered without anyone remembering to update a list.
Two places to forget is how argocd/values overlays ended up pointing at images that
were never published (#306).

Output: one `release<TAB>chart<TAB>valuesFile<TAB>manifest` line per Application.

The release name is the Application's own metadata.name, which is what Argo CD uses
when it renders the chart. Rendering under a different release name would validate
manifests nobody deploys — resource names are built from it, and their 63-character
limit is checked against the real one.
Exits non-zero if a manifest does not declare both a chart path and a values file,
so a malformed Application fails CI rather than silently dropping out of coverage.
"""
import glob
import re
import sys


def main() -> int:
    manifests = sorted(glob.glob("argocd/apps/*.yaml"))
    if not manifests:
        print("no Argo CD Application manifests found under argocd/apps/", file=sys.stderr)
        return 1

    for manifest in manifests:
        with open(manifest, encoding="utf-8") as handle:
            source = handle.read()
        release = re.search(r"^  name: (\S+)", source, re.M)
        chart = re.search(r"^    path: (\S+)", source, re.M)
        # valueFiles entries are written relative to the chart directory.
        values = re.search(r"^        - \.\./\.\./(\S+)", source, re.M)
        if not release or not chart or not values:
            print(f"{manifest}: no metadata.name, spec.source.path and/or helm.valueFiles entry",
                  file=sys.stderr)
            return 1
        print(f"{release.group(1)}\t{chart.group(1)}\t{values.group(1)}\t{manifest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
