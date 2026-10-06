# -*- coding: utf-8 -*-
# Одноразовый патч живого конфига OctaWelcome на тест-сервере:
# чистый контент вкладок 1-10, Tab10 -> "БОССЫ", команда /bosses.
import json, io

CFG = r'D:\Octavian Rust\Octavian_Test_Server\423\server\carbon\configs\OctaWelcome.json'

T = {
1: (" ГЛАВНАЯ", 4, [
 "<size=45><color=#E8B84B>OCTAVIAN</color></size> ",
 "<size=25>ВАЙПЫ <color=#83b8c7>ЕЖЕНЕДЕЛЬНЫЕ</color></size>",
 "<size=25>РЕЙТЫ <color=#83b8c7>x5 ДОБЫЧА</color> | <color=#83b8c7>x5 ЛУТ</color></size> ",
 "<size=25>КАРТА <color=#83b8c7>4200</color></size> ",
 "",
 "<size=15>Сервер в EU. Вайп карт — еженедельно, чертежи — ежемесячно.</size>",
 "<size=15>Вкладки слева: правила, наборы, VIP, боссы и контакты.</size>",
 "",
 "<size=15><color=#83b8c7>Наборы — вкладка НАБОРЫ или /kits • Боссы — вкладка БОССЫ или /bosses</color></size>"]),
2: (" ПРАВИЛА", 4, [
 "<size=40><color=#E8B84B>ПРАВИЛА СЕРВЕРА</color></size>",
 "",
 "<size=17><color=#83b8c7>1.</color> Читы, макросы, багоюз — перманентный бан без предупреждений.</size>",
 "<size=17><color=#83b8c7>2.</color> Уважай игроков и администрацию — токсичность карается.</size>",
 "<size=17><color=#83b8c7>3.</color> Инсайдерство и кража у своей команды запрещены.</size>",
 "<size=17><color=#83b8c7>4.</color> Оффлайн-рейды разрешены — это Rust.</size>",
 "<size=17><color=#83b8c7>5.</color> Реклама сторонних серверов — бан.</size>",
 "",
 "<size=14>Незнание правил не освобождает от ответственности.</size>",
 "<size=14>Споры решает администрация — вкладка АДМИНЫ или ДИСКОРД.</size>"]),
3: ("   НАБОРЫ", 1, [
 "<size=40><color=#E8B84B>НАБОРЫ</color></size>",
 "",
 "<size=17>Список наборов: <color=#83b8c7>/kits</color></size>",
 "",
 "<size=17><color=#83b8c7>STARTER</color> — инструменты и еда на первый день.</size>",
 "<size=17><color=#83b8c7>BUILDER</color> — материалы для быстрой постройки.</size>",
 "<size=17><color=#83b8c7>VIP</color> — расширенные наборы по статусу (вкладка VIP).</size>",
 "",
 "<size=14>Кулдауны видны прямо в /kits.</size>"]),
4: ("   ВАЙПЫ", 4, [
 "<size=40><color=#E8B84B>РАСПИСАНИЕ ВАЙПОВ</color></size>",
 "",
 "<size=22>КАРТА <color=#83b8c7>каждый четверг</color></size>",
 "<size=22>ЧЕРТЕЖИ <color=#83b8c7>первый четверг месяца</color></size>",
 "",
 "<size=17>До вайпа: {wipecountdown}</size>",
 "",
 "<size=14>Вайп карт не трогает чертежи. Точное время — в ДИСКОРД.</size>"]),
5: ("  VIP", 4, [
 "<size=40><color=#E8B84B>VIP СТАТУСЫ</color></size>",
 "",
 "<size=20><color=#83b8c7>BRONZE → SILVER → GOLD → BRILLIANT</color></size>",
 "",
 "<size=16><color=#83b8c7>◆</color> Приоритет входа в очередь</size>",
 "<size=16><color=#83b8c7>◆</color> Ускоренный крафт и печи</size>",
 "<size=16><color=#83b8c7>◆</color> Расширенные наборы /kits</size>",
 "<size=16><color=#83b8c7>◆</color> Увеличенный рюкзак</size>",
 "",
 "<size=14>Оформить — в ДИСКОРД или у администрации.</size>"]),
6: ("  ДИСКОРД", 4, [
 "<size=40><color=#E8B84B>ДИСКОРД</color></size>",
 "",
 "<size=20>Наш дом за пределами острова:</size>",
 "<size=22><color=#83b8c7>discord.gg/octavian</color></size>",
 "",
 "<size=16>Анонсы вайпов, розыгрыши, репорты,</size>",
 "<size=16>поиск тиммейтов и прямая связь с админами.</size>",
 "",
 "<size=14>Нашёл баг? Пиши в #reports — чиним быстро.</size>"]),
7: (" МАГАЗИН", 4, [
 "<size=40><color=#E8B84B>МАГАЗИН</color></size>",
 "",
 "<size=17>Внутриигровой магазин: <color=#83b8c7>/shop</color></size>",
 "",
 "<size=16>RP капает за активность: боссы, ивенты, часы в игре.</size>",
 "<size=16>Трать на оружие, ресурсы, скины и привилегии.</size>",
 "",
 "<size=16>Босс-ивенты — самый жирный источник RP.</size>",
 "<size=16>Смотри вкладку БОССЫ и /bosstimer.</size>"]),
8: (" АДМИНЫ", 4, [
 "<size=40><color=#E8B84B>АДМИНИСТРАЦИЯ</color></size>",
 "",
 "<size=17>Онлайн-админы видны в списке игроков (TAB).</size>",
 "",
 "<size=16>Репорт игрока: <color=#83b8c7>/report <ник> <причина></color></size>",
 "<size=16>Срочно: тикет в ДИСКОРД #support.</size>",
 "",
 "<size=14>Админы не играют на равных и не выдают лут.</size>",
 "<size=14>Жалоба на админа — создателю в ДИСКОРД.</size>"]),
9: (" НОВОСТИ", 4, [
 "<size=40><color=#E8B84B>НОВОСТИ</color></size>",
 "",
 "<size=17><color=#83b8c7>◆</color> Запущена система боссов-зверей — вкладка БОССЫ.</size>",
 "<size=17><color=#83b8c7>◆</color> Обновлены наборы и VIP-привилегии.</size>",
 "<size=17><color=#83b8c7>◆</color> Рейт x5 на добычу и лут активен.</size>",
 "",
 "<size=14>Свежее — в ДИСКОРД #announcements.</size>"]),
10: (" БОССЫ", 4, [
 "<size=40><color=#E8B84B>БОССЫ ОСТРОВА</color></size>",
 "",
 "<size=16>Гигантские звери с лутом и наградами появляются по расписанию.</size>",
 "",
 "<size=16><color=#83b8c7>◆</color> Место спавна — красный маркер на карте.</size>",
 "<size=16><color=#83b8c7>◆</color> Награда достаётся ТОП-дамагеру, не последнему удару.</size>",
 "<size=16><color=#83b8c7>◆</color> Отвлёкся на минуту — босс залечит раны.</size>",
 "",
 "<size=17>Листай страницы → каждый босс со статами, свитой и лутом.</size>",
 "",
 "<size=14>/bosstimer — расписание • /bossinfo — кто жив • /bosslast — киллеры</size>"]),
}

with io.open(CFG, encoding='utf-8-sig') as f:
    c = json.load(f)

for i, (title, align, lines) in T.items():
    t = c[f'Tab #{i} Settings']
    t['Button Title'] = title
    t['Text Align'] = align
    t['Text Lines'] = lines
    t['Text Style'] = 'robotocondensed-bold.ttf' if i in (2, 3, 4, 6) else 'robotocondensed-regular.ttf'
    t['Text Outline Thickness'] = '0.5'

c['Tab #6 Settings']['Background Image URL'] = ''  # мёртвая fakaheda-ссылка
c['General Settings']['Custom chat commands (don\'t delete from list, just rename).'][9] = 'bosses'

with io.open(CFG, 'w', encoding='utf-8') as f:
    json.dump(c, f, ensure_ascii=False, indent=2)

print('patched ok')
