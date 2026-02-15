# dump2ufs-gui

A powerful, modern Windows GUI for **dump2ufs**. This application simplifies converting PS5 game dumps into optimized UFS2 filesystem images (`.ffpkg`) with a focus on ease of use and automated batch processing.

![Demo](demo.gif)

## Key Features

- **Batch Processing**: Drag and drop multiple game folders to queue them. The app will process them sequentially while you do other things.
- **Fully Standalone**: UFS2Tool 3.0 is integrated directly into the executable. No separate downloads or manual configuration required on first launch.
- **Smart Update Checker**: Keep your core tool up to date. Check for the latest UFS2Tool releases from GitHub directly within the app and update with one click.
- **Reversion System**: Easily roll back to the stable integrated v3.0 version at any time if a newer update causes issues.
- **Self-Healing Infrastructure**: At every launch, the app performs a health check on its internal components and automatically repairs itself if files are missing or corrupted.
- **Auto-Detection**: Automatically parses game metadata (Title Name, Title ID, Version) to generate optimized labels and filenames.
- **Optimization**: Efficiently calculates the best block sizes to produce the smallest possible images.
- **Output Flexibility**: Support for both Fake FPKGs (`.ffpkg`) and raw PFS Images (`.img`).
- **Dump Installer Compatibility**: Optional one-click wrapping for EchoStretch's [Dump Installer](https://github.com/EchoStretch/dump_installer), including automated `sce_sys` metadata injection.

> ⚠️ **Prerequisite**: This tool requires extracted (foldered) PS5 game dumps. Compressed archives (`.rar`, `.zip`, `.7z`, etc.) are **not** supported — please extract your dump first.

## Usage

1. **Launch**: Run `dump2ufs-gui.exe`. On the first run, it will automatically initialize its internal tools.
2. **Queue Games**: Drag and drop one or more PS5 game folders into the main window.
3. **Configure Output**: (Optional) Set your preferred output directory.
4. **Convert**: Click the "Convert" button. You can monitor progress and logs in the side panel.
5. **Manage Tools**: Use the version display in the bottom-right to check for updates or revert versions.

## Building from Source

To build the compressed, single-file executable:

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Clone the repository.
3. Run the following command:

```powershell
dotnet publish -c Release
```

The standalone executable will be generated at:  
`bin/Release/net8.0-windows/win-x64/publish/dump2ufs-gui.exe`

## ⚓ EchoStretch's Dump Installer Compatibility

The "Dump Installer Compatible" toggle prepares your `.ffpkg` files specifically for use with EchoStretch's [Dump Installer](https://github.com/EchoStretch/dump_installer). This feature automatically bundles the required `sce_sys` metadata into a compatibility trailer at the end of the file.

**Setup**: To use this feature effectively, you must configure your PS5 environment according to the instructions provided in the [Dump Installer repository](https://github.com/EchoStretch/dump_installer).

## Credits

- **JD Ros**: GUI Architecture and Implementation.
- **dump2ufs**: Core conversion logic.
- **SvenGDK**: For [UFS2Tool](https://github.com/SvenGDK/UFS2Tool), the engine powering the filesystem creation.

---
*Disclaimer: This tool is a GUI wrapper for established community tools. I am only responsible for the frontend and automation logic.*
