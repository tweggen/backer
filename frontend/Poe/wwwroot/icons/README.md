# PWA Icons

This directory contains the icons for the Backer PWA (Progressive Web App).

## Generating PNG Icons from SVG

The `icon.svg` file is the source icon. You need to generate PNG versions in the following sizes:

- 72x72
- 96x96
- 128x128
- 144x144
- 152x152
- 192x192
- 384x384
- 512x512

### Option 1: Using ImageMagick (Command Line)

```bash
# Install ImageMagick if not already installed
# Windows: winget install ImageMagick.ImageMagick
# macOS: brew install imagemagick
# Linux: sudo apt install imagemagick

# Generate all sizes
for size in 72 96 128 144 152 192 384 512; do
    magick convert icon.svg -resize ${size}x${size} icon-${size}x${size}.png
done
```

### Option 2: Using Inkscape (Command Line)

```bash
for size in 72 96 128 144 152 192 384 512; do
    inkscape icon.svg --export-type=png --export-filename=icon-${size}x${size}.png -w $size -h $size
done
```

### Option 3: Online Tools

1. Go to https://realfavicongenerator.net/
2. Upload the `icon.svg` file
3. Download the generated icon pack
4. Extract and copy the PNG files here

### Option 4: Using a Node.js Script

```bash
npm install sharp
```

```javascript
const sharp = require('sharp');
const sizes = [72, 96, 128, 144, 152, 192, 384, 512];

sizes.forEach(size => {
    sharp('icon.svg')
        .resize(size, size)
        .png()
        .toFile(`icon-${size}x${size}.png`);
});
```

## Required Files

After generating, you should have these files:

- `icon.svg` (source)
- `icon-72x72.png`
- `icon-96x96.png`
- `icon-128x128.png`
- `icon-144x144.png`
- `icon-152x152.png`
- `icon-192x192.png`
- `icon-384x384.png`
- `icon-512x512.png`

## Apple Touch Icon

For iOS devices, also copy `icon-192x192.png` as `apple-touch-icon.png` in the wwwroot folder:

```bash
cp icon-192x192.png ../apple-touch-icon.png
```
