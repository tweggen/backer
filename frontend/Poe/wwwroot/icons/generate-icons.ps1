# Generate PWA icons from SVG
# Requires ImageMagick to be installed: winget install ImageMagick.ImageMagick

$sizes = @(72, 96, 128, 144, 152, 192, 384, 512)
$sourceIcon = "icon.svg"

foreach ($size in $sizes) {
    $outputFile = "icon-${size}x${size}.png"
    Write-Host "Generating $outputFile..."
    magick convert $sourceIcon -resize "${size}x${size}" $outputFile
}

# Copy 192x192 as apple-touch-icon
Copy-Item "icon-192x192.png" "../apple-touch-icon.png"

Write-Host "Done! Generated all PWA icons."
