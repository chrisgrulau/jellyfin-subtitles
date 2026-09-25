# Contributing

Issues and pull requests are welcome.

- Target `main`; keep pull requests focused.
- Match `.editorconfig`; the build treats warnings as errors with all analysers enabled.
- Add unit tests for scoring, parsing and synchronisation logic; they are pure functions and easy to test.
- Never commit media, subtitle files from real libraries, library paths or credentials. Test fixtures use invented text.

```bash
dotnet build -c Debug
dotnet test --solution Jellyfin.Plugin.Subtitles.sln
```
