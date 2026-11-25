# WipeKit Hub – Protokoll (Version 1)

Gilt für die WipeKit-Apps (macOS, Windows, Linux) und den selbst gehosteten
[WipeKit Hub](https://github.com/mllsm66/WipeKit-Hub). Ziel: Der Hub sieht **nie** den Inhalt der
Protokolle. Lesbar ist für ihn nur der kurze Hinweistext der Push-Mitteilung.

## Kopplung

Im Hub legt man ein Gerät an und bekommt einen **Kopplungscode**:

```
WKHUB1:<base64url(JSON)>
JSON = {"url": "https://hub.example.org", "token": "<Gerätetoken>", "key": "<base64 öffentlicher Schlüssel>"}
```

- `token`: zufällig, 32 Byte, nur für dieses Gerät, im Hub widerrufbar.
- `key`: öffentlicher P-256-Schlüssel des Hubs (unkomprimiert, 65 Byte, X9.63). Den passenden
  privaten Schlüssel kennt nur der Browser des Nutzers (er liegt im Hub nur mit dem Passwort des
  Nutzers verschlüsselt).

## Verschlüsselung („WKE1“)

ECIES mit Standardbausteinen, verfügbar in CryptoKit, .NET, Python `cryptography` und WebCrypto:

```
eph      = neues P-256-Schlüsselpaar (pro Nachricht)
secret   = ECDH(eph.private, hub.public)              # 32 Byte, x-Koordinate
key      = HKDF-SHA256(secret, salt = eph.public || hub.public, info = "WipeKit Hub v1", 32 Byte)
box      = AES-256-GCM(key, nonce = 12 zufällige Byte, aad = "WKE1")
blob     = "WKE1" || eph.public (65) || nonce (12) || ciphertext || tag (16)
```

## Ereignis senden

```
POST {url}/api/v1/events
Authorization: Bearer <token>
Content-Type: application/json

{
  "v": 1,
  "id": "<UUID>",
  "created": "2026-11-25T14:05:00Z",
  "kind": "wipe" | "check" | "test",
  "state": "done" | "failed" | "cancelled",
  "warning": false,                    # Gesundheitswarnung
  "notice": "Samsung SSD 970 EVO Plus: erfolgreich gelöscht",
  "meta": "<base64(blob)>",            # verschlüsseltes JSON, siehe unten
  "files": ["<base64(blob)>", ...]     # verschlüsselte PDFs, Reihenfolge wie meta.files
}
```

Antwort: `201` mit `{"ok": true}`. Höchstens 20 MB pro Ereignis.

### Inhalt von `meta` (verschlüsselt)

```json
{
  "vendor": "Samsung", "model": "SSD 970 EVO Plus 1TB", "serial": "S4EWNX0R…",
  "size": 1000204886016, "type": "nvme",          // hdd | ssd | nvme | usb | unknown
  "mode": "Sicher", "method": "2 Durchgänge …", "result": "Erfolgreich gelöscht",
  "rating": "Sehr gut", "started": "…", "ended": "…", "duration": 4380,
  "host": "iMac von Ben", "platform": "macOS 26.7.3", "app": "1.2.0",
  "files": ["Loeschprotokoll.pdf", "Loeschprotokoll-ausfuehrlich.pdf"]
}
```

## Anmeldung und Tresor (im Browser)

```
master   = PBKDF2-SHA256(Passwort, vaultSalt, 600 000 Durchläufe, 32 Byte)
authKey  = HKDF-SHA256(master, salt = authSalt, info = "WipeKit Hub auth", 32 Byte)   → an den Server
vaultKey = HKDF-SHA256(master, salt = authSalt, info = "WipeKit Hub vault", 32 Byte)  → bleibt im Browser
encPriv  = nonce (12) || AES-256-GCM(vaultKey, privater Schlüssel als PKCS#8)
```

Der Server speichert `PBKDF2-SHA256(authKey, Server-Salz, 100 000)` zum Vergleich sowie `encPriv`.
Aus `authKey` lässt sich `vaultKey` nicht berechnen.

## Mitteilungen

Web Push nach RFC 8030 / 8291 (aes128gcm) / 8292 (VAPID). Inhalt: `{"title", "body", "url", "tag"}` –
`body` ist die kurze Mitteilung aus dem Ereignis.
