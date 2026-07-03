# DILOS AR/BE Parser Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse DILOS return files — AR (Auftragsrückmeldung / orders dispatched, record types `K*`/`C*`/`P*`/`L*`) and BE (Bestandsmeldung / stock report) — into clean C# objects, mirroring the existing AS/AI writers.

**Architecture:** Static parsers (`DilosArParser`, `DilosBeParser`) over shared format helpers (`DilosFormat`) in `Lkv.WeClapp.Core.Dilos`. AR groups sequentially: `K*` opens a `DilosArShipment` aggregate; `C*`/`P*`/`L*` attach to it (guarded by `OrderNumber1` match). Fail-loud via `DilosParseException` (line number) on any structural defect. Spec: `docs/superpowers/specs/2026-07-03-ar-be-parser-design.md`.

**Tech Stack:** .NET 10, xUnit, no external packages. Golden fixtures from `C:\Users\martin-lt\Development\LKV-Vorbereitung\LKV-Logistics-files\TestFiles\`.

## Global Constraints

- Working directory for all commands: `C:\Users\martin-lt\Development\LkvWeClapp`
- Commit messages: `AB#4228: <description>` (meshmakers wiki guideline) + `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
- Pre-commit gate (octo-adapter-demos convention): `dotnet format Lkv.WeClapp.sln --verify-no-changes && dotnet build Lkv.WeClapp.sln && dotnet test Lkv.WeClapp.sln`
- English code + XML docs on public types/members; DILOS original field name + 1-based field index in every property XML doc
- AR/BE numbers use **comma** decimal separator (`2,5`); dot must FAIL (differs from AI/AS!)
- Field counts are exact: `K*`=14, `C*`=7, `P*`=12, `L*`=11, BE=6 (verified on all 1871 golden lines)
- Model style mirrors `WeClappSalesOrder`: `public sealed record`, `{ get; init; } = ""` defaults, `List<T> X { get; init; } = new();`
- No new NuGet packages

---

### Task 1: Repo hygiene — solution file + strict compiler flags

**Files:**
- Create: `Lkv.WeClapp.sln` (via `dotnet new sln`)
- Modify: `src/Lkv.WeClapp.Core/Lkv.WeClapp.Core.csproj`
- Modify: `tests/Lkv.WeClapp.Core.Tests/Lkv.WeClapp.Core.Tests.csproj`

**Interfaces:**
- Consumes: nothing
- Produces: `Lkv.WeClapp.sln` used by every later gate command; both csproj gain `TreatWarningsAsErrors` + `LangVersion`

- [ ] **Step 1: Create solution and add projects**

```powershell
dotnet new sln -n Lkv.WeClapp
dotnet sln Lkv.WeClapp.sln add src/Lkv.WeClapp.Core/Lkv.WeClapp.Core.csproj tests/Lkv.WeClapp.Core.Tests/Lkv.WeClapp.Core.Tests.csproj
```

Expected: both `add` lines report the project was added.

- [ ] **Step 2: Add strict flags to both csproj**

In `src/Lkv.WeClapp.Core/Lkv.WeClapp.Core.csproj`, extend the existing `<PropertyGroup>`:

```xml
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latestmajor</LangVersion>
  </PropertyGroup>
```

Same two lines (`TreatWarningsAsErrors`, `LangVersion`) into the `<PropertyGroup>` of `tests/Lkv.WeClapp.Core.Tests/Lkv.WeClapp.Core.Tests.csproj` (keep `IsPackable` as is).

- [ ] **Step 3: Apply formatter once (baseline), then verify gate passes**

```powershell
dotnet format Lkv.WeClapp.sln
dotnet format Lkv.WeClapp.sln --verify-no-changes
dotnet build Lkv.WeClapp.sln
dotnet test Lkv.WeClapp.sln
```

Expected: verify exits 0; build 0 warnings/errors; **18 tests pass**. If `TreatWarningsAsErrors` surfaces warnings in existing code, fix them minimally (they are bugs by our own convention now).

- [ ] **Step 4: Commit**

```powershell
git add -A
git commit -m "AB#4228: add solution + TreatWarningsAsErrors/LangVersion per meshmakers template"
```

---

### Task 2: DilosParseException + DilosFormat helpers

**Files:**
- Create: `src/Lkv.WeClapp.Core/Dilos/DilosParseException.cs`
- Create: `src/Lkv.WeClapp.Core/Dilos/DilosFormat.cs`
- Modify: `src/Lkv.WeClapp.Core/Lkv.WeClapp.Core.csproj` (InternalsVisibleTo)
- Test: `tests/Lkv.WeClapp.Core.Tests/DilosFormatTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces (used by Tasks 3–5):
  - `public class DilosParseException(int lineNumber, string message) : Exception` with `public int LineNumber { get; }`
  - `internal static class DilosFormat` with:
    - `IEnumerable<(string Line, int Number)> DataLines(string content)` — physical 1-based line numbers, skips empty lines, strips `\r`
    - `decimal Dec(string value, int lineNumber, string field)` — comma decimal, leading sign; throws `DilosParseException`
    - `decimal? OptDec(string value, int lineNumber, string field)` — `""` → null
    - `int? OptInt(string value, int lineNumber, string field)` — `""` → null
    - `DateOnly? OptDate(string value, int lineNumber, string field)` — `dd.MM.yyyy` exact, `""` → null

- [ ] **Step 1: Add InternalsVisibleTo to core csproj**

Append inside `<Project>` of `src/Lkv.WeClapp.Core/Lkv.WeClapp.Core.csproj`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Lkv.WeClapp.Core.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Lkv.WeClapp.Core.Tests/DilosFormatTests.cs`:

```csharp
using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.Tests;

public class DilosFormatTests
{
    [Fact]
    public void DataLines_NumbersPhysicalLinesAndSkipsEmpty()
    {
        var lines = DilosFormat.DataLines("a\r\n\r\nb\r\n").ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal(("a", 1), lines[0]);
        Assert.Equal(("b", 3), lines[1]);
    }

    [Theory]
    [InlineData("2,5", 2.5)]
    [InlineData("-1", -1)]
    [InlineData("108", 108)]
    public void Dec_ParsesCommaDecimalAndSign(string raw, decimal expected)
    {
        Assert.Equal(expected, DilosFormat.Dec(raw, 7, "Menge"));
    }

    [Theory]
    [InlineData("2.5")]   // dot is NOT the AR/BE decimal separator — must fail loud
    [InlineData("1.000")] // no thousands separators either
    [InlineData("")]
    [InlineData("x")]
    public void Dec_ThrowsWithLineNumberOnInvalid(string raw)
    {
        var ex = Assert.Throws<DilosParseException>(() => DilosFormat.Dec(raw, 7, "Menge"));

        Assert.Equal(7, ex.LineNumber);
        Assert.Contains("Line 7", ex.Message);
        Assert.Contains("Menge", ex.Message);
    }

    [Fact]
    public void OptDec_EmptyIsNull() => Assert.Null(DilosFormat.OptDec("", 1, "f"));

    [Fact]
    public void OptInt_ParsesAndEmptyIsNull()
    {
        Assert.Equal(3, DilosFormat.OptInt("3", 1, "f"));
        Assert.Null(DilosFormat.OptInt("", 1, "f"));
        Assert.Throws<DilosParseException>(() => DilosFormat.OptInt("3,5", 1, "f"));
    }

    [Fact]
    public void OptDate_ParsesGermanFormatExactly()
    {
        Assert.Equal(new DateOnly(2024, 4, 10), DilosFormat.OptDate("10.04.2024", 1, "Datum"));
        Assert.Null(DilosFormat.OptDate("", 1, "Datum"));
        Assert.Throws<DilosParseException>(() => DilosFormat.OptDate("2024-04-10", 1, "Datum"));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Lkv.WeClapp.sln --filter DilosFormatTests`
Expected: compile error — `DilosFormat` does not exist.

- [ ] **Step 4: Implement**

Create `src/Lkv.WeClapp.Core/Dilos/DilosParseException.cs`:

```csharp
namespace Lkv.WeClapp.Core.Dilos;

/// <summary>Structural defect in a DILOS file (AR/BE). Fail-loud: no silent skipping.</summary>
/// <param name="lineNumber">Physical 1-based line number in the parsed content.</param>
/// <param name="message">Defect description; the line number is prefixed automatically.</param>
public class DilosParseException(int lineNumber, string message)
    : Exception($"Line {lineNumber}: {message}")
{
    /// <summary>Physical 1-based line number the defect was found at.</summary>
    public int LineNumber { get; } = lineNumber;
}
```

Create `src/Lkv.WeClapp.Core/Dilos/DilosFormat.cs`:

```csharp
using System.Globalization;

namespace Lkv.WeClapp.Core.Dilos;

/// <summary>
/// Value-format helpers for DILOS return files (AR/BE): comma decimal separator
/// (unlike AI/AS which use dot!), dates as dd.MM.yyyy, CRLF records.
/// </summary>
internal static class DilosFormat
{
    private static readonly NumberFormatInfo CommaDecimal = new()
    {
        NumberDecimalSeparator = ",",
        NegativeSign = "-",
    };

    /// <summary>Splits content into non-empty lines with physical 1-based line numbers.</summary>
    public static IEnumerable<(string Line, int Number)> DataLines(string content) =>
        content.Split('\n')
            .Select((raw, i) => (Line: raw.TrimEnd('\r'), Number: i + 1))
            .Where(t => t.Line.Length > 0);

    /// <summary>Mandatory decimal, comma separator, optional leading sign. Dot fails loud.</summary>
    public static decimal Dec(string value, int lineNumber, string field) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CommaDecimal, out var d)
            ? d
            : throw new DilosParseException(lineNumber, $"Field '{field}' is not a DILOS number: '{value}'");

    /// <summary>Optional decimal: empty → null.</summary>
    public static decimal? OptDec(string value, int lineNumber, string field) =>
        value.Length == 0 ? null : Dec(value, lineNumber, field);

    /// <summary>Optional integer: empty → null.</summary>
    public static int? OptInt(string value, int lineNumber, string field) =>
        value.Length == 0
            ? null
            : int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i)
                ? i
                : throw new DilosParseException(lineNumber, $"Field '{field}' is not an integer: '{value}'");

    /// <summary>Optional date, exactly dd.MM.yyyy (DILOS "TT.MM.JJJJ"): empty → null.</summary>
    public static DateOnly? OptDate(string value, int lineNumber, string field) =>
        value.Length == 0
            ? null
            : DateOnly.TryParseExact(value, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d)
                ? d
                : throw new DilosParseException(lineNumber, $"Field '{field}' is not a dd.MM.yyyy date: '{value}'");
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Lkv.WeClapp.sln --filter DilosFormatTests`
Expected: all DilosFormatTests PASS (18 existing tests untouched).

- [ ] **Step 6: Gate + commit**

```powershell
dotnet format Lkv.WeClapp.sln --verify-no-changes; dotnet build Lkv.WeClapp.sln; dotnet test Lkv.WeClapp.sln
git add -A
git commit -m "AB#4228: DILOS format helpers + fail-loud parse exception"
```

---

### Task 3: DilosBeParser (Bestandsmeldung)

**Files:**
- Create: `src/Lkv.WeClapp.Core/Dilos/DilosBeParser.cs`
- Create (fixtures): copy 3 files `BE_*.txt` into `tests/Lkv.WeClapp.Core.Tests/Fixtures/`
- Test: `tests/Lkv.WeClapp.Core.Tests/DilosBeParserTests.cs`

**Interfaces:**
- Consumes: `DilosFormat.DataLines/Dec`, `DilosParseException` (Task 2)
- Produces:
  - `public enum DilosStockStatus { Available, Blocked }`
  - `public sealed record DilosStockLine` with `string ArticleNumber/Characteristic1/Characteristic2/LotNumber`, `decimal Quantity`, `DilosStockStatus Status`
  - `public static class DilosBeParser` with `public static IReadOnlyList<DilosStockLine> Parse(string content)`

- [ ] **Step 1: Copy BE golden fixtures**

```powershell
Copy-Item "C:\Users\martin-lt\Development\LKV-Vorbereitung\LKV-Logistics-files\TestFiles\BE_*.txt" "tests\Lkv.WeClapp.Core.Tests\Fixtures\"
```

(The tests csproj already copies `Fixtures\**\*` to output — no csproj change.)

- [ ] **Step 2: Write the failing tests**

Create `tests/Lkv.WeClapp.Core.Tests/DilosBeParserTests.cs`:

```csharp
using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.Tests;

public class DilosBeParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    [Fact]
    public void Parse_ReadsGoldenStockLine()
    {
        // First line of BE_20240205035403463.txt: 39287037853853|0|0||108|VER
        var lines = DilosBeParser.Parse(Fixture("BE_20240205035403463.txt"));

        var first = lines[0];
        Assert.Equal("39287037853853", first.ArticleNumber);
        Assert.Equal("0", first.Characteristic1);
        Assert.Equal("0", first.Characteristic2);
        Assert.Equal("", first.LotNumber);
        Assert.Equal(108m, first.Quantity);
        Assert.Equal(DilosStockStatus.Available, first.Status);
    }

    [Theory]
    [InlineData("BE_20240205035403463.txt")]
    [InlineData("BE_20240206035402497.txt")]
    [InlineData("BE_20240410153954163.txt")]
    public void Parse_AllGoldenFilesRoundTripLineCounts(string file)
    {
        var content = Fixture(file);
        var nonEmptyLines = content.Split('\n').Count(l => l.TrimEnd('\r').Length > 0);

        var lines = DilosBeParser.Parse(content);

        Assert.Equal(nonEmptyLines, lines.Count);
        Assert.All(lines, l => Assert.NotEqual("", l.ArticleNumber));
    }

    [Fact]
    public void Parse_MapsGesToBlocked()
    {
        var lines = DilosBeParser.Parse("A1|0|0||5|GES\r\n");

        Assert.Equal(DilosStockStatus.Blocked, lines[0].Status);
    }

    [Fact]
    public void Parse_UnknownStatusThrows()
    {
        var ex = Assert.Throws<DilosParseException>(() => DilosBeParser.Parse("A1|0|0||5|XXX\r\n"));

        Assert.Equal(1, ex.LineNumber);
    }

    [Fact]
    public void Parse_WrongFieldCountThrows_IncludingBillbee7FieldVariant()
    {
        // Billbee's CsvBestandsmeldung expects 7 fields (extra SKU column) — the LKV
        // spec + all 1114 golden lines have 6. If the new customer ever gets the
        // 7-field variant, this must surface immediately, not parse shifted.
        var sevenFields = "A1|SKU-1|0|0||5|VER\r\n";

        var ex = Assert.Throws<DilosParseException>(() => DilosBeParser.Parse(sevenFields));

        Assert.Equal(1, ex.LineNumber);
        Assert.Contains("expected 6", ex.Message);
    }

    [Fact]
    public void Parse_EmptyQuantityThrows()
    {
        Assert.Throws<DilosParseException>(() => DilosBeParser.Parse("A1|0|0|||VER\r\n"));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Lkv.WeClapp.sln --filter DilosBeParserTests`
Expected: compile error — `DilosBeParser` does not exist.

- [ ] **Step 4: Implement**

Create `src/Lkv.WeClapp.Core/Dilos/DilosBeParser.cs`:

```csharp
namespace Lkv.WeClapp.Core.Dilos;

/// <summary>Stock condition (BE field 6 "Zustand"): VER → Available, GES → Blocked.
/// Names align with the Industry.Logistics CK enum StockStatus.</summary>
public enum DilosStockStatus
{
    /// <summary>DILOS "VER" (verfügbar / available).</summary>
    Available,

    /// <summary>DILOS "GES" (gesperrt / not available).</summary>
    Blocked,
}

/// <summary>One line of a DILOS BE file ("Bestandsmeldung", stock report). 6 pipe fields, no record prefix.</summary>
public sealed record DilosStockLine
{
    /// <summary>DILOS "Artikelnummer" (field 1).</summary>
    public string ArticleNumber { get; init; } = "";

    /// <summary>DILOS "Merkmal 1" (field 2), e.g. colour; raw.</summary>
    public string Characteristic1 { get; init; } = "";

    /// <summary>DILOS "Merkmal 2" (field 3), e.g. size; raw.</summary>
    public string Characteristic2 { get; init; } = "";

    /// <summary>DILOS "Lotnummer" (field 4); empty in spec and golden files.</summary>
    public string LotNumber { get; init; } = "";

    /// <summary>DILOS "Menge" (field 5): stock quantity including open order quantities in DILOS.</summary>
    public decimal Quantity { get; init; }

    /// <summary>DILOS "Zustand" (field 6): VER/GES.</summary>
    public DilosStockStatus Status { get; init; }
}

/// <summary>
/// Parses DILOS BE files ("Bestandsmeldung", stock report) — the read side of the
/// LKV → WeClapp return path. Fail-loud on structural defects (field count, unknown
/// status, unparsable quantity); see the design spec for the golden-file evidence.
/// </summary>
public static class DilosBeParser
{
    private const int FieldCount = 6;

    /// <summary>Parses BE file content (already decoded) into stock lines.</summary>
    /// <exception cref="DilosParseException">On any structural defect (fail-loud).</exception>
    public static IReadOnlyList<DilosStockLine> Parse(string content)
    {
        var result = new List<DilosStockLine>();

        foreach (var (line, number) in DilosFormat.DataLines(content))
        {
            var f = line.Split('|');
            if (f.Length != FieldCount)
            {
                throw new DilosParseException(number, $"BE record has {f.Length} fields, expected {FieldCount}");
            }

            result.Add(new DilosStockLine
            {
                ArticleNumber = f[0],
                Characteristic1 = f[1],
                Characteristic2 = f[2],
                LotNumber = f[3],
                Quantity = DilosFormat.Dec(f[4], number, "Menge"),
                Status = f[5] switch
                {
                    "VER" => DilosStockStatus.Available,
                    "GES" => DilosStockStatus.Blocked,
                    _ => throw new DilosParseException(number, $"Unknown Zustand '{f[5]}' (expected VER or GES)"),
                },
            });
        }

        return result;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Lkv.WeClapp.sln --filter DilosBeParserTests`
Expected: all PASS.

- [ ] **Step 6: Gate + commit**

```powershell
dotnet format Lkv.WeClapp.sln --verify-no-changes; dotnet build Lkv.WeClapp.sln; dotnet test Lkv.WeClapp.sln
git add -A
git commit -m "AB#4228: DILOS BE parser (stock report) with golden fixtures"
```

---

### Task 4: AR models + K* header parsing

**Files:**
- Create: `src/Lkv.WeClapp.Core/Dilos/DilosArModels.cs`
- Create: `src/Lkv.WeClapp.Core/Dilos/DilosArParser.cs`
- Create (fixture): copy `AR00006946.TXT` into `tests/Lkv.WeClapp.Core.Tests/Fixtures/`
- Test: `tests/Lkv.WeClapp.Core.Tests/DilosArParserTests.cs`

**Interfaces:**
- Consumes: `DilosFormat`, `DilosParseException` (Task 2)
- Produces (Task 5 extends the parser; Task 6 tests against it):
  - `public sealed record DilosArShipment` — K\* fields as listed in the code below + `List<DilosParcel> Parcels`, `List<DilosArItem> Items`, `List<DilosPackingLine> PackingLines`
  - `public sealed record DilosParcel`, `public sealed record DilosArItem`, `public sealed record DilosPackingLine` (full definitions below)
  - `public static class DilosArParser` with `public static IReadOnlyList<DilosArShipment> Parse(string content)`

- [ ] **Step 1: Copy the small AR golden fixture**

```powershell
Copy-Item "C:\Users\martin-lt\Development\LKV-Vorbereitung\LKV-Logistics-files\TestFiles\AR00006946.TXT" "tests\Lkv.WeClapp.Core.Tests\Fixtures\"
```

- [ ] **Step 2: Write the failing tests (K\* header slice)**

Create `tests/Lkv.WeClapp.Core.Tests/DilosArParserTests.cs`:

```csharp
using Lkv.WeClapp.Core.Dilos;

namespace Lkv.WeClapp.Core.Tests;

public class DilosArParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    // Golden AR00006946.TXT line 1:
    // K*|1|1|400000001572890||400000001247987|TEST-123|1001801714|1400137|2|10.04.2024|1|1|2,5
    [Fact]
    public void Parse_ReadsGoldenHeader()
    {
        var shipments = DilosArParser.Parse(Fixture("AR00006946.TXT"));

        var s = Assert.Single(shipments);
        Assert.Equal("1", s.Division);
        Assert.Equal("1", s.ClientId);
        Assert.Equal("400000001572890", s.InvoiceClientId);
        Assert.Equal("", s.Zone);
        Assert.Equal("400000001247987", s.OrderNumber1);
        Assert.Equal("TEST-123", s.OrderNumber2);
        Assert.Equal("1001801714", s.DilosOrderNumber);
        Assert.Equal("1400137", s.DilosForwardingNumber);
        Assert.Equal("2", s.Difference);
        Assert.Equal(new DateOnly(2024, 4, 10), s.ShipmentDate);
        Assert.Equal(1m, s.TotalQuantity);
        Assert.Equal(1, s.ParcelCount);
        Assert.Equal(2.5m, s.TotalWeight);
    }

    [Fact]
    public void Parse_HeaderWithWrongFieldCountThrows()
    {
        var ex = Assert.Throws<DilosParseException>(() => DilosArParser.Parse("K*|1|2\r\n"));

        Assert.Equal(1, ex.LineNumber);
        Assert.Contains("expected 14", ex.Message);
    }

    [Fact]
    public void Parse_UnknownPrefixThrows()
    {
        var ex = Assert.Throws<DilosParseException>(() => DilosArParser.Parse("X*|foo\r\n"));

        Assert.Equal(1, ex.LineNumber);
        Assert.Contains("X*", ex.Message);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Lkv.WeClapp.sln --filter DilosArParserTests`
Expected: compile error — types do not exist.

- [ ] **Step 4: Implement models (all four records) and the complete parser**

The parser below is complete (all four record types) — Task 4's tests exercise the
header and error paths; Task 5 adds the sub-record assertions against this same code.

Create `src/Lkv.WeClapp.Core/Dilos/DilosArModels.cs` (complete file):

```csharp
namespace Lkv.WeClapp.Core.Dilos;

/// <summary>
/// One shipment from a DILOS AR file ("Auftragsrückmeldung", orders dispatched):
/// a K* header plus its C* parcels, P* items and L* packing lines. Property names
/// align with the Industry.Logistics CK; XML docs carry the DILOS original names.
/// </summary>
public sealed record DilosArShipment
{
    /// <summary>DILOS "Submandant" (K* field 2, spec EN "Division").</summary>
    public string Division { get; init; } = "";

    /// <summary>DILOS "ClientIdnummer" (K* field 3): customer number of the goods recipient.</summary>
    public string ClientId { get; init; } = "";

    /// <summary>DILOS "ClientIdnummerkunde" (K* field 4): customer number of the invoice address.</summary>
    public string InvoiceClientId { get; init; } = "";

    /// <summary>DILOS "Zone" (K* field 5).</summary>
    public string Zone { get; init; } = "";

    /// <summary>DILOS "Auftragsnummer1" (K* field 6): delivery note number — our AI Auftragsnummer1.</summary>
    public string OrderNumber1 { get; init; } = "";

    /// <summary>DILOS "Auftragsnummer2" (K* field 7): shop order number (filled in golden files despite spec).</summary>
    public string OrderNumber2 { get; init; } = "";

    /// <summary>DILOS "Auftragsnummerintern" (K* field 8): DILOS-internal order number.</summary>
    public string DilosOrderNumber { get; init; } = "";

    /// <summary>DILOS "DILOS-Frachtnummer" (K* field 9): DILOS forwarding number.</summary>
    public string DilosForwardingNumber { get; init; } = "";

    /// <summary>DILOS "Differenzen" (K* field 10), raw: "0" = complete, "2" = shortages (not backordered by DILOS).</summary>
    public string Difference { get; init; } = "";

    /// <summary>DILOS "Datum" (K* field 11): date the order was dispatched (dd.MM.yyyy).</summary>
    public DateOnly? ShipmentDate { get; init; }

    /// <summary>DILOS "Gesamtmenge" (K* field 12): delivered quantity (number of parts).</summary>
    public decimal? TotalQuantity { get; init; }

    /// <summary>DILOS "Summe Colli" (K* field 13): number of outgoing parcels/pallets.</summary>
    public int? ParcelCount { get; init; }

    /// <summary>DILOS "Summe Gewicht" (K* field 14): total weight in kg.</summary>
    public decimal? TotalWeight { get; init; }

    /// <summary>C* records belonging to this shipment.</summary>
    public List<DilosParcel> Parcels { get; init; } = new();

    /// <summary>P* records belonging to this shipment.</summary>
    public List<DilosArItem> Items { get; init; } = new();

    /// <summary>L* records belonging to this shipment.</summary>
    public List<DilosPackingLine> PackingLines { get; init; } = new();
}

/// <summary>One DILOS AR C* record (parcel of a shipment).</summary>
public sealed record DilosParcel
{
    /// <summary>DILOS "Auftragsnummer1" (C* field 2); always matches the K* header (guarded).</summary>
    public string OrderNumber1 { get; init; } = "";

    /// <summary>DILOS "Spediteur" (C* field 3), raw carrier code (e.g. "800" = DPD per spec; mapping is adapter concern).</summary>
    public string Carrier { get; init; } = "";

    /// <summary>DILOS "Paketnummer" (C* field 4), raw: may be a carrier URL containing multiple
    /// comma-separated tracking numbers (golden DPD case) — no splitting here.</summary>
    public string TrackingNumber { get; init; } = "";

    /// <summary>DILOS "Verpackungsart" (C* field 5), e.g. "Karton".</summary>
    public string PackagingType { get; init; } = "";

    /// <summary>DILOS "Serviceart" (C* field 6), e.g. "Standard".</summary>
    public string ServiceType { get; init; } = "";

    /// <summary>DILOS "Gewicht" (C* field 7): weight per parcel in kg.</summary>
    public decimal? Weight { get; init; }
}

/// <summary>One DILOS AR P* record (dispatched order position).</summary>
public sealed record DilosArItem
{
    /// <summary>DILOS "Auftragsnummer1" (P* field 2); always matches the K* header (guarded).</summary>
    public string OrderNumber1 { get; init; } = "";

    /// <summary>DILOS "Position" (P* field 3): position on the delivery note.</summary>
    public int? Position { get; init; }

    /// <summary>DILOS "Artikelnummer" (P* field 4); may be empty in golden files (shipping-cost counterpart).</summary>
    public string ArticleNumber { get; init; } = "";

    /// <summary>DILOS "Teilezustand" (P* field 5).</summary>
    public string PartCondition { get; init; } = "";

    /// <summary>DILOS "Merkmal1" (P* field 6), raw.</summary>
    public string Characteristic1 { get; init; } = "";

    /// <summary>DILOS "Merkmal2" (P* field 7), raw.</summary>
    public string Characteristic2 { get; init; } = "";

    /// <summary>DILOS "Serialnummer" (P* field 8).</summary>
    public string SerialNumber { get; init; } = "";

    /// <summary>DILOS "Chargennummer" (P* field 9).</summary>
    public string BatchNumber { get; init; } = "";

    /// <summary>DILOS "Auftragsmenge" (P* field 10): quantity that should have been delivered.</summary>
    public decimal? OrderedQuantity { get; init; }

    /// <summary>DILOS "Menge geliefert" (P* field 11, mandatory): quantity dispatched, in stock unit.</summary>
    public decimal DeliveredQuantity { get; init; }

    /// <summary>DILOS "Offene Menge" (P* field 12): undelivered quantity; negative = over-delivery (golden: -1).</summary>
    public decimal? OpenQuantity { get; init; }
}

/// <summary>One DILOS AR L* record (packing list line: which article quantity is in which parcel).</summary>
public sealed record DilosPackingLine
{
    /// <summary>DILOS "Auftragsnummer1 / Sendungsnummer" (L* field 2). Golden files always carry
    /// Auftragsnummer1; the parser guards against the current K* and fails loud otherwise
    /// (deliberately strict — relax if real files ever carry forwarding numbers).</summary>
    public string OrderNumber1 { get; init; } = "";

    /// <summary>DILOS "Position" (L* field 3).</summary>
    public int? Position { get; init; }

    /// <summary>DILOS "Artikelnummer" (L* field 4, mandatory per spec).</summary>
    public string ArticleNumber { get; init; } = "";

    /// <summary>DILOS "Teilezustand" (L* field 5).</summary>
    public string PartCondition { get; init; } = "";

    /// <summary>DILOS "Merkmal1" (L* field 6), raw.</summary>
    public string Characteristic1 { get; init; } = "";

    /// <summary>DILOS "Merkmal2" (L* field 7), raw.</summary>
    public string Characteristic2 { get; init; } = "";

    /// <summary>DILOS "Serialnummer" (L* field 8).</summary>
    public string SerialNumber { get; init; } = "";

    /// <summary>DILOS "Chargennummer" (L* field 9).</summary>
    public string BatchNumber { get; init; } = "";

    /// <summary>DILOS "Packstückmenge" (L* field 10, mandatory): article quantity in this parcel.</summary>
    public decimal PackedQuantity { get; init; }

    /// <summary>DILOS "Paketnummer" (L* field 11): parcel/tracking number. NOTE: when the C* record
    /// carries a tracking URL, this bare number does NOT textually match C* (golden DPD case) —
    /// no automatic L*↔C* linking.</summary>
    public string TrackingNumber { get; init; } = "";
}
```

Create `src/Lkv.WeClapp.Core/Dilos/DilosArParser.cs` (K\* handling; `C*`/`P*`/`L*` cases throw a placeholder `DilosParseException` in this task and are completed in Task 5 — the golden fixture test for this task only asserts the header, so use this exact interim body):

```csharp
namespace Lkv.WeClapp.Core.Dilos;

/// <summary>
/// Parses DILOS AR files ("Auftragsrückmeldung", orders dispatched) — the read side of the
/// LKV → WeClapp return path. Sequential grouping: a K* header opens a shipment, subsequent
/// C*/P*/L* records attach to it (OrderNumber1 guarded). Fail-loud via DilosParseException.
/// </summary>
public static class DilosArParser
{
    private const int HeaderFieldCount = 14;
    private const int ParcelFieldCount = 7;
    private const int ItemFieldCount = 12;
    private const int PackingFieldCount = 11;

    /// <summary>Parses AR file content (already decoded) into shipments.</summary>
    /// <exception cref="DilosParseException">On any structural defect (fail-loud).</exception>
    public static IReadOnlyList<DilosArShipment> Parse(string content)
    {
        var shipments = new List<DilosArShipment>();
        DilosArShipment? current = null;

        foreach (var (line, number) in DilosFormat.DataLines(content))
        {
            var f = line.Split('|');
            switch (f[0])
            {
                case "K*":
                    Require(f, HeaderFieldCount, number);
                    current = ReadHeader(f, number);
                    shipments.Add(current);
                    break;

                case "C*":
                    Require(f, ParcelFieldCount, number);
                    Attach(current, f[1], number, "C*").Parcels.Add(ReadParcel(f, number));
                    break;

                case "P*":
                    Require(f, ItemFieldCount, number);
                    Attach(current, f[1], number, "P*").Items.Add(ReadItem(f, number));
                    break;

                case "L*":
                    Require(f, PackingFieldCount, number);
                    Attach(current, f[1], number, "L*").PackingLines.Add(ReadPackingLine(f, number));
                    break;

                default:
                    throw new DilosParseException(number, $"Unknown AR record prefix '{f[0]}'");
            }
        }

        return shipments;
    }

    private static void Require(string[] f, int count, int lineNumber)
    {
        if (f.Length != count)
        {
            throw new DilosParseException(lineNumber, $"{f[0]} record has {f.Length} fields, expected {count}");
        }
    }

    private static DilosArShipment Attach(DilosArShipment? current, string orderNumber1, int lineNumber, string record)
    {
        if (current is null)
        {
            throw new DilosParseException(lineNumber, $"{record} record before first K* header");
        }

        if (orderNumber1 != current.OrderNumber1)
        {
            throw new DilosParseException(lineNumber,
                $"{record} record OrderNumber1 '{orderNumber1}' does not match current K* '{current.OrderNumber1}'");
        }

        return current;
    }

    private static DilosArShipment ReadHeader(string[] f, int n) => new()
    {
        Division = f[1],
        ClientId = f[2],
        InvoiceClientId = f[3],
        Zone = f[4],
        OrderNumber1 = f[5],
        OrderNumber2 = f[6],
        DilosOrderNumber = f[7],
        DilosForwardingNumber = f[8],
        Difference = f[9],
        ShipmentDate = DilosFormat.OptDate(f[10], n, "Datum"),
        TotalQuantity = DilosFormat.OptDec(f[11], n, "Gesamtmenge"),
        ParcelCount = DilosFormat.OptInt(f[12], n, "Summe Colli"),
        TotalWeight = DilosFormat.OptDec(f[13], n, "Summe Gewicht"),
    };

    private static DilosParcel ReadParcel(string[] f, int n) => new()
    {
        OrderNumber1 = f[1],
        Carrier = f[2],
        TrackingNumber = f[3],
        PackagingType = f[4],
        ServiceType = f[5],
        Weight = DilosFormat.OptDec(f[6], n, "Gewicht"),
    };

    private static DilosArItem ReadItem(string[] f, int n) => new()
    {
        OrderNumber1 = f[1],
        Position = DilosFormat.OptInt(f[2], n, "Position"),
        ArticleNumber = f[3],
        PartCondition = f[4],
        Characteristic1 = f[5],
        Characteristic2 = f[6],
        SerialNumber = f[7],
        BatchNumber = f[8],
        OrderedQuantity = DilosFormat.OptDec(f[9], n, "Auftragsmenge"),
        DeliveredQuantity = DilosFormat.Dec(f[10], n, "Menge geliefert"),
        OpenQuantity = DilosFormat.OptDec(f[11], n, "Offene Menge"),
    };

    private static DilosPackingLine ReadPackingLine(string[] f, int n) => new()
    {
        OrderNumber1 = f[1],
        Position = DilosFormat.OptInt(f[2], n, "Position"),
        ArticleNumber = f[3],
        PartCondition = f[4],
        Characteristic1 = f[5],
        Characteristic2 = f[6],
        SerialNumber = f[7],
        BatchNumber = f[8],
        PackedQuantity = DilosFormat.Dec(f[9], n, "Packstückmenge"),
        TrackingNumber = f[10],
    };
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Lkv.WeClapp.sln --filter DilosArParserTests`
Expected: 3 tests PASS.

- [ ] **Step 6: Gate + commit**

```powershell
dotnet format Lkv.WeClapp.sln --verify-no-changes; dotnet build Lkv.WeClapp.sln; dotnet test Lkv.WeClapp.sln
git add -A
git commit -m "AB#4228: DILOS AR models + parser (K*/C*/P*/L*, sequential grouping)"
```

---

### Task 5: C\*/P\*/L\* assertions + grouping guards (golden AR00006946 complete)

**Files:**
- Modify: `tests/Lkv.WeClapp.Core.Tests/DilosArParserTests.cs` (add tests; parser exists from Task 4)

**Interfaces:**
- Consumes: `DilosArParser.Parse`, models (Task 4)
- Produces: verified sub-record behavior for Task 6's golden suite

- [ ] **Step 1: Write the tests (append to `DilosArParserTests`)**

```csharp
    // Golden AR00006946.TXT lines 2-4:
    // C*|400000001247987|9|1013408501850970172035|Karton|Standard|2,5
    // P*|400000001247987|1|400000001273682||||||0|1|-1
    // L*|400000001247987|1|400000001273682||||||1|1013408501850970172035
    [Fact]
    public void Parse_ReadsGoldenParcelItemAndPackingLine()
    {
        var s = Assert.Single(DilosArParser.Parse(Fixture("AR00006946.TXT")));

        var parcel = Assert.Single(s.Parcels);
        Assert.Equal("400000001247987", parcel.OrderNumber1);
        Assert.Equal("9", parcel.Carrier);
        Assert.Equal("1013408501850970172035", parcel.TrackingNumber);
        Assert.Equal("Karton", parcel.PackagingType);
        Assert.Equal("Standard", parcel.ServiceType);
        Assert.Equal(2.5m, parcel.Weight);

        var item = Assert.Single(s.Items);
        Assert.Equal(1, item.Position);
        Assert.Equal("400000001273682", item.ArticleNumber);
        Assert.Equal("", item.PartCondition);
        Assert.Equal(0m, item.OrderedQuantity);
        Assert.Equal(1m, item.DeliveredQuantity);
        Assert.Equal(-1m, item.OpenQuantity); // over-delivery, golden-verified

        var packing = Assert.Single(s.PackingLines);
        Assert.Equal(1, packing.Position);
        Assert.Equal("400000001273682", packing.ArticleNumber);
        Assert.Equal(1m, packing.PackedQuantity);
        Assert.Equal("1013408501850970172035", packing.TrackingNumber);
    }

    [Fact]
    public void Parse_SubRecordBeforeHeaderThrows()
    {
        var ex = Assert.Throws<DilosParseException>(
            () => DilosArParser.Parse("C*|123|800|T1|Karton|Standard|1,0\r\n"));

        Assert.Equal(1, ex.LineNumber);
        Assert.Contains("before first K*", ex.Message);
    }

    [Fact]
    public void Parse_SubRecordOrderNumberMismatchThrows()
    {
        var content =
            "K*|1|1|||ORDER-A||1|1|0|10.04.2024|1|1|1,0\r\n" +
            "C*|ORDER-B|800|T1|Karton|Standard|1,0\r\n";

        var ex = Assert.Throws<DilosParseException>(() => DilosArParser.Parse(content));

        Assert.Equal(2, ex.LineNumber);
        Assert.Contains("ORDER-B", ex.Message);
        Assert.Contains("ORDER-A", ex.Message);
    }

    [Fact]
    public void Parse_SubRecordWrongFieldCountThrows()
    {
        var content =
            "K*|1|1|||ORDER-A||1|1|0|10.04.2024|1|1|1,0\r\n" +
            "P*|ORDER-A|1|ART|x\r\n";

        var ex = Assert.Throws<DilosParseException>(() => DilosArParser.Parse(content));

        Assert.Equal(2, ex.LineNumber);
        Assert.Contains("expected 12", ex.Message);
    }
```

- [ ] **Step 2: Run tests to verify they pass** (parser is complete since Task 4)

Run: `dotnet test Lkv.WeClapp.sln --filter DilosArParserTests`
Expected: all PASS. If any fail, the parser (not the test) is wrong — fix parser, keep test.

- [ ] **Step 3: Gate + commit**

```powershell
dotnet format Lkv.WeClapp.sln --verify-no-changes; dotnet build Lkv.WeClapp.sln; dotnet test Lkv.WeClapp.sln
git add -A
git commit -m "AB#4228: AR sub-record assertions + grouping guard tests"
```

---

### Task 6: Golden full suite — multi-shipment file + real-world quirks

**Files:**
- Create (fixtures): copy remaining 4 `AR*.TXT` into `tests/Lkv.WeClapp.Core.Tests/Fixtures/`
- Modify: `tests/Lkv.WeClapp.Core.Tests/DilosArParserTests.cs` (append tests)

**Interfaces:**
- Consumes: `DilosArParser.Parse` (Task 4)
- Produces: full golden coverage; done-signal for the parser feature

- [ ] **Step 1: Copy remaining AR golden fixtures**

```powershell
Copy-Item "C:\Users\martin-lt\Development\LKV-Vorbereitung\LKV-Logistics-files\TestFiles\AR2024*.TXT" "tests\Lkv.WeClapp.Core.Tests\Fixtures\"
```

- [ ] **Step 2: Write the tests (append to `DilosArParserTests`)**

```csharp
    // awk-verified 2026-07-03 on AR20240205143134947.TXT: 33x K*, 33x C*, 92x P*, 59x L*
    [Fact]
    public void Parse_MultiShipmentFileGroupsAllRecords()
    {
        var shipments = DilosArParser.Parse(Fixture("AR20240205143134947.TXT"));

        Assert.Equal(33, shipments.Count);
        Assert.Equal(33, shipments.Sum(s => s.Parcels.Count));
        Assert.Equal(92, shipments.Sum(s => s.Items.Count));
        Assert.Equal(59, shipments.Sum(s => s.PackingLines.Count));
        Assert.All(shipments, s => Assert.NotEqual("", s.OrderNumber1));
    }

    // Golden: C* TrackingNumber can be a carrier URL with multiple comma-separated
    // tracking numbers — the parser must keep it raw, no splitting.
    [Fact]
    public void Parse_KeepsTrackingUrlRaw()
    {
        var shipments = DilosArParser.Parse(Fixture("AR20240205143134947.TXT"));

        var first = shipments[0]; // K* OrderNumber1 5905280991569, C* carrier 800 (DPD)
        var parcel = Assert.Single(first.Parcels);
        Assert.Equal("800", parcel.Carrier);
        Assert.StartsWith("http://www.mydpd.at/", parcel.TrackingNumber);
        Assert.Contains(",", parcel.TrackingNumber); // multiple tracking numbers inside
    }

    // Golden: P*|5905280991569|3|||||||1|1|0 — empty ArticleNumber is valid data.
    [Fact]
    public void Parse_EmptyItemArticleNumberIsKeptNotRejected()
    {
        var shipments = DilosArParser.Parse(Fixture("AR20240205143134947.TXT"));

        var withEmpty = shipments[0].Items.Single(i => i.Position == 3);
        Assert.Equal("", withEmpty.ArticleNumber);
        Assert.Equal(1m, withEmpty.DeliveredQuantity);
    }

    [Theory]
    [InlineData("AR00006946.TXT")]
    [InlineData("AR20240205143134947.TXT")]
    [InlineData("AR20240205150134383.TXT")]
    [InlineData("AR20240206080135220.TXT")]
    [InlineData("AR20240206083134910.TXT")]
    public void Parse_AllGoldenFilesParseCleanly(string file)
    {
        var shipments = DilosArParser.Parse(Fixture(file));

        Assert.NotEmpty(shipments);
        Assert.All(shipments, s => Assert.NotEmpty(s.Parcels));
    }
```

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test Lkv.WeClapp.sln --filter DilosArParserTests`
Expected: all PASS (multi-shipment counts must match the awk-verified numbers exactly).

- [ ] **Step 4: Gate + commit**

```powershell
dotnet format Lkv.WeClapp.sln --verify-no-changes; dotnet build Lkv.WeClapp.sln; dotnet test Lkv.WeClapp.sln
git add -A
git commit -m "AB#4228: golden full suite for AR parser (multi-shipment, URL tracking, quirks)"
```

---

### Task 7: README + final verification sweep

**Files:**
- Create: `README.md` (repo root)

**Interfaces:**
- Consumes: everything above
- Produces: documentation per octo-adapter-demos convention (docs are mandatory with code changes)

- [ ] **Step 1: Write README.md**

```markdown
# LkvWeClapp

Core library for the LKV ↔ WeClapp integration (meshmakers). Plain .NET 10,
no platform dependencies — designed to be consumed by the OctoMesh adapter
(Phase 2, per `octo-adapter-demos` template).

## What's inside

`Lkv.WeClapp.Core`:

- **WeClapp → DILOS (outbound)**: `WeClappJson` parsing, `WeClappToDilos`
  value rules, `DilosArticleWriter` (AS `A*`), `DilosOrderWriter` (AI `K*`/`P*`)
- **DILOS → WeClapp (return path)**: `DilosArParser` (AR `K*`/`C*`/`P*`/`L*`
  → `DilosArShipment` aggregates), `DilosBeParser` (BE stock lines)

Design docs: `docs/superpowers/specs/`. All field mappings are verified against
the official DILOS specs and real LKV golden files (see spec legend 📘/📁/✅).

## Build & test

```powershell
dotnet build Lkv.WeClapp.sln
dotnet test Lkv.WeClapp.sln

# pre-commit gate (meshmakers convention)
dotnet format Lkv.WeClapp.sln --verify-no-changes; dotnet build Lkv.WeClapp.sln; dotnet test Lkv.WeClapp.sln
```

Commits: `AB#4228: <description>` (Azure Boards work item link).
```

- [ ] **Step 2: Full gate**

```powershell
dotnet format Lkv.WeClapp.sln --verify-no-changes
dotnet build Lkv.WeClapp.sln
dotnet test Lkv.WeClapp.sln
```

Expected: format clean; build 0 warnings; **all tests pass** (18 existing + new DilosFormat/Be/Ar suites).

- [ ] **Step 3: Spec coverage check**

Walk `docs/superpowers/specs/2026-07-03-ar-be-parser-design.md` section by section and confirm each Scope item and each test group (1–7) exists in code/tests. Fix gaps before committing.

- [ ] **Step 4: Commit**

```powershell
git add -A
git commit -m "AB#4228: README documenting core lib + AR/BE return path"
```
