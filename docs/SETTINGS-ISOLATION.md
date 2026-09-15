# Appearance defaults and isolated UI tours

New or unreadable/invalid settings use **Dark** and reduced motion **on**. Valid saved `light`, `dark`, and `system` choices and the saved reduced-motion boolean remain unchanged when reopening. The application loads the preference before constructing its first window. Follow system clears the application override as well as the window override; it does not inherit forced dark.

## Isolate a native tour from personal preferences and recents

Set `SOUNDOFF_SETTINGS_PATH` to a **fully qualified absolute filename** in a dedicated tour directory before launching the process, for example in Git Bash on Windows:

```bash
SOUNDOFF_SETTINGS_PATH='C:/Users/pcuser/source/repos/SoundOff-worktrees/dark-audit/artifacts/tour/settings.json' \
  dotnet src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll
```

Use the final integrated worktree path when touring the integrated build. Choose a fresh directory for a new-install appearance; do not delete an existing user's settings. Settings save on an appearance change, not ordinary opening. `recent-projects.json` lives beside this settings file, so a dedicated directory isolates both preferences and the recent-project list. Create/import only synthetic projects in the tour's artifact directory; this override does not redirect project pickers or constrain filesystem access.

When the variable is absent, the normal path remains `%LOCALAPPDATA%/SoundOff/settings.json`. A configured empty/whitespace value, relative path, drive-relative path, root, or existing directory is rejected instead of silently falling back to personal data. An invalid override is a launch configuration error; unset or correct it before retrying.

`SOUNDOFF_SETTINGS_PATH` is independent of `SOUNDOFF_HOME`: it does **not** relocate, download, modify, or grant access to inference runtimes or model packs. No authentication, permission, or OS appearance setting changes are needed. The headless regression suite checks pre-show application/window themes and repeat startup via fresh settings-store instances; native first-frame appearance and actual OS theme changes remain part of the parent’s serial GUI tour.
