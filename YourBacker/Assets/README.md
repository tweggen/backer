# Assets

Place your application icons here.

## Required Files

| File | Platform | Purpose |
|------|----------|---------|
| `YourBacker.icns` | macOS | App icon for dock/Finder |
| `YourBacker.ico` | Windows | App icon for taskbar/explorer |
| `backer-tray.png` | All | Tray icon (32x32 recommended) |

## Creating the macOS .icns file

1. **Create an iconset folder** with the required sizes:
   ```
   YourBacker.iconset/
   ├── icon_16x16.png
   ├── icon_16x16@2x.png      (32x32)
   ├── icon_32x32.png
   ├── icon_32x32@2x.png      (64x64)
   ├── icon_128x128.png
   ├── icon_128x128@2x.png    (256x256)
   ├── icon_256x256.png
   ├── icon_256x256@2x.png    (512x512)
   ├── icon_512x512.png
   └── icon_512x512@2x.png    (1024x1024)
   ```

2. **Convert to .icns**:
   ```bash
   iconutil -c icns YourBacker.iconset -o YourBacker.icns
   ```

3. **Copy the .icns file** to this Assets folder.

### Quick generation from a single high-res PNG:
```bash
# Starting with a 1024x1024 source image
mkdir YourBacker.iconset

sips -z 16 16     source.png --out YourBacker.iconset/icon_16x16.png
sips -z 32 32     source.png --out YourBacker.iconset/icon_16x16@2x.png
sips -z 32 32     source.png --out YourBacker.iconset/icon_32x32.png
sips -z 64 64     source.png --out YourBacker.iconset/icon_32x32@2x.png
sips -z 128 128   source.png --out YourBacker.iconset/icon_128x128.png
sips -z 256 256   source.png --out YourBacker.iconset/icon_128x128@2x.png
sips -z 256 256   source.png --out YourBacker.iconset/icon_256x256.png
sips -z 512 512   source.png --out YourBacker.iconset/icon_256x256@2x.png
sips -z 512 512   source.png --out YourBacker.iconset/icon_512x512.png
sips -z 1024 1024 source.png --out YourBacker.iconset/icon_512x512@2x.png

iconutil -c icns YourBacker.iconset -o Assets/YourBacker.icns
rm -rf YourBacker.iconset
```

## Creating the Windows .ico file

Use ImageMagick or an online converter:
```bash
# With ImageMagick
convert source.png -define icon:auto-resize=256,128,64,48,32,16 YourBacker.ico
```

## Tray Icon Notes

- **macOS menu bar**: Prefers "template" images (white/black on transparent, 18×18 @1x)
- **Windows system tray**: 16×16 or 32×32
- **Linux**: 22×22 or 24×24

The current fallback code creates a simple colored square if no icon file is found.
