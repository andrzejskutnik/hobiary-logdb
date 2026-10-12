# HobiaryLogDB

Experimental append-only log storage engine written in **C11**, with segmented
files, secondary indexes, retention and a C# P/Invoke wrapper. This is a
portfolio project, not a production durability guarantee.

```text
C# application -> P/Invoke -> C11 engine -> .hlog segments + .hidx indexes
```

## Try the demo

Requirements: CMake 3.20+, a C compiler, Ninja and .NET 10 SDK.
Run from the repository root:

```sh
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build
dotnet run --project samples/LogDemo/LogDemo.csproj
dotnet test tests/HobiaryLogDB.Tests/HobiaryLogDB.Tests.csproj
```

On Windows, use a MinGW UCRT64 compiler with Ninja. For a Visual Studio build,
pass `-p:NativeBuildDirectory=<absolute-path-to-build-Release-directory>`
to `dotnet run`.

The demo creates a **new temporary directory**, writes three synthetic records,
closes and reopens the log, compares the stored fields and payloads, and queries
the error-level index. It does not overwrite existing logs.

Expected output:

```text
[Info] Orders: Order 42 created
[Warn] Inventory: Stock is running low
[Error] Payments: Synthetic payment failure
PASS: reopened log contains 3 matching records; error index returned 1 record.
```

The last line prints the directory containing the actual data files for inspection.
Failures produce a nonzero exit code. The test suite also covers rotation,
indexes, retention and recovery; see [architecture](ARCHITECTURE.md).

## Portfolio ecosystem

- [HobiaryDB](https://github.com/andrzejskutnik/hobiarydb): general-purpose embedded database.
- [HobiaryLogPipe](https://github.com/andrzejskutnik/hobiary-logpipe): .NET log pipeline using this engine, with brokerless console and web demos.

Licensed under [Apache-2.0](LICENSE). The wrapper used by this demo is linked from
the existing test sources; there is no separately published NuGet package.

---

## Dokumentacja użytkowa

## Szybki start

### 1. Zbuduj bibliotekę

```powershell
# Windows (MinGW / ucrt64)
cmake -S . -B build -G "Ninja" -DCMAKE_BUILD_TYPE=Debug
cmake --build build
```

```bash
# Linux (Ninja)
cmake -S . -B build -G "Ninja" -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

Wynik: `build/hobiarylogdb.dll` (Windows) lub `build/libhobiarylogdb.so` (Linux).

### 2. Dodaj do projektu .NET

Skopiuj `hobiarylogdb.dll` / `libhobiarylogdb.so` do katalogu wyjściowego projektu, a następnie dodaj pliki z katalogu `tests/HobiaryLogDB.Tests/`:

- `HobiaryLogClient.cs` — zarządzany wrapper (etap 7)
- `NativeHLog.cs` — P/Invoke dla rdzenia logu
- `NativeHLogIndex.cs` — P/Invoke dla indeksów

Opcjonalnie (Serilog):

- `HobiaryLogSink.cs` — sink Serilog (etap 8)
- NuGet: `Serilog` ≥ 4.0

---

## API — HobiaryLogClient

### Otwieranie logu

```csharp
// Otwórz (lub utwórz) katalog logów z domyślnym limitem segmentu (64 MB)
using var client = new HobiaryLogClient("/var/log/myapp");

// Z własnym limitem segmentu (np. 8 MB — częstsza rotacja)
using var client = new HobiaryLogClient("/var/log/myapp", maxSegmentBytes: 8 * 1024 * 1024);
```

Konstruktor rzuca `InvalidOperationException` jeśli katalogu nie można otworzyć/utworzyć.

---

### Zapis rekordów

```csharp
// Minimalna wersja
client.Append(new LogRecord(HlogLevel.Info, timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L));

// Z tekstem (payload UTF-8) i źródłem
var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
client.Append(new LogRecord(
    Level:   HlogLevel.Error,
    Timestamp: ts,
    Source:  "DatabaseService",
    Service: "api-gateway",
    Payload: System.Text.Encoding.UTF8.GetBytes("Connection timeout after 30s")
));
```

**Uwaga:** `Timestamp` to Unix epoch w **mikrosekundach**. Konwersja z `DateTimeOffset`:

```csharp
long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
```

**Kody zwrotne `Append`:**

| Kod | Stała | Znaczenie |
|---|---|---|
| 0 | `HLOG_OK` | Sukces |
| -1 | `HLOG_ERR_IO` | Błąd I/O |
| -2 | `HLOG_ERR_ARGS` | Nieprawidłowe argumenty |
| -4 | `HLOG_ERR_FULL` | StringTable pełna (> 256 unikalnych stringów) |

---

### Odczyt — VisitRange

```csharp
// Wszystkie rekordy
int total = client.VisitRange(long.MinValue, long.MaxValue, rec =>
{
    Console.WriteLine($"[{rec.Level}] {Encoding.UTF8.GetString(rec.Payload ?? [])}");
    return true; // true = kontynuuj, false = przerwij
});

// Tylko ostatnia godzina
long oneHourAgo = (DateTimeOffset.UtcNow - TimeSpan.FromHours(1))
    .ToUnixTimeMilliseconds() * 1000L;
client.VisitRange(oneHourAgo, long.MaxValue, rec => { ... return true; });
```

---

### Odczyt — ReadRange (lista)

```csharp
// Wygodna wersja zwracająca List<LogRecord>
List<LogRecord> records = client.ReadRange();                         // wszystkie
List<LogRecord> recent  = client.ReadRange(fromTs: oneHourAgo);      // ostatnia godzina
List<LogRecord> window  = client.ReadRange(fromTs: t1, toTs: t2);    // przedział
```

---

### Odczyt — filtrowanie po poziomie

```csharp
// Wszystkie błędy
int errCount = client.VisitLevel(HlogLevel.Error, rec =>
{
    Console.WriteLine(Encoding.UTF8.GetString(rec.Payload ?? []));
    return true;
});
```

---

### Odczyt — filtrowanie po źródle

```csharp
int found = client.VisitSource(sourceId: 1, rec =>
{
    Console.WriteLine($"Source: {rec.Source}, {Encoding.UTF8.GetString(rec.Payload ?? [])}");
    return true;
});
```

---

### Właściwości informacyjne

```csharp
uint  segCount  = client.SegmentCount;       // łączna liczba segmentów
ulong activeSeq = client.ActiveSeq;          // numer aktywnego segmentu
ulong recCount  = client.ActiveRecordCount;  // rekordy w aktywnym segmencie
```

---

### TTL / retencja

```csharp
// Usuń segmenty starsze niż 7 dni
long cutoff = (DateTimeOffset.UtcNow - TimeSpan.FromDays(7))
    .ToUnixTimeMilliseconds() * 1000L;

int removed = client.PurgeBefore(cutoff);
Console.WriteLine($"Usunięto {removed} segmentów.");
```

`PurgeBefore` usuwa tylko **zapieczętowane** segmenty. Aktywny nigdy nie jest usuwany.

---

## API — Serilog Sink

### Instalacja

```xml
<PackageReference Include="Serilog" Version="4.*" />
```

Skopiuj `HobiaryLogSink.cs` do projektu.

---

### Użycie z istniejącym klientem

```csharp
using var client = new HobiaryLogClient("/var/log/myapp");

var logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.HobiaryLog(client)
    .CreateLogger();

logger.Information("Aplikacja uruchomiona");
logger.Error(ex, "Błąd połączenia");

((IDisposable)logger).Dispose(); // przed Dispose klienta!
```

---

### Użycie z katalogiem (sink zarządza klientem)

```csharp
var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.HobiaryLog(
        logDir: "/var/log/myapp",
        maxSegmentBytes: 16 * 1024 * 1024,
        restrictedToMinimumLevel: LogEventLevel.Warning)
    .CreateLogger();

((IDisposable)logger).Dispose(); // zamknie wewnętrzny HobiaryLogClient
```

---

### Mapowanie poziomów Serilog → HobiaryLogDB

| Serilog | HlogLevel |
|---|---|
| Verbose | Trace |
| Debug | Debug |
| Information | Info |
| Warning | Warn |
| Error | Error |
| Fatal | Fatal |

---

## Limity

| Parametr | Wartość |
|---|---|
| Max rozmiar segmentu | 64 MB (konfigurowalne) |
| Max payload per rekord | 64 KB |
| Max unikalnych stringów per segment | 256 |
| Max długość stringa | 255 znaków |

---

## Typowe błędy

**`InvalidOperationException: Nie można otworzyć logu`**
Sprawdź uprawnienia do katalogu i obecność `hobiarylogdb.dll` w katalogu wyjściowym.

**`Append` zwraca `-4`**
StringTable pełna. Zmniejsz liczbę unikalnych nazw source/service lub zmniejsz `maxSegmentBytes`.

**Log rośnie bez ograniczeń**
Zaplanuj regularne wywołanie `PurgeBefore`:

```csharp
client.PurgeBefore((DateTimeOffset.UtcNow - TimeSpan.FromDays(30)).ToUnixTimeMilliseconds() * 1000L);
```

---

## Ograniczenia

- **Thread-unsafe** — używaj `lock` przy współdzieleniu między wątkami
- **Little-endian** — format on-disk zakłada little-endian (x86/x64)
- **Brak szyfrowania / kompresji** — pliki `.hlog` zawierają dane w postaci jawnej

---

## Dokumentacja projektowa

Architektura, format on-disk, opis modułów i zasady rozwoju: [ARCHITECTURE.md](ARCHITECTURE.md)
