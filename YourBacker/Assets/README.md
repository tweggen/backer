# Assets

Place your application icons here:

## Required Icons

### Windows
- `backer-icon.ico` - Windows application icon (256x256 recommended, with multiple sizes embedded)

### macOS
- `backer-icon.icns` - macOS application icon bundle

### Linux / Cross-platform
- `backer-icon.png` - PNG icon (256x256 recommended)
- `backer-icon.svg` - Vector icon for scalability

## Tray Icons

The tray icon should be smaller and simpler:
- Windows: 16x16 or 32x32 in the .ico
- macOS: Template images (white on transparent) at 18x18 @1x and 36x36 @2x
- Linux: 22x22 or 24x24 PNG

## Generating Icons

You can use tools like:
- [ImageMagick](https://imagemagick.org/) for conversion
- [icns-generator](https://github.com/nickytonline/icns-generator) for macOS
- Online converters like [ConvertICO](https://convertico.com/)

Example ImageMagick command:
```bash
# Create .ico from PNG
convert icon-256.png -define icon:auto-resize=256,128,64,48,32,16 backer-icon.ico

# Create various sizes for macOS iconset
mkdir backer-icon.iconset
for size in 16 32 128 256 512; do
    convert icon-512.png -resize ${size}x${size} backer-icon.iconset/icon_${size}x${size}.png
    convert icon-512.png -resize $((size*2))x$((size*2)) backer-icon.iconset/icon_${size}x${size}@2x.png
done
iconutil -c icns backer-icon.iconset
```
