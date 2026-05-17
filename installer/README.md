# 安装包版本和界面

MSI 使用 `major.minor.patch` 版本号。

- `build-msi.bat` 会显示固定选择菜单，让用户选择本次打包属于哪类改动。
- `build-msi.bat patch` 使用 `installer/version.txt` 中的版本构建，成功后递增 patch。
- `build-msi.bat minor` 构建下一个 minor 版本，并把 patch 重置为 `0`，成功后把下一次版本推进到 `.1`。
- `build-msi.bat major` 构建下一个 major 版本，并把 minor 和 patch 重置为 `0`，成功后把下一次版本推进到 `.0.1`。
- 脚本不接受手动输入版本号。
- 构建失败不会推进 `installer/version.txt`。

安装界面使用 WiX `WixUI_InstallDir` 向导集。用户可以确认或修改安装目录，默认安装到 64 位 `Program Files` 路径，然后进入确认页、安装进度页，最后显示完成页或失败页。

如果希望安装流程更接近传统安装向导和截图中的样式，使用 `build-setup.bat` 生成 NSIS `.exe` 安装器。它包含安装目录页、详细安装进度日志、完成页的运行程序复选框，并写入控制面板卸载项。

NSIS 安装器会在应用安装目录下写入 `uninstall.exe`，开始菜单卸载快捷方式和控制面板卸载项都会指向这个文件。

示例：

```bat
build-setup.bat
build-setup.bat patch
build-msi.bat
build-msi.bat minor
build-msi.bat major
build-msi.bat patch win-x64 Release
```
