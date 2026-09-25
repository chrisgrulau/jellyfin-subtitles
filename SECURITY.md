# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's
[private vulnerability reporting](https://github.com/chrisgrulau/jellyfin-subtitles/security/advisories/new) instead.

## Secrets

API keys for cloud speech-to-text services are entered by the user and stored in Jellyfin's plugin configuration on
their server. The plugin never logs them or includes them in alerts. Nothing secret belongs in this repository.

## Files and downloads

The plugin writes only subtitle files (and their provenance records) next to videos, always keeping the original. The
built-in speech-to-text program is only downloaded after an administrator allows it. It comes over HTTPS from this
project's releases only, is checked against a SHA-256 compiled into the plugin before it is ever run (and again every
time it starts), and runs without a shell, with a time limit and low priority. See `docs/DESIGN.md` for the full list.
