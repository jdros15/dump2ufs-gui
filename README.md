# dump2ufs-gui

A simple Windows GUI wrapper for **dump2ufs**. This tool simplifies the process of converting PS5 game dumps into optimized UFS2 filesystem images (`.ffpkg`) by providing a user-friendly interface.

![Demo](demo.gif)

## Features

- **Drag & Drop**: Easily drag your game dump folders directly into the application.
- **Auto-Detection**: Automatically detects game title, ID, and generates an appropriate label.
- **Optimization**: Tests multiple block sizes to ensure the smallest possible file size (just like the original script).
- **Windows Only**: Built specifically for Windows users.

> ⚠️ **This tool only accepts extracted (foldered) PS5 game dumps.** Compressed archives (`.rar`, `.zip`, `.7z`, etc.) are **not** supported — please extract your dump first.

## Usage

1. **Download** the latest release.
2. **Run** `dump2ufs-gui.exe`.
3. **Drag and drop** your PS5 game folder into the window.
4. **Click "Convert"** and wait for the process to finish.

## Credits

- **JD Ros**: GUI Implementation.
- **dump2ufs**: The core logic and methodology behind the conversion.
- **SvenGDK**: For [UFS2Tool](https://github.com/SvenGDK/UFS2Tool), which powers the filesystem creation.

> **Note**: This tool acts as a frontend. All conversion logic handles by `dump2ufs` behind the scenes.
