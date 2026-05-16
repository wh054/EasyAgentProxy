# Installer Versioning

The MSI uses `major.minor.patch` versions.

- `build-msi.bat` shows a fixed choice menu for this package's change type.
- `build-msi.bat patch` builds the version stored in `installer/version.txt`, then advances patch by one after a successful build.
- `build-msi.bat minor` builds the next minor release, resets patch to `0`, then advances the next version to `.1`.
- `build-msi.bat major` builds the next major release, resets minor and patch to `0`, then advances the next version to `.0.1`.
- The script never accepts a manually typed version number.
- Failed builds do not advance `installer/version.txt`.

Examples:

```bat
build-msi.bat
build-msi.bat minor
build-msi.bat major
build-msi.bat patch win-x64 Release
```
