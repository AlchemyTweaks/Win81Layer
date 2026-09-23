# Contributing

Thanks for your interest. This guide explains how to report a problem and how to
send a change in a safe and useful way.

## Report a bug

Open an issue and include:

- Your Windows version and edition (for example Windows 11 23H2, 64 bit)
- What you did, step by step
- What you expected to happen
- What happened instead
- A screenshot if it helps
- The log file if you have one. The app writes a log under
  `%LOCALAPPDATA%\Win81Layer`. Remove anything private before you attach it.

One issue per bug is easier to track. Search the open issues first so you do not
file a duplicate.

## Build and run for development

See the README for the exact commands. In short:

```
dotnet build src/Win81Layer/Win81Layer.csproj -c Release
dotnet run --project src/Win81Layer/Win81Layer.csproj -c Release
```

While testing, you can close the shell overlay with
`Ctrl + Alt + Shift + Backspace`.

## Send a change

1. Fork the repository and create a branch for your change.
2. Keep the change small and focused. One fix or one feature per pull request.
3. Make sure the project still builds with no errors before you open the pull
   request.
4. Describe what you changed and why. If it fixes an issue, link the issue.
5. Match the style of the code around your change.

Small fixes are welcome without asking first. For a large change, open an issue
to discuss it before you write a lot of code, so the work is not wasted.

## Safety rules

This project is a desktop shell, so a few rules keep it safe for everyone.

- Do not commit secrets. No API keys, tokens, passwords or OAuth client secrets.
  These belong in local files that are not tracked, not in the repository.
- Do not commit Microsoft material. Icons, cursors, themes, wallpapers and
  system DLLs from Windows are copyrighted and must stay out of the repository.
  The `assets` folder is ignored by git for this reason.
- Keep changes reversible and per user. Do not add code that deletes or replaces
  system files, and be careful with the registry and with anything that runs at
  startup.
- Test that the app still starts and that you can exit it with the hotkey above.
- Do not add large binaries. Keep the repository to source code.

## Code style

The code was produced by decompiling and cleaning an earlier build, so you will
see some compiler generated names and comments. New code does not need to copy
that. Write clear, normal C#, and keep it close to the style of the file you are
editing.

## Security issues

Do not open a public issue for a security problem. See
[SECURITY.md](SECURITY.md).
