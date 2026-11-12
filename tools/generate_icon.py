import math
from PIL import Image, ImageDraw, ImageFilter

def create_earcue_icon(output_ico_path, output_png_path):
    # Render at 1024x1024 for 4x supersampling down to 256
    size = 1024
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # 1. Background Squircle with subtle gradient simulation
    pad = 48
    radius = 220
    # Base background: deep dark blue
    draw.rounded_rectangle(
        [pad, pad, size - pad, size - pad],
        radius=radius,
        fill=(14, 21, 33, 255),
        outline=(56, 189, 248, 120),
        width=12
    )

    # Inner subtle glow rectangle
    inner_pad = pad + 18
    draw.rounded_rectangle(
        [inner_pad, inner_pad, size - inner_pad, size - inner_pad],
        radius=radius - 12,
        outline=(30, 41, 59, 180),
        width=6
    )

    # 2. Main Ear Graphic (Stylized modern minimalist ear curve)
    # Outer ear curve (arc & bezier-like polyline)
    ear_color = (56, 189, 248, 255) # Electric Sky Blue
    inner_ear_color = (0, 240, 255, 230) # Vibrant Cyan
    wave_color_1 = (52, 211, 153, 240) # Emerald/Mint
    wave_color_2 = (56, 189, 248, 200) # Cyan
    wave_color_3 = (125, 211, 252, 160) # Light blue

    # Ear lobe and curve
    # Left outer contour
    ear_points = []
    # Center of ear: around (420, 512)
    cx, cy = 410, 500
    
    # Outer arc of the ear
    for angle_deg in range(-120, 110, 2):
        rad = math.radians(angle_deg)
        r = 210
        x = cx + r * math.cos(rad)
        y = cy + r * math.sin(rad) * 1.25
        ear_points.append((x, y))

    # Inner turn to ear canal
    for angle_deg in range(110, 260, 4):
        rad = math.radians(angle_deg)
        r = 100
        x = (cx + 30) + r * math.cos(rad)
        y = (cy + 110) + r * math.sin(rad) * 1.1
        ear_points.append((x, y))

    draw.line(ear_points, fill=ear_color, width=44, joint="curve")

    # Inner ear detail (helix fold)
    inner_points = []
    for angle_deg in range(-100, 70, 3):
        rad = math.radians(angle_deg)
        r = 120
        x = cx + r * math.cos(rad)
        y = (cy - 10) + r * math.sin(rad) * 1.15
        inner_points.append((x, y))
    draw.line(inner_points, fill=inner_ear_color, width=30, joint="curve")

    # Tragus / Canal dot
    draw.ellipse([cx - 25, cy + 60, cx + 35, cy + 120], fill=inner_ear_color)

    # 3. Sound cue waves / acoustic signal (Whisper / Cue arcs on the right)
    # Arc 1
    arc_center = (cx + 40, cy - 20)
    for i, (r_val, col, w) in enumerate([
        (260, wave_color_1, 34),
        (350, wave_color_2, 30),
        (440, wave_color_3, 26)
    ]):
        bbox = [
            arc_center[0] - r_val,
            arc_center[1] - r_val * 1.1,
            arc_center[0] + r_val,
            arc_center[1] + r_val * 1.1
        ]
        draw.arc(bbox, start=-40, end=40, fill=col, width=w)

    # 4. Small cue / prompt star spark at upper right (signifying AI prompt)
    sx, sy = 740, 320
    star_color = (0, 240, 255, 255)
    draw.ellipse([sx - 16, sy - 16, sx + 16, sy + 16], fill=star_color)
    draw.line([(sx - 40, sy), (sx + 40, sy)], fill=star_color, width=8)
    draw.line([(sx, sy - 40), (sx, sy + 40)], fill=star_color, width=8)

    # Downsample with Lanczos high quality filter
    img_256 = img.resize((256, 256), Image.Resampling.LANCZOS)
    img_256.save(output_png_path, "PNG")

    # Generate multi-size ICO
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    img.save(output_ico_path, format="ICO", sizes=sizes)
    print(f"Generated icon: {output_ico_path} and {output_png_path}")

if __name__ == "__main__":
    create_earcue_icon("src/Earcue.App/Assets/app.ico", "src/Earcue.App/Assets/app.png")
