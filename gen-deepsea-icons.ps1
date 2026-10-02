# Генератор иконок Deep Sea для OctaPanel — вызывает python+PIL, пишет PNG + base64 для встраивания
$py = @'
from PIL import Image, ImageDraw
import base64, io, os

W = H = 128
FG = (255, 255, 255, 255)
DIM = (215, 215, 215, 255)  # чуть тусклее — «второй план» в силуэте

def rect(d, x0, y0, x1, y1, c=FG):
    d.rectangle((x0, y0, x1, y1), fill=c)

# ── ПРИЗРАЧНЫЙ КОРАБЛЬ: парусник силуэтом ──
img = Image.new('RGBA', (W, H), (0, 0, 0, 0)); d = ImageDraw.Draw(img)
# корпус (сужающийся кубик)
d.polygon([(12, 88), (116, 88), (104, 108), (24, 108)], fill=FG)
# носовой бушприт
d.polygon([(116, 88), (127, 80), (118, 96)], fill=FG)
# кормовая надстройка
rect(d, 16, 78, 26, 88)
# мачты: фок (передняя), грот (главная), бизань (кормовая)
rect(d, 30, 42, 34, 88)
rect(d, 56, 18, 60, 88)
rect(d, 86, 32, 90, 88)
# паруса — треугольные, «потрёпанные»
d.polygon([(28, 44), (48, 74), (28, 74)], fill=FG)            # фок слева
d.polygon([(36, 44), (50, 76), (36, 76)], fill=DIM)
d.polygon([(54, 20), (78, 70), (54, 70)], fill=FG)            # грот справа
d.polygon([(62, 20), (46, 68), (62, 68)], fill=DIM)           # грот слева
d.polygon([(84, 34), (66, 68), (84, 68)], fill=FG)            # бизань слева
d.polygon([(92, 34), (110, 72), (92, 72)], fill=FG)           # бизань справа
# волны под корпусом
d.ellipse((16, 104, 34, 122), fill=FG)
d.ellipse((94, 104, 112, 122), fill=FG)
b = io.BytesIO(); img.save(b, 'PNG')
ghost_b64 = base64.b64encode(b.getvalue()).decode()
img.save('icon-ghostship.png')

# ── ПЛАВУЧИЙ ГОРОД: платформа на сваях ──
img = Image.new('RGBA', (W, H), (0, 0, 0, 0)); d = ImageDraw.Draw(img)
# башни разной высоты
rect(d, 28, 34, 42, 78)
rect(d, 48, 20, 64, 78)
rect(d, 70, 42, 86, 78)
rect(d, 90, 50, 102, 78)
# антенна и купол-фара на главной башне
rect(d, 54, 10, 58, 20)
d.polygon([(48, 20), (64, 20), (56, 12)], fill=FG)
# палуба
rect(d, 18, 78, 110, 90)
# опоры-понтоны
rect(d, 26, 90, 40, 114)
rect(d, 58, 90, 72, 114)
rect(d, 88, 90, 102, 114)
# волны
d.ellipse((10, 108, 36, 130), fill=FG)
d.ellipse((92, 108, 118, 130), fill=FG)
b = io.BytesIO(); img.save(b, 'PNG')
city_b64 = base64.b64encode(b.getvalue()).decode()
img.save('icon-floatingcity.png')

with open('icons-b64.txt', 'w', encoding='utf-8') as f:
    f.write('GHOST:\n' + ghost_b64 + '\n\nCITY:\n' + city_b64 + '\n')
print('ghost', len(ghost_b64), 'city', len(city_b64))
'@
python -c $py