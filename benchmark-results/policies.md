| scenario | policy | rolled back | promoted | detection p50 / p95 (s after onset) | canary errors served after onset (p50) |
|---|---|---:|---:|---:|---:|
| healthy | naive threshold (5%, any n, 1 breach) | 4.3% | 95.7% | - | 0 |
| healthy | guarded threshold (5%, n>=100, 2 breaches) | 0% | 100% | - | 0 |
| healthy | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 4.7% | 95.3% | - | 0 |
| healthy | combined (guarded AND relative) | 0% | 100% | - | 0 |
| healthy-low-traffic | naive threshold (5%, any n, 1 breach) | 26.3% | 73.7% | - | 0 |
| healthy-low-traffic | guarded threshold (5%, n>=100, 2 breaches) | 0% | 0% | - | 0 |
| healthy-low-traffic | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 0% | 0% | - | 0 |
| healthy-low-traffic | combined (guarded AND relative) | 0% | 0% | - | 0 |
| shared-incident | naive threshold (5%, any n, 1 breach) | 100% | 0% | - | 0 |
| shared-incident | guarded threshold (5%, n>=100, 2 breaches) | 100% | 0% | - | 0 |
| shared-incident | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 3.3% | 96.7% | - | 0 |
| shared-incident | combined (guarded AND relative) | 0.3% | 99.7% | - | 0 |
| subtle-regression | naive threshold (5%, any n, 1 breach) | 46.7% | 53.3% | 10 / 95 | 923 |
| subtle-regression | guarded threshold (5%, n>=100, 2 breaches) | 13.7% | 86.3% | 35 / 115 | 957 |
| subtle-regression | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 100% | 0% | 60 / 170 | 11 |
| subtle-regression | combined (guarded AND relative) | 13.3% | 86.7% | 35 / 115 | 957 |
| bad-canary | naive threshold (5%, any n, 1 breach) | 100% | 0% | 30 / 35 | 73 |
| bad-canary | guarded threshold (5%, n>=100, 2 breaches) | 100% | 0% | 35 / 40 | 86 |
| bad-canary | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 100% | 0% | 15 / 25 | 44 |
| bad-canary | combined (guarded AND relative) | 100% | 0% | 35 / 40 | 86 |
| broken-canary | naive threshold (5%, any n, 1 breach) | 100% | 0% | 5 / 10 | 75 |
| broken-canary | guarded threshold (5%, n>=100, 2 breaches) | 100% | 0% | 10 / 15 | 138 |
| broken-canary | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 100% | 0% | 10 / 10 | 133 |
| broken-canary | combined (guarded AND relative) | 100% | 0% | 10 / 15 | 138 |
