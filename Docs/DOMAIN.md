# Свой домен для Rust-сервера (подключение по `rust.example.com`)

> По мотивам официального гайда: https://wiki.facepunch.com/rust/dns-records
> (дата проверки: 2026-01-10)

Rust умеет подключаться к серверу по доменному имени через **DNS SRV-запись**.
Игроку достаточно ввести в консоли (F1):

```
client.connect rust.example.com
```

Порт указывать не нужно — клиент сам получает его из SRV-записи.

---

## Что нужно создать в DNS

У вашего DNS-провайдера (Cloudflare, GoDaddy, reg.ru, Selectel и т.д.)
нужно создать **две записи**:

| Тип | Имя | Значение |
|-----|-----|----------|
| **A** | `rust` (→ `rust.example.com`) | IPv4-адрес сервера, напр. `188.47.38.152` |
| **SRV** | `_rust._udp.rust` | target: `rust.example.com`, port: ваш `server.port` |

Требования по гайду Facepunch:
- service = `_rust`, protocol = `_udp`;
- на один сервер — **только одна** SRV-запись;
- target SRV-записи — именно A-запись (домен, а не IP).

---

## Пример для Cloudflare

`dash.cloudflare.com` → ваш домен → **DNS → Records → Add record**.

### 1. A-запись

```
Type:   A
Name:   rust                     (полное имя будет rust.example.com)
IPv4:   188.47.38.152            (ваш публичный IP)
Proxy:  DNS only  ⬅ ВАЖНО, серое облако!
TTL:    Auto
```

**Оранжевое облако (Proxied) выключить обязательно**: проксирование
Cloudflare работает только для HTTP/HTTPS-трафика. Игровой UDP-трафик
Rust через него не пойдёт — клиенты просто не найдут сервер.

### 2. SRV-запись

```
Type:     SRV
Service:  _rust
Proto:    _udp
Name:     rust                   (в итоге _rust._udp.rust.example.com)
TTL:      Auto
Priority: 0
Weight:   0
Port:     28115                  (ваш server.port)
Target:   rust.example.com
```

У Cloudflare service/proto/name указываются в полях формы — она сама
соберёт полное имя `_rust._udp.rust.example.com`.

### Про cloudflared (Tunnel)

Туннель `cloudflared` (Cloudflare Tunnel / Zero Trust) проксирует
HTTP/HTTPS и, в расширенных схемах, TCP/SSH/RDP — но **не является
прозрачным прокси для UDP-трафика игры**. Для Rust он не нужен:
достаточно обычных DNS-записей выше. Туннель имеет смысл разве что
для веб-панелей/API, не для подключения игроков.

---

## Проектирование портов (не забудьте!)

DNS только «указывает дорогу» — сам трафик должен доходить до сервера:

- **UDP `server.port`** (игровой, напр. 28115) — открыть в брандмауэре
  Windows и пробросить на роутере, если сервер за NAT;
- **UDP `server.queryport`** — нужен для отображения в списке серверов
  Steam и для аутентификации SRV-адреса в браузере;
- **TCP `rcon.port`** — пробрасывать наружу **не обязательно** (и небезопасно
  без необходимости) — RSM использует его локально.

---

## Проверка

```powershell
# SRV-запись
nslookup -type=SRV _rust._udp.rust.example.com
Resolve-DnsName -Type SRV _rust._udp.rust.example.com

# A-запись
nslookup rust.example.com
```

Ожидаемый ответ SRV:

```
_rust._udp.rust.example.com  SRV service location:
    priority = 0, weight = 0, port = 28115, svr hostname = rust.example.com
```

Далее — консоль клиента (F1):

```
client.connect rust.example.com
```

В логе клиента должно быть:

```
Connecting through SRV DNS record rust.example.com -> rust.example.com:28115
Host Entry Found: 188.47.38.152
Connecting: 188.47.38.152:28115 (Raknet)
```

> DNS-записи могут обновляться до нескольких минут/часов (TTL).
> Если SRV ещё не отдаётся — проверьте через `1.1.1.1`:
> `nslookup -type=SRV _rust._udp.rust.example.com 1.1.1.1`

---

## Что делает OctavianRSM

- **Поле «Домен сервера»** в «Серв. конфиг → ОБЩЕЕ» и на шаге
  «Основные параметры» мастера установки: впишите сюда вашу A-запись
  (`rust.example.com`, без `http://` и без порта).
- При сохранении RSM прописывает в `server.cfg`:

  ```
  server.favoritesEndpoint "rust.example.com"
  ```

  Это включает «умное избранное»: игроки, добавившие сервер в
  избранное, будут находить его по домену даже после смены IP или порта.
- На **дашборде** строка `client.connect` начинает показывать домен
  вместо IP:порта — удобно копировать и кидать игрокам.

Краткая версия этого гайда встроена в приложение: кнопка
**«❔ КАК НАСТРОИТЬ ДОМЕН»** на вкладке «Серв. конфиг».
