import AppKit
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

// A new vector drawing, with no generated raster input or texture processing.
// Run from the project root: swift Tools/ArtBake/ReadyBadge/render.swift
let size = 1024
let sampleScale = 4
let center = CGFloat(size) / 2
let outerRadius: CGFloat = 428
let faceRadius: CGFloat = 400
let strokeWidth: CGFloat = 80
let checkPoints: [CGPoint] = [
    CGPoint(x: 304, y: 513),
    CGPoint(x: 453, y: 655),
    CGPoint(x: 729, y: 374)
]
let goldTop = "FFE5A0"
let goldBottom = "EAB344"
let blueTop = "57B3F4"
let blueBottom = "2464D1"
let ivory = "FFFEF5"

func color(_ hex: String) -> CGColor {
    let rgb = UInt32(hex, radix: 16)!
    return CGColor(srgbRed: CGFloat((rgb >> 16) & 255) / 255,
                   green: CGFloat((rgb >> 8) & 255) / 255,
                   blue: CGFloat(rgb & 255) / 255, alpha: 1)
}

let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!
func makeContext(_ pixels: Int) -> CGContext {
    let context = CGContext(data: nil, width: pixels, height: pixels,
        bitsPerComponent: 8, bytesPerRow: pixels * 4, space: colorSpace,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    context.setAllowsAntialiasing(true)
    context.setShouldAntialias(true)
    return context
}

let context = makeContext(size * sampleScale)
context.scaleBy(x: CGFloat(sampleScale), y: CGFloat(sampleScale))
context.translateBy(x: 0, y: CGFloat(size))
context.scaleBy(x: 1, y: -1)

func drawDisc(radius: CGFloat, top: String, bottom: String) {
    context.saveGState()
    context.addEllipse(in: CGRect(x: center - radius, y: center - radius,
                                 width: radius * 2, height: radius * 2))
    context.clip()
    let gradient = CGGradient(colorsSpace: colorSpace,
        colors: [color(top), color(bottom)] as CFArray, locations: [0, 1])!
    context.drawLinearGradient(gradient,
        start: CGPoint(x: center, y: center - radius),
        end: CGPoint(x: center, y: center + radius), options: [])
    context.restoreGState()
}

drawDisc(radius: outerRadius, top: goldTop, bottom: goldBottom)
drawDisc(radius: faceRadius, top: blueTop, bottom: blueBottom)
context.setStrokeColor(color(ivory))
context.setLineWidth(strokeWidth)
context.setLineCap(.round)
context.setLineJoin(.round)
context.move(to: checkPoints[0])
for point in checkPoints.dropFirst() { context.addLine(to: point) }
context.strokePath()

let output = makeContext(size)
output.interpolationQuality = .high
output.draw(context.makeImage()!, in: CGRect(x: 0, y: 0, width: size, height: size))
let project = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let asset = project.appendingPathComponent(
    "Assets/LiquidSort/RoyalGlassLab/Art/CheckBadge_Royal_CleanBlue_v4.png")
let destination = CGImageDestinationCreateWithURL(asset as CFURL,
    UTType.png.identifier as CFString, 1, nil)!
CGImageDestinationAddImage(destination, output.makeImage()!, nil)
guard CGImageDestinationFinalize(destination) else { fatalError("PNG export failed") }

let points = checkPoints.map { "\(Int($0.x)),\(Int($0.y))" }.joined(separator: " ")
let svg = """
<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024">
  <defs>
    <linearGradient id="gold" x1="0" y1="0" x2="0" y2="1">
      <stop stop-color="#\(goldTop)"/><stop offset="1" stop-color="#\(goldBottom)"/>
    </linearGradient>
    <linearGradient id="blue" x1="0" y1="0" x2="0" y2="1">
      <stop stop-color="#\(blueTop)"/><stop offset="1" stop-color="#\(blueBottom)"/>
    </linearGradient>
  </defs>
  <circle cx="512" cy="512" r="\(Int(outerRadius))" fill="url(#gold)"/>
  <circle cx="512" cy="512" r="\(Int(faceRadius))" fill="url(#blue)"/>
  <polyline points="\(points)" fill="none" stroke="#\(ivory)"
    stroke-width="\(Int(strokeWidth))" stroke-linecap="round" stroke-linejoin="round"/>
</svg>

"""
let svgURL = project.appendingPathComponent("Tools/ArtBake/ReadyBadge/CheckBadge_Royal_CleanBlue.svg")
try svg.write(to: svgURL, atomically: true, encoding: .utf8)
print("Exported clean vector badge: \(asset.path)")
print("Editable source: \(svgURL.path)")
