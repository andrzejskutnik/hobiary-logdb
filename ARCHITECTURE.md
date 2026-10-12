# HobiaryLogDB — Dokumentacja Projektowa

## 1. Przegląd projektu

**HobiaryLogDB** to osadzany silnik logów zbudowany od podstaw w C11.
Przeznaczony jako zamiennik MongoDB + Serilog w projektach .NET desktopowych (EWD, CMM i podobnych).

**Cel edukacyjny**: zrozumieć jak działa wydajna, append-only baza logów od strony struktury plików, indeksowania i obsługi błędów na poziomie systemu.

### Główne cechy

- **Append-only** — rekordy nigdy nie są nadpisywane po zapisie
- **Segmentowane** — pliki `.hlog` do 64 MB; starsze segmenty są zapieczętowane i archiwizowane
- **Indeksowane** — sparse index po czasie + dense index po poziomie logowania i źródle
- **CRC32-chronione** — każdy rekord i nagłówek ma sumę kontrolną
- **Crash-safe** — przy starcie automatycznie wykrywa i naprawia uszkodzony ogon segmentu oraz brakujące indeksy
- **Wieloplatformowe** — Windows + Linux, zero zewnętrznych zależności C
- **Zarządzany wrapper** — `HobiaryLogClient` (C#, IDisposable) + Serilog sink

---

## 2. Stack technologiczny

| Warstwa | Technologia | Uwagi |
|---|---|---|
| Rdzeń silnika | **C11** | Zero zewnętrznych zależności NuGet/vcpkg |
| Build | **CMake 3.20+** | VS 2022 (Windows), Ninja (Linux) |
| Testy | **C# xUnit** (net10.0) | P/Invoke do `hobiarylogdb.dll` |
| Serilog sink | **Serilog 4.x** | NuGet w projekcie testowym |
| Platforma | **Windows + Linux** | `_WIN32` / POSIX ifdefs |

---

## 3. Architektura — warstwy

```
┌───────────────────────────────────────────────────────────────┐
│  Aplikacja .NET (EWD / CMM / inny projekt)                    │
├───────────────────────────────────────────────────────────────┤
│  Etap 8: HobiaryLogSink   ← Serilog ILogEventSink             │
├───────────────────────────────────────────────────────────────┤
│  Etap 7: HobiaryLogClient ← zarządzany wrapper C# (IDisposable)│
├───────────────────────────────────────────────────────────────┤
│  P/Invoke                 ← NativeHLog / NativeHLogFacade     │
├───────────────────────────────────────────────────────────────┤
│  Etap 3: hlog.c (fasada)  ← rotacja, TTL, visit              │
│    ├─ Etap 4: hlog_purge_before (TTL / retencja)              │
│    ├─ Etap 5: hlog_visit_level / hlog_visit_source            │
│    └─ Etap 6: crash recovery (scan_valid + rebuild indexes)   │
├────────────────────────┬──────────────────────────────────────┤
│  Etap 2: hlog_index.c  │  Etap 1: hlog_segment.c             │
│  (.hidx_* pliki)       │  (.hlog plik segmentu)               │
└────────────────────────┴──────────────────────────────────────┘
                         │
                  Pliki na dysku
```

---

## 4. Struktura katalogów

```
hobiary-logdb/
├── CMakeLists.txt                 ← definicja biblioteki hobiarylogdb
├── include/
│   ├── hlog_segment.h             ← publiczne API segmentu (etapy 1, 6)
│   ├── hlog_index.h               ← publiczne API indeksów (etapy 2, 5)
│   └── hlog.h                     ← fasada publiczna (etapy 3, 4, 5)
├── src/
│   ├── segment/
│   │   └── hlog_segment.c
│   ├── index/
│   │   └── hlog_index.c
│   └── log/
│       └── hlog.c
├── tests/
│   └── HobiaryLogDB.Tests/
│       ├── HobiaryLogDB.Tests.csproj
│       ├── NativeHLog.cs           ← P/Invoke: segment + etap 6
│       ├── NativeHLogIndex.cs      ← P/Invoke: indeksy (etap 5)
│       ├── SegmentTests.cs         ← etap 1 (12 testów)
│       ├── IndexTests.cs           ← etap 2 (15 testów)
│       ├── HlogTests.cs            ← etap 3 (12 testów)
│       ├── TtlTests.cs             ← etap 4 (7 testów)
│       ├── Level5Tests.cs          ← etap 5 (8 testów)
│       ├── CrashRecoveryTests.cs   ← etap 6 (8 testów)
│       ├── HobiaryLogClient.cs     ← wrapper C# (etap 7)
│       ├── ClientTests.cs          ← etap 7 (11 testów)
│       ├── HobiaryLogSink.cs       ← Serilog sink (etap 8)
│       └── SerilogSinkTests.cs     ← etap 8 (15 testów)
└── build/                          ← artefakty CMake (gitignored)
```

---

## 5. Format on-disk (NIEZMIENNY)

### 5.1 Plik segmentu `.hlog`

```
Offset    Rozmiar   Zawartość
──────────────────────────────────────────────────────────────
0         64 B      HlogSegmentFileHeader
64        4096 B    StringTable block
4160      n B       LogRecord[0]
4160+n    m B       LogRecord[1]
...
```

#### HlogSegmentFileHeader (64 bajty, `#pragma pack(1)`)

| Offset | Rozmiar | Pole | Opis |
|---|---|---|---|
| 0 | 4 | `magic` | `0x484C4F47` ("HLOG") |
| 4 | 2 | `version` | `1` |
| 6 | 1 | `flags` | `HLOG_FLAG_*` |
| 7 | 1 | `_reserved` | — |
| 8 | 8 | `created_at` | Unix epoch µs |
| 16 | 8 | `segment_seq` | numer sekwencji (1-based) |
| 24 | 8 | `record_count` | liczba rekordów |
| 32 | 8 | `first_ts` | timestamp pierwszego rekordu (µs) |
| 40 | 8 | `last_ts` | timestamp ostatniego rekordu (µs) |
| 48 | 4 | `strtable_offset` | zawsze `64` |
| 52 | 4 | `strtable_size` | bajty użyte w bloku 4096 |
| 56 | 4 | `header_crc32` | CRC32 bajtów [0..55] |
| 60 | 4 | `_padding` | — |

#### StringTable block (4096 bajtów na offset 64)

```
[uint16_t count]           2 B   — liczba zarejestrowanych stringów
[uint16_t offsets[256]]  512 B   — offset w data[] dla każdego stringa
[char data[3582]]              — null-terminated stringi (UTF-8)
```

String ID `0` = `""` (pusty/unknown) — zawsze. Max 256 unikalnych stringów per segment.

#### LogRecord

```
[HlogRecordHeader  80 B]
[payload            n B]    (n = payload_len, max 64 KB)
[uint32_t crc32     4 B]    CRC32 nagłówka + payloadu
```

#### HlogRecordHeader (80 bajtów, `#pragma pack(1)`)

| Offset | Rozmiar | Pole | Opis |
|---|---|---|---|
| 0 | 1 | `magic` | `0xA5` |
| 1 | 4 | `record_len` | header + payload + crc32 |
| 5 | 1 | `level` | `HlogLevel` (0–5) |
| 6 | 2 | `flags` | `HLOG_RFLAG_*` |
| 8 | 4 | `payload_len` | bajty payloadu |
| 12 | 8 | `timestamp` | Unix epoch µs |
| 20 | 8 | `ingested_at` | Unix epoch µs (czas zapisu) |
| 28 | 2 | `source_id` | ID w StringTable segmentu |
| 30 | 2 | `service_id` | ID w StringTable segmentu |
| 32 | 2 | `operation_id` | ID w StringTable segmentu |
| 34 | 2 | `entity_id` | ID w StringTable segmentu |
| 36 | 16 | `trace_id` | binary UUID |
| 52 | 16 | `correlation_id` | binary UUID |
| 68 | 8 | `span_id` | uint64 |
| 76 | 4 | `_padding` | — |

---

### 5.2 Pliki indeksów

#### Nagłówek wspólny (32 bajty, `#pragma pack(1)`)

| Offset | Rozmiar | Pole |
|---|---|---|
| 0 | 4 | `magic` |
| 4 | 2 | `version` |
| 6 | 2 | `_reserved` |
| 8 | 8 | `segment_seq` |
| 16 | 8 | `entry_count` |
| 24 | 4 | `header_crc32` |
| 28 | 4 | `_padding` |

#### `.hidx_time` — sparse, co 64 rekordy (magic `0x48494454`)

Wpis 12 B: `int64_t timestamp | uint32_t offset`

Umożliwia binary search + skip do offsetu przed żądanym czasem.

#### `.hidx_error` — dense, każdy ERROR/FATAL (magic `0x48494445`)

Wpis 12 B: jak wyżej. Pozwala pobrać tylko błędy bez skanowania całego segmentu.

#### `.hidx_level` — dense, każdy rekord (magic `0x4849444C`)

Wpis 16 B: `int64_t timestamp | uint32_t offset | uint8_t level | uint8_t[3] _pad`

#### `.hidx_src` — dense, rekordy z source_id ≠ 0 (magic `0x48494453`)

Wpis 16 B: `int64_t timestamp | uint32_t offset | uint16_t source_id | uint16_t _pad`

---

### 5.3 Układ katalogu logów

```
logs/
  hlog_000001.hlog          ← zapieczętowany (flag HLOG_FLAG_SEALED)
  hlog_000001.hidx_time
  hlog_000001.hidx_error
  hlog_000001.hidx_level
  hlog_000001.hidx_src
  hlog_000002.hlog          ← aktywny
  hlog_000002.hidx_time
  hlog_000002.hidx_error
  hlog_000002.hidx_level
  hlog_000002.hidx_src
```

Aktywny segment = najwyższy numer **bez** flagi `HLOG_FLAG_SEALED`. Brak dodatkowych metaplików.

---

## 6. Moduły — opis techniczny

### Etap 1: `hlog_segment`

Niskopoziomowy I/O jednego pliku `.hlog`.

**Kluczowe funkcje:**

- `hlog_segment_create(path, seq)` — tworzy nowy plik, inicjalizuje nagłówek i StringTable
- `hlog_segment_open(path)` — otwiera istniejący, wczytuje StringTable do pamięci
- `hlog_segment_append(seg, rec)` — internuje stringi z `HlogRecordInput`, serializuje nagłówek, oblicza CRC32, zapisuje rekord
- `hlog_segment_read_at(seg, offset, &hdr, &payload)` — odczytuje rekord, weryfikuje magic i CRC32
- `hlog_segment_close(seg, seal)` — opcjonalne zapieczętowanie (ustawia flagę + zapisuje nagłówek)
- `hlog_segment_scan_valid(seg, &end_offset, &last_ts)` *(etap 6)* — skanuje do pierwszego uszkodzonego, zwraca liczbę poprawnych rekordów
- `hlog_segment_truncate(seg, offset, count, last_ts)` *(etap 6)* — obcina plik do `offset`, aktualizuje nagłówek

**StringTable:** strings internowane przy każdym `append`; ID 0 = "" (pusty); maks. 256 unikalnych stringów / 3582 B danych per segment.

**CRC32:** table-based, zaimplementowany bezpośrednio w module (zero zależności).

---

### Etap 2: `hlog_index`

Zapis i odczyt plików `.hidx_*`.

**Kluczowe funkcje:**

- `hlog_index_create(path, magic, seq)` — tworzy nowy plik indeksu (time lub error)
- `hlog_index_open(path, expected_magic)` — otwiera istniejący, weryfikuje magic + CRC32 nagłówka
- `hlog_index_append(idx, ts, offset)` — dopisuje wpis 12 B
- `hlog_index_find_ge(idx, ts, &entry)` — binary search: szuka pierwszego wpisu `>= ts`
- `hlog_index_find_range(idx, from_ts, to_ts, fn, ctx)` — iteruje wpisy w przedziale czasu
- `hlog_level_index_append(idx, ts, offset, level)` *(etap 5)* — wpis 16 B z poziomem
- `hlog_src_index_append(idx, ts, offset, source_id)` *(etap 5)* — wpis 16 B z ID źródła
- `hlog_level_index_scan(idx, level, fn, ctx)` *(etap 5)* — skanuje wpisy o danym poziomie
- `hlog_src_index_scan(idx, source_id, fn, ctx)` *(etap 5)* — skanuje wpisy o danym source_id

---

### Etap 3: `hlog` (fasada)

Zarządza katalogiem — kolekcją segmentów.

**Kluczowe funkcje:**

- `hlog_open(dir, max_bytes)` — skanuje katalog, identyfikuje aktywny segment, otwiera wszystkie pliki; tworzy nowy segment jeśli katalog pusty
- `hlog_append(log, rec)` — deleguje do `hlog_segment_append`; po przekroczeniu `max_bytes` wykonuje rotację (seal + nowy segment z `seq+1`)
- `hlog_visit_range(log, from_ts, to_ts, fn, ctx)` — iteruje segmenty od najstarszego, używa `hidx_time` do skip'owania, wywołuje callback dla każdego pasującego rekordu
- `hlog_close(log)` — zamyka wszystkie pliki, zwalnia pamięć

**Rotacja:** przy każdym `append` sprawdzany jest rozmiar aktywnego segmentu. Przekroczenie `max_bytes` → `hlog_segment_close(seg, seal=1)` → `open_new_active(seq+1)`.

---

### Etap 4: TTL / retencja

- `hlog_purge_before(log, cutoff_ts)` — usuwa zapieczętowane segmenty, których `last_ts < cutoff_ts`. Usuwa 5 plików per segment: `.hlog`, `.hidx_time`, `.hidx_error`, `.hidx_level`, `.hidx_src`. Aktywny segment jest chroniony.

---

### Etap 5: Indeksy level/source

Dwa nowe pliki per segment:

- `.hidx_level` — wpis dla każdego rekordu (wszystkie poziomy)
- `.hidx_src` — wpis dla każdego rekordu z `source_id ≠ 0`

Fasada:

- `hlog_visit_level(log, level, fn, ctx)` — iteruje wszystkie segmenty przez `hlog_level_index_scan`
- `hlog_visit_source(log, source_id, fn, ctx)` — iteruje przez `hlog_src_index_scan`

---

### Etap 6: Crash recovery

Automatyczna naprawa przy `hlog_open`:

1. **Truncation recovery**: `hlog_segment_scan_valid` skanuje rekordy od `HLOG_FIRST_RECORD_OFFSET`. Jeśli `valid_end_offset < file_size` lub `valid_count ≠ header.record_count` → `hlog_segment_truncate` przywraca spójność.

2. **Index rebuild**: próba otwarcia wszystkich 4 plików indeksów. Jeśli jakikolwiek jest brakujący lub ma zły magic/CRC → `rebuild_indexes_for_seq` usuwa wszystkie 4, tworzy nowe, skanuje segment i odbudowuje indeksy w jednym przebiegu.

---

### Etap 7: `HobiaryLogClient` (C#)

Zarządzany wrapper eliminujący surowy P/Invoke z kodu użytkownika.

**Typy publiczne:**

- `HlogLevel` — enum (Trace, Debug, Info, Warn, Error, Fatal)
- `LogRecord` — `sealed record (HlogLevel Level, long Timestamp, string? Source, string? Service, byte[]? Payload)`
- `HobiaryLogClient : IDisposable`

**Ważne szczegóły implementacji:**

- `_visitFn` przechowywany jako pole instancji — zapobiega zebraniu delegate przez GC podczas trwającego wywołania natywnego
- `Append` automatycznie ustawia `ingested_at = DateTimeOffset.UtcNow` w µs
- `VisitCallback` tłumaczy `source_id`/`service_id` na string przez `hlog_segment_str`

---

### Etap 8: `HobiaryLogSink` (Serilog)

`ILogEventSink` zapisujący `LogEvent` Seriologa do `HobiaryLogClient`.

**Mapowanie pól:**

| Serilog | HobiaryLogDB |
|---|---|
| `LogEventLevel` | `HlogLevel` (1:1) |
| Rendered message | Payload (UTF-8 bytes) |
| `SourceContext` property | `Source` |
| `RequestId` / `EventId` property | `Service` |
| `Timestamp` | Unix epoch µs |

**Dwa warianty:**

1. `HobiaryLogSink(client)` — sink nie przejmuje własności klienta
2. `HobiaryLogSink(logDir)` — sink tworzy własny klient, `Dispose` go zamknie

---

## 7. Limity

| Parametr | Wartość |
|---|---|
| Max rozmiar segmentu | 64 MB (konfigurowalne) |
| Max payload per rekord | 64 KB (obcinany z flagą `HLOG_RFLAG_PAYLOAD_TRUNC`) |
| Max unikalnych stringów per segment | 256 |
| Max długość stringa | 255 znaków |
| Max rozmiar danych StringTable | 3582 B |
| Stride indeksu czasu | co 64 rekordy |

---

## 8. Kodowanie danych

- **Timestampy**: Unix epoch w **mikrosekundach** (int64_t). W C# konwersja: `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L`
- **Payload**: surowe bajty, dowolny format; konwencja projektu: UTF-8 tekst lub JSON
- **Stringi** (source, service, operation, entity): ANSI/UTF-8, max 255 znaków
- **Endianność**: natywna maszyny (project zakłada little-endian)

---

## 9. Build i testy

### Build (Windows)

```powershell
cd hobiary-logdb
cmake -S . -B build -G "Visual Studio 17 2022"
cmake --build build --config Release
# DLL kopiowana automatycznie do tests/.../bin/Debug/net10.0/
```

### Build (Linux)

```bash
cmake -S . -B build -G "Ninja" -DCMAKE_BUILD_TYPE=Release
cmake --build build
cp build/libhobiarylogdb.so tests/HobiaryLogDB.Tests/bin/Debug/net10.0/
```

### Uruchomienie testów

```powershell
dotnet test tests/HobiaryLogDB.Tests/HobiaryLogDB.Tests.csproj --logger "console;verbosity=normal"
```

### Statystyki testów

| Etap | Klasa testów | Testów |
|---|---|---|
| 1 | `SegmentTests` | 12 |
| 2 | `IndexTests` | 15 |
| 3 | `HlogTests` | 12 |
| 4 | `TtlTests` | 7 |
| 5 | `Level5Tests` | 8 |
| 6 | `CrashRecoveryTests` | 8 |
| 7 | `ClientTests` | 11 |
| 8 | `SerilogSinkTests` | 15 |
| **Łącznie** | | **88** |

---

## 10. Zasady rozwoju

- **Format on-disk jest niezmienny** po ustaleniu — każda zmiana struktury wymaga nowej wersji pola `version` w nagłówku i procedury migracji
- **Zero zewnętrznych zależności C** — tylko biblioteka standardowa C11
- **`#ifdef _WIN32 #define strncasecmp _strnicmp`** — wymagane we wszystkich modułach C używających porównań stringów
- **Każda nowa funkcja musi mieć test** w odpowiednim pliku `*Tests.cs`
- **Nie commituj ze złamanymi testami**
- Decyzje wymagające potwierdzenia: zmiana formatu on-disk, dodanie zewnętrznej zależności C
