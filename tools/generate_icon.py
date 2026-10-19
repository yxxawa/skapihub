"""Generate the original skapihub icon; Pillow is a development-only dependency."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
SCALE = 12
im = Image.new("RGBA", (40*SCALE, 40*SCALE))
draw = ImageDraw.Draw(im)
draw.rounded_rectangle((0, 0, 40*SCALE-1, 40*SCALE-1), radius=12*SCALE, fill="#6256d9")
def segment(points):
    draw.line([(round(x*SCALE), round(y*SCALE)) for x,y in points], fill="white", width=3*SCALE, joint="curve")
    for x,y in (points[0], points[-1]):
        draw.ellipse(((x-1.5)*SCALE,(y-1.5)*SCALE,(x+1.5)*SCALE,(y+1.5)*SCALE), fill="white")
def curve(a,b,c,d):
    return [((1-t)**3*a[0]+3*(1-t)**2*t*b[0]+3*(1-t)*t*t*c[0]+t**3*d[0], (1-t)**3*a[1]+3*(1-t)**2*t*b[1]+3*(1-t)*t*t*c[1]+t**3*d[1]) for t in [i/60 for i in range(61)]]
segment([(11,10),(23,10)] + curve((23,10),(30,10),(30,20),(23,20)) + [(17,20)] + curve((17,20),(10,20),(10,30),(17,30)))
segment([(29,30),(17,30)] + curve((17,30),(10,30),(10,20),(17,20)) + [(23,20)] + curve((23,20),(30,20),(30,10),(23,10)))
for x,y in [(11,10),(29,30)]:
    draw.ellipse(((x-2.5)*SCALE,(y-2.5)*SCALE,(x+2.5)*SCALE,(y+2.5)*SCALE), fill="white")
out = ROOT / "src/Skapihub.Desktop/Assets"
out.mkdir(parents=True, exist_ok=True)
im.save(out / "skapihub.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
print(out / "skapihub.ico")
